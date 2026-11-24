using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SmartUsbBackup.Core;
using SmartUsbBackup.Usb;

namespace SmartUsbBackup.Sync;

/// <summary>
/// Оркестратор: WM_DEVICECHANGE → FileSyncService.
/// Ничего не опрашивает и не держит хэндлов на флешке в простое:
/// синхронизация запускается при подключении включённой флешки или по кнопке.
/// </summary>
public sealed class BackupOrchestrator : IDisposable
{
    private readonly ConcurrentDictionary<string, ConnectedSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _fullSyncs = new(StringComparer.OrdinalIgnoreCase);

    // Запущенные задачи полной синхронизации — нужны, чтобы при удалении бэкапа
    // дождаться остановки копирования, а не удалять папку «из-под» активной записи.
    private readonly ConcurrentDictionary<string, Task> _syncTasks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Событие прогресса для UI (маршалинг в поток UI — задача окна).</summary>
    public event Action<ConnectedSession, SyncProgress>? ProgressChanged;

    /// <summary>Событие завершения прогона зеркальной синхронизации.</summary>
    public event Action<ConnectedSession, SyncResult>? SyncFinished;

    /// <summary>Событие изменения состояния записи в таблице «Флешки».</summary>
    public event Action? StateChanged;

    /// <summary>Сессии, живые прямо сейчас (флешка вставлена).</summary>
    public IReadOnlyCollection<ConnectedSession> ActiveSessions => (IReadOnlyCollection<ConnectedSession>)_sessions.Values;

    /// <summary>Задачи полной синхронизации (для ожидания завершения при переносе бэкапа).</summary>
    public ConcurrentDictionary<string, Task> SyncTasks => _syncTasks;

    // =====================================================================
    //  Реакция на системные события
    // =====================================================================

    /// <summary>Флешка подключилась.</summary>
    public Task<DriveProfile> OnDriveArrivedAsync(UsbDriveEventArgs e, IProgress<SyncProgress>? progress = null)
    {
        // Профиль по серийному номеру (никогда по букве диска).
        var profile = SettingsStore.GetOrCreate(e.Serial, e.VolumeLabel);
        profile.DriveLetter = e.DriveLetter;

        var session = new ConnectedSession(e.DriveLetter, profile, new FileSyncService());
        var key = e.DriveLetter.ToString();

        // Заменяем возможную старую сессию для этой же буквы.
        if (_sessions.TryGetValue(key, out var previous))
        {
            AppLog.Warn($"Подключение {key}: заменяю незакрытую сессию прошлого подключения");
            if (_fullSyncs.TryRemove(key, out var previousCts))
            {
                try { previousCts.Cancel(); } catch { /* задача освободит CTS сама */ }
            }
            _syncTasks.TryRemove(key, out _);
            try { previous.Dispose(); } catch { }
        }

        _sessions[key] = session;

        // Бэкап на самой флешке (или внутри её корня) недопустим: иначе файлы
        // следующего прогона начнут копироваться внутрь уже созданной копии.
        bool backupOnSource = false;
        try
        {
            var backupFull = Path.GetFullPath(profile.BackupPath);
            backupOnSource = PathSafety.IsSameVolume(backupFull, session.SourceRoot) ||
                             PathSafety.IsInsideOrEqual(session.SourceRoot, backupFull);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Не удалось проверить путь бэкапа");
            backupOnSource = true;
        }

        AppLog.Info($"Флешка подключена: {e.DriveLetter}: «{profile.Label}» serial={profile.Serial}, резервирование={(profile.Enabled ? "вкл" : "выкл")}");

        StateChanged?.Invoke();

        string driveName = string.IsNullOrWhiteSpace(profile.Label) ? "USB" : (string.IsNullOrWhiteSpace(profile.Serial) ? profile.Label : $"{profile.Label} [{profile.Serial}]");
        ProgressChanged?.Invoke(session, new SyncProgress('\0', 0, 0, 0, 0, 0, 0, "", SyncPhase.Connecting) { DriveName = driveName });

        if (backupOnSource)
        {
            session.State = SessionState.Error;
            session.LastResult = "папка бэкапа находится на этой же флешке — выберите другую папку хранения";
            AppLog.Error(null, $"Синхронизация {key} отменена: папка бэкапа на источнике: {profile.BackupPath}");
            StateChanged?.Invoke();
        }
        else if (profile.Enabled)
        {
            StartFullSync(session);
        }
        else
        {
            AppLog.Info($"Флешка «{profile.Label}» подключена, но её резервирование выключено");
        }

        return Task.FromResult(profile);
    }

