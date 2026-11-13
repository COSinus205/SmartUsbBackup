using System;
using System.IO;
using SmartUsbBackup.Core;
using Xunit;

namespace SmartUsbBackup.Tests;

public class UpdateVersionTests
{
    [Theory]
    [InlineData("4.3.1", "4.3.1.0", 0)]
    [InlineData("4.3.1", "4.3.2", -1)]
    [InlineData("4.4.0", "4.3.9", 1)]
    [InlineData("v5.0.0", "4.9.9", 1)]
    public void Compare_NormalizesTagsAndAssemblyVersion(string left, string right, int expectedSign)
    {
        Assert.True(Version.TryParse(left.TrimStart('v', 'V'), out var l));
        Assert.True(Version.TryParse(right.TrimStart('v', 'V'), out var r));

        int cmp = UpdateService.Compare(l, r);
        Assert.Equal(expectedSign, Math.Sign(cmp));
    }

    [Fact]
    public void IsNewer_SameVersionWithDifferentRevision_IsFalse()
    {
        Assert.False(UpdateService.IsNewer(new Version(4, 3, 1, 0), new Version(4, 3, 1)));
    }

    [Fact]
    public void IsNewer_HigherRemote_IsTrue()
    {
        Assert.True(UpdateService.IsNewer("4.3.1", "4.3.2"));
        Assert.False(UpdateService.IsNewer("4.4.0", "4.3.9"));
    }
}

public class PathSafetyExtraTests
{
    [Fact]
    public void IsInsideOrEqual_TrueForEqualDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartusb_path_a");
        Assert.True(PathSafety.IsInsideOrEqual(root, root));
    }

    [Fact]
    public void IsInsideOrEqual_TrueForChild_FalseForSibling()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartusb_path_b");
        Assert.True(PathSafety.IsInsideOrEqual(root, Path.Combine(root, "child")));
        Assert.False(PathSafety.IsInsideOrEqual(root, root + "_sibling"));
    }

    [Fact]
    public void IsSameVolume_ComparesDriveRoots()
    {
        var a = Path.Combine(Path.GetTempPath(), "one");
        var b = Path.Combine(Path.GetTempPath(), "two");
        Assert.True(PathSafety.IsSameVolume(a, b));
    }

    [Fact]
    public void CanonicalizeBase_RejectsDriveRootAndWindows()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Null(PathSafety.CanonicalizeBase(root));
        Assert.Null(PathSafety.CanonicalizeBase(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.Null(PathSafety.CanonicalizeBase("relative\\path"));
    }

    [Fact]
    public void CanonicalizeBase_AcceptsUserFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "smartusb_canon_" + Guid.NewGuid().ToString("N"));
        var result = PathSafety.CanonicalizeBase(dir);
        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(dir), result);
    }
}
