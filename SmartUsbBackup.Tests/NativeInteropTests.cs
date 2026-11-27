using System.Runtime.InteropServices;
using Xunit;

namespace SmartUsbBackup.Tests;

public sealed class NativeInteropTests
{
    [Fact]
    public void DeviceInterfaceHeader_HasExpectedSize()
    {
        // Имя в DEV_BROADCAST_DEVICEINTERFACE начинается сразу после заголовка;
        // ошибка в размере сдвинет чтение строки.
        Assert.Equal(28, Marshal.SizeOf<NativeMethods.DEV_BROADCAST_DEVICEINTERFACE_HEADER>());
    }
}