    internal static IEnumerable<char> DriveLetterFromUnitMask(uint mask)
    {
        for (int bit = 0; bit < 26; bit++)
        {
            if ((mask & (1u << bit)) == 0) continue;
            yield return (char)('A' + bit);
        }
    }

    /// <summary>Флешка фактически отключена (REMOVECOMPLETE).</summary>
    public void OnDriveRemoveComplete(char letter)
    {
        var key = letter.ToString();
        if (!_sessions.TryRemove(key, out var session)) return;

        if (_fullSyncs.TryRemove(key, out var cts))
        {
            try { cts.Cancel(); } catch { }
            // Dispose сделает задача синхронизации в своём finally.
        }
        _syncTasks.TryRemove(key, out _);

        // Буква больше не принадлежит этой флешке: иначе после переиспользования
        // буквы другим диском строка показывала бы «Включено» и цепляла чужой том.
        session.Profile.DriveLetter = '\0';

        string driveName = string.IsNullOrWhiteSpace(session.Profile.Label) ? "USB" : (string.IsNullOrWhiteSpace(session.Profile.Serial) ? session.Profile.Label : $"{session.Profile.Label} [{session.Profile.Serial}]");
        ProgressChanged?.Invoke(session, new SyncProgress('\0', 0, 0, 0, 0, 0, 0, "", SyncPhase.Idle) { DriveName = driveName });

        session.DetachRequested = false;
        try { session.Dispose(); }
        catch (Exception ex) { AppLog.Error(ex, $"Ошибка освобождения сессии {letter}:"); }

        StateChanged?.Invoke();
        AppLog.Info($"Флешка отключена: {letter}:");
    }

    /// <summary>
    /// Синхронная подготовка к безопасному извлечению: отменяем копирование
    /// и ждём завершения задач, чтобы Windows не увидела «Устройство занято».
    /// </summary>
    public bool HandleDriveQueryRemove(char letter)
    {
        var key = letter.ToString();
        if (!_sessions.TryGetValue(key, out var session))
        {
            AppLog.Info($"[SAFE-REMOVE] {letter}: foreign/unknown session — TRUE");
            return true;
        }

        if (session.AttachmentState == DriveAttachmentState.Detaching)
        {
            AppLog.Info($"[SAFE-REMOVE] {letter}: already detaching — TRUE");
            return true;
        }
        if (session.AttachmentState == DriveAttachmentState.Removed)
            return true;

        var sw = Stopwatch.StartNew();
        session.DetachRequested = true;
        session.BeginDetach();

        int waitMs = 0;
        bool timedOut = false;
        if (_syncTasks.TryGetValue(key, out var running) && !running.IsCompleted)
        {
            var waitSw = Stopwatch.StartNew();
            try { Task.WhenAny(running, Task.Delay(1000)).Wait(); }
            catch (Exception ex) { AppLog.Error(ex, $"[SAFE-REMOVE] {letter}: ожидание sync завершилось ошибкой"); }
            waitMs = (int)waitSw.ElapsedMilliseconds;
            timedOut = !running.IsCompleted;
        }

        bool success = !timedOut;
        AppLog.Info($"[SAFE-REMOVE] {letter}: syncWait={waitMs} ms{(timedOut ? " (TIMEOUT)" : "")}, total={sw.ElapsedMilliseconds} ms -> {(success ? "TRUE" : "DENY")}");

        if (!success)
            StateChanged?.Invoke();

        return success;
    }

