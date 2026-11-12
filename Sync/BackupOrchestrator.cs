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

    /// <summary>Набор флешек, находящихся в фазе QUERYREMOVE-detach (по букве диска).</summary>
    private readonly ConcurrentDictionary<string, byte> _detachingDrives = new(StringComparer.OrdinalIgnoreCase);

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
    public Task<DriveProfile> OnDriveArrivedAsync(UsbDriveEventArgs e, IProgress<SyncProgress>? progress = null)
    {
        // Профиль по серийному номеру (никогда не по букве диска).
        var profile = SettingsStore.GetOrCreate(e.Serial, e.VolumeLabel);
        profile.DriveLetter = e.DriveLetter;

        var session = new ConnectedSession(e.DriveLetter, profile, new FileSyncService(
            profile.MirrorModeOverride ?? SettingsStore.Current.MirrorMode,
            profile.VerifyByCrcOverride ?? SettingsStore.Current.VerifyByCrc,
            profile.SkipHiddenSystemOverride ?? SettingsStore.Current.SkipHiddenSystem,
            profile.BufferSizeKbOverride ?? SettingsStore.Current.DefaultBufferSizeKb,
            profile.FlushAfterFileOverride ?? SettingsStore.Current.DefaultFlushAfterFile,
             profile.FastSmallFileCopyOverride ?? SettingsStore.Current.DefaultFastCopy));

        if (SettingsStore.Current.ForceLegacyWatcher)
        {
            session.FileWatcher = new UsbFileWatcher(session.SourceRoot);
            WireFileWatcher(session);
        }

        _sessions[$"{e.DriveLetter}"] = session;

        if (!SettingsStore.Current.ForceLegacyWatcher && SettingsStore.Current.UsePollingInIdle)
        {
            session.LastPollSignature = ComputePollSignature(session.SourceRoot);
            StartPolling(session);
        }

        AppLog.Info($"Флешка подключена: {e.DriveLetter}: «{profile.Label}» serial={profile.Serial}, резервирование={(profile.Enabled ? "вкл" : "выкл")}");

        StateChanged?.Invoke();

        string driveNameArrived = string.IsNullOrWhiteSpace(profile.Label) ? "USB" : (string.IsNullOrWhiteSpace(profile.Serial) ? profile.Label : $"{profile.Label} [{profile.Serial}]");
        // UI: флешка подключается, метаданные ещё читаются.
        ProgressChanged?.Invoke(session, new SyncProgress('\0', 0, 0, 0, 0, 0, 0, "", SyncPhase.Connecting) { DriveName = driveNameArrived });

        // Полная зеркальная синхронизация при подключении (только если профиль включён).
        if (profile.Enabled) StartFullSync(session);
        else AppLog.Info($"Флешка «{profile.Label}» подключена, но её резервирование выключено");

        // Авто-тест НЕ задерживает подключение: чтение до 16 МБ с флешки занимает
        // секунды. Тест уходит в фон и запускается после первой синхронизации,
        // чтобы не конкурировать с копированием за пропускную способность.
        if (SettingsStore.Current.AutoTestNewDrives && !profile.SettingsTested)
        {
            StartAutoTestInBackground(profile, e.DriveLetter, session);
        }
        else if (!profile.SettingsTested)
        {
            // Автотест выключен пользователем, считаем глобальные настройки подтверждёнными.
            profile.SettingsTested = true;
            SettingsStore.Save();
        }

        return Task.FromResult(profile);
    }

    /// <summary>
    /// Фоновый авто-тест новой флешки: ждём завершения первой синхронизации,
    /// затем читаем выборку файлов и подбираем размер буфера для будущих прогонов.
    /// </summary>
    private void StartAutoTestInBackground(DriveProfile profile, char letter, ConnectedSession session)
    {
        _ = Task.Run(async () =>
        {
            // Токен берём сразу: после detach CTS может быть уже освобождён.
            var ct = session.DetachCts.Token;
            try
            {
                if (_syncTasks.TryGetValue(letter.ToString(), out var running))
                    await Task.WhenAny(running, Task.Delay(Timeout.Infinite, ct));

                ct.ThrowIfCancellationRequested();
                await Task.Delay(2000, ct);
                await RunDriveSettingsTest(profile, letter, progress: null, ct);
                profile.SettingsTested = true;
                SettingsStore.Save();
            }
            catch (OperationCanceledException)
            {
                AppLog.Warn("Авто-тест настроек прерван из-за извлечения флешки");
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Ошибка авто-теста настроек флешки");
            }
        });
    }

    /// <summary>
    /// Безопасный тест настроек для новой флешки (только чтение, без записи).
    /// Считает файлы и измеряет скорость чтения выборки, затем подбирает параметры.
    /// Прогресс выводится через IProgress (в UI он попадает в прогресс-бар).
    /// </summary>
    internal async Task RunDriveSettingsTest(DriveProfile profile, char letter, IProgress<SyncProgress>? progress, CancellationToken ct = default)
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
                    ct.ThrowIfCancellationRequested();
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
                                ct.ThrowIfCancellationRequested();
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
            catch (OperationCanceledException)
            {
                throw;
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
        // Тест подбирает только размер буфера чтения/записи. Остальные параметры
        // (зеркало, CRC, skip, live watch, flush, fast copy) остаются как задал пользователь.
        if (profile.BufferSizeKbOverride is null)
        {
            // Эмпирические диапазоны для внешних USB-накопителей (см. замеры скорости чтения).
            //  - медленные USB 2.0 / старые контроллеры: небольшой буфер снижает накладные расходы
            //  - быстрые USB 3.x / SSD-флешки: larger chunk лучше насыщает шину и контроллер NAND
            profile.BufferSizeKbOverride = mbps switch
            {
                <= 0 => 4096,
                < 5 => 512,
                < 15 => 1024,
                < 30 => 2048,
                < 70 => 4096,
                < 150 => 8192,
                < 300 => 16384,
                _ => 16384,
            };
        }

        AppLog.Info(
            $"Тест флешки {profile.Label}: файлов={fileCount}, свободно={freeBytes / 1024 / 1024}MB, " +
            $"скорость={mbps:F1}MB/s, буфер={(profile.BufferSizeKbOverride ?? 0)}КБ");

        progress?.Report(new SyncProgress(letter, 100, 100, 0, 0, 0, 100, "", SyncPhase.Testing) { DriveName = driveName });
    }

    private void WireFileWatcher(ConnectedSession session)
    {
        if (session.FileWatcher is null) return;

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
    }

    private void EnsureWatcher(ConnectedSession session)
    {
        lock (session.SyncRoot)
        {
            if (session.AttachmentState != DriveAttachmentState.Attached) return;
            if (session.FileWatcher is not null) return;
            session.FileWatcher = new UsbFileWatcher(session.SourceRoot);
        }
        WireFileWatcher(session);
    }

    internal static IEnumerable<char> DriveLetterFromUnitMask(uint mask)
    {
        for (int bit = 0; bit < 26; bit++)
        {
            if ((mask & (1u << bit)) == 0) continue;
            yield return (char)('A' + bit);
        }
    }

    private void CloseWatcher(ConnectedSession session)
    {
        UsbFileWatcher? watcher;
        lock (session.SyncRoot)
        {
            watcher = session.FileWatcher;
            session.FileWatcher = null;
        }
        if (watcher is null) return;
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        catch (Exception ex) { AppLog.Error(ex, $"[WATCH] Ошибка закрытия watcher {session.Letter}:"); }
    }

    private void StartPolling(ConnectedSession session)
    {
        if (!SettingsStore.Current.UsePollingInIdle || session.DetachRequested) return;
        if (session.PollingTimer is not null) return;
        int interval = Math.Clamp(SettingsStore.Current.PollingIntervalSeconds, 5, 3600);
        session.PollingTimer = new System.Threading.Timer(_ =>
        {
            _ = PollSessionAsync(session);
        }, null, TimeSpan.FromSeconds(interval), TimeSpan.FromSeconds(interval));
        session.LastPollSignature = ComputePollSignature(session.SourceRoot);
        AppLog.Info($"[POLLING] {session.Letter}: baseline taken (root + first level)");
    }

    private void StopPolling(ConnectedSession session, out int waitMs, out bool completed)
    {
        waitMs = 0;
        completed = true;
        if (session.PollingTimer is not null)
        {
            try { session.PollingTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
            try { session.PollingTimer.Dispose(); } catch { }
            session.PollingTimer = null;
        }
        var task = session.PollingTask;
        if (task is not null)
        {
            var sw = Stopwatch.StartNew();
            try { Task.WhenAny(task, Task.Delay(500)).Wait(); }
            catch { }
            waitMs = (int)sw.ElapsedMilliseconds;
            completed = task.IsCompleted;
        }
    }

    private async Task PollSessionAsync(ConnectedSession session)
    {
        if (!SettingsStore.Current.UsePollingInIdle || session.DetachRequested) return;
        if (session.AttachmentState != DriveAttachmentState.Attached) return;
        if (Interlocked.CompareExchange(ref session.PollingInProgress, 1, 0) != 0) return;

        session.PollingTask = Task.Run(() => ComputePollSignature(session.SourceRoot));
        bool signatureChanged = false;
        try
        {
            string signature = await session.PollingTask;
            if (session.DetachRequested || !Directory.Exists(session.SourceRoot)) return;

            if (string.IsNullOrEmpty(session.LastPollSignature))
                session.LastPollSignature = signature;
            else if (signature != session.LastPollSignature)
            {
                signatureChanged = true;
                session.LastPollSignature = signature;
                session.PendingChanges = true;
                var effectiveLiveWatch = session.Profile.LiveWatchOverride ?? SettingsStore.Current.LiveWatch;
                if (effectiveLiveWatch && session.Profile.Enabled)
                {
                    if (!_syncTasks.ContainsKey(session.Letter.ToString()))
                        StartFullSync(session);
                }
                StateChanged?.Invoke();
                AppLog.Info($"[POLLING] {session.Letter}: signature changed, sync={(effectiveLiveWatch ? "started" : "pending")}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, $"[POLLING] {session.Letter} error");
        }
        finally
        {
            if (signatureChanged)
            {
                // baseline already updated above
            }
            Interlocked.Exchange(ref session.PollingInProgress, 0);
            session.PollingTask = null;
        }
    }

    /// <summary>
    /// Дешёвая «подпись» состояния корня флешки и первого уровня вложенности.
    /// Раньше здесь строилась огромная строка и считался SHA256 — на тысячах
    /// файлов это давало заметный CPU-спайк каждые 30 секунд. Теперь это один
    /// проход по каталогам и 64-битный FNV-1a хэш без промежуточных строк.
    ///
    /// Важно: mtime каталогов НЕ используется. NTFS обновляет его лениво
    /// (метка может «доехать» через несколько миллисекунд после создания
    /// вложенной папки), из-за чего подпись дёргалась без реальных изменений.
    /// Вместо этого учитываются имена вложенных папок — они меняются
    /// синхронно и дают ту же чувствительность к появлению/удалению папок.
    /// </summary>
    internal static string ComputePollSignature(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return "";

            var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            var rootInfo = new DirectoryInfo(root);

            // FNV-1a: быстрый, без аллокаций. Коллизии для детектора изменений
            // не опасны — в хэш входят имена, размеры и mtime файлов.
            ulong hash = 14695981039346656037UL;

            // FileInfo из перечисления каталога уже содержит размер и даты
            // (берутся из FindFirst/FindNext), т.е. без отдельного syscall на файл.
            foreach (var fi in rootInfo.EnumerateFiles("*", opts))
            {
                hash = FnvString(hash, fi.Name);
                hash = Fnv(hash, fi.Length);
                hash = Fnv(hash, fi.LastWriteTimeUtc.Ticks);
            }

            foreach (var di in rootInfo.EnumerateDirectories("*", opts))
            {
                hash = Fnv(hash, 0x444952L); // маркер «первый уровень»
                hash = FnvString(hash, di.Name);

                try
                {
                    foreach (var fi in di.EnumerateFiles("*", opts))
                    {
                        hash = FnvString(hash, fi.Name);
                        hash = Fnv(hash, fi.Length);
                        hash = Fnv(hash, fi.LastWriteTimeUtc.Ticks);
                    }

                    // Имена папок второго уровня: ловят создание/удаление/переименование
                    // подпапок, не завися от ленивого mtime каталога.
                    foreach (var nested in di.EnumerateDirectories("*", opts))
                    {
                        hash = Fnv(hash, 0x535542L); // маркер «вложенная папка»
                        hash = FnvString(hash, nested.Name);
                    }
                }
                catch { /* недоступный каталог — пропускаем */ }
            }

            return hash.ToString("X16");
        }
        catch
        {
            return "";
        }
    }

    private static ulong Fnv(ulong hash, long value)
    {
        unchecked
        {
            hash ^= (ulong)value;
            hash *= 1099511628211UL;
            return hash;
        }
    }

    private static ulong FnvString(ulong hash, string value)
    {
        unchecked
        {
            foreach (char ch in value)
            {
                hash ^= ch;
                hash *= 1099511628211UL;
            }
            hash ^= 0x1F; // разделитель имён, чтобы «ab»+«c» и «a»+«bc» различались
            hash *= 1099511628211UL;
            return hash;
        }
    }

    /// <summary>Флешка фактически отключена (REMOVECOMPLETE).</summary>
    public void OnDriveRemoveComplete(char letter)
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
        _detachingDrives.TryRemove(key, out _);
        StopPolling(session, out _, out _);
        string driveNameRemoved = string.IsNullOrWhiteSpace(session.Profile.Label) ? "USB" : (string.IsNullOrWhiteSpace(session.Profile.Serial) ? session.Profile.Label : $"{session.Profile.Label} [{session.Profile.Serial}]");
        ProgressChanged?.Invoke(session, new SyncProgress('\0', 0, 0, 0, 0, 0, 0, "", SyncPhase.Idle) { DriveName = driveNameRemoved });

        session.DetachRequested = false;
        try { session.Dispose(); }
        catch (Exception ex) { AppLog.Error(ex, "Ошибка освобождения сессии {letter}:"); }

        StateChanged?.Invoke();
        AppLog.Info($"Флешка отключена: {letter}:");
    }

    /// <summary>
    /// Синхронная подготовка к безопасному извлечению.
    /// </summary>
    public bool HandleDriveQueryRemove(char letter)
    {
        if (!SettingsStore.Current.HandleSafeRemoval)
        {
            AppLog.Info($"[SAFE-REMOVE] {letter}: HandleSafeRemoval=false — no-op, TRUE");
            return true;
        }

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

        var wholeSw = Stopwatch.StartNew();

        int pollMs = 0;
        bool pollCompleted = true;
        StopPolling(session, out pollMs, out pollCompleted);

        _detachingDrives.TryAdd(key, 0);
        session.DetachRequested = true;

        bool watcherTimedOut;
        int watcherMs;
        bool watcherOk = session.BeginDetachForSafeRemoval(out watcherMs, out watcherTimedOut);

        int syncMs = 0;
        bool syncTimedOut = false;
        if (_syncTasks.TryGetValue(key, out var running))
        {
            var syncSw = Stopwatch.StartNew();
            try { Task.WhenAny(running, Task.Delay(1000)).Wait(); }
            catch (Exception ex) { AppLog.Error(ex, $"[SAFE-REMOVE] {letter}: ожидание sync завершилось ошибкой"); }
            syncMs = (int)syncSw.ElapsedMilliseconds;
            syncTimedOut = !running.IsCompleted;
        }

        int totalMs = (int)wholeSw.ElapsedMilliseconds;
        bool success = pollCompleted &&
            watcherOk && !watcherTimedOut &&
            !syncTimedOut &&
            // финальная проверка: никаких незакрытых хэндлов/задач
            (session.PollingTask == null || session.PollingTask.IsCompleted) &&
            session.FileWatcher == null &&
            (!_syncTasks.ContainsKey(key) || _syncTasks[key].IsCompleted);

        AppLog.Info(
            $"[SAFE-REMOVE] {letter}: pollWait={pollMs} ms{(pollCompleted ? "" : " (TIMEOUT)")} " +
            $"watcher.Dispose={watcherMs} ms{(watcherTimedOut ? " (TIMEOUT)" : "")}, " +
            $"syncWait={syncMs} ms{(syncTimedOut ? " (TIMEOUT)" : "")}, total={totalMs} ms -> {(success ? "TRUE" : "DENY")}");

        if (!success)
            StateChanged?.Invoke();

        return success;
    }

    public void OnDriveQueryRemoveFailed(char letter)
    {
        var key = letter.ToString();
        _detachingDrives.TryRemove(key, out _);

        if (!_sessions.TryGetValue(key, out var session)) return;

        try { session.CancelDetachAndResume(); }
        catch (Exception ex) { AppLog.Error(ex, $"[SAFE-REMOVE] {letter}: CancelDetachAndResume error"); }

        if (session.AttachmentState == DriveAttachmentState.Attached)
            session.DetachRequested = false;

        if (session.AttachmentState != DriveAttachmentState.Removed && SettingsStore.Current.UsePollingInIdle)
            StartPolling(session);

        StateChanged?.Invoke();
        AppLog.Info($"[SAFE-REMOVE] {letter}: QUERYREMOVEFAILED → {(session.AttachmentState == DriveAttachmentState.Attached ? "Attached" : "Removed")}");
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
        session.PendingChanges = false;

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
                if (SettingsStore.Current.ForceLegacyWatcher && session.FileWatcher is null)
                    session.FileWatcher = new UsbFileWatcher(session.SourceRoot);

                EnsureWatcher(session);
                var cts = session.CurrentCts!;
                var result = await session.FsSync.SyncAllAsync(
                    session.SourceRoot, session.BackupPath, progress, cts.Token);

                session.State = result.Error is null ? SessionState.Idle : SessionState.Error;
                session.LastResult = result.Error ?? $"копий {result.Copied}, удалено {result.Deleted}, пропущено {result.Skipped}";
                if (result.Error is null)
                    session.PendingChanges = false;
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
                if (!SettingsStore.Current.ForceLegacyWatcher)
                    CloseWatcher(session);
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
                var wipeSw = Stopwatch.StartNew();
                progress?.Report(new SyncProgress(driveLetter, 0, totalFiles, 0, 0, 0, totalFiles, "", SyncPhase.Wipe) { DriveName = driveNameWipe });

                if (Directory.Exists(profile.BackupPath) && files.Count > 0)
                {
                    foreach (var file in files)
                    {
                        try
                        {
                            System.IO.File.Delete(file);
                            done++;

                            // Прогресс удаления не должен душить UI-поток: отчитываемся
                            // батчами (каждые 128 файлов или каждые 150 мс).
                            if (progress is not null &&
                                (done % 128 == 0 || wipeSw.ElapsedMilliseconds >= 150 || done == totalFiles))
                            {
                                progress.Report(new SyncProgress(driveLetter, done, totalFiles, 0, 0, done, totalFiles, "", SyncPhase.Wipe) { DriveName = driveNameWipe });
                                wipeSw.Restart();
                            }
                        }
                        catch (Exception ex) { AppLog.Error(ex, "Не удалось удалить файл: " + file); }
                    }

                    // Финальный отчёт после цикла (если последний батч не совпал с % 128).
                    if (progress is not null && done > 0 && done != totalFiles)
                        progress.Report(new SyncProgress(driveLetter, done, totalFiles, 0, 0, done, totalFiles, "", SyncPhase.Wipe) { DriveName = driveNameWipe });

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

    public async Task TestDriveSettingsAsync(DriveProfile profile, IProgress<SyncProgress>? progress, CancellationToken ct, bool resetOverrides = true)
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
            await RunDriveSettingsTest(profile, profile.DriveLetter, progress, ct);
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
            try { StopPolling(s, out _, out _); } catch { }
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
public enum DriveAttachmentState { Attached, Detaching, Removed }

/// <summary>Логическое «флешка сейчас в системе»: буква, профиль, watcher, сервис копирования.</summary>
public sealed class ConnectedSession : IDisposable
{
    public readonly object SyncRoot = new();
    private bool _disposed;

    public char Letter { get; }
    public DriveProfile Profile { get; }
    public UsbFileWatcher? FileWatcher { get; internal set; }
    public FileSyncService FsSync { get; }
    public CancellationTokenSource? CurrentCts { get; set; }
    public CancellationTokenSource DetachCts { get; private set; } = new();

    public SessionState State { get; set; } = SessionState.Idle;
    public DriveAttachmentState AttachmentState { get; private set; } = DriveAttachmentState.Attached;
    public string LastResult { get; set; } = "";

    public volatile bool PendingChanges;
    public volatile int PollingInProgress;
    internal string LastPollSignature { get; set; } = "";
    internal System.Threading.Tasks.Task<string>? PollingTask { get; set; }
    internal System.Threading.Timer? PollingTimer { get; set; }
    public volatile bool DetachRequested;
    public System.DateTime LastSuccessfulSyncUtc { get; set; } = System.DateTime.MinValue;
    /// <summary>Корень флешки, например "D:\".</summary>
    public string SourceRoot => $@"{Letter}:\";

    /// <summary>Папка, в которую пишется копия.</summary>
    public string BackupPath => Profile.BackupPath;

    public ConnectedSession(char letter, DriveProfile profile, FileSyncService fsSync)
    {
        Letter = letter;
        Profile = profile;
        FsSync = fsSync;
        FileWatcher = null;
    }

    public bool BeginDetachForSafeRemoval(out int watcherMs, out bool watcherTimedOut)
    {
        watcherMs = 0;
        watcherTimedOut = false;

        lock (SyncRoot)
        {
            if (_disposed) return true;
            if (AttachmentState != DriveAttachmentState.Attached) return true;
            AttachmentState = DriveAttachmentState.Detaching;
        }

        try { CurrentCts?.Cancel(); } catch { }
        try { DetachCts.Cancel(); } catch { }

        if (FileWatcher is not null)
        {
            var fw = FileWatcher;
            FileWatcher = null;
            var sw = Stopwatch.StartNew();
            try
            {
                var task = Task.Run(() => { try { fw.Dispose(); } catch { } });
                if (!task.Wait(500))
                    watcherTimedOut = true;
            }
            catch
            {
                watcherTimedOut = true;
            }
            watcherMs = (int)sw.ElapsedMilliseconds;
            if (watcherTimedOut)
                AppLog.Warn($"[SAFE-REMOVE] {Letter}: watcher.Dispose timeout ({watcherMs} ms)");
            else if (watcherMs > 300)
                AppLog.Warn($"[SAFE-REMOVE] {Letter}: watcher.Dispose slow ({watcherMs} ms)");
        }

        return !watcherTimedOut;
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

            try { CurrentCts?.Dispose(); } catch { }
            CurrentCts = null;
            try { DetachCts.Dispose(); } catch { }
            DetachCts = new CancellationTokenSource();
            FileWatcher = null;
            AttachmentState = DriveAttachmentState.Attached;
        }
    }

    public void Dispose()
    {
        lock (SyncRoot)
        {
            if (_disposed) return;
            _disposed = true;

            try { FileWatcher?.Dispose(); } catch { }
            FileWatcher = null;

            try { CurrentCts?.Cancel(); } catch { }
        try { DetachCts.Cancel(); } catch { }
            try { CurrentCts?.Dispose(); } catch { }
            CurrentCts = null;

            AttachmentState = DriveAttachmentState.Removed;
        }
    }
}
