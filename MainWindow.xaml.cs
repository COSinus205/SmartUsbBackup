using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Foundation.Collections;
using Windows.Graphics;
using Windows.Storage.Pickers;
using SmartUsbBackup.Core;
using SmartUsbBackup.Sync;
using SmartUsbBackup.Usb;
using static SmartUsbBackup.NativeMethods;

namespace SmartUsbBackup;

/// <summary>
/// Главное окно приложения (WinUI 3, unpackaged).
///
/// Архитектура UI:
///   • WM_DEVICECHANGE перехватывается через подмену оконной процедуры HWND —
///     arrives/disappears событий достаточно, polling отсутствует (0% CPU в простое).
///   • Флешки — ListView со строками DriveRow (x:Bind + INPC) поверх DriveProfile.
///   • Прогресс — «(3 / 14452, 2,9 МБ) F:\...», обновление ≤5 раз/с (экономия CPU).
///   •.contentDialog вместо MessageBox: confirm-диалоги на ContentDialog.
///   • Крестик = свернуть в трей (H.NotifyIcon); полный выход — из меню трея.
///   • Все события с фоновых потоков маршалится через DispatcherQueue.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly UsbDeviceWatcher _usbWatcher = new();
    private readonly BackupOrchestrator _orchestrator = new();

    private readonly System.Collections.ObjectModel.ObservableCollection<DriveRow> _rows = new();

    private DispatcherQueue _dq = null!;
    private IntPtr _hwnd;
    private AppWindow _appWindow = null!;

    private readonly bool _startHidden;
    private long _lastProgressPaint;   // throttle отрисовки прогресса (≤5 раз/с)

    // true — пользователь выбрал «Выход» в трее: крестик больше НЕ сворачивает окно,
    // а даёт приложению действительно завершиться.
    private bool _isExiting;

    // Собственная иконка трея (Shell_NotifyIcon). Создаётся в конструкторе.
    private NativeTrayIcon? _tray;

    public MainWindow(bool startHidden)
    {
        _startHidden = startHidden;
        InitializeComponent();

        // ---------- Первый запуск: создаём настройки ----------
        if (string.IsNullOrEmpty(SettingsStore.Current.BaseBackupPath))
        {
            SettingsStore.Current.BaseBackupPath = SettingsStore.DefaultBasePath;
            SettingsStore.Save();
        }

        _dq = DispatcherQueue.GetForCurrentThread();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = this.AppWindow;

        ConfigureWindow();
        HookOrchestrator();
        HookUsb();

        // Трей: нативное меню + иконка. Клик по иконке/«Открыть окно» показывает
        // окно, «Выход» завершает приложение.
        _tray = new NativeTrayIcon();
        _tray.OpenRequested += () => _dq.TryEnqueue(RestoreWindow);
        _tray.ExitRequested += ExitFromTray;
        UpdateTrayText();

        // Все Click/Checked/Unchecked-обработчики подключены в XAML (атрибуты Click=...).

        DriveList.ItemsSource = _rows;

        // Начальные значения чекбоксов (до подписки событий их нельзя ставить — но
        // события ставят ТУ ЖЕ самое значение, harmless).
        ChkMirror.IsChecked = SettingsStore.Current.MirrorMode;
        ChkCrc.IsChecked = SettingsStore.Current.VerifyByCrc;
        ChkLiveWatch.IsChecked = SettingsStore.Current.LiveWatch;
    RefreshStartupCheckboxFromRegistry();
        TxtBackupPath.Text = SettingsStore.Current.BaseBackupPath;

        RefreshList();
        AppLog.CleanupOld();
        EnumerateAlreadyPresentDrives();
    }

    // =====================================================================
    //  ОКНО: размер, центр, Mica, крестик → трей
    // =====================================================================

    private void ConfigureWindow()
    {
        Title = "Smart USB Backup";

        // Mica для unpackaged: официальный путь через SystemBackdrop (WASDK 1.3+).
        // На Windows 11 даёт фон в цвет рабочего стола; работает без MSIX.
        try { SystemBackdrop = new MicaBackdrop(); } catch { /* нет Mica — останется стандартный фон */ }

        // Иконка окна + exe (unpackaged).
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.ico");
            if (File.Exists(iconPath)) _appWindow.SetIcon(iconPath);
        }
        catch { /* не критично */ }

        // Минимальный размер окна (700×520 DIP) — классический WM_GETMINMAXINFO,
        // не зависящий от версии Windows App SDK.
        WindowMinSize.Install(_hwnd, 700, 520);

        // Начальный размер задаём ПОСЛЕ первой активации: при Activate() WinUI
        // перезаписывает размер окна размером содержимого, поэтому Resize до
        // показа не держится (окно открывалось 640×427 вместо 960×640).
        Activated += OnFirstActivated;

        // Крестик = сворачивание в трей (как в WinForms-версии).
        _appWindow.Closing += (_, e) =>
        {
            // Настоящий выход из меню трея: не мешаем окну закрыться,
            // иначе Application.Exit() отменялся и процесс оставался висеть.
            if (_isExiting) return;

            e.Cancel = true; // окно не уничтожаем — оно продолжает слушать USB
            _dq.TryEnqueue(() =>
            {
                try { _appWindow.Hide(); } catch { }
                UpdateTrayText();
            });
        };
    }

    private bool _initialLayoutApplied;

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_initialLayoutApplied) return;
        _initialLayoutApplied = true;
        ApplyInitialLayout();
    }

    /// <summary>
    /// Размер 960×640 DIP (с учётом DPI монитора) + центрирование на рабочей области.
    /// Вызывается один раз, после того как окно реально активировано.
    /// </summary>
    private void ApplyInitialLayout()
    {
        try
        {
            // RasterizationScale — единственный надёжный источник масштаба для XAML:
            // GetDpiForWindow в момент первой активации ещё может вернуть 96,
            // из-за чего окно открывалось 960×640 ФИЗИЧЕСКИХ пикселей (640×427 DIP)
            // и правые колонки таблицы обрезались.
            double scale = Content?.XamlRoot?.RasterizationScale ?? 0;
            if (scale <= 0)
            {
                uint dpi = GetDpiForWindow(_hwnd);
                scale = dpi == 0 ? 1.0 : dpi / 96.0;
            }

            int w = (int)Math.Round(960 * scale);
            int h = (int)Math.Round(640 * scale);

            _appWindow.Resize(new SizeInt32(w, h));

            var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
            _appWindow.Move(new PointInt32(
                area.WorkArea.X + Math.Max(0, (area.WorkArea.Width - w) / 2),
                area.WorkArea.Y + Math.Max(0, (area.WorkArea.Height - h) / 2)));

            AppLog.Info($"Окно: масштаб {scale:F2}, размер {w}×{h} px");
        }
        catch (Exception ex) { AppLog.Error(ex, "Не удалось задать начальный размер окна"); }
    }

    /// <summary>Вторая копия приложения (или пользователь) просит показать окно.</summary>
    private void RestoreWindow()
    {
        AppLog.Info("Трей: восстановление окна");
        try
        {
            // Одного AppWindow.Show() недостаточно: WinUI после Hide() не
            // восстанавливает окно Win32, поэтому показываем его ещё и через
            // ShowWindow + SW_RESTORE и активируем.
            _appWindow.Show();
            ShowWindow(_hwnd, SW_SHOW);
            ShowWindow(_hwnd, SW_RESTORE);
            Activate();
            SetForegroundWindow(_hwnd);

            AppLog.Info($"Трей: окно видимо = {_appWindow.IsVisible}");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Не удалось показать окно из трея");
        }

        UpdateTrayText();
    }

    // =====================================================================
    //  СОБЫТИЯ СИСТЕМЫ → UI (через DispatcherQueue, без опроса)
    // =====================================================================

    private void HookOrchestrator()
    {
        _orchestrator.ProgressChanged += (_, p) => _dq.TryEnqueue(() => OnProgress(p));
        _orchestrator.StateChanged += () => _dq.TryEnqueue(RefreshList);
    }

    private void HookUsb()
    {
        // DriveArrived может прийти из фонового Task.Run (чтение серийника) — генерируем в UI.
        _usbWatcher.DriveArrived += e => _dq.TryEnqueue(() => OnDriveArrivedUi(e));
        // DriveRemoved/Restore приходят прямо из оконной процедуры (UI-поток), но очередь
        // всё равно не помешает (гарантия отсутствия reentrancy в WndProc).
        _usbWatcher.DriveRemoved += l => _dq.TryEnqueue(() => OnDriveRemovedUi(l));
        _usbWatcher.RestoreRequested += () => _dq.TryEnqueue(RestoreWindow);

        _usbWatcher.Attach(_hwnd);
    }

    private void OnDriveArrivedUi(UsbDriveEventArgs e)
    {
        _orchestrator.OnDriveArrived(e);
        RefreshList();

        // Новая флешка по умолчанию НЕ резервируется — подсказываем, что делать.
        var profile = SettingsStore.Current.Drives
            .FirstOrDefault(d => string.Equals(d.Serial, e.Serial, StringComparison.OrdinalIgnoreCase));
        if (profile is not null && !profile.Enabled)
            TxtStatus.Text = $"Флешка «{e.VolumeLabel}» добавлена. Поставьте галочку «Активна», чтобы начать резервирование.";

        if (IsWindowHidden()) // (только если окно скрыто — иначе пользователь всё видит)
            SafeNotify("Smart USB Backup",
                $"Флешка «{e.VolumeLabel}» подключена — синхронизация начата");
    }

    private void OnDriveRemovedUi(char letter)
    {
        _orchestrator.OnDriveRemoved(letter);
        RefreshList();

        ResetProgressIfIdle();

        if (IsWindowHidden())
            SafeNotify("Smart USB Backup", "Флешка отключена");
    }

    private bool IsWindowHidden()
    {
        try { return !_appWindow.IsVisible; } catch { return false; }
    }

    private void SafeNotify(string title, string message)
    {
        try { _tray?.ShowBalloon(title, message); } catch { /* трей мог не создаться */ }
    }

    /// <summary>
    /// Флешки, УЖЕ вставленные до запуска приложения: ОДНОРАЗОВЫЙ обход при старте
    /// (не polling). Позже реагируем только на WM_DEVICECHANGE.
    /// </summary>
    private void EnumerateAlreadyPresentDrives()
    {
        _ = Task.Run(() =>
        {
            foreach (var drive in DriveInfo.GetDrives()
                         .Where(d => d.DriveType == DriveType.Removable && d.IsReady))
            {
                var q = UsbDeviceWatcher.QueryDrive(drive.Name[0], retry: 1, delayMs: 0);
                if (q is not null)
                    _usbWatcher.RaiseArrived(drive.Name[0], q.Value.VolumeLabel, q.Value.Serial);
            }
        });

        // Сжимаем рабочий набор после «тихой» фазы (~0% CPU).
        _ = Task.Delay(2000).ContinueWith(_ =>
        {
            if (IsWindowHidden()) NativeMethods.TrimWorkingSet();
        });
    }

    // =====================================================================
    //  КНОПКИ И ЧЕКБОКСЫ
    // =====================================================================

    private async void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            // Pickers в unpackaged-приложении требуют привязки к окну.
            picker.FileTypeFilter.Add("*"); // обязательный workaround для FolderPicker
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            SettingsStore.Current.BaseBackupPath = folder.Path;
            SettingsStore.Save();
            TxtBackupPath.Text = folder.Path;
            RefreshList();
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Не удалось выбрать папку бэкапов");
            await ShowInfoAsync("Ошибка", "Не удалось открыть диалог выбора папки:\n" + ex.Message);
        }
    }

    private void OnSyncAllClicked(object sender, RoutedEventArgs e)
    {
        if (!_orchestrator.ActiveSessions.Any(s => s.Profile.Enabled))
        {
            _ = ShowInfoAsync("Нет активных флешек",
                "Вставьте флешку и поставьте галочку «Активна» — синхронизация запустится автоматически.");
            return;
        }
        _orchestrator.SyncAllNow();
        RefreshList();
    }

    private async void OnWipeSelectedClicked(object sender, RoutedEventArgs e)
    {
        if (DriveList.SelectedItem is not DriveRow row) return;

        var ok = await ConfirmAsync(
            "Удаление бэкапа",
            $"Удалить бэкап флешки «{row.Profile.Label}»?\nПапка: {row.BackupPath}",
            "Удалить");
        if (!ok) return;

        await _orchestrator.WipeOneAsync(row.Profile);
        RefreshList();
        ResetProgressIfIdle();
    }

    // ---------- Настройки-чекбоксы ----------

    private void OnMirrorChecked(object sender, RoutedEventArgs e) => SaveSetting(s => s.MirrorMode = true);
    private void OnMirrorUnchecked(object sender, RoutedEventArgs e) => SaveSetting(s => s.MirrorMode = false);

    private void OnCrcChecked(object sender, RoutedEventArgs e) => SaveSetting(s => s.VerifyByCrc = true);
    private void OnCrcUnchecked(object sender, RoutedEventArgs e) => SaveSetting(s => s.VerifyByCrc = false);
    private void OnLiveChecked(object sender, RoutedEventArgs e) => SaveSetting(s => s.LiveWatch = true);
    private void OnLiveUnchecked(object sender, RoutedEventArgs e) => SaveSetting(s => s.LiveWatch = false);

    private void OnStartupChecked(object sender, RoutedEventArgs e) => ToggleStartup(enable: true);
    private void OnStartupUnchecked(object sender, RoutedEventArgs e) => ToggleStartup(enable: false);

    private static void SaveSetting(Action<AppSettings> apply)
    {
        apply(SettingsStore.Current);
        SettingsStore.Save();
    }

    private void RefreshStartupCheckboxFromRegistry()
    {
        // Установщик мог уже включить автозапуск — ориентируемся на реестр.
        ChkStartup.IsChecked = CurrentRunAtStartup();
    }

    private void ToggleStartup(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            var exe = Environment.ProcessPath;
            if (exe is null) return;

            if (enable)
                key?.SetValue("SmartUsbBackup", $"\"{exe}\" -hidden");
            else
                key?.DeleteValue("SmartUsbBackup", throwOnMissingValue: false);

            SettingsStore.Current.RunAtStartup = enable;
            SettingsStore.Save();
            AppLog.Info($"Автозапуск с Windows: {enable}");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Не удалось изменить автозапуск");
        }
    }

    private static bool CurrentRunAtStartup()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue("SmartUsbBackup") is not null;
    }

    // =====================================================================
    //  ТАБЛИЦА ФЛЕШЕК
    // =====================================================================

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
        => BtnWipeSelected.IsEnabled = DriveList.SelectedItem is DriveRow;

    private void OnListDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        // Двойной клик по строке — немедленная синхронизация этой флешки.
        if (DriveList.SelectedItem is DriveRow row && row.Profile.Enabled)
            _orchestrator.SyncOne(row.Profile);
    }

    private void OnRowEnabledChecked(object sender, RoutedEventArgs e) => OnRowEnabledChanged(sender, true);
    private void OnRowEnabledUnchecked(object sender, RoutedEventArgs e) => OnRowEnabledChanged(sender, false);

    private void OnRowEnabledChanged(object sender, bool enabled)
    {
        if (sender is not FrameworkElement { DataContext: DriveRow row }) return;
        if (row.Profile.Enabled == enabled) return;

        row.Profile.Enabled = enabled;
        SettingsStore.Save();
        AppLog.Info($"Резервирование «{row.Profile.Label}» → {enabled}");

        // Включили галочку у вставленной флешки — сразу синхронизируем.
        if (enabled)
        {
            foreach (var s in _orchestrator.ActiveSessions)
                if (ReferenceEquals(s.Profile, row.Profile))
                    _orchestrator.SyncOne(row.Profile);
        }
        else
        {
            // Сняли галочку — активное копирование должно немедленно остановиться.
            _orchestrator.CancelSync(row.Profile);
            ResetProgressIfIdle();
        }
    }

    /// <summary>
    /// Пересобирает состояние таблицы из настроек + сессий оркестратора.
    /// НЕ очищает ObservableCollection целиком (сохраняет прокрутку/выделение),
    /// а обновляет существующие строки через INPC.
    /// </summary>
    private void RefreshList()
    {
        var profiles = SettingsStore.Current.Drives;

        // 1) убираем удалённые профили
        for (int i = _rows.Count - 1; i >= 0; i--)
            if (!profiles.Contains(_rows[i].Profile))
                _rows.RemoveAt(i);

        // 2) добавляем/сдвигаем/обновляем в порядке настроек
        for (int i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            var row = _rows.FirstOrDefault(r => ReferenceEquals(r.Profile, profile));

            if (row is null)
            {
                _rows.Insert(Math.Min(i, _rows.Count), new DriveRow(profile));
                row = _rows[i];
            }
            else
            {
                var currentIndex = _rows.IndexOf(row);
                if (currentIndex != i) _rows.Move(currentIndex, i);
            }

            row.Enabled = profile.Enabled;
            row.RefreshState(ComputeState(profile));
            row.BackupState = HasBackup(profile.BackupPath);
        }

        // 3) выделение могло исчезнуть вместе со строкой
        if (DriveList.SelectedItem is DriveRow sel && !_rows.Contains(sel))
            DriveList.SelectedIndex = -1;
        BtnWipeSelected.IsEnabled = DriveList.SelectedItem is DriveRow;

        UpdateTrayText();
    }

    /// <summary>Есть ли в папке бэкапа хотя бы один файл/подпапка.</summary>
    private static string HasBackup(string backupPath)
    {
        try
        {
            return Directory.Exists(backupPath) &&
                   Directory.EnumerateFileSystemEntries(backupPath).Any()
                ? "Есть сохранение"
                : "Нет сохранения";
        }
        catch
        {
            return "Нет сохранения";
        }
    }

    private string ComputeState(DriveProfile profile)
    {
        var session = _orchestrator.ActiveSessions
            .FirstOrDefault(s => ReferenceEquals(s.Profile, profile)
                                 || string.Equals(s.Profile.Serial, profile.Serial, StringComparison.OrdinalIgnoreCase));

        if (!profile.Enabled) return "Выключено";
        if (session is null) return "Не подключена";
        return session.State switch
        {
            SessionState.Syncing => "Резервирование…",
            SessionState.Error => "Ошибка: " + session.LastResult,
            _ => "Синхронизировано",
        };
    }

    /// <summary>
    /// Вернуть прогресс в исходное «Ожидание флешки…», если ни одна флешка
    /// сейчас не копируется (после отмены, удаления бэкапа или извлечения).
    /// </summary>
    private void ResetProgressIfIdle()
    {
        if (_orchestrator.ActiveSessions.Any(s => s.State == SessionState.Syncing))
            return;

        ProgressSync.IsIndeterminate = false;
        ProgressSync.Value = 0;
        TxtStatus.Text = "Ожидание флешки…";
    }

    private void UpdateTrayText()
    {
        try
        {
            _tray?.SetToolTip(_orchestrator.ActiveSessions.Count == 0
                ? "Smart USB Backup — флешки нет"
                : "Smart USB Backup — активных флешек: " + _orchestrator.ActiveSessions.Count);
        }
        catch { /* трей мог не создаться */ }
    }

    // =====================================================================
    //  ПРОГРЕСС: «(3 / 14452, 2,9 МБ) F:\1-2 курс\GOIDA.m»
    // =====================================================================

    private void OnProgress(SyncProgress p)
    {
        // ≤5 перерисовок/с (минимальная CPU-нагрузка), НО финальный статус — всегда.
        bool isFinal = p.Phase is SyncPhase.Done or SyncPhase.Error;
        if (!isFinal && Stopwatch.GetTimestamp() - _lastProgressPaint < Stopwatch.Frequency / 5)
            return;
        _lastProgressPaint = Stopwatch.GetTimestamp();

        switch (p.Phase)
        {
            case SyncPhase.Scanning:
                ProgressSync.IsIndeterminate = true;
                TxtStatus.Text = "Сканирование флешки…";
                break;

            case SyncPhase.Copying:
                ProgressSync.IsIndeterminate = false;
                ProgressSync.Value = Math.Clamp(p.BytesDone * 100.0 / Math.Max(1, p.TotalBytes), 0, 100);
                TxtStatus.Text =
                    $"({p.CurrentFile} / {p.TotalFiles}, {p.BytesDone / 1048576.0:F1} МБ) {p.CurrentPath}";
                break;

            case SyncPhase.Pruning:
                ProgressSync.IsIndeterminate = true;
                TxtStatus.Text = "Режим «Зеркало»: очистка бэкапа…";
                break;

            case SyncPhase.Error:
                ProgressSync.IsIndeterminate = false;
                ProgressSync.Value = 0;
                TxtStatus.Text = "Ошибка: " + p.CurrentPath;
                break;

            default: // Done
                ProgressSync.IsIndeterminate = false;
                ProgressSync.Value = 100;
                TxtStatus.Text = string.IsNullOrEmpty(p.CurrentPath)
                    ? "Готово"
                    : "Готово. Последний файл: " + p.CurrentPath;
                break;
        }
    }

    // =====================================================================
    //  ДИАЛОГИ (MessageBox → ContentDialog)
    // =====================================================================

    private ContentDialog MakeDialog(string title, string message, string primary, string close)
        => new()
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primary,
            CloseButtonText = close,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot, // WinUI: dialogs привязываются к XamlRoot окна
        };

    private async Task<bool> ConfirmAsync(string title, string message, string primary)
    {
        try
        {
            return await MakeDialog(title, message, primary, "Отмена").ShowAsync()
                   == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "ContentDialog не открылся");
            return false;
        }
    }

    private async Task ShowInfoAsync(string title, string message)
    {
        try
        {
            await new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "ОК",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "ContentDialog не открылся");
        }
    }

    // =====================================================================
    //  ТРЕЙ
    // =====================================================================

    /// <summary>«Выход» в меню трея: останавливаем всё и завершаем процесс.</summary>
    private void ExitFromTray()
    {
        AppLog.Info("Трей: выход");

        // Полный выход: снимаем флаг «сворачивать в трей», останавливаем watcher'ы
        // и копирование, убираем иконку и завершаем приложение.
        _isExiting = true;

        try { _orchestrator.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }

        Application.Current.Exit();

        // Страховка: если по какой-то причине цикл сообщений не завершился
        // (например, окно уже скрыто в трей), гарантированно убиваем процесс,
        // чтобы «Выход» никогда не оставлял приложение висеть в фоне.
        _ = Task.Delay(1500).ContinueWith(_ => Environment.Exit(0));
    }
}