    public void OnDriveQueryRemoveFailed(char letter)
    {
        var key = letter.ToString();

        if (!_sessions.TryGetValue(key, out var session)) return;

        try { session.CancelDetachAndResume(); }
        catch (Exception ex) { AppLog.Error(ex, $"[SAFE-REMOVE] {letter}: CancelDetachAndResume error"); }

        if (session.AttachmentState == DriveAttachmentState.Attached)
            session.DetachRequested = false;

        StateChanged?.Invoke();
        AppLog.Info($"[SAFE-REMOVE] {letter}: QUERYREMOVEFAILED → {(session.AttachmentState == DriveAttachmentState.Attached ? "Attached" : "Removed")}");
    }

    // =====================================================================
    //  Полная зеркальная синхронизация
    // =====================================================================

    /// <summary>Максимум 1 задача на флешку (анти-дубль при «дёрганье»).</summary>
    private void StartFullSync(ConnectedSession session)
    {
        var key = session.Letter.ToString();

        lock (_fullSyncs)
        {
            if (_fullSyncs.TryGetValue(key, out var existingCts))
            {
                if (existingCts == session.CurrentCts)
                {
                    AppLog.Info($"Полная синхронизация {key}: уже запущена для этой сессии — пропуск");
                    return;
                }

                AppLog.Warn($"Полная синхронизация {key}: снята устаревшая запись (сессия сменилась)");
                try { existingCts.Cancel(); }
                catch { /* мог уже быть disposed */ }
                // Dispose делает владеющая задача в своём finally.
                _fullSyncs.TryRemove(key, out _);
            }

            var cts = new CancellationTokenSource();
            session.CurrentCts = cts;
            _fullSyncs[key] = cts;
        }

        var sessionCts = session.CurrentCts!;

        session.State = SessionState.Syncing;

        string driveName = string.IsNullOrWhiteSpace(session.Profile.Label)
            ? "USB"
            : (string.IsNullOrWhiteSpace(session.Profile.Serial) ? session.Profile.Label : $"{session.Profile.Label} [{session.Profile.Serial}]");

        AppLog.Info($"Полная синхронизация {key} запущена: {driveName}");
        ProgressChanged?.Invoke(session, new SyncProgress('\0', 0, 0, 0, 0, 0, 0, "", SyncPhase.Scanning) { DriveName = driveName });

        var progress = new Progress<SyncProgress>(p => ProgressChanged?.Invoke(session, p with { DriveName = driveName }));

        var task = Task.Run(async () =>
        {
            try
            {
                var result = await session.FsSync.SyncAllAsync(
                    session.SourceRoot, session.BackupPath, progress, sessionCts.Token);

                session.State = result.Error is null ? SessionState.Idle : SessionState.Error;
                session.LastResult = result.Error ?? $"копий {result.Copied}, удалено {result.Deleted}, пропущено {result.Skipped}";
                SyncFinished?.Invoke(session, result);
            }
            catch (OperationCanceledException)
            {
                AppLog.Warn($"Полная синхронизация {key} отменена");
                session.State = SessionState.Idle;
                session.LastResult = "отменено";
                ProgressChanged?.Invoke(session, new SyncProgress('\0', 0, 0, 0, 0, 0, 0, "", SyncPhase.Cancelled) { DriveName = driveName });
            }
            catch (Exception ex)
            {
                session.State = SessionState.Error;
                session.LastResult = "ошибка: " + ex.Message;
                AppLog.Error(ex, $"Полная синхронизация {session.SourceRoot} сорвалась");
                ProgressChanged?.Invoke(session, new SyncProgress('\0', 0, 0, 0, 0, 0, 0, "", SyncPhase.Error) { DriveName = driveName });
            }
            finally
            {
                lock (_fullSyncs)
                {
                    if (_fullSyncs.TryGetValue(key, out var c) && c == sessionCts)
                        _fullSyncs.TryRemove(key, out _);
                }
                if (ReferenceEquals(session.CurrentCts, sessionCts))
                    session.CurrentCts = null;
                try { sessionCts.Dispose(); } catch { }
                _syncTasks.TryRemove(key, out _);
                StateChanged?.Invoke();
            }
        });

        _syncTasks[key] = task;
    }

    /// <summary>Остановить активное копирование конкретной флешки (сняли галочку «Активна»).</summary>
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

