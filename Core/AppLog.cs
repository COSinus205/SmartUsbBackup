using System;
using System.IO;
using System.Text;
using System.Threading;

namespace SmartUsbBackup;

/// <summary>
/// Осмысленный лог в %LocalAppData%\SmartUsbBackup\logs\*.log.
/// AppendAllText в безопасном режиме; сам лог никогда не роняет приложение.
/// Пространство имён — корневое SmartUsbBackup, чтобы классы из .Core/.Sync/.Usb
/// видели его без using (как в исходной WinForms-версии).
/// </summary>
internal static class AppLog
{
    private static readonly object gate = new();

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
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}";
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(FilePath, line, Encoding.UTF8);
            }
            catch { /* лог не должен ронять приложение */ }
        }
    }

    /// <summary>Удаляет логи старше 14 дней — фоновый порядок, не нагрузка.</summary>
    public static void CleanupOld() => ThreadPool.QueueUserWorkItem(_ =>
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Dir, "backup-*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14))
                    File.Delete(f);
        }
        catch { }
    });
}
