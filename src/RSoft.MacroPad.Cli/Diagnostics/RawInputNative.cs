using System.Runtime.InteropServices;

namespace RSoft.MacroPad.Cli.Diagnostics;

/// <summary>
/// Win32 Raw Input and message-only window interop declarations.
/// Raw Input is the only way to observe keystrokes and wheel movement from this macro pad:
/// its keyboard and mouse HID collections are claimed exclusively by the kbdhid/mouhid
/// drivers, so opening them for read fails (verified against VID 0x1189 / PID 0x8840).
/// </summary>
internal static class RawInputNative
{
    internal const int WM_INPUT = 0x00FF;
    internal const int WM_CLOSE = 0x0010;
    internal const int WM_DESTROY = 0x0002;
    internal static readonly IntPtr HWND_MESSAGE = new(-3);

    internal const uint RIDEV_INPUTSINK = 0x00000100;
    internal const uint RID_INPUT = 0x10000003;
    internal const uint RIDI_DEVICENAME = 0x20000007;
    internal const uint RIDI_DEVICEINFO = 0x2000000B;

    internal const uint RIM_TYPEMOUSE = 0;
    internal const uint RIM_TYPEKEYBOARD = 1;
    internal const uint RIM_TYPEHID = 2;

    internal const ushort RI_KEY_BREAK = 0x01;
    internal const ushort RI_KEY_E0 = 0x02;
    internal const ushort RI_KEY_E1 = 0x04;

    internal const ushort RI_MOUSE_WHEEL = 0x0400;
    internal const ushort RI_MOUSE_HWHEEL = 0x0800;

    internal const uint MAPVK_VK_TO_CHAR = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTHEADER
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTDEVICELIST
    {
        public IntPtr Device;
        public uint Type;
    }

    /// <summary>
    /// RID_DEVICE_INFO. Only the HID arm is declared: the mouse and keyboard arms are smaller,
    /// so this layout is a safe superset for the buffer passed to GetRawInputDeviceInfo, and
    /// the usage page/usage fields are read only when Type is RIM_TYPEHID.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RID_DEVICE_INFO
    {
        public uint Size;
        public uint Type;
        public uint VendorId;
        public uint ProductId;
        public uint VersionNumber;
        public ushort UsagePage;
        public ushort Usage;
    }

    internal delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct WNDCLASS
    {
        public uint Style;
        public IntPtr WndProc;
        public int ClsExtra;
        public int WndExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, IntPtr data, ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? deviceList, ref uint count, uint size);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern ushort RegisterClassW(ref WNDCLASS windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateWindowExW(uint exStyle, string className, string? windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetMessageW(out MSG message, IntPtr window, uint filterMin, uint filterMax);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr DispatchMessageW(ref MSG message);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint MapVirtualKeyW(uint code, uint mapType);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandleW(string? moduleName);
}
