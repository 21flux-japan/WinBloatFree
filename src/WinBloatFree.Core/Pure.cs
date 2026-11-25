using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WinBloatFree.Core;

/// <summary>Values known only at apply time, for example the current Windows version.</summary>
public sealed record ResolveContext(SystemInfo Info, DateTime UtcNow, string? ParameterValue);

public static class Placeholders
{
    /// <summary>
    /// Replaces {productVersion}, {displayVersion}, {nowUtc} and {untilUtc}.
    /// {untilUtc} is now plus the parameter value in weeks (default 1).
    /// </summary>
    public static string Resolve(string text, ResolveContext ctx)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('{')) return text;
        int weeks = int.TryParse(ctx.ParameterValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) && w > 0 ? w : 1;
        var now = DateTime.SpecifyKind(ctx.UtcNow, DateTimeKind.Utc);
        return text
            .Replace("{productVersion}", ctx.Info.ProductVersion, StringComparison.Ordinal)
            .Replace("{displayVersion}", ctx.Info.DisplayVersion, StringComparison.Ordinal)
            .Replace("{nowUtc}", FormatUtc(now), StringComparison.Ordinal)
            .Replace("{untilUtc}", FormatUtc(now.AddDays(7 * weeks)), StringComparison.Ordinal);
    }

    /// <summary>The format Windows Update uses for its pause time stamps.</summary>
    public static string FormatUtc(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static bool HasPlaceholder(string? text) => text is not null && Regex.IsMatch(text, @"\{[A-Za-z]+\}");
}

/// <summary>Helpers for string values made of "Name=Value;" tokens, like DirectXUserGlobalSettings.</summary>
public static class RegistryTokens
{
    public static IReadOnlyList<KeyValuePair<string, string>> Parse(string? text)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrEmpty(text)) return list;
        foreach (var part in text.Split(';'))
        {
            var p = part.Trim();
            if (p.Length == 0) continue;
            int eq = p.IndexOf('=');
            list.Add(eq < 0 ? new(p, "") : new(p[..eq].Trim(), p[(eq + 1)..].Trim()));
        }
        return list;
    }

    public static string? Get(string? text, string token)
    {
        foreach (var kv in Parse(text))
            if (string.Equals(kv.Key, token, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    /// <summary>Sets one token and keeps every other token and its order unchanged.</summary>
    public static string Set(string? text, string token, string value)
    {
        var items = Parse(text).ToList();
        bool found = false;
        for (int i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Key, token, StringComparison.OrdinalIgnoreCase))
            {
                items[i] = new(items[i].Key, value);
                found = true;
            }
        }
        if (!found) items.Add(new(token, value));
        var sb = new StringBuilder();
        foreach (var kv in items) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
        return sb.ToString();
    }
}

public static class Wildcard
{
    /// <summary>Case-insensitive match where '*' means any run of characters.</summary>
    public static bool IsMatch(string text, string pattern)
    {
        if (!pattern.Contains('*')) return string.Equals(text, pattern, StringComparison.OrdinalIgnoreCase);
        var rx = "^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$";
        return Regex.IsMatch(text, rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

public static class RegValues
{
    /// <summary>Compares registry data, treating DWord/QWord numerically.</summary>
    public static bool SameData(RegKind kind, string? a, string? b)
    {
        if (a is null || b is null) return a == b;
        if (kind is RegKind.DWord or RegKind.QWord)
        {
            if (TryParseNumber(a, out var x) && TryParseNumber(b, out var y)) return x == y;
        }
        return string.Equals(a, b, StringComparison.Ordinal);
    }

    public static bool TryParseNumber(string s, out ulong value)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            value = unchecked((ulong)l);
            return true;
        }
        return ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public static string Display(RegSnapshot s)
    {
        if (!s.Exists) return "not set";
        return s.Kind is RegKind.String or RegKind.ExpandString ? $"\"{s.Data}\"" : s.Data ?? "";
    }
}

public static class StartTypes
{
    public static string Display(ServiceStart? s) => s switch
    {
        ServiceStart.AutomaticDelayed => "Automatic (delayed)",
        null => "unknown",
        _ => s.Value.ToString(),
    };
}

public static class CommandLine
{
    /// <summary>
    /// Splits an UninstallString such as "C:\\Path\\Setup.exe" /uninstall into the program and its arguments.
    /// </summary>
    public static (string File, string Arguments) Split(string commandLine)
    {
        var s = commandLine.Trim();
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            if (end > 0) return (s[1..end], s[(end + 1)..].Trim());
            return (s.Trim('"'), "");
        }
        int exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return (s[..(exe + 4)], s[(exe + 4)..].Trim());
        int sp = s.IndexOf(' ');
        return sp < 0 ? (s, "") : (s[..sp], s[(sp + 1)..].Trim());
    }
}

/// <summary>Reads the length of an existing Windows Update pause from an expiry time stamp.</summary>
public static class PauseLength
{
    static readonly Regex Stamp = new(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?", RegexOptions.CultureInvariant);

    public static int? WeeksRemaining(string? expiryText, DateTime utcNow)
    {
        if (string.IsNullOrEmpty(expiryText)) return null;
        var m = Stamp.Match(expiryText);
        if (!m.Success) return null;
        if (!DateTime.TryParse(m.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var end))
            return null;
        double days = (end - utcNow.ToUniversalTime()).TotalDays;
        if (days <= 0.5) return null;
        int weeks = (int)Math.Round(days / 7.0, MidpointRounding.AwayFromZero);
        if (weeks < 1) return 1;
        if (weeks > 5) return 5;
        return weeks;
    }

    public static bool MatchesSelectedWeeks(string? expiryText, string? weeks, DateTime utcNow)
    {
        var w = WeeksRemaining(expiryText, utcNow);
        return w is int n && weeks == n.ToString(CultureInfo.InvariantCulture);
    }
}

