using System;
using System.IO;
using SmartUsbBackup.Core;
using Xunit;

namespace SmartUsbBackup.Tests;

public sealed class BackupPresenceCacheTests
{
    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "smartusb_presence_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void MissingDirectory_NoExistsNoContent()
    {
        var cache = new BackupPresenceCache();
        var path = Path.Combine(Path.GetTempPath(), "smartusb_missing_" + Guid.NewGuid().ToString("N"));

        Assert.False(cache.Exists(path));
        Assert.False(cache.HasContent(path));
    }

    [Fact]
    public void EmptyDirectory_ExistsButNoContent()
    {
        var cache = new BackupPresenceCache();
        var dir = NewTempDir();
        try
        {
            Assert.True(cache.Exists(dir));
            Assert.False(cache.HasContent(dir));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void DirectoryWithFile_HasContent()
    {
        var cache = new BackupPresenceCache();
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "x");

            Assert.True(cache.Exists(dir));
            Assert.True(cache.HasContent(dir));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void DirectoryWithOnlySubdir_HasContent()
    {
        var cache = new BackupPresenceCache();
        var dir = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sub"));

            Assert.True(cache.HasContent(dir));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void CachedValue_NotReprobedWithinMaxAge_ButInvalidateForcesIt()
    {
        long now = 1000;
        var cache = new BackupPresenceCache(TimeSpan.FromSeconds(2), () => now);
        var dir = NewTempDir();
        try
        {
            Assert.False(cache.HasContent(dir));   // попало в кэш

            File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
            now += 500;                            // всё ещё в пределах TTL
            Assert.False(cache.HasContent(dir));   // отдаётся кэш, без диска

            cache.Invalidate(dir);
            Assert.True(cache.HasContent(dir));    // после инвалидации — реальный факт
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Entry_ExpiresAfterMaxAge()
    {
        long now = 5000;
        var cache = new BackupPresenceCache(TimeSpan.FromSeconds(2), () => now);
        var dir = NewTempDir();
        try
        {
            Assert.False(cache.HasContent(dir));

            File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
            now += 2001;                           // TTL истёк
            Assert.True(cache.HasContent(dir));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Clear_DropsAllEntries()
    {
        long now = 0;
        var cache = new BackupPresenceCache(TimeSpan.FromSeconds(30), () => now);
        var dir1 = NewTempDir();
        var dir2 = NewTempDir();
        try
        {
            Assert.False(cache.HasContent(dir1));
            Assert.False(cache.HasContent(dir2));

            File.WriteAllText(Path.Combine(dir1, "a.txt"), "x");
            File.WriteAllText(Path.Combine(dir2, "b.txt"), "x");

            Assert.False(cache.HasContent(dir1));   // из кэша

            cache.Clear();

            Assert.True(cache.HasContent(dir1));
            Assert.True(cache.HasContent(dir2));
        }
        finally
        {
            try { Directory.Delete(dir1, true); } catch { }
            try { Directory.Delete(dir2, true); } catch { }
        }
    }

    [Fact]
    public void ExistsAndHasContent_ShareOneProbe()
    {
        long now = 0;
        var cache = new BackupPresenceCache(TimeSpan.FromSeconds(30), () => now);
        var dir = NewTempDir();
        try
        {
            Assert.True(cache.Exists(dir));
            File.WriteAllText(Path.Combine(dir, "a.txt"), "x");

            // HasContent должен взять уже закэшированный ответ (файл появился позже).
            Assert.False(cache.HasContent(dir));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
