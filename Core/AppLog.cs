using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SmartUsbBackup;

/// <summary>
/// Осмысленный лог в %LocalAppData%\SmartUsbBackup\logs\*.log.
/// AppendAllText в безопасном режиме; сам лог никогда не роняет приложение.
/// </summary>
internal static class AppLog
{
    private static readonly object gate = new();
    private const long MaxFileSizeBytes = 10L * 1024 * 1024;
    private const int MaxFiles = 30;
    private const int MaxEntryLength = 4000;

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SmartUsbBackup", "logs");

    private static string FilePath => Path.Combine(Dir,
        $"backup-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);

    public static void Error(Exception? ex, string msg)
        => Write("ERROR", msg + (ex is null ? "" : "\n" + ex));

    private static void Write(string level, string msg)
    {
        var safeMsg = Sanitize(msg);
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {safeMsg}{Environment.NewLine}";
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                RotateIfNeededLocked();
                File.AppendAllText(FilePath, line, Encoding.UTF8);
            }
            catch { /* лог не должен ронять приложение */ }
        }
    }

    // SEC: E-01 — удаление control chars и замена CRLF на литералы.
    private static string Sanitize(string msg)
    {
        if (string.IsNullOrEmpty(msg))
            return string.Empty;

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

    // SEC: E-02 — ротация текущего файла по размеру.
    private static void RotateIfNeededLocked()
    {
        try
        {
            var fi = new FileInfo(FilePath);
            if (!fi.Exists || fi.Length < MaxFileSizeBytes)
                return;

            int n = 1;
            string candidate;
            do
            {
                candidate = Path.Combine(Dir, $"backup-{DateTime.Now:yyyy-MM-dd}-{n}.log");
                n++;
            } while (File.Exists(candidate));

            File.Move(FilePath, candidate);
        }
        catch { /* не критично */ }
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
