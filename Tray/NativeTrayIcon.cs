using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SmartUsbBackup;

/// <summary>
/// Иконка в системном трее + нативное контекстное меню.
///
/// Реализовано напрямую через Shell_NotifyIcon, а не через стороннюю библиотеку:
/// H.NotifyIcon 2.3.2 на WinUI 3 1.8 показывал меню, но не доставлял клики
/// (мышь оставалась захваченной скрытым окном иконки), из-за чего не работали
/// ни «Открыть окно», ни «Выход». Здесь используется классическая схема
/// Win32: скрытое окно + WM_APP-сообщение + TrackPopupMenu.
/// </summary>
internal sealed class NativeTrayIcon : IDisposable
{
    private const uint WM_TRAY = 0x8002;

    /// <summary>Идентификатор иконки внутри нашего окна (и в настройках Explorer).</summary>
    private const uint ICON_UID = 2;

    /// <summary>
    /// Постоянный GUID иконки: по нему Windows запоминает положение иконки
    /// (в том числе «показывать в трее») между запусками и обновлениями.
    /// </summary>
    private static readonly Guid ICON_GUID = new("7E4C2A61-5B3D-4F8A-9C21-3D6E8B0A5F47");
    private const uint WM_NULL = 0x0000;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;
    private const uint NIF_GUID = 0x00000020;

    private const uint NIIF_INFO = 0x00000001;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;

    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint LR_DEFAULTSIZE = 0x0040;
    private const int IDI_APPLICATION = 32512;

    private const int CMD_OPEN = 1;
    private const int CMD_UPDATE = 2;
    private const int CMD_EXIT = 3;

    private readonly WndProcDelegate _wndProc;
    private readonly string _className;
    private readonly IntPtr _hwnd;
    private readonly IntPtr _menu;
    private readonly IntPtr _hIcon;
    private readonly bool _ownsIcon;
    private readonly uint _taskbarCreatedMessage;

    private string _tip = "Smart USB Backup";
    private bool _disposed;

    /// <summary>Пользователь выбрал «Открыть окно» (или кликнул по иконке).</summary>
    public event Action? OpenRequested;

    /// <summary>Пользователь выбрал «Выход».</summary>
    public event Action? ExitRequested;

    /// <summary>Пользователь выбрал «Проверить обновления».</summary>
    public event Action? UpdateRequested;

    public NativeTrayIcon()
    {
        _wndProc = WndProc;

        var hInstance = GetModuleHandle(null);
        _className = "SmartUsbBackupTray_" + Guid.NewGuid().ToString("N");

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = _className,
        };

