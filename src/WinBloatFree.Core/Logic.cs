using System.Text.Json;

namespace WinBloatFree.Core;

public static class Presets
{
    /// <summary>
    /// Pure: which tweak ids a preset selects. In every exclusive group exactly one
    /// option is chosen: the one in the preset, otherwise the group's "leave as is".
    /// </summary>
    public static HashSet<string> Select(TweakCatalog catalog, Preset preset)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (preset == Preset.Custom) return ids;
        foreach (var t in catalog.Tweaks.Where(t => t.Group is null && t.InPreset(preset))) ids.Add(t.Id);
        foreach (var g in catalog.Tweaks.Where(t => t.Group is not null).GroupBy(t => t.Group))
        {
            var pick = g.FirstOrDefault(t => t.InPreset(preset)) ?? g.First(t => t.Actions.Count == 0);
            ids.Add(pick.Id);
        }
        return ids;
    }

    /// <summary>
    /// A default preset, with exclusive-group options replaced by the level already in place on this PC, if any.
    /// </summary>
    public static HashSet<string> SelectDefault(TweakCatalog catalog, Preset baseline, Func<Tweak, TweakStatus> status)
    {
        var ids = Select(catalog, baseline);
        foreach (var g in catalog.Tweaks.Where(t => t.Group is not null).GroupBy(t => t.Group!))
        {
            var applied = g.FirstOrDefault(t => t.Actions.Count > 0 && status(t) == TweakStatus.Off);
            if (applied is null) continue;
            foreach (var t in g) ids.Remove(t.Id);
            ids.Add(applied.Id);
        }
        return ids;
    }

    /// <summary>Pure: the preset that matches a selection exactly, or Custom.</summary>
    public static Preset Match(TweakCatalog catalog, IReadOnlySet<string> selected)
    {
        foreach (var p in new[] { Preset.Recommended, Preset.Privacy, Preset.Everything })
            if (Select(catalog, p).SetEquals(selected)) return p;
        return Preset.Custom;
    }
}

public sealed record ReviewGroup(string CategoryId, string CategoryName, IReadOnlyList<Tweak> Tweaks);

public sealed record ReviewSummary(int Total, int Safe, int Moderate, int Advanced, int NeedRestart, IReadOnlyList<ReviewGroup> Groups);

public static class Review
{
    /// <summary>
    /// Pure: what will actually change. Skips "leave as is" options and tweaks whose
    /// status says there is nothing to do.
    /// </summary>
    public static IReadOnlyList<Tweak> Pending(TweakCatalog catalog, IReadOnlySet<string> selected, Func<Tweak, TweakStatus> status, Func<Tweak, bool>? parameterNeedsApply = null) =>
        catalog.Tweaks.Where(t => selected.Contains(t.Id) && t.Actions.Count > 0 && NeedsApply(t, status(t), parameterNeedsApply)).ToList();

    static bool NeedsApply(Tweak t, TweakStatus status, Func<Tweak, bool>? parameterNeedsApply) =>
        status is TweakStatus.On or TweakStatus.Partial or TweakStatus.Unknown
        || (t.Parameter is not null && status == TweakStatus.Off && parameterNeedsApply?.Invoke(t) == true);

    public static ReviewSummary Summarize(TweakCatalog catalog, IReadOnlyList<Tweak> pending)
    {
        var groups = catalog.Categories
            .Select(c => new ReviewGroup(c.Id, c.Name, pending.Where(t => t.Category == c.Id).ToList()))
            .Where(g => g.Tweaks.Count > 0).ToList();
        return new ReviewSummary(pending.Count,
            pending.Count(t => t.Risk == Risk.Safe), pending.Count(t => t.Risk == Risk.Moderate), pending.Count(t => t.Risk == Risk.Advanced),
            pending.Count(t => t.Restart), groups);
    }

    /// <summary>Pure: generated "How undo works" text when the definition has none.</summary>
    public static string UndoText(Tweak t)
    {
        if (!string.IsNullOrEmpty(t.Undo)) return t.Undo!;
        var types = t.Actions.Select(a => a.Type).Distinct().ToList();
        if (types.Count == 0) return "Nothing is changed, so there is nothing to undo.";
        if (types.Contains(ActionType.Appx))
            return "The app is removed for all users and for new accounts. Removed apps cannot be put back offline. Reinstall it for free from the Microsoft Store if you want it back.";
        var parts = new List<string>();
        if (types.Any(x => x is ActionType.Registry or ActionType.RegistryDelete or ActionType.RegistryToken))
            parts.Add("registry values");
        if (types.Contains(ActionType.Service)) parts.Add("service startup types");
        if (types.Contains(ActionType.Task)) parts.Add("scheduled task states");
        if (types.Contains(ActionType.NvidiaDrs)) parts.Add("the NVIDIA driver setting");
        var list = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
        var text = $"The old {list} are saved first. Undo / History puts them back exactly as they were";
        text += types.Contains(ActionType.Service) ? " and starts services that were running." : ".";
        return text;
    }
}

/// <summary>Saves each run as one JSON file. Files are written to a temp name first, then moved.</summary>
public sealed class HistoryStore
{
    public string Directory { get; }

    public HistoryStore(string directory) => Directory = directory;

    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinBloatFree", "history");

    public string PathFor(RunRecord run) => Path.Combine(Directory, $"run-{run.Id}.json");

    public void Save(RunRecord run)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(run);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(run, TweakCatalog.JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    public List<RunRecord> LoadAll()
    {
        var list = new List<RunRecord>();
        if (!System.IO.Directory.Exists(Directory)) return list;
        foreach (var f in System.IO.Directory.GetFiles(Directory, "run-*.json"))
        {
            try
            {
                var r = JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(f), TweakCatalog.JsonOptions);
                if (r is not null) list.Add(r);
            }
            catch (JsonException) { /* a damaged file is skipped, never deleted */ }
        }
        return list.OrderByDescending(r => r.StartedAt).ToList();
    }

    /// <summary>Gives a unique id when two runs start in the same second.</summary>
    public void EnsureUniqueId(RunRecord run)
    {
        var baseId = run.Id;
        for (int n = 2; File.Exists(PathFor(run)); n++) run.Id = $"{baseId}-{n}";
    }
}
