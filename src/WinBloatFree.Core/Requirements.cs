using System.Globalization;
using System.Text.RegularExpressions;

namespace WinBloatFree.Core;

/// <summary>
/// Interprets a tweak's <see cref="Tweak.Requires"/> value. "nvidia" means an NVIDIA
/// driver must be present. "build&lt;26100" and similar limits apply to the Windows build.
/// </summary>
public static class Requirements
{
    static readonly Regex BuildLimit = new(@"^build\s*(<=|>=|==|=|<|>)\s*(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsKnown(string? requires) =>
        string.IsNullOrEmpty(requires) || requires == "nvidia" || BuildLimit.IsMatch(requires);

    /// <summary>True when a failed requirement should hide the tweak instead of showing "Not applicable".</summary>
    public static bool HideWhenUnmet(string? requires) =>
        !string.IsNullOrEmpty(requires) && BuildLimit.IsMatch(requires);

    public static bool IsMet(string? requires, SystemInfo info, bool nvidiaAvailable)
    {
        if (string.IsNullOrEmpty(requires)) return true;
        if (string.Equals(requires, "nvidia", StringComparison.OrdinalIgnoreCase)) return nvidiaAvailable;
        var m = BuildLimit.Match(requires);
        if (!m.Success) return true;
        int limit = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return m.Groups[1].Value switch
        {
            "<" => info.Build < limit,
            "<=" => info.Build <= limit,
            ">" => info.Build > limit,
            ">=" => info.Build >= limit,
            _ => info.Build == limit,
        };
    }

    public static string SkipMessage(string? requires) =>
        string.Equals(requires, "nvidia", StringComparison.OrdinalIgnoreCase)
            ? "No NVIDIA driver found."
            : "Not available on this Windows version.";
}
