using System.IO;
using SmartUsbBackup.Core;
using Xunit;

namespace SmartUsbBackup.Tests;

public class PathSafetyTests
{
    [Theory]
    [InlineData(@"C:\Backups", @"C:\Backups\USB_1234", true)]
    [InlineData(@"C:\Backups", @"C:\Backups", false)]
    [InlineData(@"C:\Backups", @"C:\Backups\..\Windows", false)]
    [InlineData(@"C:\Backups", @"C:\Backups2\x", false)]
    [InlineData(@"C:\Backups", @"C:\Backups\USB_1\..\..\Windows", false)]
    public void IsSafeSubPath_Cases(string basePath, string candidate, bool expected)
    {
        Assert.Equal(expected, PathSafety.IsSafeSubPath(basePath, candidate));
    }

    [Theory]
    [InlineData(@"C:\", null)]
    [InlineData(@"C:\Windows", null)]
    [InlineData(@"C:\Users\Test", null)]
    [InlineData(@"C:\Users\Test\Backups", @"C:\Users\Test\Backups")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("Backups", null)]
    public void CanonicalizeBase_Cases(string? path, string? expected)
    {
        Assert.Equal(expected, PathSafety.CanonicalizeBase(path));
    }

    [Theory]
    [InlineData("CON", true)]
    [InlineData("con.txt", true)]
    [InlineData("COM1", true)]
    [InlineData("LPT9", true)]
    [InlineData("CONSOLE", false)]
    [InlineData("COM10", false)]
    [InlineData("name.", true)]
    [InlineData("name ", true)]
    public void IsReservedWindowsName_Cases(string name, bool expected)
    {
        Assert.Equal(expected, PathSafety.IsReservedWindowsName(name));
    }
}