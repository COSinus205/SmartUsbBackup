using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    // =====================================================================
    //  Реакция на системные события
    // =====================================================================

    /// <summary>Флешка подключилась.</summary>
    public void OnDriveArrived(UsbDriveEventArgs e)
    {
        // Профиль по серийному номеру (никогда не по букве диска).
        var profile = SettingsStore.GetOrCreate(e.Serial, e.VolumeLabel);
        var session = new ConnectedSession(e.DriveLetter, profile, new FileSyncService(
            SettingsStore.Current.MirrorMode,
            SettingsStore.Current.VerifyByCrc,
            SettingsStore.Current.SkipHiddenSystem));

        session.FileWatcher.FileChanged += path => OnFileChanged(session, path);
        session.FileWatcher.FileDeleted += path => OnFileDeleted(session, path);
        _sessions[$"{e.DriveLetter}"] = session;

        StateChanged?.Invoke();

        // Полная зеркальная синхронизация при подключении (только если профиль включён).
        if (profile.Enabled) StartFullSync(session);
        else AppLog.Info($"Флешка «{profile.Label}» подключена, но её резервирование выключено");
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

        // Анти-дубль: если полный прогон уже идёт — не стартуем второй.
        lock (_fullSyncs)
        {
            if (_fullSyncs.ContainsKey(key)) return;
            var cts = new CancellationTokenSource();
            session.CurrentCts = cts;
            _fullSyncs[key] = cts;
        }

        session.State = SessionState.Syncing;

        // IProgress<SyncProgress> маршалит вызовы в UI-поток (SynchronizationContext).
        var progress = new Progress<SyncProgress>(p => ProgressChanged?.Invoke(session, p));

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
            }
            catch (OperationCanceledException)
            {
                session.State = SessionState.Idle;
                session.LastResult = "отменено";
            }
            catch (Exception ex)
            {
                session.State = SessionState.Error;
                session.LastResult = "ошибка: " + ex.Message;
                AppLog.Error(ex, $"Полная синхронизация {session.SourceRoot} сорвалась");
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
        if (!session.Profile.Enabled) return;
        await session.FsSync.SyncSingleFileAsync(fullPath, session.SourceRoot, session.BackupPath);
    }

    private void OnFileDeleted(ConnectedSession session, string fullPath)
    {
        if (!session.Profile.Enabled) return;
        session.FsSync.DeleteFromBackup(fullPath, session.SourceRoot, session.BackupPath);
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
        foreach (var letter in _sessions.Keys)
        {
            if (_fullSyncs.TryRemove(letter, out var cts)) { cts.Cancel(); cts.Dispose(); }
        }
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();

        try
        {
            if (Directory.Exists(SettingsStore.Current.BaseBackupPath))
                Directory.Delete(SettingsStore.Current.BaseBackupPath, recursive: true);
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
    public async Task WipeOneAsync(DriveProfile profile)
    {
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

        try
        {
            if (Directory.Exists(profile.BackupPath))
                Directory.Delete(profile.BackupPath, recursive: true);
            AppLog.Info($"Бэкап флешки удалён: {profile.BackupPath}");
        }
        catch (Exception ex) { AppLog.Error(ex, "Не удалось удалить папку бэкапа"); }

        profile.Enabled = false;
        SettingsStore.Save();
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        // Отменяем все активные копирования: при выходе из трея ни один поток
        // не должен продолжать писать на диск.
        foreach (var cts in _fullSyncs.Values)
        {
            try { cts.Cancel(); } catch { }
        }
        _fullSyncs.Clear();
        _syncTasks.Clear();

        foreach (var s in _sessions.Values) s.Dispose();
        _sessions.Clear();
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
    public string BackupPath => Path.Combine(SettingsStore.Current.BaseBackupPath, Profile.FolderName);

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
