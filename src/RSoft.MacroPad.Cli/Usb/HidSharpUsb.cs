using HidSharp;
using RSoft.MacroPad.BLL.Infrasturture.Model;
using RSoft.MacroPad.BLL.Infrasturture.Protocol;
using RSoft.MacroPad.BLL.Infrasturture.UsbDevice;

namespace RSoft.MacroPad.Cli.Usb;

public sealed class HidSharpUsb : UsbBase, IDisposable
{
    private HidDevice? _device;
    private HidStream? _stream;
    private int _outputReportLength;
    private bool _disposed;

    public override bool Write(Report report)
    {
        if (!CheckIfConnectedInternal() || _stream == null)
        {
            return false;
        }

        var bufferLength = _outputReportLength > 0 ? _outputReportLength : report.Data.Length + 1;
        var buffer = new byte[bufferLength];
        buffer[0] = report.ReportId;
        Array.Copy(report.Data, 0, buffer, 1, Math.Min(report.Data.Length, buffer.Length - 1));

        try
        {
            _stream.Write(buffer, 0, buffer.Length);
            return true;
        }
        catch (IOException)
        {
            IsConnected = false;
            return false;
        }
        catch (ObjectDisposedException)
        {
            IsConnected = false;
            return false;
        }
    }

    protected override bool CheckIfConnectedInternal()
    {
        return IsConnected = _stream is { CanWrite: true };
    }

    protected override bool ConnectInternal()
    {
        ThrowIfDisposed();
        DisposeStream();

        var devices = DeviceList.Local.GetHidDevices().ToArray();
        var supported = SupportedDevices.ToArray();
        var candidates = devices
            .SelectMany(device => supported
                .Where(profile => device.VendorID == profile.VendorId && device.ProductID == profile.ProductId)
                .Select(profile => new DeviceCandidate(device, profile.VendorId, profile.ProductId, profile.PathFragment, profile.protocolType)))
            .ToArray();

        foreach (var candidate in SelectPreferredCandidates(candidates))
        {
            try
            {
                _stream = candidate.Device.Open();
                _device = candidate.Device;
                _outputReportLength = Math.Max(1, GetOutputReportLength(candidate.Device));
                VendorId = candidate.VendorId;
                ProductId = candidate.ProductId;
                ProtocolType = candidate.ProtocolType;

                Connected();
                return IsConnected = true;
            }
            catch (IOException)
            {
                DisposeStream();
            }
            catch (UnauthorizedAccessException)
            {
                DisposeStream();
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

        DisposeStream();
        _disposed = true;
    }

    private static IEnumerable<DeviceCandidate> SelectPreferredCandidates(IEnumerable<DeviceCandidate> candidates)
    {
        return candidates
            .OrderByDescending(candidate => candidate.OutputReportLength >= 65)
            .ThenByDescending(candidate => candidate.OutputReportLength)
            .ThenByDescending(candidate => candidate.PathMatches)
            .ThenBy(candidate => candidate.Device.DevicePath, StringComparer.OrdinalIgnoreCase);
    }

    private void DisposeStream()
    {
        _stream?.Dispose();
        _stream = null;
        _device = null;
        _outputReportLength = 0;
        IsConnected = false;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(HidSharpUsb));
        }
    }

    private sealed class DeviceCandidate
    {
        public DeviceCandidate(
            HidDevice device,
            ushort vendorId,
            ushort productId,
            string pathFragment,
            ProtocolType protocolType)
        {
            Device = device;
            VendorId = vendorId;
            ProductId = productId;
            PathFragment = pathFragment;
            ProtocolType = protocolType;
        }

        public HidDevice Device { get; }
        public ushort VendorId { get; }
        public ushort ProductId { get; }
        public string PathFragment { get; }
        public ProtocolType ProtocolType { get; }
        public int OutputReportLength => GetOutputReportLength(Device);

        public bool PathMatches
            => string.IsNullOrWhiteSpace(PathFragment)
               || Device.DevicePath.Contains(PathFragment, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetOutputReportLength(HidDevice device)
    {
        try
        {
            return device.GetMaxOutputReportLength();
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
        catch (NotSupportedException)
        {
            return 0;
        }
    }
}
