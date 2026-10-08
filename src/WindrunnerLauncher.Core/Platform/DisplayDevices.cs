using System.Runtime.InteropServices;

namespace WindrunnerLauncher.Core.Platform;

/// <summary>
/// Reads the Windows display-device list. The index returned here is the <c>iDevNum</c> that
/// <c>EnumDisplayDevices</c> uses, which is the number VanillaMultiMonitorFix expects in
/// <c>VMMFix_preferred_monitor.txt</c>.
/// </summary>
public static class DisplayDevices
{
    private const uint DisplayDeviceActive = 0x1;
    private const uint DisplayDevicePrimary = 0x4;

    /// <summary>Index of the primary display device, or null when none can be found.</summary>
    public static int? PrimaryIndex()
    {
        if (!PlatformInfo.IsWindows)
            return null;

        for (uint i = 0; ; i++)
        {
            var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(null, i, ref device, 0))
                return null;

            if ((device.StateFlags & DisplayDeviceActive) != 0 && (device.StateFlags & DisplayDevicePrimary) != 0)
                return (int)i;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);
}
