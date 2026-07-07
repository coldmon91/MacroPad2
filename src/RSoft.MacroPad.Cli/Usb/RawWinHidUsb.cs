using System.Runtime.InteropServices;
using HidLibrary;
using RSoft.MacroPad.BLL.Infrasturture.Protocol;
using RSoft.MacroPad.BLL.Infrasturture.UsbDevice;

namespace RSoft.MacroPad.Cli.Usb;

/// <summary>
/// Writes HID output reports via raw overlapped WriteFile instead of HidLibrary/HidSharp,
/// both of which fail with ERROR_INVALID_PARAMETER on this hardware (VID 0x1189 / PID 0x8840,
/// a WCH CH552-based macro pad). Two device-specific quirks, confirmed against an independent
/// reverse-engineering of this exact VID:PID (see https://github.com/mikhailvs/macropad),
/// are corrected here rather than in the shared BLL so other device variants are unaffected:
///   - Only report ID 3 is registered as an Output report in this device's HID descriptor;
///     report IDs 0/2 (tried by the shared version-check logic) fail immediately.
///   - The device expects a magic byte of 0xFD at the start of the report payload, not the
///     0xFE the shared BLL's ExtendedReport builds; writes with 0xFE are silently aborted by
///     the device (surfaces as ERROR_OPERATION_ABORTED on the completed overlapped write).
///     Each write must also be followed by a commit packet (03 FD FE FF) plus a short delay
///     for the firmware to persist it.
/// </summary>
public sealed class RawWinHidUsb : UsbBase, IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int ErrorIoPending = 997;
    private const uint WriteTimeoutMs = 1000;
    private const byte DeviceReportId = 3;
    private const byte BllMagicByte = 0xFE;
    private const byte DeviceMagicByte = 0xFD;
    private const int CommitDelayMs = 200;

    private IntPtr _handle = new(-1);
    private int _outputReportLength;
    private bool _disposed;

    protected override byte KeyBoardVersionCheck()
    {
        // Probing report IDs 0/2 reliably fails ERROR_INVALID_PARAMETER on this device
        // (only report ID 3 is registered in its HID descriptor); skip straight to it.
        return Version = DeviceReportId;
    }

    public override bool Write(Report report)
    {
        if (_handle == new IntPtr(-1))
        {
            return false;
        }

        var buffer = BuildFrame(report.ReportId, report.Data);
        if (buffer.Length > 1 && buffer[1] == BllMagicByte)
        {
            buffer[1] = DeviceMagicByte;
        }

        // Write the binding, then the commit sequence the firmware needs to persist it:
        // separator (03 AA AA), finalize (03 FD FE FF), terminator (03 AA AA).
        if (!WriteRaw(buffer)
            || !WriteRaw(BuildFrame(DeviceReportId, 0xAA, 0xAA))
            || !WriteRaw(BuildFrame(DeviceReportId, DeviceMagicByte, 0xFE, 0xFF))
            || !WriteRaw(BuildFrame(DeviceReportId, 0xAA, 0xAA)))
        {
            return false;
        }

        Thread.Sleep(CommitDelayMs);
        return true;
    }

    private byte[] BuildFrame(byte reportId, params byte[] payload)
    {
        var length = _outputReportLength > 0 ? _outputReportLength : payload.Length + 1;
        var buffer = new byte[length];
        buffer[0] = reportId;
        Array.Copy(payload, 0, buffer, 1, Math.Min(payload.Length, buffer.Length - 1));
        return buffer;
    }

    private bool WriteRaw(byte[] buffer)
    {
        var eventHandle = CreateEvent(IntPtr.Zero, true, false, null);
        try
        {
            var overlapped = new NativeOverlapped { OffsetLow = 0xFFFFFFFF, OffsetHigh = 0xFFFFFFFF, hEvent = eventHandle };
            var ok = WriteFile(_handle, buffer, (uint)buffer.Length, out _, ref overlapped);
            if (ok)
            {
                return true;
            }

            if (Marshal.GetLastWin32Error() != ErrorIoPending)
            {
                return false;
            }

            var waitResult = WaitForSingleObject(eventHandle, WriteTimeoutMs);
            if (waitResult != 0) // WAIT_OBJECT_0
            {
                CancelIo(_handle);
                return false;
            }

            return GetOverlappedResult(_handle, ref overlapped, out _, false);
        }
        finally
        {
            CloseHandle(eventHandle);
        }
    }

    protected override bool CheckIfConnectedInternal()
    {
        return IsConnected = _handle != new IntPtr(-1);
    }

    protected override bool ConnectInternal()
    {
        foreach (var supported in SupportedDevices)
        {
            var candidates = HidDevices.Enumerate(supported.VendorId, supported.ProductId)
                .Where(d => string.IsNullOrWhiteSpace(supported.PathFragment) || d.DevicePath.Contains(supported.PathFragment, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach (var candidate in candidates)
            {
                var handle = CreateFile(candidate.DevicePath, GenericRead | GenericWrite, 0, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
                if (handle == new IntPtr(-1))
                {
                    continue;
                }

                _handle = handle;
                _outputReportLength = Math.Max(1, (int)candidate.Capabilities.OutputReportByteLength);
                VendorId = supported.VendorId;
                ProductId = supported.ProductId;
                ProtocolType = supported.protocolType;

                Connected();
                return IsConnected = true;
            }
        }

        return IsConnected = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_handle != new IntPtr(-1))
        {
            CloseHandle(_handle);
            _handle = new IntPtr(-1);
        }

        _disposed = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOverlapped
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint OffsetLow;
        public uint OffsetHigh;
        public IntPtr hEvent;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr file, byte[] buffer, uint numberOfBytesToWrite, out uint numberOfBytesWritten, ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(IntPtr file, ref NativeOverlapped overlapped, out uint numberOfBytesTransferred, bool wait);

    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateEvent(IntPtr eventAttributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool CancelIo(IntPtr file);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
