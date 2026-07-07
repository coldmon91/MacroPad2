using RSoft.MacroPad.BLL.Infrasturture.Physical;

namespace RSoft.MacroPad.Cli.Config;

public static class DeviceProfileResolver
{
    public static IReadOnlyList<DeviceProfile> ResolveProfiles(
        string deviceSelector,
        IEnumerable<(ushort VendorId, ushort ProductId, string PathPattern, RSoft.MacroPad.BLL.Infrasturture.Model.ProtocolType ProtocolType)> supportedDevices)
    {
        var profiles = supportedDevices
            .Select(device => new DeviceProfile(device.VendorId, device.ProductId, device.ProtocolType))
            .Distinct()
            .ToArray();

        if (string.Equals(deviceSelector, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return profiles;
        }

        var (vendorId, productId) = ParseVidPid(deviceSelector);
        var filtered = profiles
            .Where(device => device.VendorId == vendorId && device.ProductId == productId)
            .ToArray();

        if (filtered.Length == 0)
        {
            throw new InvalidOperationException($"Device {FormatVidPid(vendorId, productId)} is not listed in config.txt.");
        }

        return filtered;
    }

    public static DeviceProfile ResolveDryRunProfile(
        IReadOnlyList<DeviceProfile> profiles,
        KeyboardLayout[] layouts,
        MacroConfig config)
    {
        foreach (var profile in profiles)
        {
            if (TryResolveLayout(layouts, profile.VendorId, profile.ProductId, config, out _))
            {
                return profile;
            }
        }

        if (profiles.Count > 0)
        {
            return profiles[0];
        }

        throw new InvalidOperationException("No supported devices were loaded from config.txt.");
    }

    public static KeyboardLayout ResolveLayout(
        KeyboardLayout[] layouts,
        ushort vendorId,
        ushort productId,
        MacroConfig config)
    {
        if (TryResolveLayout(layouts, vendorId, productId, config, out var layout))
        {
            return layout;
        }

        throw new InvalidOperationException($"No compatible layout was found for device {FormatVidPid(vendorId, productId)}.");
    }

    public static (ushort VendorId, ushort ProductId) ParseVidPid(string value)
    {
        var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new FormatException("device must be 'auto' or 'VID:PID'.");
        }

        return (ParseUshort(parts[0], "VID"), ParseUshort(parts[1], "PID"));
    }

    public static string FormatVidPid(ushort vendorId, ushort productId)
        => $"{vendorId}:{productId} (0x{vendorId:X4}:0x{productId:X4})";

    private static bool TryResolveLayout(
        KeyboardLayout[] layouts,
        ushort vendorId,
        ushort productId,
        MacroConfig config,
        out KeyboardLayout layout)
    {
        var exactMatches = layouts
            .Where(candidate => candidate.Products.Any(product => product.VendorId == vendorId && product.ProductId == productId));

        var productMatches = layouts
            .Where(candidate => candidate.Products.Any(product => product.ProductId == productId));

        layout = exactMatches
            .Concat(productMatches)
            .Distinct()
            .OrderBy(candidate => CompatibilityPenalty(candidate, config))
            .ThenBy(candidate => candidate.Controls.Count())
            .FirstOrDefault(candidate => CompatibilityPenalty(candidate, config) == 0)!;

        return layout != null;
    }

    private static int CompatibilityPenalty(KeyboardLayout layout, MacroConfig config)
    {
        var maxButton = layout.Controls
            .OfType<PhysicalButton>()
            .SelectMany(control => control.Actions)
            .Select(action => (int)action)
            .DefaultIfEmpty(0)
            .Max();

        var maxKnob = layout.Controls.OfType<PhysicalKnob>().Count();
        var penalty = 0;

        foreach (var binding in config.Bindings)
        {
            if (binding.Button.HasValue && binding.Button.Value > maxButton)
            {
                penalty++;
            }

            if (binding.Knob.HasValue && binding.Knob.Value > maxKnob)
            {
                penalty++;
            }
        }

        if (config.Layer < 0 || config.Layer >= layout.LayerCount)
        {
            penalty++;
        }

        return penalty;
    }

    private static ushort ParseUshort(string value, string name)
    {
        var numberStyle = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? System.Globalization.NumberStyles.HexNumber
            : System.Globalization.NumberStyles.Integer;
        var normalized = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;

        if (!ushort.TryParse(normalized, numberStyle, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            throw new FormatException($"{name} '{value}' is not a valid 16-bit number.");
        }

        return parsed;
    }
}
