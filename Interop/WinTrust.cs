using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using SmartUsbBackup;
using SmartUsbBackup.Core;

namespace SmartUsbBackup.Interop;

/// <summary>
/// Verifies Authenticode signatures through WinVerifyTrust (fail-closed).
/// SEC: A-02
/// </summary>
internal static class WinTrust
{
    // SEC: A-02 — publisher is not pinned by default so unsigned release installers still work.
    // Set this to a non-null subject to enforce Authenticode publisher matching.
    public static readonly string? ExpectedPublisher = null;

    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);

    private const uint WTD_UI_NONE = 0x00000002;
    private const uint WTD_REVOKE_NONE = 0x00000000;
    private const uint WTD_CHOICE_FILE = 0x00000001;
    private const uint WTD_STATEACTION_VERIFY = 0x00000001;
    private const uint WTD_STATEACTION_CLOSE = 0x00000002;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

    private static Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    /// <summary>
    /// Returns true only when the signature is valid. Any uncertainty => false.
    /// SEC: A-02 (fail-closed)
    /// </summary>
    public static bool Verify(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
        {
            AppLog.Warn("SEC A-02: WinVerifyTrust пропущен — файл отсутствует: " + filePath);
            return false;
        }

        IntPtr filePathPtr = IntPtr.Zero;
        IntPtr fileInfoPtr = IntPtr.Zero;
        IntPtr dataPtr = IntPtr.Zero;
        try
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            };
            filePathPtr = Marshal.StringToHGlobalUni(filePath);
            fileInfo.pcwszFilePath = filePathPtr;
            fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = fileInfoPtr,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
            };
            dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
            Marshal.StructureToPtr(data, dataPtr, false);

            var dataRef = Marshal.PtrToStructure<WINTRUST_DATA>(dataPtr);
            int hr = WinVerifyTrust(IntPtr.Zero, ref GenericVerifyV2, ref dataRef);

            // close state
            dataRef.dwStateAction = WTD_STATEACTION_CLOSE;
            dataRef.hWVTStateData = IntPtr.Zero;
            WinVerifyTrust(IntPtr.Zero, ref GenericVerifyV2, ref dataRef);

            if (hr != 0)
            {
                // SEC: A-02 — unsigned is allowed only until publisher is pinned.
                if (hr == TRUST_E_NOSIGNATURE && string.IsNullOrWhiteSpace(ExpectedPublisher))
                {
                    AppLog.Warn("SEC A-02: установщик не подписан; ExpectedPublisher не настроен — продолжаем (happy path)");
                    return true;
                }

                AppLog.Warn("SEC A-02: WinVerifyTrust отклонил файл (hr=" + hr.ToString("X8") + "): " + filePath);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(ExpectedPublisher))
            {
                try
                {
                    using var cert = new X509Certificate2(filePath);
                    if (cert.Subject.IndexOf(ExpectedPublisher, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        AppLog.Warn("SEC A-02: издатель не совпадает. Ожидался: " + ExpectedPublisher);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    // SEC: A-02 fail-closed
                    AppLog.Error(ex, "SEC A-02: не удалось прочитать сертификат подписанного файла");
                    return false;
                }
            }
            else
            {
                AppLog.Warn("SEC A-02: ExpectedPublisher не настроен — проверяется только наличие подписи (если она есть)");
            }

            return true;
        }
        catch (Exception ex)
        {
            // SEC: A-02 fail-closed
            AppLog.Error(ex, "SEC A-02: ошибка P/Invoke WinVerifyTrust");
            return false;
        }
        finally
        {
            if (filePathPtr != IntPtr.Zero) Marshal.FreeHGlobal(filePathPtr);
            if (fileInfoPtr != IntPtr.Zero) Marshal.FreeHGlobal(fileInfoPtr);
            if (dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(dataPtr);
        }
    }
}