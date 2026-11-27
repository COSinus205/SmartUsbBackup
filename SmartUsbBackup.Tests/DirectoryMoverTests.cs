using System;
using System.IO;
using SmartUsbBackup.Core;
using Xunit;

namespace SmartUsbBackup.Tests;

/// <summary>
/// Перенос папки бэкапа не должен позволять «перенести папку в саму себя»:
/// иначе fallback-копирование удаляло бы исходник вместе с вложенной копией.
/// </summary>
public sealed class DirectoryMoverTests
{
    [Fact]
    public void Move_IntoSelf_ThrowsAndKeepsSource()
    {
        var root = TempRoot();
        var src = Path.Combine(root, "USB_X");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "important.txt"), "data");
        var nested = Path.Combine(src, "USB_X");

        try
        {
            Assert.Throws<InvalidOperationException>(() => DirectoryMover.Move(src, nested));

            Assert.True(File.Exists(Path.Combine(src, "important.txt")));
            Assert.False(Directory.Exists(nested));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Move_SameVolume_MovesWholeTree()
    {
        var root = TempRoot();
        var src = Path.Combine(root, "USB_X");
        var dst = Path.Combine(root, "new", "USB_X");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "a.txt"), "a");
        File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "b");

        try
        {
            DirectoryMover.Move(src, dst);

            Assert.False(Directory.Exists(src));
            Assert.Equal("a", File.ReadAllText(Path.Combine(dst, "a.txt")));
            Assert.Equal("b", File.ReadAllText(Path.Combine(dst, "sub", "b.txt")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Move_ToSibling_IsAllowed()
    {
        var root = TempRoot();
        var src = Path.Combine(root, "USB_X");
        var dst = Path.Combine(root, "USB_Y");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "a");

        try
        {
            DirectoryMover.Move(src, dst);

            Assert.False(Directory.Exists(src));
            Assert.True(File.Exists(Path.Combine(dst, "a.txt")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "smartusb_move_" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
