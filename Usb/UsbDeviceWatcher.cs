using System;
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
/// Событийный детектор USB-накопителей на базе WM_DEVICECHANGE — версия для WinUI 3.
///
/// Как это работает и почему 0% CPU в простое:
///   • В WinUI 3 нет WndProc, поэтому HWND главного окна подменяется через
///     comctl32!SetWindowSubclass — официальный приём для WinUI 3 (так же добавляют
///     обработку WM_POWERBROADCAST и т.п.). Дефолтная процедура вызывается через
///     DefSubclassProc, поведение окна не ломается.
///   • Windows САМА рассылает WM_DEVICECHANGE всем top-level окнам (в т.ч. скрытым),
///     когда подключают/отключают том.
///   • DBT_DEVICEARRIVAL        (0x8000) — том появился (флешку вставили).
///   • DBT_DEVICEREMOVECOMPLETE (0x8004) — том исчез (извлекли).
///   • В пакете dbcv_unitmask — битовая маска букв дисков: бит 3 = 'D', бит 4 = 'E', …
///   • Ноль таймеров, ноль циклов опроса. Между событиями процесс спит в GetMessage.
/// </summary>
public sealed class UsbDeviceWatcher
{
    /// <summary>Флешка подключена (уже удачно прочитаны метка и серийный номер).
    /// МОЖЕТ прийти из фонового потока — подписчик обязан маршалить в UI.</summary>
    public event Action<UsbDriveEventArgs>? DriveArrived;

    /// <summary>Флешка отключена (по букве диска). Приходит из оконной процедуры (UI-поток).</summary>
    public event Action<char>? DriveRemoved;

    /// <summary>Событие «второй инстанс просит развернуть окно».</summary>
    public event Action? RestoreRequested;

    // Делегат обязан ЖИТЬ всё время жизни окна — иначе GC соберёт его до неуправляемого вызова.
    private SubclassProcDelegate? _subclassProc;

    private const uint SUBCLASS_ID = 0x5342; // "SB" — произвольный уникальный id

    /// <summary>Подключить перехватчик к HWND главного окна (вызов один раз при старте).</summary>
    public void Attach(IntPtr hwnd)
    {
        _subclassProc = SubclassHandler;
        SetWindowSubclass(hwnd, _subclassProc, SUBCLASS_ID, IntPtr.Zero);
    }

    private IntPtr SubclassHandler(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint id, IntPtr refData)
    {
        // Приватная команда от второго инстанса App — развернуть главное окно.
        if (IsRestoreMessage(msg))
        {
            RestoreRequested?.Invoke();
            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        if (msg == WM_DEVICECHANGE)
        {
            var evt = wParam.ToInt64();
            if ((evt & 0xFFFF) is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE
                && lParam != IntPtr.Zero)
            {
                var hdr = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);
                if (hdr.dbch_devicetype == DBT_DEVTYP_VOLUME)
                {
                    var vol = Marshal.PtrToStructure<DEV_BROADCAST_VOLUME>(lParam);
                    if (vol.dbcv_flags == 0) // не DBTF_MEDIA: реальный носитель с ФС
                    {
                        bool arrived = (evt & 0xFFFF) == DBT_DEVICEARRIVAL;
                        DispatchLetters(vol.dbcv_unitmask, arrived);
                    }
                }
            }
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>Разбираем битовую маску на буквы дисков и поднимаем события.</summary>
    private void DispatchLetters(uint unitmask, bool arrived)
    {
        for (int bit = 0; bit < 26; bit++)
        {
            if ((unitmask & (1u << bit)) == 0) continue;
            char letter = (char)('A' + bit);

            if (arrived)
                OnArrival(letter);
            else
            {
                AppLog.Info($"WM_DEVICECHANGE: REMOVE {letter}:");
                DriveRemoved?.Invoke(letter);
            }
        }
    }

    /// <summary>
    /// Подключение: спрашиваем метку и серийный номер. Драйвер флешки может монтировать том
    /// не мгновенно, поэтому до 3 попыток с паузой 200 мс. Это НЕ polling — это ограниченная
    /// реакция на одно системное событие (максимум 3×200 мс на событие). Работает в фоне,
    /// чтобы не замораживать UI-поток как в WinForms-версии.
    /// </summary>
    private void OnArrival(char letter)
    {
        _ = Task.Run(() =>
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
        });
    }

    /// <summary>Ручной подъём события «флешка уже вставлена» (первичный обход при старте).</summary>
    public void RaiseArrived(char letter, string volumeLabel, string serial)
        => DriveArrived?.Invoke(new UsbDriveEventArgs(letter, volumeLabel, serial));

    /// <summary>
    /// Читает label + Volume Serial Number, отбирая ТОЛЬКО съёмные тома (DRIVE_REMOVABLE).
    /// Возвращает null, если подключённый том — не флешка (eSATA-диск, CD-привод и т.д.).
    /// </summary>
    public static (string VolumeLabel, string Serial)? QueryDrive(char letter, int retry, int delayMs)
    {
        var root = $@"{letter}:\";
        for (int attempt = 0; attempt < retry; attempt++)
        {
            if (GetDriveTypeW(root) == 2 /* DRIVE_REMOVABLE */)
            {
                var label = new StringBuilder(261);
                var fs = new StringBuilder(64);
                if (GetVolumeInformationW(root, label, label.Capacity,
                        out uint serialNum, out _, out _, fs, fs.Capacity) != 0)
                {
                    // Серийный номер в виде 1234-ABCD — том же виде, что показывает `dir`.
                    var serial = $"{serialNum >> 16:X4}-{serialNum & 0xFFFF:X4}";
                    return (label.Length > 0 ? label.ToString() : "USB-накопитель", serial);
                }
            }
            Thread.Sleep(delayMs); // микропауза между попытками чтения того же диска
        }
        return null;
    }
}
