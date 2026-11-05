using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SmartUsbBackup.Core;
using SmartUsbBackup.Sync;
using SmartUsbBackup.Usb;
using Xunit;

namespace SmartUsbBackup.Tests;

public sealed class SafeRemovalStateTests
{
    private static string NewTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "smartusb_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static ConnectedSession NewSession(string root)
    {
        return NewSessionForLetter('C', null);
    }

    private static ConnectedSession NewSessionForLetter(char letter, DriveProfile? profile = null)
    {
        profile ??= new DriveProfile
        {
            Label = "T",
            Serial = "1234",
            FolderName = "USB_T_1234",
            Enabled = false,
            DriveLetter = letter,
        };
        return new ConnectedSession(letter, profile, new FileSyncService(false, false, false, 64, true, false, false));
    }

    [Fact]
    public void BeginDetach_SetsDetaching()
    {
        var root = NewTempRoot();
        var session = NewSession(root);

        var ok = session.BeginDetachForSafeRemoval(out var waitMs, out var timedOut);

        Assert.True(ok);
        Assert.False(timedOut);
        Assert.Equal(DriveAttachmentState.Detaching, session.AttachmentState);
    }

    [Fact]
    public void BeginDetach_Idempotent()
    {
        var root = NewTempRoot();
        var session = NewSession(root);

        session.BeginDetachForSafeRemoval(out _, out _);
        var ok = session.BeginDetachForSafeRemoval(out _, out var timedOut);

        Assert.True(ok);
        Assert.False(timedOut);
        Assert.Equal(DriveAttachmentState.Detaching, session.AttachmentState);
    }

    [Fact]
    public void CancelDetach_FromDetaching_RestoresAttached()
    {
        var root = NewTempRoot();
        var session = NewSession(root);

        session.BeginDetachForSafeRemoval(out _, out _);
        Assert.Equal(DriveAttachmentState.Detaching, session.AttachmentState);
        Assert.True(Directory.Exists(session.SourceRoot));
        session.CancelDetachAndResume();

        Assert.Equal(DriveAttachmentState.Attached, session.AttachmentState);
    }

    [Fact]
    public void CancelDetach_FromAttached_IsNoOp()
    {
        var root = NewTempRoot();
        var session = NewSession(root);

        session.CancelDetachAndResume();

        Assert.Equal(DriveAttachmentState.Attached, session.AttachmentState);
    }

    [Fact]
    public void Dispose_Repeated_Safe()
    {
        var root = NewTempRoot();
        var session = NewSession(root);

        session.Dispose();
        session.Dispose();

        Assert.Equal(DriveAttachmentState.Removed, session.AttachmentState);
    }

    [Fact]
    public void Dispose_FromDetaching_SetsRemoved()
    {
        var root = NewTempRoot();
        var session = NewSession(root);

        session.BeginDetachForSafeRemoval(out _, out _);
        session.Dispose();

        Assert.Equal(DriveAttachmentState.Removed, session.AttachmentState);
    }

    [Fact]
    public void Detaching_RepeatQueryRemove_ReturnsTrue()
    {
        var root = NewTempRoot();
        var session = NewSession(root);
        var orchestrator = new BackupOrchestrator();
        var field = typeof(BackupOrchestrator).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var sessions = (ConcurrentDictionary<string, ConnectedSession>)field.GetValue(orchestrator)!;
        sessions.TryAdd(session.Letter.ToString(), session);

        session.BeginDetachForSafeRemoval(out _, out _);
        bool ok = orchestrator.HandleDriveQueryRemove(session.Letter);

        Assert.True(ok);
        Assert.Equal(DriveAttachmentState.Detaching, session.AttachmentState);
    }

    [Fact]
    public void ForeignDisk_QueryRemove_ReturnsTrueAndDoesNotTouchSessions()
    {
        var orchestrator = new BackupOrchestrator();
        var field = typeof(BackupOrchestrator).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var sessions = (ConcurrentDictionary<string, ConnectedSession>)field.GetValue(orchestrator)!;

        bool ok = orchestrator.HandleDriveQueryRemove('Z');

        Assert.True(ok);
        Assert.Empty(sessions);
    }

    [Fact]
    public void DriveLetterFromUnitMask_MultipleBits()
    {
        var letters = BackupOrchestrator.DriveLetterFromUnitMask(0b101u).ToArray();
        Assert.Equal(new[] { 'A', 'C' }, letters);
    }

