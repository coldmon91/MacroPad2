using HidSharp;
using RSoft.MacroPad.BLL.Infrasturture.Configuration;
using RSoft.MacroPad.BLL.Infrasturture.Physical;
using RSoft.MacroPad.Cli.Config;
using RSoft.MacroPad.Cli.Usb;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RSoft.MacroPad.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintUsage();
                return 0;
            }

            return args[0] switch
            {
                "list-devices" => ListDevices(),
                "apply" => Apply(args.Skip(1).ToArray()),
                _ => Fail($"Unknown command '{args[0]}'.")
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int ListDevices()
    {
        var devices = DeviceList.Local.GetHidDevices()
            .OrderBy(device => device.VendorID)
            .ThenBy(device => device.ProductID)
            .ThenBy(device => device.DevicePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Console.WriteLine("VID dec VID hex PID dec PID hex UsagePage MaxOut Product Manufacturer");
        foreach (var device in devices)
        {
            Console.WriteLine(
                $"{device.VendorID,7} 0x{device.VendorID:X4} {device.ProductID,7} 0x{device.ProductID:X4} 0x{GetUsagePage(device):X4} {SafeGetInt(device.GetMaxOutputReportLength),6} {SafeGet(device.GetProductName),-30} {SafeGet(device.GetManufacturer)}");
        }

        return 0;
    }

    private static int Apply(string[] args)
    {
        if (args.Length == 0)
        {
            return Fail("apply requires a YAML file path.");
        }

        var yamlPath = args[0];
        var dryRun = args.Skip(1).Any(arg => arg == "--dry-run");
        var unknownArg = args.Skip(1).FirstOrDefault(arg => arg != "--dry-run");
        if (unknownArg != null)
        {
            return Fail($"Unknown apply option '{unknownArg}'.");
        }

        var macroConfig = LoadMacroConfig(yamlPath);
        var configuration = LoadDeviceConfiguration();
        var layouts = LoadLayouts();
        var profiles = DeviceProfileResolver.ResolveProfiles(macroConfig.Device, configuration.SupportedDevices);
        var applier = new BindingApplier();

        if (dryRun)
        {
            var profile = DeviceProfileResolver.ResolveDryRunProfile(profiles, layouts, macroConfig);
            var layout = DeviceProfileResolver.ResolveLayout(layouts, profile.VendorId, profile.ProductId, macroConfig);
            var reports = applier.BuildReports(macroConfig, layout, profile.ProtocolType, version: 0);

            Console.WriteLine($"Dry run for {DeviceProfileResolver.FormatVidPid(profile.VendorId, profile.ProductId)}, protocol {profile.ProtocolType}, layout '{layout.Name}'.");
            PrintReports(reports);
            return 0;
        }

        using var usb = new HidSharpUsb
        {
            SupportedDevices = configuration.SupportedDevices
                .Where(device => profiles.Any(profile => profile.VendorId == device.VendorId && profile.ProductId == device.ProductId))
                .Select(device => (device.VendorId, device.ProductId, device.PathPattern, device.ProtocolType))
                .ToArray()
        };

        if (!usb.Connect())
        {
            throw new InvalidOperationException("No supported HID macro pad could be opened.");
        }

        var connectedLayout = DeviceProfileResolver.ResolveLayout(layouts, usb.VendorId, usb.ProductId, macroConfig);
        applier.Apply(macroConfig, connectedLayout, usb);
        Console.WriteLine($"Applied {macroConfig.Bindings.Count} binding(s) to {DeviceProfileResolver.FormatVidPid(usb.VendorId, usb.ProductId)} using layout '{connectedLayout.Name}'.");
        return 0;
    }

    private static MacroConfig LoadMacroConfig(string yamlPath)
    {
        if (!File.Exists(yamlPath))
        {
            throw new FileNotFoundException($"YAML file '{yamlPath}' was not found.");
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var config = deserializer.Deserialize<MacroConfig>(File.ReadAllText(yamlPath))
                     ?? throw new InvalidOperationException("YAML file is empty.");
        config.Device = string.IsNullOrWhiteSpace(config.Device) ? "auto" : config.Device.Trim();
        config.Bindings ??= [];
        return config;
    }

    private static RSoft.MacroPad.BLL.Infrasturture.Configuration.Configuration LoadDeviceConfiguration()
    {
        var path = ResolveDataFile("config.txt", Path.Combine("..", "RSoft.MacroPad", "config.txt"));
        return new ConfigurationReader().Read(path)
               ?? throw new InvalidOperationException($"Could not read device configuration from '{path}'.");
    }

    private static KeyboardLayout[] LoadLayouts()
    {
        var path = ResolveDataFile("layouts.txt", Path.Combine("..", "RSoft.MacroPad", "layouts.txt"));
        return new LayoutParser().Parse(path);
    }

    private static string ResolveDataFile(string fileName, string sourceRelativePath)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(Environment.CurrentDirectory, fileName),
            Path.Combine(Environment.CurrentDirectory, sourceRelativePath)
        };

        return candidates.FirstOrDefault(File.Exists)
               ?? throw new FileNotFoundException($"Required data file '{fileName}' was not found.");
    }

    private static void PrintReports(IReadOnlyList<RSoft.MacroPad.BLL.Infrasturture.Protocol.Report> reports)
    {
        for (var index = 0; index < reports.Count; index++)
        {
            var report = reports[index];
            Console.WriteLine($"{index:000}: ReportId=0x{report.ReportId:X2} Data={Convert.ToHexString(report.Data)}");
        }
    }

    private static string SafeGet(Func<string> getter)
    {
        try
        {
            return getter() ?? "";
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
        catch (NotSupportedException)
        {
            return "";
        }
    }

    private static int SafeGetInt(Func<int> getter)
    {
        try
        {
            return getter();
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

    private static int GetUsagePage(HidDevice device)
    {
        try
        {
            var item = device.GetReportDescriptor().DeviceItems.FirstOrDefault();
            var usage = item?.Usages.GetAllValues().Cast<uint?>().FirstOrDefault();
            return usage.HasValue ? (int)(usage.Value >> 16) : 0;
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

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
        Usage:
          RSoft.MacroPad.Cli list-devices
          RSoft.MacroPad.Cli apply <file.yaml> [--dry-run]

        Commands:
          list-devices             List connected HID devices.
          apply <file.yaml>        Apply macro bindings to a supported macro pad.
          apply <file.yaml> --dry-run
                                   Build reports and print hex output without opening or writing a HID device.
        """);
    }
}
