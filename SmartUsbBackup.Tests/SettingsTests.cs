using System.IO;
using System.Reflection;
using System.Text.Json;
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
        var original = SettingsStore.Current.AutoCheckForUpdates;
        try
        {
            SettingsStore.Current.AutoCheckForUpdates = false;
            SettingsStore.Save();

            var loadMethod = typeof(SettingsStore).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(loadMethod);
            var loaded = (AppSettings)loadMethod!.Invoke(null, null)!;

            Assert.False(loaded.AutoCheckForUpdates);
        }
        finally
        {
            SettingsStore.Current.AutoCheckForUpdates = original;
            SettingsStore.Save();
        }
    }
}