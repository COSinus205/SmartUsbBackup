using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SmartUsbBackup;

/// <summary>Прямые P/Invoke-вызовы. Всё, что касается Win32 — только здесь.</summary>
internal static class NativeMethods
{
    // ---------- WM_DEVICECHANGE ----------

    public const int WM_DEVICECHANGE = 0x0219;

    public const int DBT_DEVICEARRIVAL             = 0x8000;
    public const int DBT_DEVICEQUERYREMOVE         = 0x8001;
    public const int DBT_DEVICEQUERYREMOVEFAILED   = 0x8002;
    // Официально почти не используется; оставлен только для логирования (log-only).
    public const int DBT_DEVICEQUERYREMOVEPENDING  = 0x8003;
    public const int DBT_DEVICEREMOVECOMPLETE      = 0x8004;

    public const int DBT_DEVTYP_VOLUME            = 0x00000002;
    public const int DBT_DEVTYP_DEVICEINTERFACE   = 0x00000005;

    /// <summary>Единственный корректный отказ на DBT_DEVICEQUERYREMOVE.</summary>
    public const int BROADCAST_QUERY_DENY         = 0x424D5144;

    public const uint DEVICE_NOTIFY_WINDOW_HANDLE          = 0x00000000;
    public const uint DEVICE_NOTIFY_ALL_INTERFACE_CLASSES   = 0x00000004;

    /// <summary>Головной пакет уведомления DBT (DeviceInfoHeader).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DEV_BROADCAST_HDR
    {
        public int dbch_size;
        public int dbch_devicetype;
        public int dbch_reserved;
    }

    /// <summary>Пакет DBT для логического тома (флешка): содержит битовую маску букв дисков.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DEV_BROADCAST_VOLUME
    {
        public int dbcv_size;          // sizeof = 20 байт ... с 32-битным unitmask
        public int dbcv_devicetype;    // DBT_DEVTYP_VOLUME (2)
        public int dbcv_reserved;
        public uint dbcv_unitmask;     // битовая маска: бит 0 = 'A', бит 3 = 'D' ...
        public ushort dbcv_flags;      // 0 = устройство с файловой системой
    }

    // ---------- Информация о томе (Volume Serial Number) ----------

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetVolumeInformationW(
        string rootPathName,
        System.Text.StringBuilder? volumeName,
        int volumeNameBufferSize,
        out uint volumeSerialNumber,
        out uint maxComponentLength,
        out uint fileSystemFlags,
        System.Text.StringBuilder? fileSystemName,
        int fileSystemNameBufferSize);

    /// <summary>Интерфейсное уведомление для RegisterDeviceNotification (GUID_DEVINTERFACE_VOLUME).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 255)]
        public string dbcc_name;
    }

    public static readonly Guid GUID_DEVINTERFACE_VOLUME =
        Guid.Parse("53f5630d-b6bf-11d0-94f2-00a0c91efb8b");

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr RegisterDeviceNotification(
        IntPtr hRecipient, IntPtr NotificationFilter, uint Flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterDeviceNotification(IntPtr Handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern bool GetVolumeNameForVolumeMountPoint(
        string lpszVolumeMountPoint,
        System.Text.StringBuilder lpszVolumeName,
        uint cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetDriveTypeW(string rootPathName);

    // ---------- Автозагрузка (HKCU\...\Run) ----------

    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // ---------- Активация «второй инстанс → показать первое окно» ----------

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>DPI монитора, на котором находится окно (96 = 100%, 144 = 150%).</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    // ---------- Показ окна из трея ----------

    public const int SW_SHOW = 5;
    public const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    private const uint WM_APP_RESTORE = 0x8001; // WM_APP + 1 — наша приватная команда

    /// <summary>Посылает всем окнам (через HWND_BROADCAST) команду развернуться. Вызов из 2-го процесса.</summary>
    public static void BroadcastRestoreWindow()
    {
        PostMessage(IntPtr.Zero /* HWND_BROADCAST */, WM_APP_RESTORE, IntPtr.Zero, IntPtr.Zero);
    }

    public static bool IsRestoreMessage(uint msg) => msg == WM_APP_RESTORE;

    // ---------- Подмена оконной процедуры (subclassing) ----------
    // В WinUI 3 нет WndProc, как в WinForms, поэтому WM_DEVICECHANGE и WM_APP_RESTORE
    // перехватываются через SetWindowSubclass (comctl32) у HWND главного окна.

    public delegate IntPtr SubclassProcDelegate(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
        uint uIDSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    public static extern bool SetWindowSubclass(
        IntPtr hWnd, SubclassProcDelegate pfnSubclass, uint uIDSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    public static extern IntPtr DefSubclassProc(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    // ---------- Метрики (Task Manager: «меньше памяти в простое») ----------

    /// <summary>
    /// Просит ОС сжать рабочий набор процесса. Вызывается после «тихой» фазы
    /// (все флешки отключены), поэтому в Task Manager видно ~15–25 МБ в простое.
    /// </summary>
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    public static void TrimWorkingSet()
    {
        try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); } catch { /* не критично */ }
    }
}
