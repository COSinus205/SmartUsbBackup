using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SmartUsbBackup.Core;

/// <summary>
/// Строка таблицы «Флешки» (обёртка над DriveProfile с INotifyPropertyChanged —
/// WinUI-биндинги x:Bind Mode=OneWay/TwoWay обновляются только через INPC).
/// </summary>
public sealed class DriveRow : INotifyPropertyChanged
{
    public DriveProfile Profile { get; }

    public DriveRow(DriveProfile profile)
    {
        Profile = profile;
        _enabled = profile.Enabled;
    }

    /// <summary>Колонка «Имя (серийник)»: «GOIDA [04A2-9F13]».</summary>
    public string Name => string.IsNullOrWhiteSpace(Profile.Serial)
        ? Profile.Label
        : $"{Profile.Label} [{Profile.Serial}]";

    /// <summary>Колонка «Путь к папке».</summary>
    public string BackupPath => Profile.BackupPath;

    private string _state = "Ожидание";
    /// <summary>Колонка «Состояние».</summary>
    public string State
    {
        get => _state;
        set => Set(ref _state, value);
    }

    private string _backupState = "Нет сохранения";
    /// <summary>Колонка «Сохранение»: есть ли уже копия этой флешки на диске.</summary>
    public string BackupState
    {
        get => _backupState;
        set => Set(ref _backupState, value);
    }

    private bool _enabled;
    /// <summary>Колонка-чекбокс «активна/нет» (TwoWay из XAML).</summary>
    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    // ------------------------------------------------------------------
    public void RefreshState(string newState) => State = newState;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
