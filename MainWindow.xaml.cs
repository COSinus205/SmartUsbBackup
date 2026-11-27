using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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

    private long _lastProgressPaint;   // throttle отрисовки прогресса (≤5 раз/с)

    // true — пользователь выбрал «Выход» в трее: крестик больше НЕ сворачивает окно,
    // а даёт приложению действительно завершиться.
    private bool _isExiting;

    // Собственная иконка трея (Shell_NotifyIcon). Создаётся в конструкторе.
    private NativeTrayIcon? _tray;
    private bool _checkingUpdate;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _updateTimer;

    public static MainWindow? Instance;
    public static DispatcherQueue? UiDispatcher;
    private readonly HashSet<DriveProfile> _syncingDrives = new();
    private readonly HashSet<DriveProfile> _deletingDrives = new();
    private readonly object _driveStateLock = new();

    // «Есть ли сохранение» — результат кэшируется, чтобы RefreshList не
    // устраивал по 3–4 обращения к диску на каждый профиль на UI-потоке.
    private readonly BackupPresenceCache _backupPresence = new();



    public MainWindow(bool startHidden)
    {
        // Признак тихого старта нужен только App (окно не активируется), внутри окна
        // фактическая видимость определяется через AppWindow.
        _ = startHidden;
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

        TxtBackupPath.Text = SettingsStore.Current.BaseBackupPath;

        RefreshList();
        AppLog.CleanupOld();
        EnumerateAlreadyPresentDrives();

        // Авто-проверка обновлений раз в сутки. Планируем здесь, а не в
        // OnFirstActivated: при старте с -hidden окно не активируется, событие
        // Activated не приходит — и проверка не выполнялась.
        _ = Task.Delay(8000).ContinueWith(_ =>
            _dq.TryEnqueue(() => _ = SafeFireAndForget(CheckForUpdatesAsync(false))));

        // Пока приложение живёт в трее, проверяем обновления и позже: сам
        // CheckForUpdatesAsync пропускает повтор в течение 24 часов после
        // успешной проверки, поэтому час — безопасный интервал опроса.
        _updateTimer = _dq.CreateTimer();
        _updateTimer.Interval = TimeSpan.FromHours(1);
        _updateTimer.Tick += (_, _) => _ = SafeFireAndForget(CheckForUpdatesAsync(false));
        _updateTimer.Start();
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

        // ---------- Кастомный TitleBar (строка заголовка над списком) ----------
        ExtendsContentIntoTitleBar = true;
        try { SetTitleBar(TitleBarGrid); } catch { /* не критично */ }

        var systemBar = AppWindow.TitleBar;
        systemBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        systemBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        systemBar.PreferredHeightOption = TitleBarHeightOption.Standard;

        TitleBarGrid.Loaded += (_, _) => ApplyTitleBarInsets();
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
            if (TitleBarGrid is null) return;

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

            TitleArea.Margin = new Thickness(Math.Max(0, leftInsetDip), 0, 8, 0);
            TitleBarRightSpacer.Width = Math.Max(0, rightInsetDip);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Не удалось настроить TitleBar insets");
        }
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
        _orchestrator.SyncFinished += (session, result) =>
        {
            if (result.Error is null)
            {
                session.LastSuccessfulSyncUtc = DateTime.Now;
            }

            // Прогон мог завершиться результатом с ошибкой (например, сбой
            // сканирования), не прислав фазу Error. Снимаем «Синхронизируется»
            // здесь, иначе колонка «Сохранение» зависает до переподключения.
            lock (_driveStateLock)
            {
                _syncingDrives.Remove(session.Profile);
            }

            // После прогона папка бэкапа точно изменилась — кэш presence устарел.
            _backupPresence.Invalidate(session.Profile.BackupPath);

            _dq.TryEnqueue(() =>
            {
                RefreshList();

                if (result.Error is not null &&
                    !_orchestrator.ActiveSessions.Any(s => s.State == SessionState.Syncing))
                {
                    GlobalProgressBar.IsIndeterminate = false;
                    GlobalProgressBar.Value = 0;
                    TxtGlobalStatus.Text = "Ошибка синхронизации: " + result.Error;
                }
            });
        };
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
        _usbWatcher.DriveQueryRemove += letter =>
        {
            _dq.TryEnqueue(() => SetDriveAttachmentStatus(letter, "Подготовка к извлечению…"));
            bool ok = _orchestrator.HandleDriveQueryRemove(letter);
            _dq.TryEnqueue(() =>
            {
                SetDriveAttachmentStatus(letter, ok
                    ? "Можно безопасно извлечь"
                    : "Подготовка к извлечению…");
            });
            return ok;
        };
        _usbWatcher.DriveQueryRemoveFailed += letter => _dq.TryEnqueue(() => OnDriveQueryRemoveFailedUi(letter));
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

        if (profile is not null && IsWindowHidden()) // (только если окно скрыто — иначе пользователь всё видит)
        {
            var arrivedSession = _orchestrator.ActiveSessions.FirstOrDefault(s =>
                ReferenceEquals(s.Profile, profile) ||
                string.Equals(s.Profile.Serial, profile.Serial, StringComparison.OrdinalIgnoreCase));
            string message = arrivedSession?.State == SessionState.Error
                ? $"Флешка «{e.VolumeLabel}»: {arrivedSession.LastResult}"
                : profile.Enabled
                    ? $"Флешка «{e.VolumeLabel}» подключена — синхронизация начата"
                    : $"Флешка «{e.VolumeLabel}» подключена — включите «Активна» для резервирования";
            SafeNotify("Smart USB Backup", message);
        }
    }

    private void OnDriveRemovedUi(char letter)
    {
        var removedProfile = _orchestrator.ActiveSessions.FirstOrDefault(s => s.Letter == letter)?.Profile;
        _orchestrator.OnDriveRemoveComplete(letter);
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

    private void SetDriveAttachmentStatus(char letter, string text)
    {
        foreach (var row in _rows.Where(r => r.Profile.DriveLetter == letter))
            row.State = text;
    }

    private void OnDriveQueryRemoveFailedUi(char letter)
    {
        // Возвращаем сессию в Attached: иначе она навсегда останется Detaching,
        // и следующая попытка извлечения сразу получит TRUE без ожидания записи.
        _orchestrator.OnDriveQueryRemoveFailed(letter);

        var session = _orchestrator.ActiveSessions.FirstOrDefault(s => s.Letter == letter);
        var profile = session?.Profile;

        SetDriveAttachmentStatus(letter, "Подключена");
        RefreshList();

        // QUERYREMOVE отменил копирование, но извлечение не состоялось:
        // для включённой и всё ещё подключённой флешки запускаем прогон заново.
        bool resumed = profile is not null &&
                       profile.Enabled &&
                       session!.AttachmentState == DriveAttachmentState.Attached;
        if (resumed)
            _orchestrator.SyncOne(profile!);

        TxtGlobalStatus.Text = resumed
            ? "Извлечение отменено — копирование возобновлено"
            : "Извлечение отменено";

        if (IsWindowHidden())
            SafeNotify("Smart USB Backup", "Извлечение отменено");
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
                foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                {
                    char letter = drive.Name[0];
                    if (!UsbDeviceWatcher.IsEligibleDrive(letter))
                        continue;

                    var q = UsbDeviceWatcher.QueryDrive(letter, retry: 1, delayMs: 0);
                    if (q is not null)
                        _usbWatcher.RaiseArrived(letter, q.Value.VolumeLabel, q.Value.Serial);
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

            await ChangeGlobalBackupPathAsync(folder.Path);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Не удалось выбрать папку бэкапов");
            await ShowInfoAsync("Ошибка", "Не удалось открыть диалог выбора папки:\n" + ex.Message);
        }
    }

    /// <summary>
    /// Проверяет выбранную базовую папку: канонический путь, не системная папка,
    /// не корень и не папка на подключённой флешке. При ошибке показывает
    /// пояснение и возвращает null.
    /// </summary>
    private async Task<string?> ValidateChosenBaseAsync(string chosenPath)
    {
        var canonical = PathSafety.CanonicalizeBase(chosenPath);
        if (canonical is null)
        {
            await ShowInfoAsync("Недопустимая папка",
                "Нельзя использовать корень диска, системные папки (Windows/Program Files) " +
                "или папку пользователя. Выберите другую папку.");
            return null;
        }

        foreach (var session in _orchestrator.ActiveSessions)
        {
            if (PathSafety.IsSameVolume(canonical, session.SourceRoot) ||
                PathSafety.IsInsideOrEqual(session.SourceRoot, canonical))
            {
                await ShowInfoAsync("Недопустимая папка",
                    "Выбранная папка находится на подключённой флешке. " +
                    "Хранить бэкап на том же носителе нельзя — выберите другую папку.");
                return null;
            }
        }

        return canonical;
    }

    /// <summary>
    /// Смена глобальной папки хранения: проверка пути, предложение перенести
    /// существующие копии, затем сохранение настройки и обновление UI.
    /// </summary>
    private async Task ChangeGlobalBackupPathAsync(string chosenPath)
    {
        var canonical = await ValidateChosenBaseAsync(chosenPath);
        if (canonical is null) return;

        var oldBase = SettingsStore.Current.BaseBackupPath;
        if (string.Equals(Path.GetFullPath(oldBase), canonical, StringComparison.OrdinalIgnoreCase))
        {
            TxtBackupPath.Text = SettingsStore.Current.BaseBackupPath;
            return;
        }

        // Новая база не должна лежать внутри существующей папки бэкапа: иначе
        // перенос скопирует бэкап в его же подпапку и удалит исходник.
        foreach (var d in SettingsStore.Current.Drives)
        {
            if (!string.IsNullOrWhiteSpace(d.BaseBackupPathOverride)) continue;

            var oldProfilePath = Path.Combine(oldBase, d.FolderName);
            if (!Directory.Exists(oldProfilePath)) continue;

            var newProfilePath = Path.Combine(canonical, d.FolderName);
            if (PathSafety.IsInsideOrEqual(oldProfilePath, newProfilePath))
            {
                await ShowInfoAsync("Недопустимая папка",
                    "Новая папка хранения находится внутри папки бэкапа флешки «" + d.Label + "». " +
                    "Такой перенос удалил бы копию. Выберите другую папку.");
                TxtBackupPath.Text = SettingsStore.Current.BaseBackupPath;
                return;
            }
        }

        // Перенос касается только профилей без персонального пути.
        var movable = SettingsStore.Current.Drives
            .Where(d => string.IsNullOrWhiteSpace(d.BaseBackupPathOverride))
            .Select(d => (Profile: d,
                          OldPath: Path.Combine(oldBase, d.FolderName),
                          NewPath: Path.Combine(canonical, d.FolderName)))
            .Where(x => Directory.Exists(x.OldPath))
            .ToList();

        bool migrate = false;
        if (movable.Count > 0)
        {
            var dlg = new ContentDialog
            {
                Title = "Перенести существующие бэкапы?",
                Content = $"В старой папке найдено копий: {movable.Count}. " +
                          "Перенести их в новую папку? Если оставить, старые копии останутся на прежнем месте, " +
                          "а приложение будет использовать новую папку (в таблице появится «Нет сохранения»).",
                PrimaryButtonText = "Перенести",
                SecondaryButtonText = "Оставить на месте",
                CloseButtonText = "Отмена",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };

            var result = await ShowDialogAsync(dlg);
            if (result == ContentDialogResult.None) return;   // отмена — путь не меняем
            migrate = result == ContentDialogResult.Primary;
        }

        // Путь хранения меняется для всех профилей без персональной папки:
        // останавливаем активные копирования, иначе перенос пойдёт параллельно записи.
        foreach (var session in _orchestrator.ActiveSessions)
            session.CurrentCts?.Cancel();

        var running = _orchestrator.SyncTasks.Values.ToArray();
        if (running.Length > 0)
        {
            var all = Task.WhenAll(running);
            var finished = await Task.WhenAny(all, Task.Delay(10000));
            if (!ReferenceEquals(finished, all))
            {
                AppLog.Warn("Смена папки хранения отложена: синхронизация не остановилась за 10 с");
                TxtGlobalStatus.Text = "Не удалось остановить синхронизацию. Повторите позже.";
                return;
            }
        }

        SettingsStore.Current.BaseBackupPath = canonical;
        SettingsStore.Save();
        _backupPresence.Clear();
        TxtBackupPath.Text = canonical;
        AppLog.Info("Глобальная папка хранения бэкапов изменена: " + canonical);

        if (migrate)
        {
            TxtGlobalStatus.Text = "Перенос бэкапов…";
            int moved = 0;
            foreach (var item in movable)
            {
                // Каталог назначения уже существует? Не перетираем — оставляем как есть.
                if (Directory.Exists(item.NewPath)) continue;

                try
                {
                    TxtGlobalStatus.Text = $"Перенос «{item.Profile.Label}»…";
                    await Task.Run(() => DirectoryMover.Move(item.OldPath, item.NewPath));
                    moved++;
                }
                catch (Exception ex)
                {
                    AppLog.Error(ex, "Не удалось перенести бэкап: " + item.OldPath);
                }
            }

            _backupPresence.Clear();
            TxtGlobalStatus.Text = $"Перенесено копий: {moved}.";
            AppLog.Info($"Перенос бэкапов завершён: {moved} из {movable.Count}");
        }

        // После смены пути возобновляем включённые флешки уже в новой папке.
        foreach (var session in _orchestrator.ActiveSessions)
            if (session.Profile.Enabled)
                _orchestrator.SyncOne(session.Profile);

        RefreshList();
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
            _backupPresence.Invalidate(profile.BackupPath);   // папка удалена
            await Task.Yield();
            ShowIdle();
            RefreshList();
        }
    }

    // ---------- Настройки ----------

    /// <summary>Кнопка с шестерёнкой в строке заголовка: общие настройки.</summary>
    private async void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        var pathBox = new TextBox
        {
            IsReadOnly = true,
            Text = SettingsStore.Current.BaseBackupPath,
        };

        var chkStartup = new CheckBox
        {
            Content = "Запускать с Windows",
            IsChecked = CurrentRunAtStartup(),
        };
        chkStartup.Checked += (_, _) => ToggleStartup(enable: true);
        chkStartup.Unchecked += (_, _) => ToggleStartup(enable: false);

        var chkAutoUpdate = new CheckBox
        {
            Content = "Автообновление приложения",
            IsChecked = SettingsStore.Current.AutoCheckForUpdates,
        };
        chkAutoUpdate.Checked += (_, _) => SaveSetting(s => s.AutoCheckForUpdates = true);
        chkAutoUpdate.Unchecked += (_, _) => SaveSetting(s => s.AutoCheckForUpdates = false);

        var dialog = new ContentDialog
        {
            Title = "Настройки",
            CloseButtonText = "Закрыть",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };

        string? chosenPath = null;
        var btnBrowse = new Button
        {
            Content = "Выбрать папку хранения...",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        btnBrowse.Click += async (_, _) =>
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
                var folder = await picker.PickSingleFolderAsync();
                if (folder is null) return;

                // Смена папки может показать свой диалог — закрываем настройки,
                // чтобы не открывать два ContentDialog одновременно.
                chosenPath = folder.Path;
                dialog.Hide();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Не удалось выбрать папку хранения бэкапов");
            }
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 400 };
        panel.Children.Add(new TextBlock { Text = "Папка хранения бэкапов:" });
        panel.Children.Add(pathBox);
        panel.Children.Add(btnBrowse);
        panel.Children.Add(chkStartup);
        panel.Children.Add(chkAutoUpdate);
        panel.Children.Add(new TextBlock
        {
            Text = "Копирование всегда зеркальное: файлы, удалённые на флешке, удаляются и в бэкапе. " +
                   "Скрытые и системные файлы не копируются.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        });

        dialog.Content = new ScrollViewer { Content = panel, MaxHeight = 480 };
        await ShowDialogAsync(dialog);

        if (chosenPath is not null)
            await ChangeGlobalBackupPathAsync(chosenPath);
    }

    private static void SaveSetting(Action<AppSettings> apply)
    {
        apply(SettingsStore.Current);
        SettingsStore.Save();
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

    private async void PerDriveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DriveRow row })
            return;

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

        var backupPathText = new TextBlock
        {
            Text = $"Папка хранения: {profile.BackupPath}",
            TextWrapping = TextWrapping.Wrap,
        };

        var dialog = new ContentDialog
        {
            Title = row.Name,
            CloseButtonText = "Закрыть",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };

        string? chosenBase = null;
        bool resetToDefault = false;

        var btnRelocateBackup = new Button { Content = "Изменить папку хранения..." };
        var btnResetBackupPath = new Button { Content = "Сбросить к стандартной" };

        btnRelocateBackup.Click += async (_, _) =>
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
                var folder = await picker.PickSingleFolderAsync();
                if (folder is null) return;

                // Перенос покажет прогресс в главном окне — закрываем настройки,
                // чтобы не держать модальный диалог поверх индикатора.
                chosenBase = folder.Path;
                dialog.Hide();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Не удалось выбрать папку хранения флешки");
            }
        };

        btnResetBackupPath.Click += (_, _) =>
        {
            resetToDefault = true;
            dialog.Hide();
        };

        var stack = new StackPanel { Spacing = 10, MinWidth = 420 };
        stack.Children.Add(backupPathText);
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { btnRelocateBackup, btnResetBackupPath },
        });
        stack.Children.Add(chkEnabled);
        stack.Children.Add(statusText);

        dialog.Content = new ScrollViewer { Content = stack, MaxHeight = 480 };
        await ShowDialogAsync(dialog);

        try
        {
            if (resetToDefault)
            {
                await RelocateDriveBackupAsync(profile, SettingsStore.Current.BaseBackupPath,
                    new Progress<SyncProgress>(p => OnProgress(p)));
                _backupPresence.Clear();
                TxtGlobalStatus.Text = $"Папка хранения «{profile.Label}» сброшена к стандартной.";
                RefreshList();
            }
            else if (chosenBase is not null)
            {
                var canonical = await ValidateChosenBaseAsync(chosenBase);
                if (canonical is null) return;

                await RelocateDriveBackupAsync(profile, canonical,
                    new Progress<SyncProgress>(p => OnProgress(p)));
                _backupPresence.Clear();
                TxtGlobalStatus.Text = $"Папка хранения «{profile.Label}» изменена.";
                RefreshList();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Ошибка изменения папки хранения флешки");
            TxtGlobalStatus.Text = "Ошибка: " + ex.Message;
        }
        finally
        {
            // Не сбрасываем прогресс чужой активной синхронизации.
            ResetProgressIfIdle();
        }
    }

    private async Task RelocateDriveBackupAsync(DriveProfile profile, string newBase, IProgress<SyncProgress> progress)
    {
        // SEC: персональный путь проверяем и здесь — метод вызывается и по кнопке,
        // и при сбросе к глобальной папке.
        var canonicalBase = PathSafety.CanonicalizeBase(newBase);
        if (canonicalBase is null)
        {
            AppLog.Error(null, "SEC B-03: перенос отменён — небезопасный путь: " + newBase);
            TxtGlobalStatus.Text = "Нельзя использовать выбранную папку. Выберите другую.";
            return;
        }

        newBase = canonicalBase;
        string oldPath = profile.BackupPath;
        string oldBase = string.IsNullOrWhiteSpace(profile.BaseBackupPathOverride) ? SettingsStore.Current.BaseBackupPath : profile.BaseBackupPathOverride!;

        if (string.Equals(oldBase, newBase, StringComparison.OrdinalIgnoreCase))
        {
            progress.Report(new SyncProgress(profile.DriveLetter, 1, 1, 0, 0, 0, 1, "", SyncPhase.Moving) { DriveName = profile.Label });
            TxtGlobalStatus.Text = "Папка хранения уже стандартная (или не изменилась).";
            return;
        }

        string newPath = Path.Combine(newBase, profile.FolderName);
        if (PathSafety.IsInsideOrEqual(oldPath, newPath))
        {
            AppLog.Warn($"Перенос «{profile.Label}» отменён: новая папка внутри текущей: {newPath}");
            TxtGlobalStatus.Text = "Нельзя выбрать папку внутри текущего бэкапа. Выберите другую.";
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

        progress.Report(new SyncProgress(profile.DriveLetter, 0, 1, 0, 0, 0, 1, "", SyncPhase.Moving) { DriveName = profile.Label });

        if (Directory.Exists(oldPath))
        {
            await Task.Run(() => DirectoryMover.Move(oldPath, newPath));
        }

        // Обновляем хранилище (null = стандартный глобальный путь).
        profile.BaseBackupPathOverride = string.Equals(newBase, SettingsStore.Current.BaseBackupPath, StringComparison.OrdinalIgnoreCase)
            ? null
            : newBase;
        SettingsStore.Save();

        progress.Report(new SyncProgress(profile.DriveLetter, 1, 1, 0, 0, 0, 1, "", SyncPhase.Moving) { DriveName = profile.Label });
        AppLog.Info($"Папка хранения флешки «{profile.Label}» перенесена: {oldPath} -> {newPath}");

        // Перенос останавливал синхронизацию: для включённой флешки продолжаем
        // прогон уже в новую папку (если она всё ещё подключена).
        if (profile.Enabled)
            _orchestrator.SyncOne(profile);
    }

    private static string BuildDriveStatusText(DriveProfile profile)
        => $"Активна: {(profile.Enabled ? "да" : "нет")}";

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

    /// <summary>Прокрутка списка колесом мыши, перехваченным в WndProc.</summary>
    public static void HandleMouseWheel(int delta)
    {
        var instance = Instance;
        var dq = UiDispatcher;
        if (instance is null || dq is null) return;

        dq.TryEnqueue(() =>
        {
            try
            {
                if (instance.AppRoot is not FrameworkElement root) return;

                var viewer = FindDescendantScrollViewer(root);
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

        // Снапшот сессий: ActiveSessions — живая вьюха ConcurrentDictionary,
        // и раньше она перечислялась по два раза на каждый профиль.
        var sessions = _orchestrator.ActiveSessions;

        // 1) убираем удалённые профили
        for (int i = _rows.Count - 1; i >= 0; i--)
            if (!profiles.Contains(_rows[i].Profile))
                _rows.RemoveAt(i);
        // Убираем строки, не удовлетворяющие условию отображения (см. ниже).
        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            var proj = _rows[i].Profile;
            if (!(proj.Enabled || IsSessionPresent(sessions, proj) || _backupPresence.HasContent(proj.BackupPath)))
                _rows.RemoveAt(i);
        }

        // 2) добавляем/обновляем в порядке настроек.
        // Индекс «профиль → строка»: без линейного поиска на каждый профиль.
        var rowByProfile = new System.Collections.Generic.Dictionary<DriveProfile, DriveRow>(_rows.Count);
        foreach (var r in _rows)
            rowByProfile[r.Profile] = r;

        for (int i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            bool present = IsSessionPresent(sessions, profile);

            // Path.Combine и обход каталога — по одному разу на профиль,
            // дальше значения переиспользуются для фильтра и для колонки.
            string backupPath = profile.BackupPath;
            bool hasBackup = _backupPresence.HasContent(backupPath);
            if (!(profile.Enabled || present || hasBackup)) continue;

            if (!rowByProfile.TryGetValue(profile, out var row))
            {
                row = new DriveRow(profile);
                rowByProfile[profile] = row;
                _rows.Add(row);
            }
            // Не переставляем строки: коллекция могла потерять элементы из-за фильтра;
            // _rows.Move(currentIndex, i) здесь небезопасен (ArgumentOutOfRange).


            row.Enabled = profile.Enabled;
            row.RefreshComputed();

            // Состояние строки: выключено / не подключена / резервирование / ошибка.
            row.RefreshState(ComputeState(profile));

            bool isConnected = profile.DriveLetter != '\0' &&
                               System.IO.Directory.Exists(profile.DriveLetter + ":\\");

            // Directory.Exists(backupPath) — из того же кэша, что и hasBackup,
            // т.е. без дополнительного обращения к диску.
            string hasBackupText = _backupPresence.Exists(backupPath) ? "Есть сохранение" : "Нет сохранения";

            if (!isConnected)
            {
                lock (_driveStateLock)
                {
                    if (_syncingDrives.Contains(profile)) _syncingDrives.Remove(profile);
                    if (_deletingDrives.Contains(profile)) _deletingDrives.Remove(profile);
                }

                row.BackupState = hasBackupText;
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
                else row.BackupState = hasBackupText;
            }
        }

        // 3) выделение могло исчезнуть вместе со строкой
        if (DriveList.SelectedItem is DriveRow sel && !_rows.Contains(sel))
            DriveList.SelectedIndex = -1;
        BtnWipeSelected.IsEnabled = DriveList.SelectedItem is DriveRow;

        UpdateTrayText();
    }

    /// <summary>
    /// Есть ли активная сессия для профиля. Сравнение по серийнику — только когда
    /// он реально задан, иначе пустые серийники разных флешек считались бы одной.
    /// </summary>
    private static bool IsSessionPresent(
        System.Collections.Generic.IReadOnlyCollection<ConnectedSession> sessions,
        DriveProfile profile)
    {
        foreach (var s in sessions)
        {
            if (ReferenceEquals(s.Profile, profile))
                return true;
            if (!string.IsNullOrEmpty(profile.Serial) &&
                string.Equals(s.Profile.Serial, profile.Serial, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private string ComputeState(DriveProfile profile)
    {
        var session = _orchestrator.ActiveSessions
            .FirstOrDefault(s => ReferenceEquals(s.Profile, profile)
                                 || string.Equals(s.Profile.Serial, profile.Serial, StringComparison.OrdinalIgnoreCase));

        if (!profile.Enabled) return "Выключено";
        if (session is null) return "Не подключена";
        if (session.LastSuccessfulSyncUtc != DateTime.MinValue)
        {
            return session.State switch
            {
                SessionState.Syncing => "Резервирование…",
                SessionState.Error => "Ошибка: " + session.LastResult,
                _ => "Синхронизировано (" + session.LastSuccessfulSyncUtc.ToString("HH:mm") + ")",
            };
        }
        return session.State switch
        {
            SessionState.Syncing => "Резервирование…",
            SessionState.Error => "Ошибка: " + session.LastResult,
            _ => "Синхронизировано",
        };
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

            // Диалоги в скрытом окне невидимы: перед пользовательскими
            // действиями показываем окно.
            if (userInitiated && IsWindowHidden())
                RestoreWindow();

            var now = DateTime.Now;
            var last = SettingsStore.Current.LastUpdateCheck;
            if (!userInitiated && (now - last).TotalHours < 24)
                return;

            var info = await UpdateService.CheckAsync();
            var current = UpdateService.AppVersion;

            if (info is null)
            {
                // Метку не двигаем: неудачная попытка не блокирует повтор на сутки.
                if (userInitiated)
                    await ShowInfoAsync("Обновления", "Не удалось проверить обновления (нет сети?).");
                return;
            }

            // Проверка удалась — только теперь фиксируем время.
            SettingsStore.Current.LastUpdateCheck = now;
            SettingsStore.Save();

            // Нормализованное сравнение: тег «4.3.1» и AssemblyVersion «4.3.1.0» равны.
            int versionCmp = UpdateService.Compare(info.Version, current);
            if (versionCmp < 0)
            {
                if (userInitiated)
                    await ShowInfoAsync("Обновления", "У вас более новая версия, обновление не требуется.");
                return;
            }
            if (versionCmp == 0)
            {
                if (userInitiated)
                    await ShowInfoAsync("Обновления", $"Установлена самая свежая версия ({current}).");
                return;
            }

            // Фоновая проверка при скрытом окне: никаких модальных диалогов —
            // только уведомление в трее.
            if (IsWindowHidden())
            {
                SafeNotify("Smart USB Backup",
                    $"Доступно обновление {info.Version} (сейчас {current}). Откройте окно, чтобы установить.");
                return;
            }

            // Если открыт другой диалог (настройки и т.п.), второй ContentDialog
            // показать нельзя: сообщаем в трей/статус и не теряем проверку молча.
            if (Volatile.Read(ref _openDialogs) > 0)
            {
                TxtGlobalStatus.Text = $"Доступно обновление {info.Version}. Закройте открытый диалог, чтобы установить.";
                SafeNotify("Smart USB Backup",
                    $"Доступно обновление {info.Version}. Закройте открытый диалог, чтобы установить.");
                // Не считаем проверку завершённой: предложим обновление позже.
                SettingsStore.Current.LastUpdateCheck = last;
                SettingsStore.Save();
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

                if (await ShowDialogAsync(majorDlg) == ContentDialogResult.Primary &&
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

            // SHA-256 обязателен: без хеша целостность не подтверждена.
            if (string.IsNullOrWhiteSpace(info.Sha256))
            {
                await ShowInfoAsync("Обновления",
                    "Для этого релиза не опубликован SHA-256, поэтому автоматическая установка недоступна. " +
                    "Скачайте установщик со страницы релиза вручную.");
                return;
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

            if (await ShowDialogAsync(dlg) != ContentDialogResult.Primary)
                return;

            // Отдельный диалог прогресса: предыдущий уже закрыт ShowAsync(),
            // поэтому менять его Title/Content было бессмысленно.
            var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Width = 320 };
            var progressText = new TextBlock { Text = $"0% из {info.Size / 1048576.0:F1} МБ" };
            var progressPanel = new StackPanel { Spacing = 8 };
            progressPanel.Children.Add(progressBar);
            progressPanel.Children.Add(progressText);

            using var downloadCts = new System.Threading.CancellationTokenSource();
            var progressDlg = new ContentDialog
            {
                Title = "Загрузка обновления",
                Content = progressPanel,
                CloseButtonText = "Отмена",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            // «Отмена» в диалоге отменяет и скачивание.
            progressDlg.Closed += (_, _) => { try { downloadCts.Cancel(); } catch { } };

            var progress = new Progress<double>(v =>
            {
                double percent = Math.Clamp(v * 100, 0, 100);
                progressBar.Value = percent;
                progressText.Text = $"{percent:F0}% из {info.Size / 1048576.0:F1} МБ";
            });

            var showTask = progressDlg.ShowAsync().AsTask();
            string? path;
            try
            {
                path = await UpdateService.DownloadAsync(info, progress, downloadCts.Token);
            }
            finally
            {
                progressDlg.Hide();
                try { await showTask; } catch { }
            }

            if (path is null)
            {
                if (!downloadCts.IsCancellationRequested)
                    await ShowInfoAsync("Обновления", "Произошла ошибка загрузки. Попробуйте позже.");
                return;
            }

            // Перезапускаем так же, как приложение работало: скрыто — если было скрыто.
            if (UpdateService.ApplyUpdate(path, restartHidden: IsWindowHidden()))
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
        // Смена фазы (сканирование/подключение) не троттлится: иначе сразу
        // после выдёргивания флешки прогресс-бар может «не вернуться».
        bool isFinal = p.Phase is SyncPhase.Completed or SyncPhase.Error or SyncPhase.Cancelled or SyncPhase.Idle or SyncPhase.Wipe;
        bool isStateChange = p.Phase is SyncPhase.Scanning or SyncPhase.Connecting;
        if (!isFinal && !isStateChange && Stopwatch.GetTimestamp() - _lastProgressPaint < Stopwatch.Frequency / 5)
            return;
        _lastProgressPaint = Stopwatch.GetTimestamp();

        var drive = string.IsNullOrWhiteSpace(p.DriveName) ? p.DriveLetter.ToString().Trim() : p.DriveName;

        switch (p.Phase)
        {
            case SyncPhase.Scanning:
                GlobalProgressBar.Visibility = Visibility.Visible;
                // Обход дерева может идти долго: показываем прогресс как пульсацию,
                // а имя флешки + число уже увиденных файлов — в строке статуса.
                GlobalProgressBar.IsIndeterminate = true;
                GlobalProgressBar.Maximum = 100;
                GlobalProgressBar.Value = 0;
                if (p.ProcessedCount > 0)
                    TxtGlobalStatus.Text = $"Сканирование {drive}: {p.ProcessedCount} файлов";
                else
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

    private int _openDialogs;

    /// <summary>
    /// Показывает ContentDialog и учитывает, что он открыт: фоновые сценарии
    /// (автообновление) не должны пытаться открыть второй диалог одновременно.
    /// </summary>
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        Interlocked.Increment(ref _openDialogs);
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            Interlocked.Decrement(ref _openDialogs);
        }
    }

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
            return await ShowDialogAsync(MakeDialog(title, message, primary, "Отмена"))
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
            await ShowDialogAsync(new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "ОК",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            });
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

        try { _updateTimer?.Stop(); } catch { }
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

        try { _updateTimer?.Stop(); } catch { }
        try { _orchestrator.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }

        Application.Current.Exit();

        // Страховка: если по какой-то причине цикл сообщений не завершился
        // (например, окно уже скрыто в трей), гарантированно убиваем процесс,
        // чтобы «Выход» никогда не оставлял приложение висеть в фоне.
        _ = Task.Delay(1500).ContinueWith(_ => Environment.Exit(0));
    }
}
