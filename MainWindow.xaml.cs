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
    private bool _checkingUpdate;

    public static MainWindow? Instance;
    public static DispatcherQueue? UiDispatcher;
    private readonly HashSet<DriveProfile> _syncingDrives = new();
    private readonly HashSet<DriveProfile> _deletingDrives = new();
    private readonly object _driveStateLock = new();


    public MainWindow(bool startHidden)
    {
        _startHidden = startHidden;
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            try
            {
                var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartUsbBackup", "crash.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.WriteAllText(logPath, ex.ToString());
            }
            catch { }
            throw;
        }

        // ---------- Первый запуск: создаём настройки ----------
        if (string.IsNullOrEmpty(SettingsStore.Current.BaseBackupPath))
        {
            SettingsStore.Current.BaseBackupPath = SettingsStore.DefaultBasePath;
            SettingsStore.Save();
        }

        _dq = DispatcherQueue.GetForCurrentThread();
        Instance = this;
        UiDispatcher = _dq;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = this.AppWindow;

        ConfigureWindow();
        HookOrchestrator();
        HookUsb();

        // Трей: нативное меню + иконка. Клик по иконке/«Открыть окно» показывает
        // окно, «Выход» завершает приложение.
        _tray = new NativeTrayIcon();
        _tray.OpenRequested += () => { _ = _dq.TryEnqueue(RestoreWindow); };
        _tray.ToggleRequested += () => { _ = _dq.TryEnqueue(ToggleWindow); };
        _tray.ExitRequested += ExitFromTray;
        _tray.UpdateRequested += () => { _dq.TryEnqueue(() => _ = SafeFireAndForget(CheckForUpdatesAsync(true))); };
        UpdateTrayText();

        // Все Click/Checked/Unchecked-обработчики подключены в XAML (атрибуты Click=...).

        DriveList.ItemsSource = _rows;

        // Начальные значения чекбоксов (до подписки событий их нельзя ставить — но
        // события ставят ТУ ЖЕ самое значение, harmless).
        ChkMirror.IsChecked = SettingsStore.Current.MirrorMode;
        ChkCrc.IsChecked = SettingsStore.Current.VerifyByCrc;
        ChkSkipHidden.IsChecked = SettingsStore.Current.SkipHiddenSystem;
        ChkLiveWatch.IsChecked = SettingsStore.Current.LiveWatch;
        ChkAutoUpdate.IsChecked = SettingsStore.Current.AutoCheckForUpdates;
        ChkAutoTestNewDrives.IsChecked = SettingsStore.Current.AutoTestNewDrives;
        ChkDefaultEnabled.IsChecked = SettingsStore.Current.DefaultEnabled;

        ChkDefaultFlush.IsChecked = SettingsStore.Current.DefaultFlushAfterFile;
        CmbDefaultBuffer.SelectedIndex = SettingsStore.Current.DefaultBufferSizeKb switch
        {
            1024 => 0,
            8192 => 2,
            _ => 1,
        };
        TxtSettingsBackupPath.Text = SettingsStore.Current.BaseBackupPath;
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

        // ---------- Кастомный TitleBar (Вкладки сливаются с верхней панелью) ----------
        ExtendsContentIntoTitleBar = true;
        try { SetTitleBar(MainTabView); } catch { /* не критично */ }

        var systemBar = AppWindow.TitleBar;
        systemBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        systemBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        systemBar.PreferredHeightOption = TitleBarHeightOption.Standard;

        // Контейнер верхней панели появляется только после применения шаблона:
        MainTabView.Loaded += (_, _) => ApplyTitleBarInsets();
        SizeChanged += (_, _) => ApplyTitleBarInsets();
        Activated += (_, _) => ApplyTitleBarInsets();

        // ------------------------------------------------------------------------


        // Крестик = сворачивание в трей (как в WinForms-версии).
        _appWindow.Closing += (_, e) =>
        {
            // Настоящий выход из меню трея: не мешаем окну закрыться,
            // иначе Application.Exit() отменялся и процесс оставался висеть.
            if (_isExiting)
            {
                return;
            }

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
        ApplyTitleBarInsets();

        // Авто-проверка обновлений раз в сутки (разработчик может запретить её в настройках).
        _ = Task.Delay(8000).ContinueWith(_ => _dq.TryEnqueue(() => _ = SafeFireAndForget(CheckForUpdatesAsync(false))));
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

    /// <summary>
    /// Настраивает отступы кастомного TitleBar под системные кнопки (Min/Max/Close)
    /// и явно делает правую зону (DragRegion) зоной перетаскивания через InputNonClientPointerSource.
    /// </summary>
    private void ApplyTitleBarInsets()
    {
        try
        {
            if (MainTabView is null) return;

            double scale = Content?.XamlRoot?.RasterizationScale ?? 0;
            if (scale <= 0)
            {
                uint dpi = GetDpiForWindow(_hwnd);
                scale = dpi == 0 ? 1.0 : dpi / 96.0;
            }
            if (scale <= 0) scale = 1.0;

            double rightInsetDip = AppWindow.TitleBar.RightInset / scale;
            if (rightInsetDip < 0) rightInsetDip = 0;
            double leftInsetDip = AppWindow.TitleBar.LeftInset / scale;

            // Контент вкладок должен быть на всю ширину: отступ сбрасываем.
            MainTabView.Margin = new Thickness(0);

            var titleArea = MainTabView.FindName("TitleArea") as StackPanel;
            if (titleArea is not null)
                titleArea.Margin = new Thickness(Math.Max(0, leftInsetDip), 0, 8, 0);

            // Правый отступ только для шапки вкладок (System buttons area).
            var tabStripSpacer = MainTabView.FindName("TabStripSpacer") as Microsoft.UI.Xaml.FrameworkElement;
            if (tabStripSpacer is not null)
                tabStripSpacer.Width = Math.Max(0, rightInsetDip);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Не удалось настроить TitleBar insets");
        }
    }

    private static DependencyObject? FindNamedDescendant(DependencyObject root, string name)
    {
        if (root is FrameworkElement fe && fe.Name == name)
            return root;

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var found = FindNamedDescendant(child, name);
            if (found is not null) return found;
        }
        return null;
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
        _orchestrator.ProgressChanged += (session, p) =>
        {
            bool changed = UpdateSyncingState(session.Profile, p.Phase);
            _dq.TryEnqueue(() =>
            {
                if (changed) RefreshList();
                OnProgress(p with { DriveLetter = session.Letter });
            });
        };
        _orchestrator.StateChanged += () => _dq.TryEnqueue(RefreshList);
    }

    private bool UpdateSyncingState(DriveProfile profile, SyncPhase phase)
    {
        bool isSyncingPhase = phase is SyncPhase.Scanning or SyncPhase.Copying or SyncPhase.Pruning;
        lock (_driveStateLock)
        {
            if (isSyncingPhase)
                return _syncingDrives.Add(profile);
            return _syncingDrives.Remove(profile);
        }
    }

    private void CleanupDriveState(char letter)
    {
        var profile = _orchestrator.ActiveSessions.FirstOrDefault(s => s.Letter == letter)?.Profile;
        if (profile is null) return;
        lock (_driveStateLock)
        {
            _syncingDrives.Remove(profile);
            _deletingDrives.Remove(profile);
        }
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

    private async void OnDriveArrivedUi(UsbDriveEventArgs e)
    {
        var progress = new Progress<SyncProgress>(p => OnProgress(p));

        var profile = await _orchestrator.OnDriveArrivedAsync(e, progress);

        RefreshList();

        // Новая флешка по умолчанию НЕ резервируется — подсказываем, что делать.
        if (profile is not null && !profile.Enabled)
            TxtGlobalStatus.Text = $"Флешка «{e.VolumeLabel}» добавлена. Поставьте галочку «Активна», чтобы начать резервирование.";

        if (IsWindowHidden()) // (только если окно скрыто — иначе пользователь всё видит)
            SafeNotify("Smart USB Backup",
                $"Флешка «{e.VolumeLabel}» подключена — синхронизация начата");
    }

    private void OnDriveRemovedUi(char letter)
    {
        var removedProfile = _orchestrator.ActiveSessions.FirstOrDefault(s => s.Letter == letter)?.Profile;
        _orchestrator.OnDriveRemoved(letter);
        if (removedProfile is not null)
        {
            lock (_driveStateLock)
            {
                _syncingDrives.Remove(removedProfile);
                _deletingDrives.Remove(removedProfile);
            }
        }
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
            try
            {
                foreach (var drive in DriveInfo.GetDrives()
                             .Where(d => d.DriveType == DriveType.Removable && d.IsReady))
                {
                    var q = UsbDeviceWatcher.QueryDrive(drive.Name[0], retry: 1, delayMs: 0);
                    if (q is not null)
                        _usbWatcher.RaiseArrived(drive.Name[0], q.Value.VolumeLabel, q.Value.Serial);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Ошибка первичного обхода вставленных флешек");
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

        var profile = row.Profile;

        lock (_driveStateLock) { _deletingDrives.Add(profile); }
        RefreshList();

        var progress = new Progress<SyncProgress>(p => OnProgress(p));

        try
        {
            await _orchestrator.WipeOneAsync(profile, progress);
        }
        finally
        {
            lock (_driveStateLock) { _deletingDrives.Remove(profile); }
            await Task.Yield();
            ShowIdle();
            RefreshList();
        }
    }

    /// <summary>Удаление всех бэкапов (используется внешними сценариями, например деинсталлятором).</summary>
    private void WipeAllBackups()
    {
        ShowWipeAll();
        RefreshList();
        try
        {
            _orchestrator.WipeEverything();
        }
        finally
        {
            ShowIdle();
            RefreshList();
        }
    }

    // ---------- Настройки-чекбоксы ----------

    private void OnMirrorChecked(object sender, RoutedEventArgs e) => SaveSetting(s => s.MirrorMode = true);
    private void OnMirrorUnchecked(object sender, RoutedEventArgs e) => SaveSetting(s => s.MirrorMode = false);

    private void OnCrcChecked(object sender, RoutedEventArgs e) => SaveSetting(s => s.VerifyByCrc = true);
    private void OnCrcUnchecked(object sender, RoutedEventArgs e) => SaveSetting(s => s.VerifyByCrc = false);
    private void OnLiveChecked(object sender, RoutedEventArgs e) => SaveSetting(s => s.LiveWatch = true);
    private void OnLiveUnchecked(object sender, RoutedEventArgs e) => SaveSetting(s => s.LiveWatch = false);
    private void OnSkipHiddenChecked(object sender, RoutedEventArgs e) => SaveSetting(s => s.SkipHiddenSystem = true);
    private void OnSkipHiddenUnchecked(object sender, RoutedEventArgs e) => SaveSetting(s => s.SkipHiddenSystem = false);
    private void OnAutoTestChecked(object sender, RoutedEventArgs e) => SaveSetting(s => s.AutoTestNewDrives = true);
    private void OnAutoTestUnchecked(object sender, RoutedEventArgs e) => SaveSetting(s => s.AutoTestNewDrives = false);

    private void OnDefaultBufferChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CmbDefaultBuffer.SelectedIndex < 0) return;
        int kb = CmbDefaultBuffer.SelectedIndex switch { 0 => 1024, 2 => 8192, _ => 4096 };
        SaveSetting(s => s.DefaultBufferSizeKb = kb);
    }

    private void OnDefaultFlushChecked(object sender, RoutedEventArgs e) => SaveSetting(s => s.DefaultFlushAfterFile = true);
    private void OnDefaultFlushUnchecked(object sender, RoutedEventArgs e) => SaveSetting(s => s.DefaultFlushAfterFile = false);

    private void OnDefaultEnabledChecked(object sender, RoutedEventArgs e) => SaveSetting(s => s.DefaultEnabled = true);
    private void OnDefaultEnabledUnchecked(object sender, RoutedEventArgs e) => SaveSetting(s => s.DefaultEnabled = false);

    private async void OnSettingsBrowseClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            SettingsStore.Current.BaseBackupPath = Path.GetFullPath(folder.Path);
            SettingsStore.Save();
            TxtSettingsBackupPath.Text = SettingsStore.Current.BaseBackupPath;
            TxtBackupPath.Text = SettingsStore.Current.BaseBackupPath;
            RefreshList();
            AppLog.Info("Глобальная папка хранения бэкапов изменена: " + SettingsStore.Current.BaseBackupPath);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Не удалось выбрать папку хранения бэкапов");
        }
    }
    private void OnAutoUpdateChecked(object sender, RoutedEventArgs e) => SaveSetting(s => s.AutoCheckForUpdates = true);
    private void OnAutoUpdateUnchecked(object sender, RoutedEventArgs e) => SaveSetting(s => s.AutoCheckForUpdates = false);

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





    private void MainTabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        sender.TabItems.Remove(args.Tab);
    }

    private void OnListDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        // Двойной клик по строке — немедленная синхронизация этой флешки.
        if (DriveList.SelectedItem is DriveRow row && row.Profile.Enabled)
            _orchestrator.SyncOne(row.Profile);
    }

    private void PerDriveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DriveRow row })
            return;

        string driveId = !string.IsNullOrWhiteSpace(row.Profile.Serial)
            ? row.Profile.Serial
            : row.Name;

        foreach (var item in MainTabView.TabItems)
        {
            if (item is TabViewItem existingTab && existingTab.Tag as string == driveId)
            {
                MainTabView.SelectedItem = existingTab;
                return;
            }
        }

        var profile = row.Profile;

        var statusText = new TextBlock
        {
            Text = BuildDriveStatusText(profile),
            TextWrapping = TextWrapping.Wrap,
        };

        var chkEnabled = CreateLabeledCheckBox("Активна (резервировать)", profile.Enabled);
        chkEnabled.Checked += (_, _) =>
        {
            profile.Enabled = true;
            SettingsStore.Save();
            AppLog.Info($"Резервирование «{profile.Label}» → true");
            foreach (var s in _orchestrator.ActiveSessions)
                if (ReferenceEquals(s.Profile, profile)) _orchestrator.SyncOne(profile);
            RefreshList();
            statusText.Text = BuildDriveStatusText(profile);
        };
        chkEnabled.Unchecked += (_, _) =>
        {
            profile.Enabled = false;
            SettingsStore.Save();
            _orchestrator.CancelSync(profile);
            ResetProgressIfIdle();
            AppLog.Info($"Резервирование «{profile.Label}» → false");
            RefreshList();
            statusText.Text = BuildDriveStatusText(profile);
        };

        var chkMirror = CreateLabeledCheckBox("Зеркало", profile.MirrorModeOverride ?? SettingsStore.Current.MirrorMode);
        chkMirror.Checked += (_, _) => { profile.MirrorModeOverride = true; SettingsStore.Save(); };
        chkMirror.Unchecked += (_, _) => { profile.MirrorModeOverride = false; SettingsStore.Save(); };

        var chkCrc = CreateLabeledCheckBox("Проверять CRC32", profile.VerifyByCrcOverride ?? SettingsStore.Current.VerifyByCrc);
        chkCrc.Checked += (_, _) => { profile.VerifyByCrcOverride = true; SettingsStore.Save(); };
        chkCrc.Unchecked += (_, _) => { profile.VerifyByCrcOverride = false; SettingsStore.Save(); };

        var chkHidden = CreateLabeledCheckBox("Пропускать скрытые/системные", profile.SkipHiddenSystemOverride ?? SettingsStore.Current.SkipHiddenSystem);
        chkHidden.Checked += (_, _) => { profile.SkipHiddenSystemOverride = true; SettingsStore.Save(); };
        chkHidden.Unchecked += (_, _) => { profile.SkipHiddenSystemOverride = false; SettingsStore.Save(); };

        var chkLive = CreateLabeledCheckBox("Обновлять сразу (watch)", profile.LiveWatchOverride ?? SettingsStore.Current.LiveWatch);
        chkLive.Checked += (_, _) => { profile.LiveWatchOverride = true; SettingsStore.Save(); };
        chkLive.Unchecked += (_, _) => { profile.LiveWatchOverride = false; SettingsStore.Save(); };

        var bufferLabel = new TextBlock { Text = "Размер буфера, КБ:" };
        var cmbBuffer = new ComboBox { Width = 120 };
        cmbBuffer.Items.Add(new ComboBoxItem { Content = "по стандарту" });
        cmbBuffer.Items.Add(new ComboBoxItem { Content = "1024" });
        cmbBuffer.Items.Add(new ComboBoxItem { Content = "4096" });
        cmbBuffer.Items.Add(new ComboBoxItem { Content = "8192" });
        cmbBuffer.SelectedIndex = profile.BufferSizeKbOverride switch
        {
            null => 0,
            1024 => 1,
            4096 => 2,
            8192 => 3,
            _ => (profile.BufferSizeKbOverride < 2048 ? 1 : (profile.BufferSizeKbOverride < 6144 ? 2 : 3)),
        };
        cmbBuffer.SelectionChanged += (_, _) =>
        {
            profile.BufferSizeKbOverride = cmbBuffer.SelectedIndex switch
            {
                1 => 1024,
                2 => 4096,
                3 => 8192,
                _ => null,
            };
            SettingsStore.Save();
        };

        var chkFlush = CreateLabeledCheckBox("Flush после файла (надёжно)", profile.FlushAfterFileOverride ?? SettingsStore.Current.DefaultFlushAfterFile);
        chkFlush.Checked += (_, _) => { profile.FlushAfterFileOverride = true; SettingsStore.Save(); };
        chkFlush.Unchecked += (_, _) => { profile.FlushAfterFileOverride = false; SettingsStore.Save(); };

        var testedText = new TextBlock
        {
            Text = $"Настройки протестированы: {(profile.SettingsTested ? "да" : "нет")}",
            Opacity = 0.8,
        };

        var btnTest = new Button
        {
            Content = "Тестировать эту флешку",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
        };
        btnTest.Click += async (_, _) =>
        {
            btnTest.IsEnabled = false;
            TxtGlobalStatus.Text = $"Тестирование «{profile.Label}»...";
            try
            {
                if (!Directory.Exists(profile.DriveLetter + ":\\"))
                {
                    TxtGlobalStatus.Text = "Флешка не подключена — тест невозможен.";
                    return;
                }

                profile.MirrorModeOverride = null;
                profile.VerifyByCrcOverride = null;
                profile.SkipHiddenSystemOverride = null;
                profile.LiveWatchOverride = null;
                profile.SettingsTested = false;

                var progress = new Progress<SyncProgress>(p => OnProgress(p));
                await _orchestrator.TestDriveSettingsAsync(profile, progress, false);

                chkMirror.IsChecked = profile.MirrorModeOverride ?? SettingsStore.Current.MirrorMode;
                chkCrc.IsChecked = profile.VerifyByCrcOverride ?? SettingsStore.Current.VerifyByCrc;
                chkHidden.IsChecked = profile.SkipHiddenSystemOverride ?? SettingsStore.Current.SkipHiddenSystem;
                chkLive.IsChecked = profile.LiveWatchOverride ?? SettingsStore.Current.LiveWatch;
                cmbBuffer.SelectedIndex = profile.BufferSizeKbOverride switch
                {
                    null => 0,
                    1024 => 1,
                    4096 => 2,
                    8192 => 3,
                    _ => (profile.BufferSizeKbOverride < 2048 ? 1 : (profile.BufferSizeKbOverride < 6144 ? 2 : 3)),
                };
                chkFlush.IsChecked = profile.FlushAfterFileOverride ?? SettingsStore.Current.DefaultFlushAfterFile;

                testedText.Text = $"Настройки протестированы: {(profile.SettingsTested ? "да" : "нет")}";
                statusText.Text = BuildDriveStatusText(profile);
                TxtGlobalStatus.Text = $"Тест флешки «{profile.Label}» завершён.";
                RefreshList();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Ошибка ручного теста флешки");
                TxtGlobalStatus.Text = "Ошибка теста: " + ex.Message;
            }
            finally
            {
                btnTest.IsEnabled = true;
                await Task.Yield();
                ShowIdle();
            }
        };

        var backupPathText = new TextBlock
        {
            Text = $"Папка хранения: {profile.BackupPath}",
            TextWrapping = TextWrapping.Wrap,
        };

        var btnRelocateBackup = new Button { Content = "Изменить папку хранения..." };
        var btnResetBackupPath = new Button { Content = "Сбросить папку хранения к стандартной" };
        btnRelocateBackup.Click += async (_, _) =>
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
                var folder = await picker.PickSingleFolderAsync();
                if (folder is null) return;
                await RelocateDriveBackupAsync(profile, folder.Path, new Progress<SyncProgress>(p => OnProgress(p)));
                backupPathText.Text = $"Папка хранения: {profile.BackupPath}";
                statusText.Text = BuildDriveStatusText(profile);
                TxtGlobalStatus.Text = $"Папка хранения «{profile.Label}» изменена.";
                RefreshList();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Ошибка изменения папки хранения флешки");
                TxtGlobalStatus.Text = "Ошибка: " + ex.Message;
            }
            finally { await Task.Yield(); ShowIdle(); }
        };
        btnResetBackupPath.Click += async (_, _) =>
        {
            try
            {
                await RelocateDriveBackupAsync(profile, SettingsStore.Current.BaseBackupPath, new Progress<SyncProgress>(p => OnProgress(p)));
                backupPathText.Text = $"Папка хранения: {profile.BackupPath}";
                statusText.Text = BuildDriveStatusText(profile);
                TxtGlobalStatus.Text = $"Папка хранения «{profile.Label}» сброшена к стандартной.";
                RefreshList();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Ошибка сброса папки хранения флешки");
                TxtGlobalStatus.Text = "Ошибка: " + ex.Message;
            }
            finally { await Task.Yield(); ShowIdle(); }
        };

        var btnResetSettings = new Button { Content = "Сбросить настройки к стандартным" };
        btnResetSettings.Click += (_, _) =>
        {
            profile.MirrorModeOverride = null;
            profile.VerifyByCrcOverride = null;
            profile.SkipHiddenSystemOverride = null;
            profile.LiveWatchOverride = null;
            profile.BufferSizeKbOverride = null;
            profile.FlushAfterFileOverride = null;
            profile.SettingsTested = false;
            SettingsStore.Save();

            chkMirror.IsChecked = SettingsStore.Current.MirrorMode;
            chkCrc.IsChecked = SettingsStore.Current.VerifyByCrc;
            chkHidden.IsChecked = SettingsStore.Current.SkipHiddenSystem;
            chkLive.IsChecked = SettingsStore.Current.LiveWatch;
            cmbBuffer.SelectedIndex = SettingsStore.Current.DefaultBufferSizeKb switch
            {
                1024 => 0,
                8192 => 2,
                _ => 1,
            };
            chkFlush.IsChecked = SettingsStore.Current.DefaultFlushAfterFile;
            testedText.Text = "Настройки протестированы: нет";
            statusText.Text = BuildDriveStatusText(profile);
            TxtGlobalStatus.Text = $"Настройки «{profile.Label}» сброшены к стандартным.";
            RefreshList();
        };

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock
        {
            Text = $"{row.Name} — {profile.Serial}",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        stack.Children.Add(backupPathText);
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { btnRelocateBackup, btnResetBackupPath },
        });
        stack.Children.Add(statusText);
        stack.Children.Add(chkEnabled);
        stack.Children.Add(chkMirror);
        stack.Children.Add(chkCrc);
        stack.Children.Add(chkHidden);
        stack.Children.Add(chkLive);
        stack.Children.Add(bufferLabel);
        stack.Children.Add(cmbBuffer);
        stack.Children.Add(chkFlush);
        stack.Children.Add(testedText);
        stack.Children.Add(btnTest);
        stack.Children.Add(btnResetSettings);

        var rootGrid = new Grid { Padding = new Thickness(16) };
        rootGrid.Children.Add(new ScrollViewer { Content = stack });

        var tab = new TabViewItem
        {
            Header = row.Name,
            IsClosable = true,
            Tag = driveId,
            Content = rootGrid,
        };

        MainTabView.TabItems.Add(tab);
        MainTabView.SelectedItem = tab;
    }

    private async Task RelocateDriveBackupAsync(DriveProfile profile, string newBase, IProgress<SyncProgress> progress)
    {
        newBase = Path.GetFullPath(newBase);
        string oldPath = profile.BackupPath;
        string oldBase = string.IsNullOrWhiteSpace(profile.BaseBackupPathOverride) ? SettingsStore.Current.BaseBackupPath : profile.BaseBackupPathOverride!;

        if (string.Equals(oldBase, newBase, StringComparison.OrdinalIgnoreCase))
        {
            progress.Report(new SyncProgress(profile.DriveLetter, 1, 1, 0, 0, 0, 1, "", SyncPhase.Moving) { DriveName = profile.Label });
            TxtGlobalStatus.Text = "Папка хранения уже стандартная (или не изменилась).";
            return;
        }

        // Останавливаем активную синхронизацию этой флешки, чтобы не писать во время переноса.
        var session = _orchestrator.ActiveSessions.FirstOrDefault(s =>
            ReferenceEquals(s.Profile, profile) ||
            string.Equals(s.Profile.Serial, profile.Serial, StringComparison.OrdinalIgnoreCase));
        if (session is not null)
        {
            session.CurrentCts?.Cancel();
            var key = session.Letter.ToString();
            if (_orchestrator.SyncTasks.TryGetValue(key, out var running))
                await Task.WhenAny(running, Task.Delay(5000)).ConfigureAwait(false);
        }

        string newPath = Path.Combine(newBase, profile.FolderName);
        progress.Report(new SyncProgress(profile.DriveLetter, 0, 1, 0, 0, 0, 1, "", SyncPhase.Moving) { DriveName = profile.Label });

        if (Directory.Exists(oldPath))
        {
            await Task.Run(() => MoveDirectoryTree(oldPath, newPath));
        }

        // Обновляем хранилище (null = стандартный глобальный путь).
        profile.BaseBackupPathOverride = string.Equals(newBase, SettingsStore.Current.BaseBackupPath, StringComparison.OrdinalIgnoreCase)
            ? null
            : newBase;
        SettingsStore.Save();

        progress.Report(new SyncProgress(profile.DriveLetter, 1, 1, 0, 0, 0, 1, "", SyncPhase.Moving) { DriveName = profile.Label });
        AppLog.Info($"Папка хранения флешки «{profile.Label}» перенесена: {oldPath} -> {newPath}");
    }

    private static void MoveDirectoryTree(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (!Directory.Exists(destination))
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (IOException)
            {
                // Кросс-томовый перенос: копируем + удаляем исходное.
            }
        }

        if (!Directory.Exists(destination))
            Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }

        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)
                                     .OrderByDescending(d => d.Length))
        {
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        }
        try { Directory.Delete(source, recursive: true); } catch { /* уже очищено */ }
    }

    private static string BuildDriveStatusText(DriveProfile profile)
        => $"Активна: {(profile.Enabled ? "да" : "нет")}; протестировано: {(profile.SettingsTested ? "да" : "нет")}";

    private static CheckBox CreateLabeledCheckBox(string content, bool isChecked)
        => new CheckBox { Content = content, IsChecked = isChecked };

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindDescendantScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    public static void HandleMouseWheel(int delta)
    {
        var instance = Instance;
        var dq = UiDispatcher;
        if (instance is null || dq is null) return;

        dq.TryEnqueue(() =>
        {
            try
            {
                var tab = instance.MainTabView.SelectedItem as TabViewItem;
                var content = tab?.Content as FrameworkElement ?? instance.MainTabView as FrameworkElement;
                if (content is null) return;

                var viewer = FindDescendantScrollViewer(content);
                if (viewer is null) return;

                double step = Math.Sign(delta) * 48.0;
                double target = viewer.VerticalOffset - step;
                if (target < 0) target = 0;
                if (target > viewer.ScrollableHeight) target = viewer.ScrollableHeight;
                viewer.ChangeView(null, target, null);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Ошибка ручной прокрутки колесом мыши");
            }
        });
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
        // Убираем строки, не удовлетворяющие условию отображения (см. ниже).
        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            var proj = _rows[i].Profile;
            bool present = _orchestrator.ActiveSessions.Any(s =>
                ReferenceEquals(s.Profile, proj) ||
                string.Equals(s.Profile.Serial, proj.Serial, StringComparison.OrdinalIgnoreCase));
            bool keep = proj.Enabled || present || HasBackup(proj.BackupPath) == "Есть сохранение";
            if (!keep) _rows.RemoveAt(i);
        }

        // 2) добавляем/сдвигаем/обновляем в порядке настроек
        for (int i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            bool present = _orchestrator.ActiveSessions.Any(s =>
                ReferenceEquals(s.Profile, profile) ||
                string.Equals(s.Profile.Serial, profile.Serial, StringComparison.OrdinalIgnoreCase));
            bool hasBackup = HasBackup(profile.BackupPath) == "Есть сохранение";
            if (!(profile.Enabled || present || hasBackup)) continue;
            var row = _rows.FirstOrDefault(r => ReferenceEquals(r.Profile, profile));

            if (row is null)
            {
                var newRow = new DriveRow(profile);
                _rows.Add(newRow);
                row = newRow;
            }
            // Не переставляем строки: коллекция могла потерять элементы из-за фильтра;
            // _rows.Move(currentIndex, i) здесь небезопасен (ArgumentOutOfRange).


            row.Enabled = profile.Enabled;

            bool isConnected = false;
            if (profile.DriveLetter != '\0')
                isConnected = System.IO.Directory.Exists(profile.DriveLetter + ":\\");
            row.RefreshState(isConnected ? "Включено" : "Выключено");

            if (!isConnected)
            {
                lock (_driveStateLock)
                {
                    if (_syncingDrives.Contains(profile)) _syncingDrives.Remove(profile);
                    if (_deletingDrives.Contains(profile)) _deletingDrives.Remove(profile);
                }

                row.BackupState = Directory.Exists(profile.BackupPath) ? "Есть сохранение" : "Нет сохранения";
            }
            else
            {
                bool deleting, syncing;
                lock (_driveStateLock)
                {
                    deleting = _deletingDrives.Contains(profile);
                    syncing = _syncingDrives.Contains(profile);
                }
                if (deleting) row.BackupState = "Удаляется";
                else if (syncing) row.BackupState = "Синхронизируется";
                else if (Directory.Exists(profile.BackupPath)) row.BackupState = "Есть сохранение";
                else row.BackupState = "Нет сохранения";
            }
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

    private string ComputeBackupState(DriveProfile profile)
    {
        if (!Directory.Exists(profile.BackupPath)) return "Нет сохранения";

        bool syncing = _orchestrator.ActiveSessions.Any(s =>
            (ReferenceEquals(s.Profile, profile) ||
             string.Equals(s.Profile.Serial, profile.Serial, StringComparison.OrdinalIgnoreCase))
            && s.State == SessionState.Syncing);

        return syncing ? "Синхронизируется" : "Есть сохранение";
    }

    /// <summary>
    /// Вернуть прогресс в исходное состояние при отсутствии активной синхронизации.
    /// </summary>
    private void ShowIdle()
    {
        GlobalProgressBar.IsIndeterminate = false;
        GlobalProgressBar.Value = 0;
        TxtGlobalStatus.Text = "";
    }

    private void ShowScanning(string drive, int count)
    {
        GlobalProgressBar.IsIndeterminate = true;
        TxtGlobalStatus.Text = $"Сканирование [{drive}]: Файлов: {count}";
    }

    private void ShowCopying(string drive, int total, int copied, int skipped, string file, int percent)
    {
        GlobalProgressBar.IsIndeterminate = false;
        GlobalProgressBar.Value = Math.Clamp(percent, 0, 100);
        TxtGlobalStatus.Text = $"Копирование [{drive}]: Всего: {total} | Скопировано: {copied} | Пропущено: {skipped} | {System.IO.Path.GetFileName(file)}";
    }

    private void ShowPruning(string drive)
    {
        GlobalProgressBar.IsIndeterminate = true;
        TxtGlobalStatus.Text = "Очистка...";
    }

    private void ShowWipe(string drive)
    {
        GlobalProgressBar.IsIndeterminate = true;
        TxtGlobalStatus.Text = $"Удаление бэкапа [{drive}]...";
    }

    private void ShowWipeAll()
    {
        GlobalProgressBar.IsIndeterminate = true;
        TxtGlobalStatus.Text = "Удаление всех бэкапов...";
    }

    private void ResetProgressIfIdle()
    {
        if (_orchestrator.ActiveSessions.Any(s => s.State == SessionState.Syncing))
            return;

        GlobalProgressBar.IsIndeterminate = false;
        GlobalProgressBar.Value = 0;
        TxtGlobalStatus.Text = "";
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
    //  АВТО-ОБНОВЛЕНИЕ с GitHub Releases
    // =====================================================================

    private async Task SafeFireAndForget(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Ошибка в фоновой задаче (SafeFireAndForget)");
        }
    }

    /// <summary>
    /// Проверить наличие новой версии. В фоне вызывается раз в сутки,
    /// вручную — по пункту в трее «Проверить обновления».
    /// </summary>
    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (_checkingUpdate) return;
        _checkingUpdate = true;
        try
        {
            if (!userInitiated && !SettingsStore.Current.AutoCheckForUpdates)
            {
                AppLog.Info("Автообновление отключено в настройках, фоновая проверка пропущена.");
                return;
            }

            var now = DateTime.Now;
            var last = SettingsStore.Current.LastUpdateCheck;
            if (!userInitiated && (now - last).TotalHours < 24)
                return;

            SettingsStore.Current.LastUpdateCheck = now;
            SettingsStore.Save();

            var info = await UpdateService.CheckAsync();
            var current = UpdateService.AppVersion;

            if (info is null)
            {
                if (userInitiated)
                    await ShowInfoAsync("Обновления", "Не удалось проверить обновления (нет сети?).");
                return;
            }

            if (info.Version < current)
            {
                if (userInitiated)
                    await ShowInfoAsync("Обновления", "У вас более новая версия, обновление не требуется.");
                return;
            }
            if (info.Version == current)
            {
                if (userInitiated)
                    await ShowInfoAsync("Обновления", $"Установлена самая свежая версия ({current}).");
                return;
            }

            // Мажорные обновления (например 4.x -> 5.x) не скачиваем автоматически —
            // возможные breaking changes. Пользователь качает установщик вручную.
            if (UpdateService.IsMajorUpdate(current, info.Version))
            {
                var majorDlg = new ContentDialog
                {
                    Title = "Доступна мажорная версия",
                    Content = $"Доступна мажорная версия {info.Version}. Автоматическое обновление недоступно из-за возможных breaking changes. Пожалуйста, скачайте установщик вручную.",
                    PrimaryButtonText = "Открыть страницу релиза",
                    CloseButtonText = "Отмена",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot,
                };

                if (await majorDlg.ShowAsync() == ContentDialogResult.Primary &&
                    !string.IsNullOrWhiteSpace(info.HtmlUrl))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(info.HtmlUrl) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error(ex, "Не удалось открыть страницу релиза");
                    }
                }
                return;
            }

            bool allowUnverified = false;
            if (string.IsNullOrWhiteSpace(info.Sha256))
            {
                var unverifiedDlg = new ContentDialog
                {
                    Title = "Обновление без проверки целостности",
                    Content = "SHA-256 для этого обновления недоступен. Продолжить на свой риск?",
                    PrimaryButtonText = "Продолжить",
                    CloseButtonText = "Отменить",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot,
                };
                if (await unverifiedDlg.ShowAsync() != ContentDialogResult.Primary)
                    return;
                allowUnverified = true;
            }

            var dlg = new ContentDialog
            {
                Title = "Доступно обновление",
                Content = $"Новая версия {info.Version} доступна (сейчас {current}). Скачать и установить?",
                PrimaryButtonText = "Обновить",
                CloseButtonText = "Позже",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };

            if (await dlg.ShowAsync() != ContentDialogResult.Primary)
                return;

            dlg.Title = "Загрузка обновления";
            dlg.Content = "Подготовка…";

            var progress = new Progress<double>(v =>
                dlg.Content = $"Загрузка: {v * 100:F0}% ({info.Size / 1048576.0:F1} МБ)");

            var path = await UpdateService.DownloadAsync(info, progress, allowUnverified: allowUnverified);
            if (path is null)
            {
                await ShowInfoAsync("Обновления", "Произошла ошибка загрузки. Попробуйте позже.");
                return;
            }

            dlg.Title = "Установка";
            dlg.Content = "Запуск инсталлятора…";
            await Task.Delay(400);
            if (UpdateService.ApplyUpdate(path))
            {
                await PerformShutdownForUpdate();
            }
            else
            {
                await ShowInfoAsync("Обновления", "Не удалось запустить установщик.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Не удалось проверить/скачать обновление");
            if (userInitiated)
                await ShowInfoAsync("Обновления", "Ошибка: " + ex.Message);
        }
        finally
        {
            _checkingUpdate = false;
        }
    }
    // =====================================================================
    //  ПРОГРЕСС: «(3 / 14452, 2,9 МБ) F:\1-2 курс\GOIDA.m»
    // =====================================================================

    private void OnProgress(SyncProgress p)
    {
        // ≤5 перерисовок/с (минимальная CPU-нагрузка), НО финальный статус — всегда.
        // Смена фазы (сканирование/подключение/live-watch) не троттлим: иначе сразу
        // после выдёргивания флешки прогресс-бар может «не вернуться».
        bool isFinal = p.Phase is SyncPhase.Completed or SyncPhase.Error or SyncPhase.Cancelled or SyncPhase.Idle or SyncPhase.Wipe;
        bool isStateChange = p.Phase is SyncPhase.Scanning or SyncPhase.Connecting or SyncPhase.LiveWatch;
        if (!isFinal && !isStateChange && Stopwatch.GetTimestamp() - _lastProgressPaint < Stopwatch.Frequency / 5)
            return;
        _lastProgressPaint = Stopwatch.GetTimestamp();

        var drive = string.IsNullOrWhiteSpace(p.DriveName) ? p.DriveLetter.ToString().Trim() : p.DriveName;

        switch (p.Phase)
        {
            case SyncPhase.Testing:
                GlobalProgressBar.Visibility = Visibility.Visible;
                GlobalProgressBar.IsIndeterminate = true;
                GlobalProgressBar.Maximum = 100;
                GlobalProgressBar.Value = 0;
                TxtGlobalStatus.Text = $"Тестирование {drive}";
                break;

            case SyncPhase.Scanning:
                GlobalProgressBar.Visibility = Visibility.Visible;
                GlobalProgressBar.IsIndeterminate = true;
                GlobalProgressBar.Maximum = 100;
                GlobalProgressBar.Value = 0;
                TxtGlobalStatus.Text = $"Сканирование {drive}";
                break;

            case SyncPhase.Copying:
                GlobalProgressBar.Visibility = Visibility.Visible;
                GlobalProgressBar.IsIndeterminate = false;
                GlobalProgressBar.Maximum = p.TotalFiles > 0 ? p.TotalFiles : 1;
                GlobalProgressBar.Value = p.ProcessedCount;
                TxtGlobalStatus.Text = $"Копирование {drive}: {System.IO.Path.GetFileName(p.CurrentFile)}";
                break;

            case SyncPhase.Pruning:
                GlobalProgressBar.Visibility = Visibility.Visible;
                GlobalProgressBar.IsIndeterminate = false;
                GlobalProgressBar.Maximum = p.TotalFiles > 0 ? p.TotalFiles : 1;
                GlobalProgressBar.Value = p.ProcessedCount;
                TxtGlobalStatus.Text = $"Очистка {drive}";
                break;

            case SyncPhase.Moving:
                GlobalProgressBar.Visibility = Visibility.Visible;
                GlobalProgressBar.IsIndeterminate = true;
                TxtGlobalStatus.Text = $"Перенос бэкапа {drive}";
                break;

            case SyncPhase.Wipe:
                GlobalProgressBar.Visibility = Visibility.Visible;
                GlobalProgressBar.IsIndeterminate = false;
                GlobalProgressBar.Maximum = p.TotalFiles > 0 ? p.TotalFiles : 1;
                GlobalProgressBar.Value = p.ProcessedCount;
                TxtGlobalStatus.Text = $"Удаление бэкапа {drive}";
                break;

            case SyncPhase.LiveWatch:
                CleanupDriveState(p.DriveLetter);
                GlobalProgressBar.Visibility = Visibility.Visible;
                GlobalProgressBar.IsIndeterminate = false;
                GlobalProgressBar.Maximum = 100;
                GlobalProgressBar.Value = 100;
                TxtGlobalStatus.Text = "Слежение за изменениями (Ожидание)";
                RefreshList();
                break;

            case SyncPhase.Connecting:
                GlobalProgressBar.Visibility = Visibility.Visible;
                GlobalProgressBar.IsIndeterminate = false;
                GlobalProgressBar.Maximum = 100;
                GlobalProgressBar.Value = 0;
                TxtGlobalStatus.Text = "";
                break;

            case SyncPhase.Cancelled:
            case SyncPhase.Error:
            case SyncPhase.Idle:
            default: // Completed
                CleanupDriveState(p.DriveLetter);
                GlobalProgressBar.Visibility = Visibility.Visible;
                GlobalProgressBar.IsIndeterminate = false;
                GlobalProgressBar.Maximum = 100;
                GlobalProgressBar.Value = 0;
                TxtGlobalStatus.Text = "";
                RefreshList();
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

    /// <summary>Переключить видимость окна (левый клик по иконке трея).</summary>
    private void ToggleWindow()
    {
        try
        {
            if (_appWindow.IsVisible)
            {
                _appWindow.Hide();
                UpdateTrayText();
                return;
            }
        }
        catch { /* falls through to restore */ }
        RestoreWindow();
    }

    /// <summary>
    /// Штатное завершение при автообновлении: отмена фоновых задач,
    /// освобождение трея/Mutex и выход приложения. Установщик уже запущен
    /// отвязанно (с задержкой), поэтому этот процесс можно безопасно убить.
    /// </summary>
    private async Task PerformShutdownForUpdate()
    {
        AppLog.Info("Обновление: штатное завершение приложения");

        _isExiting = true;

        try { await _orchestrator.DisposeAsync(); } catch { /* не блокируем выход */ }
        try { _tray?.Dispose(); } catch { }
        try { App.ReleaseMutex(); } catch { }

        Application.Current.Exit();

        // Страховка: если цикл сообщений не завершился сам — убиваем процесс.
        _ = Task.Delay(1500).ContinueWith(_ => Environment.Exit(0));
    }

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
