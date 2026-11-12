using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SmartUsbBackup;

/// <summary>
/// Осмысленный лог в %LocalAppData%\SmartUsbBackup\logs\*.log.
///
/// Один открытый <see cref="StreamWriter"/> на текущие сутки: раньше каждая строка
/// открывала/закрывала файл, делала CreateDirectory и FileInfo для проверки ротации —
/// на потоках ошибок и mirror-очистке это давало заметные накладные расходы.
/// Ротация теперь считается по фактически записанным байтам, без syscall на строку.
/// Сам лог никогда не роняет приложение.
/// </summary>
internal static class AppLog
{
    private static readonly object gate = new();
    private const long MaxFileSizeBytes = 10L * 1024 * 1024;
    private const int MaxFiles = 30;
    private const int MaxEntryLength = 4000;

    private static StreamWriter? _writer;
    private static DateTime _writerDate;
    private static long _bytesWritten;

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SmartUsbBackup", "logs");

    private static string PathForDate(DateTime date) => Path.Combine(Dir, $"backup-{date:yyyy-MM-dd}.log");

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);

    public static void Error(Exception? ex, string msg)
        => Write("ERROR", msg + (ex is null ? "" : "\n" + ex));

    /// <summary>Закрыть файл лога (вызывается при штатном выходе приложения).</summary>
    public static void Shutdown()
    {
        lock (gate) CloseWriterLocked();
    }

    private static void Write(string level, string msg)
    {
        var now = DateTime.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {Sanitize(msg)}{Environment.NewLine}";

        lock (gate)
        {
            try
            {
                EnsureWriterLocked(now);
                _writer!.Write(line);
                _bytesWritten += _writer.Encoding.GetByteCount(line);

                // Ротация по реальному размеру, а не по FileInfo на каждую строку.
                if (_bytesWritten >= MaxFileSizeBytes)
                    RotateLocked(now);
            }
            catch
            {
                // Лог не должен ронять приложение: writer переоткроется при следующей записи.
                CloseWriterLocked();
            }
        }
    }

    private static void EnsureWriterLocked(DateTime now)
    {
        if (_writer is not null && _writerDate == now.Date)
            return;

        CloseWriterLocked();

        Directory.CreateDirectory(Dir);

        // ReadWrite|Delete — чтобы файл можно было читать/тащить в редактор,
        // пока приложение работает (иначе открытый писатель блокирует чтение).
        var stream = new FileStream(
            PathForDate(now), FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);

        // UTF8Encoding(true) пишет BOM только в пустой файл — как и прежний AppendAllText.
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
        {
            AutoFlush = true,
        };
        _writerDate = now.Date;
        _bytesWritten = stream.Length;
    }

    private static void CloseWriterLocked()
    {
        try { _writer?.Dispose(); } catch { /* не критично */ }
        _writer = null;
        _bytesWritten = 0;
    }

    // SEC: E-02 — ротация текущего файла по размеру.
    private static void RotateLocked(DateTime now)
    {
        try
        {
            CloseWriterLocked();

            var current = PathForDate(now);
            if (File.Exists(current))
            {
                int n = 1;
                string candidate;
                do
                {
                    candidate = Path.Combine(Dir, $"backup-{now:yyyy-MM-dd}-{n}.log");
                    n++;
                } while (File.Exists(candidate));

                File.Move(current, candidate);
            }

            EnsureWriterLocked(now);
        }
        catch
        {
            CloseWriterLocked();
        }
    }

    // SEC: E-01 — удаление control chars и замена CRLF на литералы.
    private static string Sanitize(string msg)
    {
        if (string.IsNullOrEmpty(msg))
            return string.Empty;

        // Быстрый путь: в обычных сообщениях управляющих символов нет — не аллоцируем.
        int scanLength = Math.Min(msg.Length, MaxEntryLength);
        bool needsSlowPath = msg.Length > MaxEntryLength;
        for (int i = 0; i < scanLength; i++)
        {
            char ch = msg[i];
            if (ch == '\r' || ch == '\n' || ch < 0x20 || ch == 0x7F)
            {
                needsSlowPath = true;
                break;
            }
        }
        if (!needsSlowPath)
            return msg;

        var sb = new StringBuilder(msg.Length);
        foreach (var ch in msg)
        {
            if (ch == '\r') { sb.Append("\\r"); continue; }
            if (ch == '\n') { sb.Append("\\n"); continue; }
            if (ch < 0x20 || ch == 0x7F) continue;
            sb.Append(ch);
        }

        if (sb.Length > MaxEntryLength)
        {
            sb.Length = MaxEntryLength;
            sb.Append("...[truncated]");
        }
        return sb.ToString();
    }

    /// <summary>Удаляет логи старше 14 дней и лишние файлы (больше 30).</summary>
    public static void CleanupOld() => ThreadPool.QueueUserWorkItem(_ =>
    {
        lock (gate)
        {
            try
            {
                if (!Directory.Exists(Dir))
                    return;

                var cutoff = DateTime.UtcNow.AddDays(-14);
                var files = Directory.EnumerateFiles(Dir, "backup-*.log")
                    .Select(f => new FileInfo(f))
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ToList();

                foreach (var f in files.Where(f => f.LastWriteTimeUtc < cutoff).ToList())
                {
                    try { f.Delete(); files.Remove(f); } catch { }
                }

                while (files.Count > MaxFiles)
                {
                    try { files[0].Delete(); } catch { }
                    files.RemoveAt(0);
                }
            }
            catch { }
        }
    });
}
