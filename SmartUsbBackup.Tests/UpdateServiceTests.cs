using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SmartUsbBackup.Core;
using Xunit;

namespace SmartUsbBackup.Tests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("SmartUsbBackup-Setup-3.0.6.exe", true)]
    [InlineData("..\\evil.exe", false)]
    [InlineData("C:\\Windows\\evil.exe", false)]
    [InlineData("foo/bar.exe", false)]
    [InlineData("SmartUsbBackup-Setup-3.0.6.exe.bak", false)]
    [InlineData("SmartUsbBackup-Setup-x.y.z.exe", false)]
    [InlineData("SmartUsbBackup-Setup-3.0.6", false)]
    public void IsExpectedAssetName(string name, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsExpectedAssetName(name));
    }

    [Fact]
    public void ResolveTargetPath_Valid_InsideUpdatesDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "updates-test");
        var path = UpdateService.ResolveTargetPath(dir, "SmartUsbBackup-Setup-3.0.6.exe");

        Assert.NotNull(path);
        Assert.StartsWith(Path.GetFullPath(dir), path, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("..\\evil.exe")]
    [InlineData("C:\\Windows\\evil.exe")]
    public void ResolveTargetPath_Invalid_ReturnsNull(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "updates-test");
        Assert.Null(UpdateService.ResolveTargetPath(dir, name));
    }

    [Fact]
    public async Task VerifySha256Async_Match_ReturnsTrue()
    {
        var file = Path.Combine(Path.GetTempPath(), "hash-test.bin");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4 });

        string expected;
        using (var sha = SHA256.Create())
        using (var s = File.OpenRead(file))
        {
            expected = Convert.ToHexString(await sha.ComputeHashAsync(s));
        }

        Assert.True(await UpdateService.VerifySha256Async(file, expected));
        File.Delete(file);
    }

    [Fact]
    public async Task VerifySha256Async_Mismatch_ReturnsFalse()
    {
        var file = Path.Combine(Path.GetTempPath(), "hash-test2.bin");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4 });

        Assert.False(await UpdateService.VerifySha256Async(file, new string('0', 64)));

        File.Delete(file);
    }

    [Fact]
    public void VerifyAuthenticode_InvalidFile_ReturnsFalse()
    {
        var file = Path.Combine(Path.GetTempPath(), "unsigned-test.exe");
        File.WriteAllBytes(file, new byte[] { 0x4D, 0x5A });

        Assert.False(UpdateService.VerifyAuthenticode(file));

        File.Delete(file);
    }

    [Theory]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/abc/SmartUsbBackup-Setup-3.0.8.exe", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/abc/SmartUsbBackup-Setup-3.0.8.exe", true)]
    [InlineData("https://github.com/COSinus205/SmartUsbBackup/releases/download/v3.0.8/SmartUsbBackup-Setup-3.0.8.exe", true)]
    [InlineData("https://github.com/someone/OtherRepo/releases/download/v1/x.exe", false)]
    [InlineData("https://evil.com/foo", false)]
    [InlineData("http://github.com/COSinus205/SmartUsbBackup/foo", false)]
    public void IsAllowedDownloadUrl_ValidateAllowlist(string url, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsAllowedDownloadUrl(url));
    }
}