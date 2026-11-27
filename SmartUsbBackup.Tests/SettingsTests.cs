using System.IO;
using System.Reflection;
using System.Text.Json;
using SmartUsbBackup.Core;
using Xunit;

namespace SmartUsbBackup.Tests;

public class SettingsTests
{
    private static AppSettings LoadFresh()
    {
        var loadMethod = typeof(SettingsStore).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(loadMethod);
        return (AppSettings)loadMethod!.Invoke(null, null)!;
    }

    [Fact]
    public void Load_DrivesNull_DoesNotLeaveNullList()
    {
        using var isolation = new SettingsIsolation();

        Directory.CreateDirectory(SettingsStore.Dir);
        File.WriteAllText(
            Path.Combine(SettingsStore.Dir, "settings.json"),
            "{ \"baseBackupPath\": " + JsonSerializer.Serialize(SettingsStore.DefaultBasePath) +
            ", \"drives\": null }");

        var loaded = LoadFresh();

        Assert.NotNull(loaded.Drives);
        Assert.Empty(loaded.Drives);
    }

    [Fact]
    public void Load_NullLabelAndSerial_AreNormalized()
    {
        using var isolation = new SettingsIsolation();

        Directory.CreateDirectory(SettingsStore.Dir);
        File.WriteAllText(
            Path.Combine(SettingsStore.Dir, "settings.json"),
            "{ \"baseBackupPath\": " + JsonSerializer.Serialize(SettingsStore.DefaultBasePath) +
            ", \"drives\": [ { \"serial\": null, \"label\": null, \"folderName\": null } ] }");

        var loaded = LoadFresh();

        var drive = Assert.Single(loaded.Drives);
        Assert.Equal("", drive.Serial);
        Assert.Equal("", drive.Label);
        Assert.False(string.IsNullOrWhiteSpace(drive.FolderName));
    }

    [Fact]
    public void Load_DrivesWithNullEntry_IsFiltered()
    {
        using var isolation = new SettingsIsolation();

        Directory.CreateDirectory(SettingsStore.Dir);
        File.WriteAllText(
            Path.Combine(SettingsStore.Dir, "settings.json"),
            "{ \"baseBackupPath\": " + JsonSerializer.Serialize(SettingsStore.DefaultBasePath) +
            ", \"drives\": [ null, { \"serial\": \"1\", \"label\": \"L\", \"folderName\": \"USB_L_1\" } ] }");

        var loaded = LoadFresh();

        var drive = Assert.Single(loaded.Drives);
        Assert.Equal("1", drive.Serial);
    }

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