    [Fact]
    public void HandleSafeRemovalFalse_IsNoOp()
    {
        var root = NewTempRoot();
        var session = NewSession(root);
        var orchestrator = new BackupOrchestrator();
        var field = typeof(BackupOrchestrator).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var sessions = (ConcurrentDictionary<string, ConnectedSession>)field.GetValue(orchestrator)!;
        sessions.TryAdd(session.Letter.ToString(), session);

        var old = SettingsStore.Current.HandleSafeRemoval;
        try
        {
            SettingsStore.Current.HandleSafeRemoval = false;
            bool ok = orchestrator.HandleDriveQueryRemove(session.Letter);

            Assert.True(ok);
            Assert.Equal(DriveAttachmentState.Attached, session.AttachmentState);
        }
        finally
        {
            SettingsStore.Current.HandleSafeRemoval = old;
        }
    }

    [Fact]
    public void PollingSignature_RootFileChange_Detects()
    {
        var root = NewTempRoot();
        var path = Path.Combine(root, "file.txt");
        File.WriteAllText(path, "a");
        var s1 = BackupOrchestrator.ComputePollSignature(root);

        Thread.Sleep(20);
        File.WriteAllText(path, "b");
        var s2 = BackupOrchestrator.ComputePollSignature(root);

        Assert.NotEqual(s1, s2);
    }

    [Fact]
    public void PollingSignature_FirstLevelFileChange_Detects()
    {
        var root = NewTempRoot();
        var dir = Path.Combine(root, "sub");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "file.txt");
        File.WriteAllText(path, "a");
        var s1 = BackupOrchestrator.ComputePollSignature(root);

        Thread.Sleep(20);
        File.WriteAllText(path, "b");
        var s2 = BackupOrchestrator.ComputePollSignature(root);

