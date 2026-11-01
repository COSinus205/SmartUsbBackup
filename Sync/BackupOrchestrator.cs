using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SmartUsbBackup.Core;
using SmartUsbBackup.Sync;
using SmartUsbBackup.Usb;

namespace SmartUsbBackup.Sync;

/// <summary>Агрегированный прогресс одной операции (UI рисует по нему).</summary>
public sealed record OperationProgress(
    DriveProfile Drive,
    SyncProgress Inner,
    string StateText)
{
    public override string ToString() => StateText;
}

/// <summary>
/// Оркестратор: собирает вместе WM_DEVICECHANGE → FileSystemWatcher → FileSyncService.
///
/// Идея: этот класс ничего не «опрашивает». Он только РЕАГИРУЕТ:
///   • На UsbDeviceWatcher.DriveArrived — постановка в очередь полной синхронизации.
///   • На UsbDeviceWatcher.DriveRemoved — отмена активной задачи, выключение watcher'а.
///   • В простое существует только система событий и ОДИН workspace-поток.
/// Посвящено снижение памяти: малое число длительных объектов.
/// </summary>
public sealed class BackupOrchestrator : IDisposable
{
    private readonly ConcurrentDictionary<string, ConnectedSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _fullSyncs = new(StringComparer.OrdinalIgnoreCase);

    // Запущенные задачи полной синхронизации — нужны, чтобы при удалении бэкапа
    // дождаться остановки копирования, а не удалять папку «из-под» активной записи.
    private readonly ConcurrentDictionary<string, Task> _syncTasks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Событие прогресса для UI (маршалинг в поток UI — задача Main-формы).</summary>
    public event Action<ConnectedSession, SyncProgress>? ProgressChanged;

    /// <summary>Событие завершения прогона зеркальной синхронизации.</summary>
    public event Action<ConnectedSession, SyncResult>? SyncFinished;

    /// <summary>Событие изменения состояния записи в таблице «Флешки».</summary>
    public event Action? StateChanged;

    /// <summary>Сессии, живые прямо сейчас (флешка вставлена).</summary>
    public IReadOnlyCollection<ConnectedSession> ActiveSessions => (IReadOnlyCollection<ConnectedSession>)_sessions.Values;

    /// <summary>Задачи полной синхронизации (для ожидания завершения при переносе бэкапа).</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, Task> SyncTasks => _syncTasks;

    // =====================================================================
    //  Реакция на системные события
    // =====================================================================

