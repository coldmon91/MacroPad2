using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using static RSoft.MacroPad.Cli.Diagnostics.RawInputNative;

namespace RSoft.MacroPad.Cli.Diagnostics;

/// <summary>
/// Observes Raw Input reports from selected HID devices through a message-only window.
/// Raw input registration is per usage page/usage rather than per device, so reports from
/// other keyboards and mice also arrive; they are dropped by device handle before any
/// keystroke is decoded, so only the selected macro pad is ever reported.
/// </summary>
public sealed class RawInputMonitor : IDisposable
{
    private const string WindowClassName = "RSoftMacroPadRawInput";
    private const int ErrorClassAlreadyExists = 1410;

    private static readonly Regex DeviceNamePattern = new(
        @"VID_(?<vid>[0-9A-F]{4})&PID_(?<pid>[0-9A-F]{4})(?:.*?&(?<col>COL[0-9]{2}))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IReadOnlyCollection<(ushort VendorId, ushort ProductId)> _filter;
    private readonly Dictionary<IntPtr, DeviceIdentity> _devices = new();
    private readonly object _stopLock = new();

    private WndProc? _wndProcDelegate;
    private IntPtr _window = IntPtr.Zero;
    private IntPtr _buffer = IntPtr.Zero;
    private int _bufferSize;
    private bool _stopRequested;
    private bool _disposed;

    private Action<RawInputEvent>? _sink;

    /// <param name="filter">
    /// Vendor/product ids to report on. An empty collection reports every raw input device.
    /// </param>
    public RawInputMonitor(IReadOnlyCollection<(ushort VendorId, ushort ProductId)> filter)
    {
        _filter = filter;
    }

    /// <summary>Devices matched by the filter, resolved when <see cref="Run"/> starts.</summary>
    public IReadOnlyList<DeviceIdentity> MatchedDevices { get; private set; } = [];

    /// <summary>
    /// Pumps raw input messages until <see cref="RequestStop"/> is called or the message loop ends.
    /// Blocks the calling thread, which becomes the owner of the message-only window.
    /// </summary>
    /// <param name="onEvent">Called on the pumping thread for every report from a matched device.</param>
    /// <param name="onStarted">Called once registration succeeds, before the first report.</param>
    public void Run(Action<RawInputEvent> onEvent, Action? onStarted = null)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RawInputMonitor));
        }

        _sink = onEvent;

        var matched = EnumerateDevices();
        MatchedDevices = matched;
        if (matched.Count == 0)
        {
            throw new InvalidOperationException(
                "No matching raw input device is connected. Run 'list-devices' to check, or pass --all.");
        }

        CreateMessageWindow();
        RegisterForRawInput(matched);
        onStarted?.Invoke();

        lock (_stopLock)
        {
            if (_stopRequested)
            {
                return;
            }
        }

        while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            DispatchMessageW(ref message);
        }
    }

    /// <summary>Stops <see cref="Run"/>. Safe to call from any thread, including a Ctrl+C handler.</summary>
    public void RequestStop()
    {
        lock (_stopLock)
        {
            _stopRequested = true;
            if (_window != IntPtr.Zero)
            {
                PostMessageW(_window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        }
    }

    private void CreateMessageWindow()
    {
        var instance = GetModuleHandleW(null);
        _wndProcDelegate = HandleMessage;

        var windowClass = new WNDCLASS
        {
            WndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            Instance = instance,
            ClassName = WindowClassName
        };

        if (RegisterClassW(ref windowClass) == 0 && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
        {
            throw new InvalidOperationException($"RegisterClass failed (error {Marshal.GetLastWin32Error()}).");
        }

        var window = CreateWindowExW(0, WindowClassName, null, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, instance, IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed (error {Marshal.GetLastWin32Error()}).");
        }

        lock (_stopLock)
        {
            _window = window;
        }
    }

    private void RegisterForRawInput(IReadOnlyList<DeviceIdentity> devices)
    {
        var registrations = devices
            .Select(device => (device.UsagePage, device.Usage))
            .Distinct()
            .Select(usage => new RAWINPUTDEVICE
            {
                UsagePage = usage.UsagePage,
                Usage = usage.Usage,
                Flags = RIDEV_INPUTSINK,
                Target = _window
            })
            .ToArray();

        if (!RegisterRawInputDevices(registrations, (uint)registrations.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            throw new InvalidOperationException($"RegisterRawInputDevices failed (error {Marshal.GetLastWin32Error()}).");
        }
    }

    private IntPtr HandleMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WM_INPUT:
                ProcessRawInput(lParam);
                return DefWindowProcW(window, message, wParam, lParam);
            case WM_CLOSE:
                DestroyWindow(window);
                return IntPtr.Zero;
            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProcW(window, message, wParam, lParam);
        }
    }

    private void ProcessRawInput(IntPtr rawInputHandle)
    {
        var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        if (GetRawInputData(rawInputHandle, RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0 || size == 0)
        {
            return;
        }

        EnsureBuffer((int)size);
        if (GetRawInputData(rawInputHandle, RID_INPUT, _buffer, ref size, headerSize) != size)
        {
            return;
        }

        var type = (uint)Marshal.ReadInt32(_buffer, 0);
        var device = Marshal.ReadIntPtr(_buffer, sizeof(uint) * 2);
        if (!TryGetIdentity(device, out var identity))
        {
            return;
        }

        var data = _buffer + (int)headerSize;
        switch (type)
        {
            case RIM_TYPEKEYBOARD:
                EmitKeyboard(identity, data);
                break;
            case RIM_TYPEMOUSE:
                EmitMouse(identity, data);
                break;
            case RIM_TYPEHID:
                EmitHid(identity, data, (int)size - (int)headerSize);
                break;
        }
    }

    private void EmitKeyboard(DeviceIdentity identity, IntPtr data)
    {
        var makeCode = (ushort)Marshal.ReadInt16(data, 0);
        var flags = (ushort)Marshal.ReadInt16(data, 2);
        var virtualKey = (ushort)Marshal.ReadInt16(data, 6);

        // 0xFF is the overrun/no-event placeholder some pads emit alongside a real key.
        if (virtualKey == 0xFF)
        {
            return;
        }

        Emit(identity, RawInputKind.Keyboard, RawInputDecoder.DescribeKeyboard(virtualKey, makeCode, flags), []);
    }

    private void EmitMouse(DeviceIdentity identity, IntPtr data)
    {
        var buttonFlags = (ushort)Marshal.ReadInt16(data, 4);
        var buttonData = (ushort)Marshal.ReadInt16(data, 6);
        var lastX = Marshal.ReadInt32(data, 12);
        var lastY = Marshal.ReadInt32(data, 16);

        if (buttonFlags == 0 && lastX == 0 && lastY == 0)
        {
            return;
        }

        Emit(identity, RawInputKind.Mouse, RawInputDecoder.DescribeMouse(buttonFlags, buttonData, lastX, lastY), []);
    }

    private void EmitHid(DeviceIdentity identity, IntPtr data, int available)
    {
        var reportSize = Marshal.ReadInt32(data, 0);
        var reportCount = Marshal.ReadInt32(data, 4);
        if (reportSize <= 0 || reportCount <= 0)
        {
            return;
        }

        for (var index = 0; index < reportCount; index++)
        {
            var offset = 8 + (index * reportSize);
            if (offset + reportSize > available)
            {
                return;
            }

            var report = new byte[reportSize];
            Marshal.Copy(data + offset, report, 0, reportSize);
            Emit(identity, RawInputKind.Hid, Convert.ToHexString(report), report);
        }
    }

    private void Emit(DeviceIdentity identity, RawInputKind kind, string description, byte[] rawData)
    {
        _sink?.Invoke(new RawInputEvent(
            DateTime.Now,
            kind,
            identity.VendorId,
            identity.ProductId,
            identity.Collection,
            description,
            rawData));
    }

    private bool TryGetIdentity(IntPtr device, out DeviceIdentity identity)
    {
        return _devices.TryGetValue(device, out identity!);
    }

    private void EnsureBuffer(int size)
    {
        if (_bufferSize >= size)
        {
            return;
        }

        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
        }

        _buffer = Marshal.AllocHGlobal(size);
        _bufferSize = size;
    }

    private List<DeviceIdentity> EnumerateDevices()
    {
        _devices.Clear();
        var matched = new List<DeviceIdentity>();

        uint count = 0;
        var entrySize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(null, ref count, entrySize) == unchecked((uint)-1))
        {
            throw new InvalidOperationException($"GetRawInputDeviceList failed (error {Marshal.GetLastWin32Error()}).");
        }

        if (count == 0)
        {
            // Raw Input is bound to the window station: a process on a non-interactive one
            // (a service, or a session 0 shell) sees an empty device list rather than an error.
            throw new InvalidOperationException(
                "Raw Input reports no devices. Run dump-input from an interactive desktop session.");
        }

        var list = new RAWINPUTDEVICELIST[count];
        if (GetRawInputDeviceList(list, ref count, entrySize) == unchecked((uint)-1))
        {
            throw new InvalidOperationException($"GetRawInputDeviceList failed (error {Marshal.GetLastWin32Error()}).");
        }

        foreach (var entry in list.Take((int)count))
        {
            var name = GetDeviceName(entry.Device);
            if (name == null)
            {
                continue;
            }

            var match = DeviceNamePattern.Match(name);
            if (!match.Success)
            {
                continue;
            }

            var vendorId = ushort.Parse(match.Groups["vid"].Value, System.Globalization.NumberStyles.HexNumber);
            var productId = ushort.Parse(match.Groups["pid"].Value, System.Globalization.NumberStyles.HexNumber);
            if (_filter.Count > 0 && !_filter.Contains((vendorId, productId)))
            {
                continue;
            }

            var (usagePage, usage) = GetUsage(entry.Device, entry.Type);
            var identity = new DeviceIdentity(
                vendorId,
                productId,
                match.Groups["col"].Success ? match.Groups["col"].Value.ToLowerInvariant() : "-",
                usagePage,
                usage,
                name);

            _devices[entry.Device] = identity;
            matched.Add(identity);
        }

        return matched;
    }

    private static string? GetDeviceName(IntPtr device)
    {
        uint size = 0;
        if (GetRawInputDeviceInfoW(device, RIDI_DEVICENAME, IntPtr.Zero, ref size) != 0 || size == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)size * sizeof(char));
        try
        {
            return GetRawInputDeviceInfoW(device, RIDI_DEVICENAME, buffer, ref size) == unchecked((uint)-1)
                ? null
                : Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (ushort UsagePage, ushort Usage) GetUsage(IntPtr device, uint type)
    {
        // Keyboard and mouse arms of RID_DEVICE_INFO carry no usage page, so use their fixed
        // generic desktop usages; only vendor/consumer collections need to be queried.
        if (type == RIM_TYPEKEYBOARD)
        {
            return (0x01, 0x06);
        }

        if (type == RIM_TYPEMOUSE)
        {
            return (0x01, 0x02);
        }

        var size = (uint)Marshal.SizeOf<RID_DEVICE_INFO>();
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            Marshal.WriteInt32(buffer, 0, (int)size);
            if (GetRawInputDeviceInfoW(device, RIDI_DEVICEINFO, buffer, ref size) == unchecked((uint)-1))
            {
                return (0, 0);
            }

            var info = Marshal.PtrToStructure<RID_DEVICE_INFO>(buffer);
            return (info.UsagePage, info.Usage);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_stopLock)
        {
            if (_window != IntPtr.Zero)
            {
                DestroyWindow(_window);
                _window = IntPtr.Zero;
            }
        }

        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _bufferSize = 0;
        }
    }

    /// <param name="Collection">Collection tag such as "col01", "-" when the name carries none.</param>
    public sealed record DeviceIdentity(
        ushort VendorId,
        ushort ProductId,
        string Collection,
        ushort UsagePage,
        ushort Usage,
        string Name);
}