    /// <summary>Кнопка «Синхронизировать всё сейчас».</summary>
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
    //  «Удалить всё»
    // =====================================================================

    /// <summary>
    /// Полная очистка: остановка всех задач, удаление всех папок бэкапов и настроек.
    /// Вызывать не из UI-потока — операция блокирующая.
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

        // Персональные папки профилей — до очистки списка Drives.
        var profileBackups = new List<string>();
        foreach (var profile in SettingsStore.Current.Drives)
        {
            var effectiveBase = EffectiveBase(profile);
            if (effectiveBase is null ||
                string.Equals(effectiveBase, canonicalBase, StringComparison.OrdinalIgnoreCase))
            {
                continue; // глобальная база удаляется целиком ниже
            }

            string backupPath;
            try { backupPath = Path.GetFullPath(Path.Combine(effectiveBase, profile.FolderName)); }
            catch { continue; }

            if (PathSafety.IsInsideDirectory(effectiveBase, backupPath) &&
                !PathSafety.ContainsReparsePoint(backupPath))
            {
                profileBackups.Add(backupPath);
            }
        }

        foreach (var letter in _sessions.Keys)
        {
            if (_fullSyncs.TryRemove(letter, out var cts))
            {
                // Только Cancel: Dispose — забота владеющей задачи.
                try { cts.Cancel(); } catch { }
            }
        }
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();

        try
        {
            foreach (var backupPath in profileBackups.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Directory.Exists(backupPath))
                {
                    DeleteTreeContents(backupPath);
                    try { Directory.Delete(backupPath, recursive: false); }
                    catch (Exception ex) { AppLog.Error(ex, "Не удалось удалить папку бэкапа: " + backupPath); }
                }
            }