    /// <summary>Флешка подключилась.</summary>
    public async Task<DriveProfile> OnDriveArrivedAsync(UsbDriveEventArgs e, IProgress<SyncProgress>? progress = null)
    {
        // Профиль по серийному номеру (никогда не по букве диска).
        var profile = SettingsStore.GetOrCreate(e.Serial, e.VolumeLabel);
        profile.DriveLetter = e.DriveLetter;

        // Авто-тест настроек при первом подключении (только чтение, безопасно).
        if (SettingsStore.Current.AutoTestNewDrives && !profile.SettingsTested)
        {
            try
            {
                await RunDriveSettingsTest(profile, e.DriveLetter, progress);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Ошибка авто-теста настроек флешки");
            }
            profile.SettingsTested = true;
            SettingsStore.Save();
        }
        else if (!profile.SettingsTested)
        {
            // Автотест выключен пользователем, считаем глобальные настройки подтверждёнными.
            profile.SettingsTested = true;
            SettingsStore.Save();
        }

        var session = new ConnectedSession(e.DriveLetter, profile, new FileSyncService(
            profile.MirrorModeOverride ?? SettingsStore.Current.MirrorMode,
            profile.VerifyByCrcOverride ?? SettingsStore.Current.VerifyByCrc,
            profile.SkipHiddenSystemOverride ?? SettingsStore.Current.SkipHiddenSystem,
            profile.BufferSizeKbOverride ?? SettingsStore.Current.DefaultBufferSizeKb,
            profile.FlushAfterFileOverride ?? SettingsStore.Current.DefaultFlushAfterFile));
 
        session.FileWatcher.FileChanged += path => OnFileChanged(session, path);
        session.FileWatcher.FileDeleted += path => OnFileDeleted(session, path);
        // SEC: F-01 — при переполнении буфера FSW запускаем полную синхронизацию.
        session.FileWatcher.OverflowDetected += () =>
        {
            try
            {
                if (!session.Profile.Enabled) return;
                if (_fullSyncs.ContainsKey(session.Letter.ToString())) return;
                StartFullSync(session);
            }
            catch (Exception ex) { AppLog.Error(ex, "[USB] Не удалось запустить полную синхронизацию после overflow"); }
        };
        _sessions[$"{e.DriveLetter}"] = session;

        AppLog.Info($"Флешка подключена: {e.DriveLetter}: «{profile.Label}» serial={profile.Serial}, резервирование={(profile.Enabled ? "вкл" : "выкл")}");

        StateChanged?.Invoke();

        string driveNameArrived = string.IsNullOrWhiteSpace(profile.Label) ? "USB" : (string.IsNullOrWhiteSpace(profile.Serial) ? profile.Label : $"{profile.Label} [{profile.Serial}]");
        // UI: флешка подключается, метаданные ещё читаются.
        ProgressChanged?.Invoke(session, new SyncProgress('\0', 0, 0, 0, 0, 0, 0, "", SyncPhase.Connecting) { DriveName = driveNameArrived });

        // Полная зеркальная синхронизация при подключении (только если профиль включён).
        if (profile.Enabled) StartFullSync(session);
        else AppLog.Info($"Флешка «{profile.Label}» подключена, но её резервирование выключено");

        return profile;
    }

