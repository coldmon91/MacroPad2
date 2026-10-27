namespace RSoft.MacroPad.Cli.Diagnostics;

public enum RawInputKind
{
    Keyboard,
    Mouse,
    Hid
}

/// <summary>
/// A single decoded Raw Input notification, tagged with the physical device it came from.
/// </summary>
/// <param name="Timestamp">Local time the report was dequeued.</param>
/// <param name="Kind">Which raw input arm produced the report.</param>
/// <param name="VendorId">Vendor id parsed from the device interface name, 0 when unknown.</param>
/// <param name="ProductId">Product id parsed from the device interface name, 0 when unknown.</param>
/// <param name="Collection">Collection tag (e.g. "col01") parsed from the device interface name.</param>
/// <param name="Description">Human readable decoding of the report.</param>
/// <param name="RawData">Report payload bytes, empty for keyboard and mouse reports.</param>
public sealed record RawInputEvent(
    DateTime Timestamp,
    RawInputKind Kind,
    ushort VendorId,
    ushort ProductId,
    string Collection,
    string Description,
    byte[] RawData);
