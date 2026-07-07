using RSoft.MacroPad.BLL.Infrasturture.Model;
using YamlDotNet.Serialization;

namespace RSoft.MacroPad.Cli.Config;

public sealed class MacroConfig
{
    [YamlMember(Alias = "device")]
    public string Device { get; set; } = "auto";

    [YamlMember(Alias = "layer")]
    public int Layer { get; set; }

    [YamlMember(Alias = "bindings")]
    public List<BindingConfig> Bindings { get; set; } = [];
}

public sealed class BindingConfig
{
    [YamlMember(Alias = "button")]
    public int? Button { get; set; }

    [YamlMember(Alias = "knob")]
    public int? Knob { get; set; }

    [YamlMember(Alias = "keys")]
    public string? Keys { get; set; }

    [YamlMember(Alias = "media")]
    public string? Media { get; set; }

    [YamlMember(Alias = "mouse")]
    public string? Mouse { get; set; }

    [YamlMember(Alias = "modifiers")]
    public string? Modifiers { get; set; }

    [YamlMember(Alias = "led")]
    public LedConfig? Led { get; set; }

    [YamlMember(Alias = "left")]
    public ActionConfig? Left { get; set; }

    [YamlMember(Alias = "push")]
    public ActionConfig? Push { get; set; }

    [YamlMember(Alias = "right")]
    public ActionConfig? Right { get; set; }
}

public sealed class ActionConfig
{
    [YamlMember(Alias = "keys")]
    public string? Keys { get; set; }

    [YamlMember(Alias = "media")]
    public string? Media { get; set; }

    [YamlMember(Alias = "mouse")]
    public string? Mouse { get; set; }

    [YamlMember(Alias = "modifiers")]
    public string? Modifiers { get; set; }
}

public sealed class LedConfig
{
    [YamlMember(Alias = "mode")]
    public LedMode Mode { get; set; }

    [YamlMember(Alias = "color")]
    public LedColor Color { get; set; } = LedColor.Random;
}
