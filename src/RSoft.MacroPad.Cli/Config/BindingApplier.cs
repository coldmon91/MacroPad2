using RSoft.MacroPad.BLL.Infrasturture;
using RSoft.MacroPad.BLL.Infrasturture.Model;
using RSoft.MacroPad.BLL.Infrasturture.Physical;
using RSoft.MacroPad.BLL.Infrasturture.Protocol;
using RSoft.MacroPad.BLL.Infrasturture.Protocol.Mappers;
using RSoft.MacroPad.BLL.Infrasturture.UsbDevice;

namespace RSoft.MacroPad.Cli.Config;

public sealed class BindingApplier
{
    // Device-specific key id for the single knob (see doc/protocol-6key-1knob.md):
    // knob N -> left/push/right = 0x10 + (N-1)*3 + {0,1,2}. The shared BLL mapper
    // uses 0x0D~0x0F, which is wrong for this hardware, so knob ids are corrected here.
    private const byte KnobKeyIdBase = 0x10;

    private readonly ComposerRepository _composerRepository = new();

    public IReadOnlyList<Report> BuildReports(
        MacroConfig config,
        KeyboardLayout layout,
        ProtocolType protocolType,
        byte version)
    {
        ValidateConfig(config, layout);

        var composer = _composerRepository.Get(protocolType, version);
        var reports = new List<Report>();
        var layer = checked((byte)config.Layer);

        foreach (var binding in config.Bindings)
        {
            reports.AddRange(BuildBindingReports(composer, layout, layer, binding, version));
        }

        return reports;
    }

    public void Apply(MacroConfig config, KeyboardLayout layout, IUsb usb)
    {
        var reports = BuildReports(config, layout, usb.ProtocolType, usb.Version);
        for (var index = 0; index < reports.Count; index++)
        {
            if (!usb.Write(reports[index]))
            {
                throw new IOException($"Failed to write HID report at sequence index {index}.");
            }
        }
    }

    private static IEnumerable<Report> BuildBindingReports(
        IReportComposer composer,
        KeyboardLayout layout,
        byte layer,
        BindingConfig binding,
        byte reportId)
    {
        if (binding.Button.HasValue && binding.Knob.HasValue)
        {
            throw new InvalidOperationException("A binding cannot target both button and knob.");
        }

        if (binding.Led != null && (binding.Button.HasValue || binding.Knob.HasValue))
        {
            throw new InvalidOperationException("LED bindings target the selected layer and must not set button or knob.");
        }

        if (binding.Led != null && binding.Button == null && binding.Knob == null)
        {
            ValidateLed(layout, binding.Led);
            return composer.Led(layer, binding.Led.Mode, binding.Led.Color);
        }

        if (binding.Button.HasValue)
        {
            if (binding.Left != null || binding.Push != null || binding.Right != null)
            {
                throw new InvalidOperationException($"Button {binding.Button.Value} must not define knob direction actions.");
            }

            var action = ToButtonAction(binding.Button.Value);
            ValidateActionExists(layout, action, $"button {binding.Button.Value}");
            return BuildActionReports(composer, action, layer, binding, reportId);
        }

        if (binding.Knob.HasValue)
        {
            if (HasDirectAction(binding))
            {
                throw new InvalidOperationException($"Knob {binding.Knob.Value} must define actions under left, push, or right.");
            }

            return BuildKnobReports(composer, layout, layer, binding, reportId);
        }

        throw new InvalidOperationException("Each binding must target button, knob, or top-level led.");
    }

    private static IEnumerable<Report> BuildKnobReports(
        IReportComposer composer,
        KeyboardLayout layout,
        byte layer,
        BindingConfig binding,
        byte reportId)
    {
        var knob = binding.Knob!.Value;
        var reports = new List<Report>();

        AddKnobDirection(composer, layout, layer, reports, knob, "Left", binding.Left, reportId);
        AddKnobDirection(composer, layout, layer, reports, knob, "Push", binding.Push, reportId);
        AddKnobDirection(composer, layout, layer, reports, knob, "Right", binding.Right, reportId);

        if (reports.Count == 0)
        {
            throw new InvalidOperationException($"Knob {knob} binding must define at least one of left, push, or right.");
        }

        return reports;
    }

