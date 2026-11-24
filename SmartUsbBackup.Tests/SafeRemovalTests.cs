using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SmartUsbBackup.Core;
using SmartUsbBackup.Sync;
using Xunit;

namespace SmartUsbBackup.Tests;

public sealed class SafeRemovalStateTests
{
    private static ConnectedSession NewSession(char letter = 'C', DriveProfile? profile = null)
    {
        profile ??= new DriveProfile
        {
            Label = "T",
            Serial = "1234",
            FolderName = "USB_T_1234",
            Enabled = false,
            DriveLetter = letter,
        };
        return new ConnectedSession(letter, profile, new FileSyncService());
    }

    private static ConcurrentDictionary<string, ConnectedSession> SessionsOf(BackupOrchestrator orchestrator)
    {
        var field = typeof(BackupOrchestrator).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (ConcurrentDictionary<string, ConnectedSession>)field.GetValue(orchestrator)!;
    }

    private static ConcurrentDictionary<string, Task> SyncTasksOf(BackupOrchestrator orchestrator)
    {
        var field = typeof(BackupOrchestrator).GetField("_syncTasks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (ConcurrentDictionary<string, Task>)field.GetValue(orchestrator)!;
    }

    [Fact]
    public void BeginDetach_SetsDetaching()
    {
        var session = NewSession();

        session.BeginDetach();

        Assert.Equal(DriveAttachmentState.Detaching, session.AttachmentState);
    }

    [Fact]
    public void BeginDetach_Idempotent()
    {
        var session = NewSession();

        session.BeginDetach();
        session.BeginDetach();

        Assert.Equal(DriveAttachmentState.Detaching, session.AttachmentState);
    }

    [Fact]
    public void CancelDetach_FromDetaching_RestoresAttached()
    {
        var session = NewSession();

        session.BeginDetach();
        Assert.Equal(DriveAttachmentState.Detaching, session.AttachmentState);
        session.CancelDetachAndResume();

        Assert.Equal(DriveAttachmentState.Attached, session.AttachmentState);
    }

    [Fact]
    public void CancelDetach_FromAttached_IsNoOp()
    {
        var session = NewSession();

        session.CancelDetachAndResume();

        Assert.Equal(DriveAttachmentState.Attached, session.AttachmentState);
    }

    [Fact]
    public void Dispose_Repeated_Safe()
    {
        var session = NewSession();

        session.Dispose();
        session.Dispose();

        Assert.Equal(DriveAttachmentState.Removed, session.AttachmentState);
    }

    [Fact]
    public void Dispose_FromDetaching_SetsRemoved()
    {
        var session = NewSession();

        session.BeginDetach();
        session.Dispose();

        Assert.Equal(DriveAttachmentState.Removed, session.AttachmentState);
    }

    [Fact]
    public void Detaching_RepeatQueryRemove_ReturnsTrue()
    {
        var session = NewSession();
        var orchestrator = new BackupOrchestrator();
        SessionsOf(orchestrator).TryAdd(session.Letter.ToString(), session);

        session.BeginDetach();
        bool ok = orchestrator.HandleDriveQueryRemove(session.Letter);

        Assert.True(ok);
        Assert.Equal(DriveAttachmentState.Detaching, session.AttachmentState);
    }

    [Fact]
    public void ForeignDisk_QueryRemove_ReturnsTrueAndDoesNotTouchSessions()
    {
        var orchestrator = new BackupOrchestrator();
        var sessions = SessionsOf(orchestrator);

        bool ok = orchestrator.HandleDriveQueryRemove('Z');

        Assert.True(ok);
        Assert.Empty(sessions);
    }

    [Fact]
    public void QueryRemove_DuringSync_WaitsAndReturnsTrue()
    {
        var orchestrator = new BackupOrchestrator();
        var session = NewSession();
        SessionsOf(orchestrator).TryAdd(session.Letter.ToString(), session);
        SyncTasksOf(orchestrator).TryAdd(session.Letter.ToString(), Task.Delay(50));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool ok = orchestrator.HandleDriveQueryRemove(session.Letter);
        sw.Stop();

        Assert.True(ok);
        Assert.True(sw.ElapsedMilliseconds < 1500);
        Assert.Equal(DriveAttachmentState.Detaching, session.AttachmentState);
    }

    [Fact]
    public void QueryRemove_DuringSync_Timeout_ReturnsDeny()
    {
        var orchestrator = new BackupOrchestrator();
        var session = NewSession();
        SessionsOf(orchestrator).TryAdd(session.Letter.ToString(), session);
        SyncTasksOf(orchestrator).TryAdd(session.Letter.ToString(), new TaskCompletionSource<object>().Task);

        bool ok = orchestrator.HandleDriveQueryRemove(session.Letter);

        Assert.False(ok);
    }

    [Fact]
    public void DetachRequested_ClearedOnlyAfterCancelDetachAndResumeOrRemoveComplete()
    {
        var session = NewSession();
        var orchestrator = new BackupOrchestrator();
        SessionsOf(orchestrator).TryAdd(session.Letter.ToString(), session);

        var first = orchestrator.HandleDriveQueryRemove(session.Letter);
        Assert.True(first);
        Assert.True(session.DetachRequested);

        orchestrator.OnDriveQueryRemoveFailed(session.Letter);
        Assert.False(session.DetachRequested);

        var second = orchestrator.HandleDriveQueryRemove(session.Letter);
        Assert.True(second);
        Assert.True(session.DetachRequested);
        orchestrator.OnDriveRemoveComplete(session.Letter);
        Assert.False(session.DetachRequested);
    }

    [Fact]
    public async Task SyncAllAsync_Cancellation_DoesNotHang()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartusb_test_" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var dst = Path.Combine(root, "dst");
        Directory.CreateDirectory(src);
        File.WriteAllBytes(Path.Combine(src, "big.bin"), new byte[2 * 1024 * 1024]);
        Directory.CreateDirectory(dst);

        try
        {
            using var cts = new CancellationTokenSource();
            var svc = new FileSyncService();
            var task = svc.SyncAllAsync(src, dst, null, cts.Token);
            cts.Cancel();

            var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(task, completed);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
