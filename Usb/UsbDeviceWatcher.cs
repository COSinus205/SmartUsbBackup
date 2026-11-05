using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static SmartUsbBackup.NativeMethods;

namespace SmartUsbBackup.Usb;

/// <summary>Аргументы события «флешка подключена».</summary>
public sealed record UsbDriveEventArgs(char DriveLetter, string VolumeLabel, string Serial)
{
    public override string ToString() => $@"{DriveLetter}: \ «{VolumeLabel}» [серийный № {Serial}]";
}

/// <summary>
/// Событийный детектор USB-накопителей на базе WM_DEVICECHANGE.
/// </summary>
public sealed class UsbDeviceWatcher
{
    /// <summary>Флешка подключена (уже удачно прочитаны метка и серийный номер).</summary>
    public event Action<UsbDriveEventArgs>? DriveArrived;

    /// <summary>Флешка отключена (по букве диска).</summary>
    public event Action<char>? DriveRemoved;

    /// <summary>Windows запросил безопасное извлечение тома. Обработчик обязан очистить хэндлы и
    /// вернуть true только после синхронного освобождения ресурсов.</summary>
    public event Func<char, bool>? DriveQueryRemove;

    /// <summary>Windows сообщил, что извлечение отменено/не состоялось.</summary>
    public event Action<char>? DriveQueryRemoveFailed;

    /// <summary>Финальное предупреждение перед удалением (unused на современных Windows; log-only).</summary>
    public event Action<char>? DriveQueryRemovePending;

    /// <summary>Событие «второй инстанс просит развернуть окно».</summary>
    public event Action? RestoreRequested;

    private SubclassProcDelegate? _subclassProc;
    private IntPtr _deviceNotificationHandle = IntPtr.Zero;

    private const uint SUBCLASS_ID = 0x5342;

    public void Attach(IntPtr hwnd)
    {
        _subclassProc = SubclassHandler;
        SetWindowSubclass(hwnd, _subclassProc, SUBCLASS_ID, IntPtr.Zero);
        RegisterDeviceNotificationHandle(hwnd);
    }

    private void RegisterDeviceNotificationHandle(IntPtr hwnd)
    {
        try
        {
            var filter = new DEV_BROADCAST_DEVICEINTERFACE
            {
                dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
                dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
                dbcc_reserved = 0,
                dbcc_classguid = GUID_DEVINTERFACE_VOLUME,
                dbcc_name = string.Empty,
            };
            var ptr = Marshal.AllocHGlobal(filter.dbcc_size);
            try
            {
                Marshal.StructureToPtr(filter, ptr, false);
                _deviceNotificationHandle = RegisterDeviceNotification(hwnd, ptr, (uint)(DEVICE_NOTIFY_WINDOW_HANDLE | DEVICE_NOTIFY_ALL_INTERFACE_CLASSES));
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            if (_deviceNotificationHandle == IntPtr.Zero)
                AppLog.Warn("[USB] RegisterDeviceNotification вернул 0 — DBT_DEVICEQUERYREMOVE может не прийти");
            else
                AppLog.Info($"[USB] RegisterDeviceNotification OK (handle={_deviceNotificationHandle})");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "[USB] Не удалось зарегистрировать уведомление устройства");
        }
    }

    public void Unregister()
    {
        if (_deviceNotificationHandle != IntPtr.Zero)
        {
            try { UnregisterDeviceNotification(_deviceNotificationHandle); }
            catch (Exception ex) { AppLog.Error(ex, "[USB] UnregisterDeviceNotification"); }
            _deviceNotificationHandle = IntPtr.Zero;
        }
    }

