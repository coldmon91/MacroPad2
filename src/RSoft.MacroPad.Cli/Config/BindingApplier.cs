using RSoft.MacroPad.BLL.Infrasturture;
using RSoft.MacroPad.BLL.Infrasturture.Model;
using RSoft.MacroPad.BLL.Infrasturture.Physical;
using RSoft.MacroPad.BLL.Infrasturture.Protocol;
using RSoft.MacroPad.BLL.Infrasturture.UsbDevice;

namespace RSoft.MacroPad.Cli.Config;

public sealed class BindingApplier
{
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
            reports.AddRange(BuildBindingReports(composer, layout, layer, binding));
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
        BindingConfig binding)
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
            return BuildActionReports(composer, action, layer, binding);
        }

        if (binding.Knob.HasValue)
        {
            if (HasDirectAction(binding))
            {
                throw new InvalidOperationException($"Knob {binding.Knob.Value} must define actions under left, push, or right.");
            }

            return BuildKnobReports(composer, layout, layer, binding);
        }

        throw new InvalidOperationException("Each binding must target button, knob, or top-level led.");
    }

    private static IEnumerable<Report> BuildKnobReports(
        IReportComposer composer,
        KeyboardLayout layout,
        byte layer,
        BindingConfig binding)
    {
        var knob = binding.Knob!.Value;
        var reports = new List<Report>();

        AddKnobDirection(composer, layout, layer, reports, knob, "Left", binding.Left);
        AddKnobDirection(composer, layout, layer, reports, knob, "Push", binding.Push);
        AddKnobDirection(composer, layout, layer, reports, knob, "Right", binding.Right);

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
        ActionConfig? actionConfig)
    {
        if (actionConfig == null)
        {
            return;
        }

        var action = ToKnobAction(knob, direction);
        ValidateActionExists(layout, action, $"knob {knob} {direction.ToLowerInvariant()}");
        reports.AddRange(BuildActionReports(composer, action, layer, actionConfig));
    }

    private static IEnumerable<Report> BuildActionReports(
        IReportComposer composer,
        InputAction inputAction,
        byte layer,
        BindingConfig binding)
    {
        var actionConfig = new ActionConfig
        {
            Keys = binding.Keys,
            Media = binding.Media,
            Mouse = binding.Mouse,
            Modifiers = binding.Modifiers
        };

        return BuildActionReports(composer, inputAction, layer, actionConfig);
    }

    private static IEnumerable<Report> BuildActionReports(
        IReportComposer composer,
        InputAction inputAction,
        byte layer,
        ActionConfig actionConfig)
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
            return composer.Key(inputAction, layer, delay: 0, sequence);
        }

        if (!string.IsNullOrWhiteSpace(actionConfig.Media))
        {
            return composer.Media(inputAction, layer, KeyStringParser.ParseMediaKey(actionConfig.Media));
        }

        return composer.Mouse(
            inputAction,
            layer,
            KeyStringParser.ParseMouseButton(actionConfig.Mouse!),
            KeyStringParser.ParseModifiers(actionConfig.Modifiers));
    }

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