    /// <summary>
    /// Безопасный тест настроек для новой флешки (только чтение, без записи).
    /// Считает файлы и измеряет скорость чтения выборки, затем подбирает параметры.
    /// Прогресс выводится через IProgress (в UI он попадает в прогресс-бар).
    /// </summary>
    private async Task RunDriveSettingsTest(DriveProfile profile, char letter, IProgress<SyncProgress>? progress)
    {
        string driveName = string.IsNullOrWhiteSpace(profile.Label) ? "USB" : profile.Label;
        progress?.Report(new SyncProgress(letter, 0, 100, 0, 0, 0, 100, "", SyncPhase.Testing) { DriveName = driveName });

        int fileCount = 0;
        int sampleBytes = 0;
        long freeBytes = 0;
        long totalBytes = 0;
        try
        {
            var di = new DriveInfo(letter + ":\\");
            totalBytes = di.TotalSize;
            freeBytes = di.AvailableFreeSpace;
        }
        catch { /* не критично */ }

        var sw = Stopwatch.StartNew();
        await Task.Run(() =>
        {
            try
            {
                var opts = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };

                foreach (var path in Directory.EnumerateFiles(letter + ":\\", "*", opts))
                {
                    if (fileCount >= 5000) break;
                    fileCount++;
                    try
                    {
                        if (sampleBytes < 16 * 1024 * 1024)
                        {
                            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
                            var buf = new byte[64 * 1024];
                            int read;
                            while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                            {
                                sampleBytes += read;
                                if (sampleBytes >= 16 * 1024 * 1024) break;
                            }
                        }
                    }
                    catch { /* файл недоступен — пропускаем */ }

                    if (fileCount % 500 == 0)
                    {
                        progress?.Report(new SyncProgress(letter, fileCount, Math.Max(fileCount, 1), 0, 0, 0, Math.Max(fileCount, 1), "", SyncPhase.Testing) { DriveName = driveName });
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Тест чтения флешки прерван: " + ex.Message);
            }
        });
        sw.Stop();

        double mbps = sampleBytes > 0 ? sampleBytes / 1024.0 / 1024.0 / Math.Max(sw.Elapsed.TotalSeconds, 0.001) : 0;

        // Глобальные значения используем как базу; тест уточняет только те поля,
        // которые пользователь ещё не задавал явно (null).
        profile.MirrorModeOverride ??= true;
        profile.VerifyByCrcOverride ??= (mbps > 0 && mbps < 25);
        profile.LiveWatchOverride ??= (fileCount < 3000);
        profile.SkipHiddenSystemOverride ??= true;
        profile.BufferSizeKbOverride ??= (mbps >= 60 ? 8192 : (mbps >= 20 ? 4096 : 1024));
        profile.FlushAfterFileOverride ??= true;

        AppLog.Info(
            $"Тест флешки {profile.Label}: файлов={fileCount}, свободно={freeBytes / 1024 / 1024}MB, " +
            $"скорость={mbps:F1}MB/s, CRC={(profile.VerifyByCrcOverride == true)}, " +
            $"LiveWatch={(profile.LiveWatchOverride == true)}");

        progress?.Report(new SyncProgress(letter, 100, 100, 0, 0, 0, 100, "", SyncPhase.Testing) { DriveName = driveName });
    }

    /// <summary>Флешка отсылается (извлекается).</summary>
    public void OnDriveRemoved(char letter)
    {
        var key = letter.ToString();
        if (!_sessions.TryRemove(key, out var session)) return;

        // Отменяем активную копию (CancellationTokenSource), если она была.
        if (_fullSyncs.TryRemove(key, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
        _syncTasks.TryRemove(key, out _);

        string driveNameRemoved = string.IsNullOrWhiteSpace(session.Profile.Label) ? "USB" : (string.IsNullOrWhiteSpace(session.Profile.Serial) ? session.Profile.Label : $"{session.Profile.Label} [{session.Profile.Serial}]");
        ProgressChanged?.Invoke(session, new SyncProgress('\0', 0, 0, 0, 0, 0, 0, "", SyncPhase.Idle) { DriveName = driveNameRemoved });

        try { session.Dispose(); }
        catch (Exception ex) { AppLog.Error(ex, "Ошибка освобождения сессии {letter}:"); }

        StateChanged?.Invoke();
        AppLog.Info($"Флешка отключена: {letter}:");
    }

    // =====================================================================
    //  Полная зеркальная синхронизация
    // =====================================================================

    /// <summary>
    /// Очередь полных синхронизаций: максимум 1 задача на флешку (анти-дубль при «дёрганье»).
    /// </summary>
    private void StartFullSync(ConnectedSession session)
    {
        var key = session.Letter.ToString();

        // Анти-дубль: если полный прогон уже идёт для этой же сессии — не стартуем второй.
        // Раньше старая/отменённая запись могла «залипнуть» в словаре и блокировать
        // повторный запуск после выдёргивания/вставки флешки: прогресс-бар тогда
        // не возвращался. Теперь чужую/устаревшую запись снимаем и отменяем.
        lock (_fullSyncs)
        {
            if (_fullSyncs.TryGetValue(key, out var existingCts))
            {
                if (existingCts == session.CurrentCts)
                {
                    AppLog.Info($"Полная синхронизация {key}: уже запущена для этой сессии — пропуск");
                    return; // уже запущено для этой же сессии
                }

                AppLog.Warn($"Полная синхронизация {key}: снята устаревшая запись (сессия сменилась)");
                try { existingCts.Cancel(); }
                catch { /* мог уже быть disposed */ }
                try { existingCts.Dispose(); }
                catch { }
                _fullSyncs.TryRemove(key, out _);
            }

            var cts = new CancellationTokenSource();
            session.CurrentCts = cts;
            _fullSyncs[key] = cts;
        }

        session.State = SessionState.Syncing;

        string driveNameSync = string.IsNullOrWhiteSpace(session.Profile.Label) ? "USB" : (string.IsNullOrWhiteSpace(session.Profile.Serial) ? session.Profile.Label : $"{session.Profile.Label} [{session.Profile.Serial}]");

        AppLog.Info($"Полная синхронизация {key} запущена: {driveNameSync}");
        // UI: начало полной синхронизации (обход/подсчёт), даже до первого progress.Report.
        ProgressChanged?.Invoke(session, new SyncProgress('\0', 0, 0, 0, 0, 0, 0, "", SyncPhase.Scanning) { DriveName = driveNameSync });

        // IProgress<SyncProgress> маршалит вызовы в UI-поток (SynchronizationContext).
        var progress = new Progress<SyncProgress>(p => ProgressChanged?.Invoke(session, p with { DriveName = driveNameSync }));

        // Копия «уходит» в пул потоков: интерфейс и обработка USB-событий не пустеют.
        var task = Task.Run(async () =>
        {
            try
            {
                var cts = session.CurrentCts!;
                var result = await session.FsSync.SyncAllAsync(
                    session.SourceRoot, session.BackupPath, progress, cts.Token);

                session.State = result.Error is null ? SessionState.Idle : SessionState.Error;
                session.LastResult = result.Error ?? $"копий {result.Copied}, удалено {result.Deleted}, пропущено {result.Skipped}";
                SyncFinished?.Invoke(session, result);

                // После успешного завершения переходим в режим ожидания изменений, если он включён.
                if (result.Error is null && SettingsStore.Current.LiveWatch)
                {
                    ProgressChanged?.Invoke(session, new SyncProgress('\0', 0, 0, 0, 0, 0, 0, "", SyncPhase.LiveWatch) { DriveName = driveNameSync });
                }
            }
            catch (OperationCanceledException)
            {
                AppLog.Warn($"Полная синхронизация {key} отменена");
                session.State = SessionState.Idle;
                session.LastResult = "отменено";
                ProgressChanged?.Invoke(session, new SyncProgress('\0', 0, 0, 0, 0, 0, 0, "", SyncPhase.Cancelled) { DriveName = driveNameSync });
            }
            catch (Exception ex)
            {
                session.State = SessionState.Error;
                session.LastResult = "ошибка: " + ex.Message;
                AppLog.Error(ex, $"Полная синхронизация {session.SourceRoot} сорвалась");
                ProgressChanged?.Invoke(session, new SyncProgress('\0', 0, 0, 0, 0, 0, 0, "", SyncPhase.Error) { DriveName = driveNameSync });
            }
            finally
            {
                lock (_fullSyncs)
                {
                    if (_fullSyncs.TryGetValue(key, out var c) && c == session.CurrentCts)
                        _fullSyncs.TryRemove(key, out _);
                }
                _syncTasks.TryRemove(key, out _);
                StateChanged?.Invoke();
            }
        });

        _syncTasks[key] = task;
    }

    /// <summary>
    /// Остановить активное копирование конкретной флешки (сняли галочку «Активна»).
    /// Токен отмены проверяется перед каждым файлом и внутри чанкового чтения,
    /// поэтому копирование прекращается в течение долей секунды.
    /// </summary>
    public void CancelSync(DriveProfile profile)
    {
        foreach (var session in _sessions.Values)
        {
            bool same = ReferenceEquals(session.Profile, profile) ||
                        string.Equals(session.Profile.Serial, profile.Serial, StringComparison.OrdinalIgnoreCase);
            if (!same) continue;

            session.CurrentCts?.Cancel();
            session.State = SessionState.Idle;
            session.LastResult = "остановлено пользователем";
            AppLog.Info($"Синхронизация «{profile.Label}» остановлена");
        }
        StateChanged?.Invoke();
    }

    /// <summary>Кнопка «Синхронизировать всё сейчас» (или автоматом — после подключения).</summary>
    public void SyncAllNow()
    {
        foreach (var session in _sessions.Values)
            if (session.Profile.Enabled)
                StartFullSync(session);
    }

    /// <summary>Синхронизировать только одну флешку (из таблицы).</summary>
    public void SyncOne(DriveProfile profile)
    {
        foreach (var session in _sessions.Values)
            if (ReferenceEquals(session.Profile, profile) && profile.Enabled)
                StartFullSync(session);
    }

    // =====================================================================
    //  Инкрементальные события из FileSystemWatcher
    // =====================================================================

    private async void OnFileChanged(ConnectedSession session, string fullPath)
    {
        try
        {
            if (!session.Profile.Enabled) return;
            // Мгновенная синхронизация опциональна (по умолчанию выключена): без неё
            // файлы синхронизируются только при подключении флешки или по кнопке.
            if (!SettingsStore.Current.LiveWatch) return;
            await session.FsSync.SyncSingleFileAsync(fullPath, session.SourceRoot, session.BackupPath);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Ошибка при инкрементальном копировании файла");
        }
    }

    private void OnFileDeleted(ConnectedSession session, string fullPath)
    {
        try
        {
            if (!session.Profile.Enabled) return;
            if (!SettingsStore.Current.LiveWatch) return;
            session.FsSync.DeleteFromBackup(fullPath, session.SourceRoot, session.BackupPath);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Ошибка при удалении файла из бэкапа");
        }
    }

    // =====================================================================
    //  «Удалить всё»
    // =====================================================================

    /// <summary>
    /// Полная очистка: остановка всех задач, выгрузка watcher'ов, удаление всех папок бэкапов и настроек.
    /// Используется кнопкой «Удалить всё» в UI и деинсталлятором (Inno Setup вызывает с флагами).
    /// </summary>
    public void WipeEverything()
    {
        // SEC: B-01 — каноническая проверка базового пути до удаления.
        var canonicalBase = PathSafety.CanonicalizeBase(SettingsStore.Current.BaseBackupPath);
        if (canonicalBase is null)
        {
            AppLog.Error(null, "SEC B-01: WipeEverything отменён — небезопасный базовый путь: " + SettingsStore.Current.BaseBackupPath);
            return;
        }
        if (Directory.Exists(canonicalBase) && PathSafety.ContainsReparsePoint(canonicalBase))
        {
            AppLog.Error(null, "SEC B-01: WipeEverything отменён — reparse point в пути");
            return;
        }

        foreach (var letter in _sessions.Keys)
        {
            if (_fullSyncs.TryRemove(letter, out var cts)) { cts.Cancel(); cts.Dispose(); }
        }
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();

        try
        {
            if (Directory.Exists(canonicalBase))
            {
                foreach (var file in Directory.EnumerateFiles(canonicalBase, "*", SearchOption.AllDirectories))
                {
                    try { System.IO.File.Delete(file); }
                    catch (Exception ex) { AppLog.Error(ex, "Не удалось удалить файл: " + file); }
                }
                foreach (var dir in Directory.EnumerateDirectories(canonicalBase, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                {
                    try { Directory.Delete(dir, recursive: false); }
                    catch { /* папка занята или недоступна */ }
                }
                try { Directory.Delete(canonicalBase, recursive: false); }
                catch (Exception ex) { AppLog.Error(ex, "Не удалось удалить корневую папку бэкапов"); }
            }
            SettingsStore.Current.Drives.Clear();
            SettingsStore.Save();
            AppLog.Info("Все бэкапы и настройки удалены");
        }
        catch (Exception ex) { AppLog.Error(ex, "Не удалось полностью очистить бэкапы"); }
    }


    /// <summary>
    /// Удалить бэкап ОДНОЙ флешки (кнопка «Удалить бэкап выбранной»).
    ///
    /// ВАЖНО:
    ///   • Сначала ОСТАНАВЛИВАЕМ копирование этой флешки и ждём завершения задачи —
    ///     иначе активная синхронизация продолжала писать в удаляемую папку.
    ///   • Профиль флешки НЕ удаляем: строка остаётся в таблице (раньше подключённая
    ///     флешка исчезала из интерфейса до переподключения), просто выключаем её.
    /// </summary>
    public async Task WipeOneAsync(DriveProfile profile, IProgress<SyncProgress>? progress = null)
    {
        // SEC: B-02/D-01 — каноническая проверка базы и принадлежности BackupPath.
        var canonicalBase = PathSafety.CanonicalizeBase(SettingsStore.Current.BaseBackupPath);
        if (canonicalBase is null)
        {
            AppLog.Error(null, "SEC B-02: WipeOneAsync отменён — небезопасный базовый путь");
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.FolderName) ||
            profile.FolderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            profile.FolderName.Contains("..", StringComparison.Ordinal) ||
            profile.FolderName.Contains(Path.DirectorySeparatorChar) ||
            profile.FolderName.Contains(Path.AltDirectorySeparatorChar) ||
            PathSafety.IsReservedWindowsName(profile.FolderName))
        {
            AppLog.Error(null, "SEC B-02: WipeOneAsync отменён — небезопасное имя папки: " + profile.FolderName);
            return;
        }

        string fullBackupPath;
        try { fullBackupPath = Path.GetFullPath(profile.BackupPath); }
        catch (Exception ex) { AppLog.Error(ex, "SEC B-02: некорректный путь бэкапа"); return; }

        if (!PathSafety.IsSafeSubPath(canonicalBase, fullBackupPath))
        {
            AppLog.Error(null, "SEC B-02: BackupPath вне базовой папки: " + fullBackupPath);
            return;
        }
        if (PathSafety.ContainsReparsePoint(fullBackupPath))
        {
            AppLog.Error(null, "SEC B-02: WipeOneAsync отменён — reparse point в пути");
            return;
        }

        // Legacy safety: never touch executable/installation paths.
        try
        {
            if (fullBackupPath.Contains("SmartUsbBackup.exe", StringComparison.OrdinalIgnoreCase) ||
                fullBackupPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Error(null, "Отказ удаления бэкапа: подозрительный путь: " + fullBackupPath);
                return;
            }
        }
        catch { }

        var session = _sessions.Values.FirstOrDefault(s =>
            ReferenceEquals(s.Profile, profile) ||
            string.Equals(s.Profile.Serial, profile.Serial, StringComparison.OrdinalIgnoreCase));

        if (session is not null)
        {
            var key = session.Letter.ToString();
            session.CurrentCts?.Cancel();

            if (_syncTasks.TryGetValue(key, out var running))
            {
                // Не ждём вечно: даже «залипшее» чтение с уже вынутой флешки
                // не должно подвешивать интерфейс.
                await Task.WhenAny(running, Task.Delay(5000)).ConfigureAwait(false);
            }

            session.State = SessionState.Idle;
            session.LastResult = "бэкап удалён";
        }

        char driveLetter = session?.Letter ?? '?';
        string driveNameWipe = string.IsNullOrWhiteSpace(profile.Label) ? "USB" : (string.IsNullOrWhiteSpace(profile.Serial) ? profile.Label : $"{profile.Label} [{profile.Serial}]");

        AppLog.Info($"Удаление бэкапа начато: {profile.BackupPath}");

        try
        {
            // Удаление потенциально большого числа файлов не блокирует UI-поток:
            // I/O и отчёты прогресса выполняются в фоне; IProgress сам маршалит в UI.
            await Task.Run(() =>
            {
                List<string> files = new();
                int totalFiles = 0;
                if (Directory.Exists(profile.BackupPath))
                {
                    try
                    {
                        files = Directory.EnumerateFiles(profile.BackupPath, "*", SearchOption.AllDirectories).ToList();
                        totalFiles = files.Count;
                    }
                    catch (Exception ex) { AppLog.Error(ex, "Не удалось перечислить файлы бэкапа"); }
                }

                int done = 0;
                progress?.Report(new SyncProgress(driveLetter, 0, totalFiles, 0, 0, 0, totalFiles, "", SyncPhase.Wipe) { DriveName = driveNameWipe });

                if (Directory.Exists(profile.BackupPath) && files.Count > 0)
                {
                    foreach (var file in files)
                    {
                        try
                        {
                            System.IO.File.Delete(file);
                            done++;
                            progress?.Report(new SyncProgress(driveLetter, done, totalFiles, 0, 0, done, totalFiles, "", SyncPhase.Wipe) { DriveName = driveNameWipe });
                        }
                        catch (Exception ex) { AppLog.Error(ex, "Не удалось удалить файл: " + file); }
                    }

                    var dirs = Directory.EnumerateDirectories(profile.BackupPath, "*", SearchOption.AllDirectories)
                        .OrderByDescending(d => d.Length).ToList();
                    foreach (var dir in dirs)
                    {
                        try { Directory.Delete(dir, recursive: false); }
                        catch { /* папка занята или недоступна */ }
                    }
                    try { Directory.Delete(profile.BackupPath, recursive: false); }
                    catch (Exception ex) { AppLog.Error(ex, "Не удалось удалить папку бэкапа"); }
                }

                if (totalFiles == 0)
                {
                    progress?.Report(new SyncProgress(driveLetter, 1, 1, 0, 0, 1, 1, "", SyncPhase.Wipe) { DriveName = driveNameWipe });
                }

                AppLog.Info($"Бэкап флешки удалён: {profile.BackupPath}");
            }).ConfigureAwait(false);
        }
        catch (Exception ex) { AppLog.Error(ex, "Не удалось удалить бэкап флешки"); }

        profile.Enabled = false;
        SettingsStore.Save();
        StateChanged?.Invoke();
    }

    public async Task TestDriveSettingsAsync(DriveProfile profile, IProgress<SyncProgress>? progress, bool resetOverrides = true)
    {
        if (resetOverrides)
        {
            profile.MirrorModeOverride = null;
            profile.VerifyByCrcOverride = null;
            profile.SkipHiddenSystemOverride = null;
            profile.LiveWatchOverride = null;
            profile.BufferSizeKbOverride = null;
            profile.FlushAfterFileOverride = null;
            profile.SettingsTested = false;
        }
        try
        {
            await RunDriveSettingsTest(profile, profile.DriveLetter, progress);
            profile.SettingsTested = true;
            SettingsStore.Save();
        }
        catch
        {
            profile.SettingsTested = false;
            SettingsStore.Save();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        // 1. Отменяем все активные копирования: ни один поток
        //    не должен продолжать писать на диск.
        foreach (var cts in _fullSyncs.Values)
        {
            try { cts.Cancel(); } catch { }
        }
        _fullSyncs.Clear();

        // 2. Дожидаемся завершения выполнявшихся задач (с таймаутом,
        //    чтобы процесс гарантированно не завис на остановленном I/O).
        var pendingTasks = _syncTasks.Values.ToList();
        _syncTasks.Clear();
        if (pendingTasks.Count > 0)
        {
            try
            {
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(3));
                var allTasks = Task.WhenAll(pendingTasks);
                await Task.WhenAny(allTasks, timeoutTask).ConfigureAwait(false);
            }
            catch
            {
                // Ошибки отдельных задач не должны ломать выход приложения.
            }
        }

        // 3. Освобождаем сессии (watcher'ы, CTS).
        foreach (var s in _sessions.Values)
        {
            try { s.Dispose(); } catch { }
        }
        _sessions.Clear();
    }

    public void Dispose()
    {
        // Синхронная обёртка (для IDisposable) поверх асинхронной очистки.
        try
        {
            DisposeAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // При штатном выходе ошибка очистки не должна блокировать процесс.
        }
    }
}

// =====================================================================
//  ТИПЫ-ХЕЛПЕРЫ
// =====================================================================

public enum SessionState { Idle, Syncing, Error }

/// <summary>Логическое «флешка сейчас в системе»: буква, профиль, watcher, сервис копирования.</summary>
public sealed class ConnectedSession : IDisposable
{
    public char Letter { get; }
    public DriveProfile Profile { get; }
    public UsbFileWatcher FileWatcher { get; }
    public FileSyncService FsSync { get; }
    public CancellationTokenSource? CurrentCts { get; set; }

    public SessionState State { get; set; } = SessionState.Idle;
    public string LastResult { get; set; } = "";

    /// <summary>Корень флешки, например "D:\".</summary>
    public string SourceRoot => $@"{Letter}:\";

    /// <summary>Папка, в которую пишется копия.</summary>
    public string BackupPath => Profile.BackupPath;

    public ConnectedSession(char letter, DriveProfile profile, FileSyncService fsSync)
    {
        Letter = letter;
        Profile = profile;
        FsSync = fsSync;
        FileWatcher = new UsbFileWatcher(SourceRoot);
    }

    public void Dispose()
    {
        FileWatcher.Dispose();
        CurrentCts?.Dispose();
    }
}