    private static void AddKnobDirection(
        IReportComposer composer,
        KeyboardLayout layout,
        byte layer,
        List<Report> reports,
        int knob,
        string direction,
        ActionConfig? actionConfig,
        byte reportId)
    {
        if (actionConfig == null)
        {
            return;
        }

        var action = ToKnobAction(knob, direction);
        ValidateActionExists(layout, action, $"knob {knob} {direction.ToLowerInvariant()}");
        reports.AddRange(BuildActionReports(composer, action, layer, actionConfig, reportId));
    }

    private static IEnumerable<Report> BuildActionReports(
        IReportComposer composer,
        InputAction inputAction,
        byte layer,
        BindingConfig binding,
        byte reportId)
    {
        var actionConfig = new ActionConfig
        {
            Keys = binding.Keys,
            Media = binding.Media,
            Mouse = binding.Mouse,
            Modifiers = binding.Modifiers
        };

        return BuildActionReports(composer, inputAction, layer, actionConfig, reportId);
    }

    private static IEnumerable<Report> BuildActionReports(
        IReportComposer composer,
        InputAction inputAction,
        byte layer,
        ActionConfig actionConfig,
        byte reportId)
    {
        var actionCount = CountActions(actionConfig);
        if (actionCount != 1)
        {
            throw new InvalidOperationException($"Action {inputAction} must define exactly one of keys, media, or mouse.");
        }

        if (!string.IsNullOrWhiteSpace(actionConfig.Modifiers) && string.IsNullOrWhiteSpace(actionConfig.Mouse))
        {
            throw new InvalidOperationException($"Action {inputAction} can only use modifiers with mouse.");
        }

        if (!string.IsNullOrWhiteSpace(actionConfig.Keys))
        {
            var sequence = KeyStringParser.ParseKeySequence(actionConfig.Keys);
            var reports = composer.Key(inputAction, layer, delay: 0, sequence);
            return FixKnobKeyId(reports, inputAction);
        }

        if (!string.IsNullOrWhiteSpace(actionConfig.Media))
        {
            var reports = composer.Media(inputAction, layer, KeyStringParser.ParseMediaKey(actionConfig.Media));
            return FixKnobKeyId(reports, inputAction);
        }

        var button = KeyStringParser.ParseMouseButton(actionConfig.Mouse!);
        var modifiers = KeyStringParser.ParseModifiers(actionConfig.Modifiers);
        return new[] { BuildMouseReport(reportId, inputAction, layer, button, modifiers) };
    }

    // Builds the device-correct mouse report. The shared BLL mouse encoding
    // (KeyType.Multimedia, scroll byte at a different offset) does not work on this
    // hardware; the layout below is the verified one (see doc/protocol-6key-1knob.md).
    private static Report BuildMouseReport(
        byte reportId,
        InputAction inputAction,
        byte layer,
        MouseButton button,
        Modifier modifiers)
    {
        var report = new MacroPadReport(reportId);
        var data = report.Data;

        data[0] = ProtocolMagic;               // wire[1]  bind command
        data[1] = DeviceKeyId(inputAction);    // wire[2]  key id
        data[2] = layer;                       // wire[3]  layer
        data[3] = (byte)KeyType.Mouse;         // wire[4]  macro type = mouse

        var scroll = button.Scroll();
        if (scroll != 0)
        {
            data[9] = MouseActionWheel;        // wire[10] mouse action = wheel
            data[10] = (byte)modifiers;        // wire[11] modifier
            data[14] = scroll;                 // wire[15] wheel delta (0x01 up / 0xFF down)
        }
        else
        {
            data[9] = MouseActionClick;        // wire[10] mouse action = click
            data[10] = (byte)modifiers;        // wire[11] modifier
            data[11] = button.Button();        // wire[12] button mask
        }

        return report;
    }