            if (Directory.Exists(canonicalBase))
            {
                DeleteTreeContents(canonicalBase);
                try { Directory.Delete(canonicalBase, recursive: false); }
                catch (Exception ex) { AppLog.Error(ex, "Не удалось удалить корневую папку бэкапов"); }
            }
            SettingsStore.Current.Drives.Clear();
            SettingsStore.Save();
            AppLog.Info("Все бэкапы и настройки удалены");
        }
        catch (Exception ex) { AppLog.Error(ex, "Не удалось полностью очистить бэкапы"); }
    }

    /// <summary>Безопасная база профиля: проверенный персональный путь или глобальная база.</summary>
    private static string? EffectiveBase(DriveProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.BaseBackupPathOverride))
        {
            var canonical = PathSafety.CanonicalizeBase(profile.BaseBackupPathOverride);
            if (canonical is not null) return canonical;
        }
        return PathSafety.CanonicalizeBase(SettingsStore.Current.BaseBackupPath);
    }

    /// <summary>
    /// Удаляет содержимое каталога, НЕ проходя сквозь junction/symlink:
    /// сама ссылка удаляется, её цель не затрагивается.
    /// </summary>
    private static void DeleteTreeContents(string root)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        };

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", opts))
            {
                try { System.IO.File.Delete(file); }
                catch (Exception ex) { AppLog.Error(ex, "Не удалось удалить файл: " + file); }
            }
        }
        catch (Exception ex) { AppLog.Error(ex, "Не удалось перечислить файлы: " + root); }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "*", opts))
            {
                try
                {
                    var attrs = System.IO.File.GetAttributes(dir);
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                    {
                        // Удаляем только саму точку повторной обработки.
                        try { Directory.Delete(dir, recursive: false); } catch { }
                        continue;
                    }

                    DeleteTreeContents(dir);
                    try { Directory.Delete(dir, recursive: false); } catch { /* занято */ }
                }
                catch (Exception ex) { AppLog.Error(ex, "Не удалось удалить каталог: " + dir); }
            }
        }
        catch (Exception ex) { AppLog.Error(ex, "Не удалось перечислить каталоги: " + root); }
    }

    /// <summary>
    /// Удалить бэкап ОДНОЙ флешки (кнопка «Удалить бэкап выбранной»).
    /// Сначала останавливаем копирование этой флешки и ждём задачу.
    /// Профиль не удаляем: просто выключаем резервирование.
    /// </summary>
    public async Task WipeOneAsync(DriveProfile profile, IProgress<SyncProgress>? progress = null)
    {
        // SEC: B-02/D-01 — персональная база профиля тоже должна быть безопасной.
        var effectiveBase = EffectiveBase(profile);
        if (effectiveBase is null)
        {
            AppLog.Error(null, "SEC B-02: WipeOneAsync отменён — небезопасный базовый путь профиля");
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

        if (!PathSafety.IsSafeSubPath(effectiveBase, fullBackupPath))
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

            // Сначала выключаем профиль — иначе кнопка/автоматика может запустить
            // новую синхронизацию в удаляемую папку.
            profile.Enabled = false;
            if (!ReferenceEquals(session.Profile, profile))
                session.Profile.Enabled = false;

            session.CurrentCts?.Cancel();
            if (_fullSyncs.TryGetValue(key, out var activeCts))
            {
                try { activeCts.Cancel(); } catch { }
            }

            if (_syncTasks.TryGetValue(key, out var running))
            {
                // Копирование проверяет токен перед каждым файлом, поэтому
                // останавливается быстро. Таймаут — защита от «залипшего» I/O.
                await Task.WhenAny(running, Task.Delay(10000)).ConfigureAwait(false);
            }

            session.State = SessionState.Idle;
            session.LastResult = "бэкап удалён";
        }

        char driveLetter = session?.Letter ?? '?';
        string driveName = string.IsNullOrWhiteSpace(profile.Label) ? "USB" : (string.IsNullOrWhiteSpace(profile.Serial) ? profile.Label : $"{profile.Label} [{profile.Serial}]");

        AppLog.Info($"Удаление бэкапа начато: {profile.BackupPath}");

        try
        {
            // Удаление не блокирует UI-поток: I/O в фоне, прогресс маршалит IProgress.
            await Task.Run(() =>
            {
                List<string> files = new();
                int totalFiles = 0;
                if (Directory.Exists(fullBackupPath))
                {
                    try
                    {
                        // Не проходим сквозь junction/symlink внутри бэкапа.
                        var opts = new EnumerationOptions
                        {
                            RecurseSubdirectories = true,
                            IgnoreInaccessible = true,
                            AttributesToSkip = FileAttributes.ReparsePoint,
                        };
                        files = Directory.EnumerateFiles(fullBackupPath, "*", opts).ToList();
                        totalFiles = files.Count;
                    }
                    catch (Exception ex) { AppLog.Error(ex, "Не удалось перечислить файлы бэкапа"); }
                }

                int done = 0;
                var wipeSw = Stopwatch.StartNew();
                progress?.Report(new SyncProgress(driveLetter, 0, totalFiles, 0, 0, 0, totalFiles, "", SyncPhase.Wipe) { DriveName = driveName });

                if (Directory.Exists(fullBackupPath) && files.Count > 0)
                {
                    foreach (var file in files)
                    {
                        try
                        {
                            System.IO.File.Delete(file);
                            done++;

                            if (progress is not null &&
                                (done % 128 == 0 || wipeSw.ElapsedMilliseconds >= 150 || done == totalFiles))
                            {
                                progress.Report(new SyncProgress(driveLetter, done, totalFiles, 0, 0, done, totalFiles, "", SyncPhase.Wipe) { DriveName = driveName });
                                wipeSw.Restart();
                            }
                        }
                        catch (Exception ex) { AppLog.Error(ex, "Не удалось удалить файл: " + file); }
                    }

                    if (progress is not null && done > 0 && done != totalFiles)
                        progress.Report(new SyncProgress(driveLetter, done, totalFiles, 0, 0, done, totalFiles, "", SyncPhase.Wipe) { DriveName = driveName });
                }

                if (Directory.Exists(fullBackupPath))
                {
                    // Каталоги и reparse-точки: внутрь ссылок не заходим.
                    DeleteTreeContents(fullBackupPath);
                    try { Directory.Delete(fullBackupPath, recursive: false); }
                    catch (Exception ex) { AppLog.Error(ex, "Не удалось удалить папку бэкапа"); }
                }

                if (totalFiles == 0)
                    progress?.Report(new SyncProgress(driveLetter, 1, 1, 0, 0, 1, 1, "", SyncPhase.Wipe) { DriveName = driveName });

                AppLog.Info($"Бэкап флешки удалён: {profile.BackupPath}");
            }).ConfigureAwait(false);
        }
        catch (Exception ex) { AppLog.Error(ex, "Не удалось удалить бэкап флешки"); }

        profile.Enabled = false;
        SettingsStore.Save();
        StateChanged?.Invoke();
    }

    public async Task DisposeAsync()
    {
        // 1. Отменяем все активные копирования.
        foreach (var cts in _fullSyncs.Values)
        {
            try { cts.Cancel(); } catch { }
        }
        _fullSyncs.Clear();

        // 2. Дожидаемся завершения задач (с таймаутом, чтобы процесс не завис на I/O).
        var pendingTasks = _syncTasks.Values.ToList();
        _syncTasks.Clear();
        if (pendingTasks.Count > 0)
        {
            try
            {
                await Task.WhenAny(Task.WhenAll(pendingTasks), Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
            }
            catch
            {
                // Ошибки отдельных задач не должны ломать выход приложения.
            }
        }

        // 3. Освобождаем сессии.
        foreach (var s in _sessions.Values)
        {
            try { s.Dispose(); } catch { }
        }
        _sessions.Clear();
    }

    public void Dispose()
    {
        // Синхронная обёртка (для IDisposable) поверх асинхронной очистки.
        try { DisposeAsync().GetAwaiter().GetResult(); }
        catch { /* при штатном выходе ошибка очистки не должна блокировать процесс */ }
    }
}

