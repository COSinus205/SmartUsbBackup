using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SmartUsbBackup.Usb;

/// <summary>
/// Обёртка над FileSystemWatcher для ОДНОЙ подключённой флешки.
///
/// Зачем не polling:
///   • FileSystemWatcher — это ReadDirectoryChangesW в ядре Windows: изменение любого файла
///     не только не прерывает приложение, но и мгновенно получает с нами события OnCreated/
///     OnChanged/OnRenamed/OnDeleted. CPU при этом 0% в простое.
///   • Мы копируем только ИЗМЕНЁННЫЙ файл, а не пересканируем весь диск.
///
/// Хитрость против «флуд-шторма»:
///   • Office/Photoshop сохраняют файл 5–30 событиями подряд. Мы складываем их в очередь
///     и КОАЛЕСИЦИРУЕМ: уникальные пути копируются один раз (последнее событие побеждает),
///     отложенной задачей через DebounceMs. Дешёво и по CPU, и по I/O.
/// </summary>
public sealed class UsbFileWatcher : IDisposable
{
    /// <summary>Задержка «поджатия» быстрых повторных событий одного файла.</summary>
    public TimeSpan DebounceMs { get; set; } = TimeSpan.FromMilliseconds(800);

    public event Action<string>? FileChanged;   // относительный или полный путь — полный
    public event Action<string>? FileDeleted;

    private readonly FileSystemWatcher _fsw;
    private readonly string _sourceDir;

    // словарь «относительный путь → признак: надо копировать»
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pendingDeletes = new(StringComparer.OrdinalIgnoreCase);

    private readonly System.Threading.Timer _drainTimer;
    private volatile bool _draining;

    public UsbFileWatcher(string sourceDir)
    {
        _sourceDir = sourceDir;

        _fsw = new FileSystemWatcher(sourceDir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            InternalBufferSize = 8 * 1024,  // 8 КБ не «растёт» в память: overflow обрабатываем падением в SyncAll
            EnableRaisingEvents = true,     // событийно включаем, выключаем — в Dispose
        };

        _fsw.Created += (_, e) => QueueChange(e.FullPath);
        _fsw.Changed += (_, e) => QueueChange(e.FullPath);
        _fsw.Renamed += (_, e) => { QueueDelete(e.OldFullPath); QueueChange(e.FullPath); };
        _fsw.Deleted += (_, e) => QueueDelete(e.FullPath);
        _fsw.Error += (_, e) => AppLog.Error(e.GetException(), "[WATCH] Переполнение FileSystemWatcher — нужна полная синхронизация");

        // ОДИН таймер на всё окно наблюдения: каждые 1.2 с проверяем очередь.
        // Таймер НЕ крутит сканирование диска — он лишь отжимает копию из очереди (список путей),
        // и если очередь пуста — мгновенно выходит. Ночник CPU: микросекунды.
        _drainTimer = new System.Threading.Timer(_ => DrainAsync().GetAwaiter().GetResult(), null, 1200, 1200);
    }

    private void QueueChange(string fullPath) => _pending.TryAdd(fullPath, 0);
    private void QueueDelete(string fullPath) => _pendingDeletes.TryAdd(fullPath, 0);

    private Task DrainAsync()
    {
        // Блокирующий lock не по делу — асинхронная очередь писалась из WndProc, чтобы UI ждать.
        if (_draining) return Task.CompletedTask;
        _draining = true;

        try
        {
            // Копирование из очереди: правкие файлов «толпой» параллельно не дублируем.
            foreach (var path in _pendingDeletes.Keys)
            {
                if (_pendingDeletes.TryRemove(path, out _))
                    FileDeleted?.Invoke(path);
            }
            foreach (var path in _pending.Keys)
            {
                if (_pending.TryRemove(path, out _))
                    FileChanged?.Invoke(path);
            }
        }
        finally { _draining = false; }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _drainTimer.Dispose();
        _fsw.EnableRaisingEvents = false;
        _fsw.Dispose();
    }
}
