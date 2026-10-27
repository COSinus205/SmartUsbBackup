using System;
using System.Runtime.InteropServices;
using static SmartUsbBackup.NativeMethods;

namespace SmartUsbBackup;

/// <summary>
/// Задание минимального размера окна классическим способом —
/// через WM_GETMINMAXINFO (subclassing, comctl32!SetWindowSubclass).
/// Работает во всех версиях Windows App SDK, включая unpackaged.
/// </summary>
internal static class WindowMinSize
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint SUBCLASS_ID = 0x5343; // "SS" — уникальный рядом с 0x5342 (USB-перехватчик)

    private static SubclassProcDelegate? _proc;
    private static int _minWidth, _minHeight;

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public static void Install(IntPtr hwnd, int minWidth, int minHeight)
    {
        // minWidth/minHeight задаются в DIP (как в XAML), а WM_GETMINMAXINFO
        // работает в физических пикселях — масштабируем под текущий DPI монитора.
        double scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1.0;

        _minWidth = (int)Math.Round(minWidth * scale);
        _minHeight = (int)Math.Round(minHeight * scale);
        _proc = OnSubclass;
        SetWindowSubclass(hwnd, _proc, SUBCLASS_ID, IntPtr.Zero);
    }

    private static IntPtr OnSubclass(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint id, IntPtr refData)
    {
        if (msg == WM_GETMINMAXINFO && lParam != IntPtr.Zero)
        {
            var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            info.ptMinTrackSize = new POINT { X = _minWidth, Y = _minHeight };
            Marshal.StructureToPtr(info, lParam, false);
        }

        // ВАЖНО: всегда отдаём сообщение дальше в DefSubclassProc.
        // Если вернуть 0 «как обработанное», Windows не дозаполнит ptMaxTrackSize
        // и окно получает случайный/крошечный максимальный размер (баг с 427 px).
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }
}