// =====================================================================
//  ТИПЫ-ХЕЛПЕРЫ
// =====================================================================

public enum SessionState { Idle, Syncing, Error }
public enum DriveAttachmentState { Attached, Detaching, Removed }

/// <summary>Логическое «флешка сейчас в системе»: буква, профиль, сервис копирования.</summary>
public sealed class ConnectedSession : IDisposable
{
    public readonly object SyncRoot = new();
    private bool _disposed;

    public char Letter { get; }
    public DriveProfile Profile { get; }
    public FileSyncService FsSync { get; }
    public CancellationTokenSource? CurrentCts { get; set; }

    public SessionState State { get; set; } = SessionState.Idle;
    public DriveAttachmentState AttachmentState { get; private set; } = DriveAttachmentState.Attached;
    public string LastResult { get; set; } = "";

    public volatile bool DetachRequested;
    public DateTime LastSuccessfulSyncUtc { get; set; } = DateTime.MinValue;

    /// <summary>Корень флешки, например "D:\".</summary>
    public string SourceRoot => $@"{Letter}:\";

    /// <summary>Папка, в которую пишется копия.</summary>
    public string BackupPath => Profile.BackupPath;

    public ConnectedSession(char letter, DriveProfile profile, FileSyncService fsSync)
    {
        Letter = letter;
        Profile = profile;
        FsSync = fsSync;
    }

    public void BeginDetach()
    {
        lock (SyncRoot)
        {
            if (_disposed) return;
            if (AttachmentState != DriveAttachmentState.Attached) return;
            AttachmentState = DriveAttachmentState.Detaching;
        }

        try { CurrentCts?.Cancel(); } catch { }
    }

    public void CancelDetachAndResume()
    {
        lock (SyncRoot)
        {
            if (_disposed) return;
            if (AttachmentState != DriveAttachmentState.Detaching) return;

            if (!Directory.Exists(SourceRoot))
            {
                AttachmentState = DriveAttachmentState.Removed;
                return;
            }

            try { CurrentCts?.Cancel(); } catch { }
            CurrentCts = null;
            AttachmentState = DriveAttachmentState.Attached;
        }
    }

    public void Dispose()
    {
        lock (SyncRoot)
        {
            if (_disposed) return;
            _disposed = true;

            try { CurrentCts?.Cancel(); } catch { }
            // Не Dispose: задача синхронизации могла ещё использовать токен.
            CurrentCts = null;

            AttachmentState = DriveAttachmentState.Removed;
        }
    }
}
