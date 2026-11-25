using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinBloatFree.Core;

public sealed class TweakCatalog
{
    public IReadOnlyList<Category> Categories { get; }
    public IReadOnlyList<Tweak> Tweaks { get; }

    public TweakCatalog(IEnumerable<Category> categories)
    {
        Categories = categories.OrderBy(c => c.Order).ToList();
        Tweaks = Categories.SelectMany(c => c.Tweaks).ToList();
    }

    public Tweak? Find(string id) => Tweaks.FirstOrDefault(t => t.Id == id);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Parses one category file: { id, name, ..., "tweaks": [ ... ] }.</summary>
    public static Category ParseCategory(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        var cat = root.Deserialize<Category>(JsonOptions) ?? throw new InvalidDataException("Empty category file.");
        if (root.TryGetProperty("tweaks", out var arr))
        {
            foreach (var el in arr.EnumerateArray())
            {
                var t = el.Deserialize<Tweak>(JsonOptions) ?? throw new InvalidDataException("Empty tweak.");
                t.Category = cat.Id;
                t.RawJson = JsonSerializer.Serialize(el, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                cat.Tweaks.Add(t);
            }
        }
        return cat;
    }

    /// <summary>Loads the catalog that is embedded in this assembly.</summary>
    public static TweakCatalog LoadEmbedded()
    {
        var asm = typeof(TweakCatalog).Assembly;
        var cats = new List<Category>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("WinBloatFree.Catalog.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s);
            cats.Add(ParseCategory(r.ReadToEnd()));
        }
        return new TweakCatalog(cats);
    }
}

public static class CatalogValidator
{
    /// <summary>Packages that must never be listed, because removing them breaks Windows or the Store.</summary>
    public static readonly string[] ProtectedPackages =
    {
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.SecHealthUI",
        "Microsoft.VCLibs*", "Microsoft.UI.Xaml*", "Microsoft.NET.Native*", "Microsoft.WindowsAppRuntime*",
        "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost", "MicrosoftWindows.Client.CBS",
        "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl", "Microsoft.Win32WebViewHost", "Microsoft.WindowsTerminal",
        "windows.immersivecontrolpanel", "Microsoft.Windows.Search",
    };

    static readonly string[] KnownPresets = { "recommended", "privacy", "everything" };

    /// <summary>Returns a list of problems. An empty list means the catalog is valid.</summary>
    public static List<string> Validate(TweakCatalog catalog)
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in catalog.Categories)
        {
            if (string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Name)) errors.Add("Category without id or name.");
            if (c.Kind is not ("list" or "updates")) errors.Add($"{c.Id}: unknown kind {c.Kind}.");
            CheckText(errors, c.Id, c.Name, c.Description);
        }
        foreach (var t in catalog.Tweaks)
        {
            if (!ids.Add(t.Id)) errors.Add($"Duplicate tweak id {t.Id}.");
            if (string.IsNullOrWhiteSpace(t.Name) || string.IsNullOrWhiteSpace(t.Summary) || string.IsNullOrWhiteSpace(t.Description))
                errors.Add($"{t.Id}: name, summary and description are required.");
            CheckText(errors, t.Id, t.Name, t.Summary, t.Description, t.GoodToKnow, t.Undo, t.Warning, t.AppliedLabel);
            foreach (var p in t.Presets)
                if (!KnownPresets.Contains(p, StringComparer.OrdinalIgnoreCase)) errors.Add($"{t.Id}: unknown preset {p}.");
            if (!Requirements.IsKnown(t.Requires)) errors.Add($"{t.Id}: unknown requires {t.Requires}.");
            if (t.Actions.Count == 0 && t.Group is null) errors.Add($"{t.Id}: no actions.");
            for (int i = 0; i < t.Actions.Count; i++) ValidateAction(errors, $"{t.Id}[{i}]", t.Actions[i]);
            if (t.Parameter is { } prm && (prm.Options.Count == 0 || !prm.Options.Any(o => o.Value == prm.Default)))
                errors.Add($"{t.Id}: parameter needs options and a default that is one of them.");
        }
        foreach (var g in catalog.Tweaks.Where(t => t.Group is not null).GroupBy(t => t.Group!))
        {
            foreach (var p in KnownPresets)
                if (g.Count(t => t.Presets.Contains(p, StringComparer.OrdinalIgnoreCase)) > 1)
                    errors.Add($"Group {g.Key}: more than one option in preset {p}.");
            if (g.Count(t => t.Actions.Count == 0) != 1) errors.Add($"Group {g.Key}: needs exactly one 'leave as is' option without actions.");
        }
        return errors;
    }

    static void ValidateAction(List<string> errors, string where, TweakAction a)
    {
        switch (a.Type)
        {
            case ActionType.Registry:
                if (string.IsNullOrEmpty(a.Key) || a.Name is null || a.Value is null) errors.Add($"{where}: registry needs key, name, value.");
                else if (a.Kind is RegKind.DWord or RegKind.QWord && !Placeholders.HasPlaceholder(a.Value) && !RegValues.TryParseNumber(a.Value, out _))
                    errors.Add($"{where}: value must be a number.");
                if (a.Key?.StartsWith("HK", StringComparison.OrdinalIgnoreCase) == true) errors.Add($"{where}: key must not include the hive.");
                break;
            case ActionType.RegistryDelete:
                if (string.IsNullOrEmpty(a.Key) || a.Name is null) errors.Add($"{where}: registryDelete needs key and name.");
                break;
            case ActionType.RegistryToken:
                if (string.IsNullOrEmpty(a.Key) || a.Name is null || string.IsNullOrEmpty(a.Token) || a.Value is null) errors.Add($"{where}: registryToken needs key, name, token, value.");
                break;
            case ActionType.Service:
                if (string.IsNullOrEmpty(a.Service) || a.Startup is null) errors.Add($"{where}: service needs service and startup.");
                break;
            case ActionType.Task:
                if (string.IsNullOrEmpty(a.Task) || !a.Task.StartsWith('\\')) errors.Add($"{where}: task needs a full path starting with a backslash.");
                break;
            case ActionType.Appx:
                if (string.IsNullOrEmpty(a.Package)) errors.Add($"{where}: appx needs package.");
                else if (ProtectedPackages.Any(p => Wildcard.IsMatch(a.Package, p) || Wildcard.IsMatch(p.Replace("*", ""), a.Package)))
                    errors.Add($"{where}: {a.Package} is protected and must not be removed.");
                break;
            case ActionType.NvidiaDrs:
                if (a.SettingId == 0 || a.Value is null || !RegValues.TryParseNumber(a.Value, out _)) errors.Add($"{where}: nvidiaDrs needs settingId and a numeric value.");
                break;
        }
    }

    static void CheckText(List<string> errors, string where, params string?[] texts)
    {
        foreach (var s in texts)
        {
            if (s is null) continue;
            if (s.Contains('\u2014') || s.Contains('\u2013')) errors.Add($"{where}: text contains an em or en dash.");
        }
    }
}
