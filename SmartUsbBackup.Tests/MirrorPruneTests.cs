using System;
using System.IO;
using System.Threading.Tasks;
using SmartUsbBackup.Sync;
using Xunit;

namespace SmartUsbBackup.Tests;

public sealed class MirrorPruneTests
{
    private static string NewTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "smartusb_prune_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task SyncAll_Mirror_DeletesVisibleOrphan_ButKeepsHiddenFiles()
    {
        var root = NewTempRoot();
        var src = Path.Combine(root, "src");
        var dst = Path.Combine(root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        var visible = Path.Combine(dst, "orphan.txt");
        var hidden = Path.Combine(dst, "hidden.txt");
        File.WriteAllText(visible, "old");
        File.WriteAllText(hidden, "keep");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        // Источник не пуст: иначе срабатывает защита от «пустого снимка»
        // (см. SyncAll_EmptySource_DoesNotDeleteBackup).
        File.WriteAllText(Path.Combine(src, "keep-me.txt"), "new");

        try
        {
            var result = await new FileSyncService().SyncAllAsync(src, dst, null, default);

            Assert.Null(result.Error);
            Assert.False(File.Exists(visible), "видимый файл-сирота должен быть удалён зеркалом");
            Assert.True(File.Exists(hidden), "скрытый файл бэкапа не должен удаляться");
            Assert.True(File.Exists(Path.Combine(dst, "keep-me.txt")));

            var attrs = File.GetAttributes(hidden);
            Assert.True((attrs & FileAttributes.Hidden) != 0);
        }
        finally
        {
            try { File.SetAttributes(hidden, FileAttributes.Normal); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SyncAll_EmptySource_DoesNotDeleteBackup()
    {
        var root = NewTempRoot();
        var src = Path.Combine(root, "src");
        var dst = Path.Combine(root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        var keep = Path.Combine(dst, "keep.txt");
        File.WriteAllText(keep, "backup");

        try
        {
            var result = await new FileSyncService().SyncAllAsync(src, dst, null, default);

            Assert.NotNull(result.Error);
            Assert.Contains("чистка пропущена", result.Error);
            Assert.True(File.Exists(keep), "пустой снимок не должен стирать бэкап");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