    private IntPtr SubclassHandler(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint id, IntPtr refData)
    {
        try
        {
            if (msg == 0x020A || msg == 0x020E)
            {
                int delta = (short)(wParam.ToInt64() >> 16);
                MainWindow.HandleMouseWheel(delta);
                return IntPtr.Zero;
            }

            if (IsRestoreMessage(msg))
            {
                RestoreRequested?.Invoke();
                return DefSubclassProc(hWnd, msg, wParam, lParam);
            }

            if (msg == WM_DEVICECHANGE)
            {
                var evt = wParam.ToInt64();
                var evtLow = (int)(evt & 0xFFFF);

                if (lParam != IntPtr.Zero)
                {
                    var hdr = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);

                    if (evtLow == DBT_DEVICEQUERYREMOVE)
                        AppLog.Info($"[USB] WM_DEVICECHANGE QUERYREMOVE: evtLow={evtLow}, devicetype={hdr.dbch_devicetype}, lParam={lParam.ToInt64()}");

                    if (hdr.dbch_devicetype == DBT_DEVTYP_VOLUME)
                    {
                        var vol = Marshal.PtrToStructure<DEV_BROADCAST_VOLUME>(lParam);

                        if (vol.dbcv_flags == 0)
                        {
                            switch (evtLow)
                            {
                                case DBT_DEVICEARRIVAL:
                                    DispatchVolumeLetters(vol.dbcv_unitmask, true);
                                    break;
                                case DBT_DEVICEREMOVECOMPLETE:
                                    DispatchVolumeLetters(vol.dbcv_unitmask, false);
                                    break;
                                case DBT_DEVICEQUERYREMOVE:
                                    {
                                        bool allow = true;
                                        foreach (char letter in LettersFromUnitMask(vol.dbcv_unitmask))
                                        {
                                            try
                                            {
                                                bool r = InvokeDriveQueryRemove(letter);
                                                if (!r) allow = false;
                                            }
                                            catch (Exception ex)
                                            {
                                                AppLog.Error(ex, $"[USB] Handler QUERYREMOVE {letter} failed");
                                                allow = false;
                                            }
                                        }
                                        return allow ? new IntPtr(1) : new IntPtr(BROADCAST_QUERY_DENY);
                                    }
                                case DBT_DEVICEQUERYREMOVEFAILED:
                                    DispatchQueryFailedLetters(vol.dbcv_unitmask);
                                    break;
                                case DBT_DEVICEQUERYREMOVEPENDING:
                                    DispatchQueryPendingLetters(vol.dbcv_unitmask);
                                    break;
                            }
                        }
                    }
                    else if (hdr.dbch_devicetype == DBT_DEVTYP_DEVICEINTERFACE)
                    {
                        DEV_BROADCAST_DEVICEINTERFACE dev;
                        try { dev = Marshal.PtrToStructure<DEV_BROADCAST_DEVICEINTERFACE>(lParam); }
                        catch { dev = default; }

                        char? mapped = TryMapDevicePathToDriveLetter(dev.dbcc_name);

                        switch (evtLow)
                        {
                            case DBT_DEVICEQUERYREMOVE:
                                {
                                    AppLog.Info($"[USB] QUERYREMOVE (iface): device='{dev.dbcc_name}', mapped={(mapped.HasValue ? mapped.Value.ToString() : "none")}");
                                    bool allow = true;
                                    if (mapped.HasValue)
                                    {
                                        try { if (!InvokeDriveQueryRemove(mapped.Value)) allow = false; }
                                        catch (Exception ex) { AppLog.Error(ex, $"[USB] Handler QUERYREMOVE (iface) {mapped.Value} failed"); allow = false; }
                                    }
                                    else
                                    {
                                        AppLog.Info("[USB] QUERYREMOVE (iface): no drive letter — ignored");
                                    }
                                    return allow ? new IntPtr(1) : new IntPtr(BROADCAST_QUERY_DENY);
                                }
                            case DBT_DEVICEQUERYREMOVEFAILED:
                                if (mapped.HasValue) DriveQueryRemoveFailed?.Invoke(mapped.Value);
                                break;
                            case DBT_DEVICEQUERYREMOVEPENDING:
                                if (mapped.HasValue) DriveQueryRemovePending?.Invoke(mapped.Value);
                                break;
                        }
                    }
                }
            }

            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "[USB] Необработанное исключение в SubclassHandler");
            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }
    }

    private static char? TryMapDevicePathToDriveLetter(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return null;
        var target = devicePath.Trim().TrimEnd('\\', '/');
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    var sb = new StringBuilder(260);
                    if (GetVolumeNameForVolumeMountPoint(drive.Name, sb, (uint)sb.Capacity))
                    {
                        var candidate = sb.ToString().Trim().TrimEnd('\\', '/');
                        if (string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase))
                            return drive.Name[0];
                    }
                }
                catch { /* пропускаем недоступные диски */ }
            }
        }
        catch { /* не критично */ }
        return null;
    }

    private bool InvokeDriveQueryRemove(char letter)
    {
        bool allow = true;
        var dels = DriveQueryRemove?.GetInvocationList();
        if (dels is null) return true;
        foreach (var del in dels)
        {
            if (del is Func<char, bool> f)
            {
                bool r = f(letter);
                if (!r) allow = false;
            }
        }
        return allow;
    }

    private static IEnumerable<char> LettersFromUnitMask(uint unitmask)
    {
        for (int bit = 0; bit < 26; bit++)
        {
            if ((unitmask & (1u << bit)) == 0) continue;
            yield return (char)('A' + bit);
        }
    }

    private void DispatchVolumeLetters(uint unitmask, bool arrived)
    {
        foreach (char letter in LettersFromUnitMask(unitmask))
        {
            if (arrived) OnArrival(letter);
            else
            {
                AppLog.Info($"WM_DEVICECHANGE: REMOVE {letter}:");
                DriveRemoved?.Invoke(letter);
            }
        }
    }

    private void DispatchQueryFailedLetters(uint unitmask)
    {
        foreach (char letter in LettersFromUnitMask(unitmask))
        {
            AppLog.Info($"WM_DEVICECHANGE: QUERYREMOVEFAILED {letter}:");
            DriveQueryRemoveFailed?.Invoke(letter);
        }
    }

    private void DispatchQueryPendingLetters(uint unitmask)
    {
        foreach (char letter in LettersFromUnitMask(unitmask))
        {
            AppLog.Info($"WM_DEVICECHANGE: QUERYREMOVEPENDING {letter}: (log-only)");
            DriveQueryRemovePending?.Invoke(letter);
        }
    }

    private void OnArrival(char letter)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var info = QueryDrive(letter, retry: 3, delayMs: 200);
                if (info is not null)
                {
                    AppLog.Info($"WM_DEVICECHANGE: ARRIVAL {letter}: «{info!.Value.VolumeLabel}» serial={info.Value.Serial}");
                    DriveArrived?.Invoke(new UsbDriveEventArgs(letter, info.Value.VolumeLabel, info.Value.Serial));
                }
                else
                {
                    AppLog.Warn($"WM_DEVICECHANGE: ARRIVAL {letter}: том не смонтировался вовремя");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, $"WM_DEVICECHANGE: ошибка обработки подключения {letter}:");
            }
        });
    }

    /// <summary>Ручной подъём события «флешка уже вставлена».</summary>
    public void RaiseArrived(char letter, string volumeLabel, string serial)
        => DriveArrived?.Invoke(new UsbDriveEventArgs(letter, volumeLabel, serial));

    public static (string VolumeLabel, string Serial)? QueryDrive(char letter, int retry, int delayMs)
    {
        var root = $@"{letter}:\";
        for (int attempt = 0; attempt < retry; attempt++)
        {
            if (GetDriveTypeW(root) == 2)
            {
                var label = new StringBuilder(261);
                var fs = new StringBuilder(64);
                if (GetVolumeInformationW(root, label, label.Capacity,
                        out uint serialNum, out _, out _, fs, fs.Capacity) != 0)
                {
                    var serial = $"{serialNum >> 16:X4}-{serialNum & 0xFFFF:X4}";
                    return (label.Length > 0 ? label.ToString() : "USB-накопитель", serial);
                }
            }
            Thread.Sleep(delayMs);
        }
        return null;
    }
}