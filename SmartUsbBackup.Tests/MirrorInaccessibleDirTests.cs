using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using SmartUsbBackup.Sync;
using Xunit;

namespace SmartUsbBackup.Tests;

/// <summary>
/// BUG-02: зеркальная чистка удаляла из бэкапа файлы каталогов, которые не удалось
/// прочитать при сканировании (нет доступа) — файл пропадал из бэкапа, хотя на
/// флешке он есть.
/// </summary>
public sealed class MirrorInaccessibleDirTests
{
    private static string NewTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "smartusb_mirror_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static int RunIcacls(string args)
    {
        var psi = new ProcessStartInfo("icacls", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return p.ExitCode;
    }

    [Fact]
    public async Task SyncAll_Mirror_DoesNotDeleteFilesInInaccessibleDir()
    {
        var root = NewTempRoot();
        var src = Path.Combine(root, "src");
        var dst = Path.Combine(root, "dst");
        var locked = Path.Combine(src, "locked");
        Directory.CreateDirectory(locked);
        Directory.CreateDirectory(dst);

        File.WriteAllText(Path.Combine(src, "open.txt"), "v1");
        File.WriteAllText(Path.Combine(locked, "secret.txt"), "data");

        var svc = new FileSyncService();

        await svc.SyncAllAsync(src, dst, null, default);
        var backupSecret = Path.Combine(dst, "locked", "secret.txt");
        Assert.True(File.Exists(backupSecret), "первичная копия должна быть");

        // Deny важнее allow-ов: каталог перестаёт читаться даже владельцем.
        var code = RunIcacls($"\"{locked}\" /deny \"{Environment.UserName}:(OI)(CI)(RX)\"");
        try
        {
            Assert.True(code == 0, "icacls /deny не сработал, код " + code);

            // Убеждаемся, что запрет реально действует, иначе тест ничего не проверяет.
            Assert.ThrowsAny<Exception>(() => Directory.GetFileSystemEntries(locked));

            // И что снимок фиксирует каталог как недоступный (механизм BUG-02).
            var snapshot = typeof(FileSyncService).GetMethod(
                "SnapshotFiles", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var args = new object?[] { src, null, null };
            snapshot.Invoke(svc, args);
            var inaccessible = (HashSet<string>)args[2]!;
            Assert.Contains(locked, inaccessible, StringComparer.OrdinalIgnoreCase);

            // Меняем доступный файл, чтобы прогон дошёл до зеркальной чистки.
            File.WriteAllText(Path.Combine(src, "open.txt"), "v2");
            await svc.SyncAllAsync(src, dst, null, default);

            Assert.True(File.Exists(Path.Combine(dst, "open.txt")), "доступный файл синхронизируется");
            Assert.True(File.Exists(backupSecret),
                "BUG-02: файл из недоступного каталога не должен удаляться из бэкапа");
        }
        finally
        {
            RunIcacls($"\"{locked}\" /remove:d \"{Environment.UserName}\"");
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