    private static IEnumerable<Report> FixKnobKeyId(IEnumerable<Report> reports, InputAction inputAction)
    {
        if (!IsKnob(inputAction))
        {
            return reports;
        }

        var id = DeviceKeyId(inputAction);
        var materialized = reports.ToArray();
        foreach (var report in materialized)
        {
            // ExtendedReport stores the key id at Data[1]; override the BLL mapping.
            report.Data[1] = id;
        }

        return materialized;
    }

    private static bool IsKnob(InputAction action)
        => action >= InputAction.Knob1Left && action <= InputAction.Knob3Right;

    private static byte DeviceKeyId(InputAction action)
    {
        if (IsKnob(action))
        {
            return (byte)(KnobKeyIdBase + ((int)action - (int)InputAction.Knob1Left));
        }

        return (byte)action; // Key1..Key12 map directly to 1..12.
    }

    private const byte ProtocolMagic = 0xFD;
    private const byte MouseActionClick = 0x01;
    private const byte MouseActionWheel = 0x03;

    private static void ValidateConfig(MacroConfig config, KeyboardLayout layout)
    {
        if (config.Layer < 0 || config.Layer >= layout.LayerCount)
        {
            throw new InvalidOperationException($"Layer {config.Layer} is out of range. Layout '{layout.Name}' supports layers 0 ~ {layout.LayerCount - 1}.");
        }

        if (config.Bindings.Count == 0)
        {
            throw new InvalidOperationException("bindings must contain at least one binding.");
        }
    }

    private static void ValidateLed(KeyboardLayout layout, LedConfig led)
    {
        if ((byte)led.Mode >= layout.LedModeCount)
        {
            throw new InvalidOperationException($"LED mode {led.Mode} is out of range. Layout '{layout.Name}' supports {layout.LedModeCount} LED mode(s).");
        }

        if (!layout.SupportsColor && led.Color != LedColor.Random)
        {
            throw new InvalidOperationException($"Layout '{layout.Name}' does not support LED colors.");
        }
    }

    private static void ValidateActionExists(KeyboardLayout layout, InputAction action, string target)
    {
        if (!layout.Controls.SelectMany(control => control.Actions).Contains(action))
        {
            throw new InvalidOperationException($"Layout '{layout.Name}' does not contain {target}.");
        }
    }

    private static InputAction ToButtonAction(int button)
    {
        if (button is < 1 or > 12)
        {
            throw new InvalidOperationException($"Button {button} is out of range. Supported button numbers are 1 ~ 12.");
        }

        return (InputAction)button;
    }

    private static InputAction ToKnobAction(int knob, string direction)
    {
        if (knob is < 1 or > 3)
        {
            throw new InvalidOperationException($"Knob {knob} is out of range. Supported knob numbers are 1 ~ 3.");
        }

        if (!Enum.TryParse<InputAction>($"Knob{knob}{direction}", ignoreCase: false, out var action))
        {
            throw new InvalidOperationException($"Knob action Knob{knob}{direction} is not supported by the BLL model.");
        }

        return action;
    }

    private static int CountActions(ActionConfig actionConfig)
    {
        var count = 0;
        if (!string.IsNullOrWhiteSpace(actionConfig.Keys))
        {
            count++;
        }

        if (!string.IsNullOrWhiteSpace(actionConfig.Media))
        {
            count++;
        }

        if (!string.IsNullOrWhiteSpace(actionConfig.Mouse))
        {
            count++;
        }

        return count;
    }

    private static bool HasDirectAction(BindingConfig binding)
    {
        return !string.IsNullOrWhiteSpace(binding.Keys)
               || !string.IsNullOrWhiteSpace(binding.Media)
               || !string.IsNullOrWhiteSpace(binding.Mouse)
               || !string.IsNullOrWhiteSpace(binding.Modifiers);
    }
}
