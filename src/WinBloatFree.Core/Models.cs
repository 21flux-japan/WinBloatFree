using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinBloatFree.Core;

public enum Risk { Safe, Moderate, Advanced }

public enum ActionType
{
    /// <summary>Set a registry value.</summary>
    Registry,
    /// <summary>Delete a registry value (for example a Run entry).</summary>
    RegistryDelete,
    /// <summary>Set one key=value token inside a "a=1;b=2;" string value.</summary>
    RegistryToken,
    /// <summary>Change a service startup type, optionally stopping it.</summary>
    Service,
    /// <summary>Disable a scheduled task.</summary>
    Task,
    /// <summary>Remove an Appx package for all users and deprovision it.</summary>
    Appx,
    /// <summary>Run Microsoft's own OneDrive uninstaller.</summary>
    OneDriveUninstall,
    /// <summary>Set a DWORD in the NVIDIA driver global profile (NVIDIA only).</summary>
    NvidiaDrs,
    /// <summary>Record that System Restore was turned on. Not used in the catalog.</summary>
    SystemRestore,
}

public enum RegHive { HKLM, HKCU }

/// <summary>MultiString data is joined with '\n'; Binary data is hex.</summary>
public enum RegKind { DWord, QWord, String, ExpandString, MultiString, Binary }

public enum ServiceStart { Boot, System, Automatic, AutomaticDelayed, Manual, Disabled }

public enum MatchMode
{
    /// <summary>Applied when the current value equals the wanted value.</summary>
    Equals,
    /// <summary>Applied when the value exists at all (used for time stamps).</summary>
    Exists,
    /// <summary>Applied when the value equals the wanted value, or is not set (used when "not set" means the same as the default).</summary>
    EqualsOrAbsent,
}

public enum Preset { Recommended, Privacy, Everything, Custom }

public sealed class TweakAction
{
    public ActionType Type { get; set; }

    // Registry, RegistryDelete, RegistryToken
    public RegHive Hive { get; set; }
    public string? Key { get; set; }
    public string? Name { get; set; }
    public RegKind Kind { get; set; } = RegKind.DWord;
    [JsonConverter(typeof(LooseStringConverter))]
    public string? Value { get; set; }
    public MatchMode Match { get; set; } = MatchMode.Equals;
    public string? Token { get; set; }

    // Service
    public string? Service { get; set; }
    public ServiceStart? Startup { get; set; }
    public bool Stop { get; set; }

    // Task: full path, for example \Microsoft\Windows\Autochk\Proxy
    public string? Task { get; set; }

    // Appx: package name, '*' wildcards allowed
    public string? Package { get; set; }

    // NvidiaDrs
    public uint SettingId { get; set; }
}

public sealed class ParamOption
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class TweakParameter
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public List<ParamOption> Options { get; set; } = new();
    public string Default { get; set; } = "";
}

public sealed class Tweak
{
    public string Id { get; set; } = "";
    [JsonIgnore] public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>One line shown under the name.</summary>
    public string Summary { get; set; } = "";
    /// <summary>"What it does" in the detail pane.</summary>
    public string Description { get; set; } = "";
    public Risk Risk { get; set; }
    public bool Restart { get; set; }
    public List<string> Presets { get; set; } = new();
    /// <summary>Tweaks sharing a group are mutually exclusive (radio buttons).</summary>
    public string? Group { get; set; }
    public string? GoodToKnow { get; set; }
    /// <summary>Overrides the generated "How undo works" text.</summary>
    public string? Undo { get; set; }
    /// <summary>Red warning shown when the tweak is selected.</summary>
    public string? Warning { get; set; }
    /// <summary>Optional hardware/OS requirement: "nvidia".</summary>
    public string? Requires { get; set; }
    /// <summary>Label when the tweak is applied ("Already off" by default).</summary>
    public string? AppliedLabel { get; set; }
    public TweakParameter? Parameter { get; set; }
    public List<TweakAction> Actions { get; set; } = new();
    [JsonIgnore] public string RawJson { get; set; } = "";

    public bool InPreset(Preset p) => Presets.Contains(p.ToString(), StringComparer.OrdinalIgnoreCase);
}

public sealed class Category
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Segoe Fluent Icons / MDL2 code point, for example "EA18".</summary>
    public string Icon { get; set; } = "";
    /// <summary>"list" or "updates".</summary>
    public string Kind { get; set; } = "list";
    public int Order { get; set; }
    [JsonIgnore] public List<Tweak> Tweaks { get; set; } = new();
}

/// <summary>Reads JSON numbers or strings into a string.</summary>
public sealed class LooseStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l) ? l.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                                                  : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.Null => null,
            JsonTokenType.True => "1",
            JsonTokenType.False => "0",
            _ => throw new JsonException("Expected a string or a number."),
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue(); else writer.WriteStringValue(value);
    }
}
