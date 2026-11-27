using System.Threading.Tasks;
using SmartUsbBackup.Core;
using SmartUsbBackup.Sync;
using SmartUsbBackup.Usb;
using Xunit;

namespace SmartUsbBackup.Tests;

public sealed class OrchestratorSessionTests
{
    [Fact]
    public async Task ReplugSameLetter_DelayedRemoval_DoesNotDropNewSession()
    {
        using var isolation = new SettingsIsolation();
        int before = SettingsStore.Current.Drives.Count;
        var orchestrator = new BackupOrchestrator();

        try
        {
            await orchestrator.OnDriveArrivedAsync(new UsbDriveEventArgs('Q', "Old", "AAAA-1111"));
            var second = await orchestrator.OnDriveArrivedAsync(new UsbDriveEventArgs('Q', "New", "BBBB-2222"));

            // REMOVECOMPLETE старой флешки приходит уже после подключения новой.
            orchestrator.OnDriveRemoveComplete('Q');

            var session = Assert.Single(orchestrator.ActiveSessions);
            Assert.Same(second, session.Profile);
        }
        finally
        {
            orchestrator.Dispose();
            RestoreDrives(before);
        }
    }

    [Fact]
    public async Task DuplicateArrival_SameSerial_RemovalStillWorks()
    {
        using var isolation = new SettingsIsolation();
        int before = SettingsStore.Current.Drives.Count;
        var orchestrator = new BackupOrchestrator();

        try
        {
            await orchestrator.OnDriveArrivedAsync(new UsbDriveEventArgs('Q', "Same", "CCCC-3333"));
            await orchestrator.OnDriveArrivedAsync(new UsbDriveEventArgs('Q', "Same", "CCCC-3333"));

            // Это не замена другой флешкой: REMOVECOMPLETE должен снять сессию.
            orchestrator.OnDriveRemoveComplete('Q');

            Assert.Empty(orchestrator.ActiveSessions);
        }
        finally
        {
            orchestrator.Dispose();
            RestoreDrives(before);
        }
    }

    private static void RestoreDrives(int count)
    {
        var drives = SettingsStore.Current.Drives;
        if (drives.Count > count)
            drives.RemoveRange(count, drives.Count - count);
    }
}
