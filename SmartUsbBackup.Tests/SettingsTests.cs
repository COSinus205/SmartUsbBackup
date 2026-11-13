using System.IO;
using System.Reflection;
using SmartUsbBackup.Core;
using Xunit;

namespace SmartUsbBackup.Tests;

public class SettingsTests
{
    [Fact]
    public void AutoCheckForUpdates_DefaultsToTrue()
    {
        Assert.True(new AppSettings().AutoCheckForUpdates);
    }

    [Fact]
    public void AutoCheckForUpdates_SaveAndLoad_RoundTrips()
    {
        // Тест не должен трогать настоящие настройки пользователя.
        using var isolation = new SettingsIsolation();

        var original = SettingsStore.Current.AutoCheckForUpdates;
        try
        {
            SettingsStore.Current.AutoCheckForUpdates = !original;
            SettingsStore.Save();

            var loadMethod = typeof(SettingsStore).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(loadMethod);
            var loaded = (AppSettings)loadMethod!.Invoke(null, null)!;

            Assert.Equal(!original, loaded.AutoCheckForUpdates);
        }
        finally
        {
            SettingsStore.Current.AutoCheckForUpdates = original;
            SettingsStore.Save();
        }
    }

    [Fact]
    public void BackupPath_UnsafeOverride_FallsBackToGlobalBase()
    {
        using var isolation = new SettingsIsolation();

        var globalBase = SettingsStore.Current.BaseBackupPath;
        var profile = new DriveProfile
        {
            Label = "T",
            Serial = "1234",
            FolderName = "USB_T_1234",
            BaseBackupPathOverride = @"C:\Windows",
        };

        var expected = Path.Combine(globalBase, profile.FolderName);
        Assert.Equal(expected, profile.BackupPath);
    }

    [Fact]
    public void BackupPath_SafeOverride_IsUsed()
    {
        using var isolation = new SettingsIsolation();

        var customBase = Path.Combine(Path.GetTempPath(), "smartusb_override_" + System.Guid.NewGuid().ToString("N"));
        var profile = new DriveProfile
        {
            Label = "T",
            Serial = "5678",
            FolderName = "USB_T_5678",
            BaseBackupPathOverride = customBase,
        };

        Assert.Equal(Path.Combine(customBase, profile.FolderName), profile.BackupPath);
    }
}