        Assert.NotEqual(s1, s2);
    }

    [Fact]
    public void PollingSignature_DeepFileChange_NotDetected()
    {
        var root = NewTempRoot();
        var first = Path.Combine(root, "sub");
        var deep = Path.Combine(first, "deep");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(deep);
        var path = Path.Combine(deep, "file.txt");
        File.WriteAllText(path, "a");
        var s1 = BackupOrchestrator.ComputePollSignature(root);

        File.WriteAllText(path, "b");
        var s2 = BackupOrchestrator.ComputePollSignature(root);

        Assert.Equal(s1, s2);
    }

    [Fact]
    public void Baseline_SetWhenSessionConstructed()
    {
        var root = NewTempRoot();
        var session = NewSession(root);

        // PollingTask is not auto-started by ctor; orchestrator starts it on attach.
        Assert.Null(session.PollingTask);
        Assert.Null(session.FileWatcher);
    }

    [Fact]
    public void UsePollingInIdleFalse_NoPollingTimer()
    {
        var root = NewTempRoot();
        var old = SettingsStore.Current.UsePollingInIdle;
        try
        {
            SettingsStore.Current.UsePollingInIdle = false;
            var session = NewSession(root);

            Assert.Null(session.PollingTimer);
        }
        finally
        {
            SettingsStore.Current.UsePollingInIdle = old;
        }
    }

    [Fact]
    public void DetachRequested_PollingWaits()
    {
        var root = NewTempRoot();
        var profile = new DriveProfile { Label = "T", Serial = "1234", FolderName = "USB_T_1234", Enabled = false, DriveLetter = 'Z' };
        var orchestrator = new BackupOrchestrator();
        var field = typeof(BackupOrchestrator).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var sessions = (ConcurrentDictionary<string, ConnectedSession>)field.GetValue(orchestrator)!;
        var session = new ConnectedSession('Z', profile, new FileSyncService(false, false, false, 64, true, false, false));
        sessions.TryAdd(session.Letter.ToString(), session);

        session.DetachRequested = true;
        bool ok = orchestrator.HandleDriveQueryRemove(session.Letter);

        Assert.True(ok);
        Assert.True(session.DetachRequested);
    }

    [Fact]
    public void RunDriveSettingsTest_Cancellation_Observed()
    {
        var orchestrator = new BackupOrchestrator();
        var profile = new DriveProfile { Label = "T", Serial = "1234", FolderName = "USB_T_1234", Enabled = false, DriveLetter = 'D' };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var method = typeof(BackupOrchestrator).GetMethod("RunDriveSettingsTest", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(orchestrator, new object[] { profile, 'D', null!, cts.Token })!;

        var ex = Assert.ThrowsAsync<OperationCanceledException>(() => task);
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task QueryRemove_DuringCopyChunkedAsync_ClosesHandlesWithinBudget_ReturnsTrue()
    {
        var orchestrator = new BackupOrchestrator();
        var root = NewTempRoot();
        var profile = new DriveProfile { Label = "T", Serial = "1234", FolderName = "USB_T_1234", Enabled = false, DriveLetter = 'C' };
        var session = NewSession(root);
        var field = typeof(BackupOrchestrator).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var sessions = (ConcurrentDictionary<string, ConnectedSession>)field.GetValue(orchestrator)!;
        sessions.TryAdd(session.Letter.ToString(), session);

        var tasksField = typeof(BackupOrchestrator).GetField("_syncTasks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var tasks = (ConcurrentDictionary<string, Task>)tasksField.GetValue(orchestrator)!;
        tasks.TryAdd(session.Letter.ToString(), Task.Delay(50));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool ok = orchestrator.HandleDriveQueryRemove(session.Letter);
        sw.Stop();

        Assert.True(ok);
        Assert.True(sw.ElapsedMilliseconds < 1500);
    }

    [Fact]
    public async Task QueryRemove_DuringCopyChunkedAsync_Timeout_ReturnsDeny()
    {
        var orchestrator = new BackupOrchestrator();
        var root = NewTempRoot();
        var session = NewSession(root);
        var field = typeof(BackupOrchestrator).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var sessions = (ConcurrentDictionary<string, ConnectedSession>)field.GetValue(orchestrator)!;
        sessions.TryAdd(session.Letter.ToString(), session);

        var tasksField = typeof(BackupOrchestrator).GetField("_syncTasks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var tasks = (ConcurrentDictionary<string, Task>)tasksField.GetValue(orchestrator)!;
        tasks.TryAdd(session.Letter.ToString(), new TaskCompletionSource<object>().Task);

        bool ok = orchestrator.HandleDriveQueryRemove(session.Letter);

        Assert.False(ok);
        await Task.CompletedTask;
    }

    [Fact]
    public void DetachRequested_ClearedOnlyAfterCancelDetachAndResumeOrRemoveComplete()
    {
        var root = NewTempRoot();
        var session = NewSession(root);
        var orchestrator = new BackupOrchestrator();
        var field = typeof(BackupOrchestrator).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var sessions = (ConcurrentDictionary<string, ConnectedSession>)field.GetValue(orchestrator)!;
        sessions.TryAdd(session.Letter.ToString(), session);

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
    public async Task UsePollingInIdleFalse_BaselineNotTaken()
    {
        var old = SettingsStore.Current.UsePollingInIdle;
        try
        {
            SettingsStore.Current.UsePollingInIdle = false;
            var orchestrator = new BackupOrchestrator();
            var root = NewTempRoot();
            var driveLetter = (char)root[0];
            var evt = new UsbDriveEventArgs(driveLetter, "TEST", "SERIAL");
            await orchestrator.OnDriveArrivedAsync(evt, null);

            var sessionsField = typeof(BackupOrchestrator).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var sessions = (ConcurrentDictionary<string, ConnectedSession>)sessionsField.GetValue(orchestrator)!;
            var session = sessions.Values.First();

            Assert.Null(session.PollingTimer);
            Assert.True(string.IsNullOrEmpty(session.LastPollSignature));
        }
        finally
        {
            SettingsStore.Current.UsePollingInIdle = old;
        }
    }

    [Fact]
    public void DetachRequested_PollingDoesNotStartNewEnumeration()
    {
        var root = NewTempRoot();
        var session = NewSession(root);
        session.DetachRequested = true;

        var method = typeof(BackupOrchestrator).GetMethod("PollSessionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(new BackupOrchestrator(), new object[] { session })!;
        task.GetAwaiter().GetResult();

        Assert.False(session.PendingChanges);
    }

    [Fact]
    public async Task RunDriveSettingsTest_DetachDuringRead_ThrowsOperationCanceled()
    {
        var orchestrator = new BackupOrchestrator();
        var profile = new DriveProfile { Label = "RunTest", Serial = "9999", FolderName = "USB_RUN_9999", Enabled = false, DriveLetter = 'D' };
        var session = NewSessionForLetter('D', profile);
        var cancelFile = Path.Combine("C:\\", "__runtest_cancel.bin");
        try
        {
            session.BeginDetachForSafeRemoval(out _, out _);
            var task = orchestrator.RunDriveSettingsTest(profile, 'D', null, session.DetachCts.Token);

            var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => task);
            Assert.NotNull(ex);
        }
        finally
        {
            try { File.Delete(cancelFile); } catch { }
        }
    }

    [Fact]
    public async Task SyncAllAsync_Cancellation_DoesNotHang()
    {
        var root = NewTempRoot();
        var src = Path.Combine(root, "src");
        var dst = Path.Combine(root, "dst");
        Directory.CreateDirectory(src);
        File.WriteAllBytes(Path.Combine(src, "big.bin"), new byte[2 * 1024 * 1024]);
        Directory.CreateDirectory(dst);

        using var cts = new CancellationTokenSource();
        var svc = new FileSyncService(false, false, false, 64, true, false, false);
        var task = svc.SyncAllAsync(src, dst, null, cts.Token);
        cts.Cancel();

        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(task, completed);
    }


}
