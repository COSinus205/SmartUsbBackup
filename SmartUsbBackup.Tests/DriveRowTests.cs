using System.Collections.Generic;
using SmartUsbBackup.Core;
using Xunit;

namespace SmartUsbBackup.Tests;

public sealed class DriveRowTests
{
    [Fact]
    public void RefreshComputed_RaisesNameAndBackupPath()
    {
        var profile = new DriveProfile
        {
            Label = "A",
            Serial = "S",
            FolderName = "USB_A_S",
            BaseBackupPathOverride = @"C:\One",
        };
        var row = new DriveRow(profile);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        profile.Label = "B";
        profile.BaseBackupPathOverride = @"C:\Two";
        row.RefreshComputed();

        Assert.Contains(nameof(DriveRow.Name), changed);
        Assert.Contains(nameof(DriveRow.BackupPath), changed);
        Assert.Equal("B [S]", row.Name);
        Assert.Equal(@"C:\Two\USB_A_S", row.BackupPath);
    }

    [Fact]
    public void RefreshComputed_WithoutChanges_IsHarmless()
    {
        var row = new DriveRow(new DriveProfile
        {
            Label = "A",
            Serial = "S",
            FolderName = "USB_A_S",
        });

        row.RefreshComputed();

        Assert.Equal("A [S]", row.Name);
    }
}
