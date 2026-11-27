using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;
using SmartUsbBackup.Core;
using SmartUsbBackup.Sync;
using Xunit;

namespace SmartUsbBackup.Tests;

/// <summary>
/// Защита от копирования бэкапа внутрь него самого: папка хранения на том же
/// томе, что и флешка (например, после переиспользования буквы диска).
/// </summary>
public sealed class BackupOnSourceTests
{
    private static DriveProfile NewProfile(string backupOverride) => new()
    {
        Label = "T",
        Serial = "TEST-1",
        FolderName = "USB_T_TEST1",
        Enabled = true,
        BaseBackupPathOverride = backupOverride,
    };

    private static ConcurrentDictionary<string, ConnectedSession> SessionsOf(BackupOrchestrator orchestrator)
    {
        var field = typeof(BackupOrchestrator)
            .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (ConcurrentDictionary<string, ConnectedSession>)field.GetValue(orchestrator)!;
    }

    private static ConcurrentDictionary<string, Task> SyncTasksOf(BackupOrchestrator orchestrator)
    {
        var field = typeof(BackupOrchestrator)
            .GetField("_syncTasks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (ConcurrentDictionary<string, Task>)field.GetValue(orchestrator)!;
    }

    [Fact]
    public void IsBackupOnSource_SameVolume_IsTrue()
    {
        // SourceRoot = "Q:\", бэкап на Q:\ — та же буква.
        var session = new ConnectedSession('Q', NewProfile(@"Q:\Backups"), new FileSyncService());

        Assert.True(BackupOrchestrator.IsBackupOnSource(session));
    }

    [Fact]
    public void IsBackupOnSource_DifferentVolume_IsFalse()
    {
        var session = new ConnectedSession('Q', NewProfile(@"C:\Backups"), new FileSyncService());

        Assert.False(BackupOrchestrator.IsBackupOnSource(session));
    }

    [Fact]
    public void SyncOne_BackupOnSource_DoesNotStartTask()
    {
        var orchestrator = new BackupOrchestrator();
        try
        {
            var profile = NewProfile(@"Q:\Backups");
            var session = new ConnectedSession('Q', profile, new FileSyncService());
            SessionsOf(orchestrator).TryAdd("Q", session);

            orchestrator.SyncOne(profile);

            Assert.Empty(SyncTasksOf(orchestrator));
            Assert.Equal(SessionState.Error, session.State);
            Assert.Contains("этой же флешке", session.LastResult);
        }
        finally
        {
            orchestrator.Dispose();
        }
    }
}