        if (RegisterClassEx(ref wc) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx (трей)");

        _hwnd = CreateWindowEx(0, _className, "SmartUsbBackupTray", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx (трей)");

        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        _menu = CreatePopupMenu();
        AppendMenu(_menu, MF_STRING, (IntPtr)CMD_OPEN, "Открыть окно");
        AppendMenu(_menu, MF_SEPARATOR, IntPtr.Zero, null);
        AppendMenu(_menu, MF_STRING, (IntPtr)CMD_UPDATE, "Проверить обновления");
        AppendMenu(_menu, MF_SEPARATOR, IntPtr.Zero, null);
        AppendMenu(_menu, MF_STRING, (IntPtr)CMD_EXIT, "Выход (остановит синхронизацию)");

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.ico");
        _hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
        if (_hIcon != IntPtr.Zero)
        {
            _ownsIcon = true;
        }
        else
        {
            _hIcon = LoadIcon(IntPtr.Zero, (IntPtr)IDI_APPLICATION);
            _ownsIcon = false;
            AppLog.Warn("Не удалось загрузить Assets\\appicon.ico — используется системная иконка");
        }

        AddIcon();

        // Windows 11 прячет иконки новых программ в «Скрытые значки» — для
        // фонового приложения это равносильно отсутствию интерфейса.
        _ = Task.Run(PromoteIconToTrayAsync);
    }

    /// <summary>Обновить всплывающую подсказку иконки.</summary>
    public void SetToolTip(string text)
    {
        _tip = text ?? string.Empty;
        if (_disposed) return;

        var data = CreateData();
        data.uFlags = NIF_TIP | NIF_GUID;
        data.szTip = Trim(_tip, 127);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>Показать системное уведомление (balloon).</summary>
    public void ShowBalloon(string title, string message)
    {
        if (_disposed) return;

        var data = CreateData();
        data.uFlags = NIF_INFO | NIF_GUID;
        data.szInfoTitle = Trim(title, 63);
        data.szInfo = Trim(message, 255);
        data.dwInfoFlags = NIIF_INFO;
        data.uTimeoutOrVersion = 5000;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            var data = CreateData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
        }
        catch { }

        if (_menu != IntPtr.Zero) DestroyMenu(_menu);
        if (_ownsIcon && _hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        if (!string.IsNullOrEmpty(_className)) UnregisterClass(_className, GetModuleHandle(null));
    }

    private void AddIcon()
    {
        var data = CreateData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID;
        data.uCallbackMessage = WM_TRAY;
        data.hIcon = _hIcon;
        data.szTip = Trim(_tip, 127);

        if (!Shell_NotifyIcon(NIM_ADD, ref data))
        {
            // После аварийного завершения Explorer может помнить иконку с тем же
            // GUID. Тогда NIM_ADD отклоняется — удаляем «зависшую» и пробуем снова.
            Shell_NotifyIcon(NIM_DELETE, ref data);
            if (!Shell_NotifyIcon(NIM_ADD, ref data))
                AppLog.Warn("Не удалось добавить иконку в трей");
        }
    }

    /// <summary>
    /// Показать иконку в области уведомлений (а не в «Скрытых значках»).
    ///
    /// Windows 11 по умолчанию прячет иконки новых приложений. Настройка живёт
    /// в HKCU\Control Panel\NotifyIconSettings: ключ создаёт Explorer в момент
    /// добавления иконки, поэтому сначала ждём его появления, а затем один раз
    /// ставим IsPromoted=1. Если пользователь позже скроет иконку вручную,
    /// значение уже будет существовать — и мы его больше не трогаем.
    /// </summary>
    private async Task PromoteIconToTrayAsync()
    {
        for (int attempt = 0; attempt < 10 && !_disposed; attempt++)
        {
            await Task.Delay(300).ConfigureAwait(false);

            try
            {
                if (TryPromoteIcon()) return;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Трей: не удалось показать иконку в области уведомлений: " + ex.Message);
                return;
            }
        }
    }

    private bool TryPromoteIcon()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return false;

        using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
        if (root is null) return false;

        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name, writable: true);
            if (key is null) continue;

            if (key.GetValue("IconGuid") is not string guid ||
                !string.Equals(guid, ICON_GUID.ToString("B"), StringComparison.OrdinalIgnoreCase)) continue;

            var path = key.GetValue("ExecutablePath") as string;
            if (string.IsNullOrEmpty(path)) continue;
            if (!string.Equals(Environment.ExpandEnvironmentVariables(path), exePath,
                               StringComparison.OrdinalIgnoreCase)) continue;

            // Пользователь уже сделал выбор (показать или скрыть) — уважаем его.
            if (key.GetValue("IsPromoted") is not null) return true;

            key.SetValue("IsPromoted", 1, RegistryValueKind.DWord);

            // Explorer читает настройку в момент добавления иконки — пере-добавляем.
            var data = CreateData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            AddIcon();

            AppLog.Info("Трей: иконка показана в области уведомлений");
            return true;
        }

        return false;
    }

    private void ShowMenu()
    {
        GetCursorPos(out var pt);

        // Обязательно перед TrackPopupMenu: иначе меню не закрывается
        // при клике мимо и может «залипнуть» поверх других окон.
        SetForegroundWindow(_hwnd);

        int cmd = TrackPopupMenuEx(
            _menu,
            TPM_RIGHTBUTTON | TPM_RETURNCMD,
            pt.X, pt.Y,
            _hwnd,
            IntPtr.Zero);

        PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

        if (cmd == CMD_OPEN) OpenRequested?.Invoke();
        else if (cmd == CMD_UPDATE) UpdateRequested?.Invoke();
        else if (cmd == CMD_EXIT) ExitRequested?.Invoke();
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAY)
        {
            int evt = (int)(lParam.ToInt64() & 0xFFFF);
            switch (evt)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    OpenRequested?.Invoke();
                    break;

                case WM_RBUTTONUP:
                case WM_CONTEXTMENU:
                    ShowMenu();
                    break;
            }
            return IntPtr.Zero;
        }

        // Explorer перезапустился — возвращаем иконку на место.
        if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
        {
            AddIcon();
            return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private NOTIFYICONDATA CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = ICON_UID,
        guidItem = ICON_GUID,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private static string Trim(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty
        : value.Length <= max ? value
        : value[..max];

    // =================================================================
    //  Win32
    // =================================================================

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadImage(
        IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(
        IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);
}