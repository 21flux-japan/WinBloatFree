using System.Globalization;

namespace WinBloatFree.Core;

public enum TweakStatus
{
    Unknown,
    /// <summary>The thing this tweak turns off is currently on.</summary>
    On,
    /// <summary>Everything this tweak would do is already in place.</summary>
    Off,
    /// <summary>Some parts are in place.</summary>
    Partial,
    /// <summary>Nothing to act on: the app, service or task is not on this PC.</summary>
    NotPresent,
    /// <summary>Does not apply to this PC (for example NVIDIA only).</summary>
    NotApplicable,
    /// <summary>Windows did not let us read the current state.</summary>
    CheckFailed,
}

/// <summary>State of one action as found on this PC.</summary>
public sealed record ActionState(int Index, ActionType Type, string Label, string Target, bool Present, bool Applied, string Current, string Wanted, bool Failed = false);

public sealed record Detection(TweakStatus Status, IReadOnlyList<ActionState> Actions)
{
    public static readonly Detection Unknown = new(TweakStatus.Unknown, Array.Empty<ActionState>());
    public bool HasWork => Status is TweakStatus.On or TweakStatus.Partial;
}

public enum EntryResult { Changed, Skipped, Failed }

/// <summary>One action as recorded in a history file, with the state it had before.</summary>
public sealed class BackupEntry
{
    public string TweakId { get; set; } = "";
    public string TweakName { get; set; } = "";
    public int ActionIndex { get; set; }
    public ActionType Type { get; set; }
    public string Target { get; set; } = "";
    public EntryResult Result { get; set; }
    public string? Message { get; set; }
    /// <summary>Optional longer technical text, shown under a details expander.</summary>
    public string? Details { get; set; }

    public RegHive? Hive { get; set; }
    public string? Key { get; set; }
    public string? Name { get; set; }
    public string? ServiceName { get; set; }
    public string? TaskPath { get; set; }
    public string? Package { get; set; }
    public uint? SettingId { get; set; }

    public RegSnapshot? PreviousRegistry { get; set; }
    public ServiceSnapshot? PreviousService { get; set; }
    public TaskSnapshot? PreviousTask { get; set; }
    public AppxSnapshot? PreviousAppx { get; set; }
    public NvSnapshot? PreviousNvidia { get; set; }
    public bool? PreviousOneDriveInstalled { get; set; }

    public bool Undone { get; set; }
    public string? UndoMessage { get; set; }
}

public sealed class RunRecord
{
    public string Id { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Windows { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public bool RestorePointCreated { get; set; }
    public string? RestorePointNote { get; set; }
    public List<string> TweakIds { get; set; } = new();
    public List<BackupEntry> Entries { get; set; } = new();
    public DateTimeOffset? UndoneAt { get; set; }

    public int ChangedCount => Entries.Count(e => e.Result == EntryResult.Changed);
    public int FailedCount => Entries.Count(e => e.Result == EntryResult.Failed);
    public bool CanUndo => Entries.Any(e => e.Result == EntryResult.Changed && !e.Undone);
}

public sealed record UndoReport(int Restored, int Failed, int Manual, IReadOnlyList<string> Messages);

public sealed record ApplyProgress(int Done, int Total, string Text);

public sealed class Engine
{
    readonly ISystemAccess _sys;

    public Engine(ISystemAccess sys) => _sys = sys;

    // ------------------------------------------------------------------ detect

    public Detection Detect(Tweak tweak, string? parameterValue = null)
    {
        if (tweak.Requires == "nvidia" && !_sys.GetNvidiaSetting(0).Available)
            return new Detection(TweakStatus.NotApplicable, Array.Empty<ActionState>());
        if (tweak.Actions.Count == 0) return new Detection(TweakStatus.Off, Array.Empty<ActionState>());

        var ctx = new ResolveContext(_sys.Info, DateTime.UtcNow, parameterValue ?? tweak.Parameter?.Default);
        var states = new List<ActionState>();
        for (int i = 0; i < tweak.Actions.Count; i++)
        {
            try { states.Add(DetectAction(i, tweak.Actions[i], ctx)); }
            catch (Exception ex) { states.Add(new ActionState(i, tweak.Actions[i].Type, Label(tweak.Actions[i].Type), Target(tweak.Actions[i]), true, false, "could not read: " + ex.Message, "", Failed: true)); }
        }
        return new Detection(Summarize(states), states);
    }

    /// <summary>Pure: turns per-action states into one status.</summary>
    public static TweakStatus Summarize(IReadOnlyList<ActionState> states)
    {
        if (states.Any(s => s.Failed)) return TweakStatus.CheckFailed;
        var present = states.Where(s => s.Present).ToList();
        if (present.Count == 0) return TweakStatus.NotPresent;
        int applied = present.Count(s => s.Applied);
        if (applied == present.Count) return TweakStatus.Off;
        return applied == 0 ? TweakStatus.On : TweakStatus.Partial;
    }

    /// <summary>Short per-action label for the details pane.</summary>
    public static string ActionStatusText(ActionState s)
    {
        if (s.Failed) return "Could not check";
        if (!s.Present) return "Not on this PC";
        return s.Applied ? "Done" : "Still on";
    }

    public static BackupEntry SystemRestoreEnabledEntry(string drive) => new()
    {
        TweakId = "system-restore.enable",
        TweakName = "Turn on System Restore",
        Type = ActionType.SystemRestore,
        Target = drive,
        Result = EntryResult.Changed,
        Message = "Turned on for the system drive.",
    };

    public static string Label(ActionType t) => t switch
    {
        ActionType.Registry or ActionType.RegistryDelete or ActionType.RegistryToken => "REGISTRY",
        ActionType.Service => "SERVICE",
        ActionType.Task => "SCHEDULED TASK",
        ActionType.Appx => "APP PACKAGE",
        ActionType.OneDriveUninstall => "PROGRAM",
        ActionType.NvidiaDrs => "NVIDIA DRIVER PROFILE",
        ActionType.SystemRestore => "SYSTEM RESTORE",
        _ => t.ToString().ToUpperInvariant(),
    };

    public static string Target(TweakAction a) => a.Type switch
    {
        ActionType.Registry or ActionType.RegistryDelete or ActionType.RegistryToken => $"{a.Hive}\\{a.Key}",
        ActionType.Service => a.Service!,
        ActionType.Task => a.Task!,
        ActionType.Appx => a.Package!,
        ActionType.OneDriveUninstall => "Microsoft OneDrive",
        ActionType.NvidiaDrs => "Global profile, setting 0x" + a.SettingId.ToString("X8", CultureInfo.InvariantCulture),
        ActionType.SystemRestore => "",
        _ => "",
    };

    static string DisplayWanted(RegKind kind, string value) => kind is RegKind.String or RegKind.ExpandString ? $"\"{value}\"" : value;

    ActionState DetectAction(int i, TweakAction a, ResolveContext ctx)
    {
        string label = Label(a.Type), target = Target(a);
        switch (a.Type)
        {
            case ActionType.Registry:
            {
                var cur = _sys.ReadRegistry(a.Hive, a.Key!, a.Name!);
                var want = Placeholders.Resolve(a.Value!, ctx);
                bool ok = a.Match switch
                {
                    MatchMode.Exists => cur.Exists,
                    MatchMode.EqualsOrAbsent => !cur.Exists || RegValues.SameData(a.Kind, cur.Data, want),
                    _ => cur.Exists && RegValues.SameData(a.Kind, cur.Data, want),
                };
                return new(i, a.Type, label, target, true, ok, $"{a.Name} = {RegValues.Display(cur)}", a.Match == MatchMode.Exists ? "date and time" : DisplayWanted(a.Kind, want));
            }
            case ActionType.RegistryDelete:
            {
                var cur = _sys.ReadRegistry(a.Hive, a.Key!, a.Name!);
                return new(i, a.Type, label, target, true, !cur.Exists, $"{a.Name} = {RegValues.Display(cur)}", "removed");
            }
            case ActionType.RegistryToken:
            {
                var cur = _sys.ReadRegistry(a.Hive, a.Key!, a.Name!);
                var tok = cur.Exists ? RegistryTokens.Get(cur.Data, a.Token!) : null;
                bool ok = tok is not null && RegValues.SameData(RegKind.DWord, tok, a.Value);
                return new(i, a.Type, label, target, true, ok, $"{a.Name}: {a.Token} = {(tok ?? "not set")}", a.Value!);
            }
            case ActionType.Service:
            {
                var s = _sys.GetService(a.Service!);
                if (!s.Exists) return new(i, a.Type, label, target, false, true, "not on this PC", StartTypes.Display(a.Startup));
                bool ok = s.Start == a.Startup && (!a.Stop || !s.Running || a.Startup != ServiceStart.Disabled);
                return new(i, a.Type, label, target, true, ok, "Startup type: " + StartTypes.Display(s.Start) + (s.Running ? ", running" : ""), StartTypes.Display(a.Startup) + (a.Stop && s.Running ? ", stopped now" : ""));
            }
            case ActionType.Task:
            {
                var t = _sys.GetTask(a.Task!);
                if (!t.Exists) return new(i, a.Type, label, target, false, true, "not on this PC", "Disabled");
                return new(i, a.Type, label, target, true, !t.Enabled, t.Enabled ? "Enabled" : "Disabled", "Disabled");
            }
            case ActionType.Appx:
            {
                var p = _sys.GetAppx(a.Package!);
                if (!p.Present) return new(i, a.Type, label, target, false, true, "not installed", "Removed");
                string cur = (p.Installed, p.Provisioned) switch
                {
                    (true, true) => "Installed and provisioned for new users",
                    (true, false) => "Installed",
                    (false, true) => "Provisioned for new users",
                    _ => "not installed",
                };
                return new(i, a.Type, label, target, true, false, cur, "Removed for all users");
            }
            case ActionType.OneDriveUninstall:
            {
                bool inst = _sys.IsOneDriveInstalled();
                return new(i, a.Type, label, target, inst, !inst, inst ? "Installed" : "not installed", "Uninstalled");
            }
            case ActionType.NvidiaDrs:
            {
                var n = _sys.GetNvidiaSetting(a.SettingId);
                if (!n.Available) return new(i, a.Type, label, target, false, true, "no NVIDIA driver", a.Value!);
                bool ok = n.IsSet && n.Value.HasValue && RegValues.SameData(RegKind.DWord, n.Value.Value.ToString(CultureInfo.InvariantCulture), a.Value);
                return new(i, a.Type, label, target, true, ok, n.IsSet ? $"Enable G-SYNC globally = {n.Value}" : "Enable G-SYNC globally = driver default", a.Value!);
            }
        }
        throw new NotSupportedException(a.Type.ToString());
    }

    // ------------------------------------------------------------------ apply

    /// <summary>
    /// Applies the tweaks in order. Before each change the previous state is recorded,
    /// and <paramref name="checkpoint"/> is called after every tweak so the history
    /// file on disk is always up to date, even if the app is closed halfway.
    /// </summary>
    public RunRecord Apply(IReadOnlyList<Tweak> tweaks, IReadOnlyDictionary<string, string>? parameters, string appVersion,
                           Action<RunRecord>? checkpoint = null, IProgress<ApplyProgress>? progress = null, DateTimeOffset? now = null)
    {
        var started = now ?? DateTimeOffset.Now;
        var run = new RunRecord
        {
            Id = started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
            StartedAt = started,
            Windows = _sys.Info.FriendlyName,
            AppVersion = appVersion,
            TweakIds = tweaks.Select(t => t.Id).ToList(),
        };
        int total = tweaks.Sum(t => Math.Max(1, t.Actions.Count)), done = 0;
        foreach (var tweak in tweaks)
        {
            string? pv = null;
            parameters?.TryGetValue(tweak.Id, out pv);
            var ctx = new ResolveContext(_sys.Info, DateTime.UtcNow, pv ?? tweak.Parameter?.Default);
            bool nvidiaMissing = tweak.Requires == "nvidia" && !_sys.GetNvidiaSetting(0).Available;
            for (int i = 0; i < tweak.Actions.Count; i++)
            {
                var a = tweak.Actions[i];
                progress?.Report(new ApplyProgress(done, total, $"{tweak.Name}: {Target(a)}"));
                var entry = NewEntry(tweak, i, a);
                if (nvidiaMissing) { entry.Result = EntryResult.Skipped; entry.Message = "No NVIDIA driver found."; }
                else
                {
                    try { ApplyAction(a, entry, ctx); }
                    catch (Exception ex)
                    {
                        entry.Result = EntryResult.Failed;
                        entry.Message = ex.Message;
                        if (ex is DetailedException de && !string.IsNullOrEmpty(de.Details)) entry.Details = de.Details;
                    }
                }
                run.Entries.Add(entry);
                done++;
            }
            checkpoint?.Invoke(run);
        }
        run.FinishedAt = DateTimeOffset.Now;
        progress?.Report(new ApplyProgress(total, total, "Done"));
        checkpoint?.Invoke(run);
        return run;
    }

    static BackupEntry NewEntry(Tweak t, int i, TweakAction a) => new()
    {
        TweakId = t.Id, TweakName = t.Name, ActionIndex = i, Type = a.Type, Target = Target(a),
        Hive = a.Type is ActionType.Registry or ActionType.RegistryDelete or ActionType.RegistryToken ? a.Hive : null,
        Key = a.Key, Name = a.Name, ServiceName = a.Service, TaskPath = a.Task, Package = a.Package,
        SettingId = a.Type == ActionType.NvidiaDrs ? a.SettingId : null,
    };

    void ApplyAction(TweakAction a, BackupEntry e, ResolveContext ctx)
    {
        e.Result = EntryResult.Skipped;
        switch (a.Type)
        {
            case ActionType.Registry:
            {
                var cur = _sys.ReadRegistry(a.Hive, a.Key!, a.Name!);
                var want = Placeholders.Resolve(a.Value!, ctx);
                // Time stamps (Match = Exists) are always written again so a new pause starts from now.
                bool already = a.Match != MatchMode.Exists && (
                    (cur.Exists && cur.Kind == a.Kind && RegValues.SameData(a.Kind, cur.Data, want))
                    || (a.Match == MatchMode.EqualsOrAbsent && !cur.Exists));
                if (already) { e.Message = "Already set."; return; }
                e.PreviousRegistry = cur;
                _sys.WriteRegistry(a.Hive, a.Key!, a.Name!, a.Kind, want);
                e.Result = EntryResult.Changed;
                e.Message = $"{a.Name} = {want}";
                return;
            }
            case ActionType.RegistryDelete:
            {
                var cur = _sys.ReadRegistry(a.Hive, a.Key!, a.Name!);
                if (!cur.Exists) { e.Message = "Not present."; return; }
                e.PreviousRegistry = cur;
                _sys.DeleteRegistryValue(a.Hive, a.Key!, a.Name!);
                e.Result = EntryResult.Changed;
                e.Message = $"{a.Name} removed";
                return;
            }
            case ActionType.RegistryToken:
            {
                var cur = _sys.ReadRegistry(a.Hive, a.Key!, a.Name!);
                var text = cur.Exists ? cur.Data : null;
                var tok = RegistryTokens.Get(text, a.Token!);
                if (tok is not null && RegValues.SameData(RegKind.DWord, tok, a.Value)) { e.Message = "Already set."; return; }
                e.PreviousRegistry = cur;
                var next = RegistryTokens.Set(text, a.Token!, a.Value!);
                _sys.WriteRegistry(a.Hive, a.Key!, a.Name!, RegKind.String, next);
                e.Result = EntryResult.Changed;
                e.Message = $"{a.Name} = \"{next}\"";
                return;
            }
            case ActionType.Service:
            {
                var s = _sys.GetService(a.Service!);
                if (!s.Exists) { e.Message = "Service not on this PC."; return; }
                e.PreviousService = s;
                bool changed = false;
                if (s.Start != a.Startup) { _sys.SetServiceStart(a.Service!, a.Startup!.Value); changed = true; }
                if (a.Stop && s.Running)
                {
                    try { _sys.StopService(a.Service!); changed = true; }
                    catch (Exception ex) { e.Message = "Startup type changed, but the service could not be stopped now: " + ex.Message; }
                }
                if (changed) { e.Result = EntryResult.Changed; e.Message ??= $"Startup type {StartTypes.Display(s.Start)} -> {StartTypes.Display(a.Startup)}"; }
                else e.Message = "Already set.";
                return;
            }
            case ActionType.Task:
            {
                var t = _sys.GetTask(a.Task!);
                if (!t.Exists) { e.Message = "Task not on this PC."; return; }
                if (!t.Enabled) { e.Message = "Already disabled."; return; }
                e.PreviousTask = t;
                _sys.SetTaskEnabled(a.Task!, false);
                e.Result = EntryResult.Changed;
                e.Message = "Disabled";
                return;
            }
            case ActionType.Appx:
            {
                var p = _sys.GetAppx(a.Package!);
                if (!p.Present) { e.Message = "Not installed."; return; }
                e.PreviousAppx = p;
                _sys.RemoveAppx(a.Package!);
                e.Result = EntryResult.Changed;
                e.Message = "Removed for all users";
                return;
            }
            case ActionType.OneDriveUninstall:
            {
                if (!_sys.IsOneDriveInstalled()) { e.Message = "Not installed."; return; }
                e.PreviousOneDriveInstalled = true;
                _sys.UninstallOneDrive();
                e.Result = EntryResult.Changed;
                e.Message = "Uninstalled";
                return;
            }
            case ActionType.NvidiaDrs:
            {
                var n = _sys.GetNvidiaSetting(a.SettingId);
                if (!n.Available) { e.Message = "No NVIDIA driver found."; return; }
                RegValues.TryParseNumber(a.Value!, out var v);
                if (n.IsSet && n.Value == (uint)v) { e.Message = "Already set."; return; }
                e.PreviousNvidia = n;
                _sys.SetNvidiaSetting(a.SettingId, (uint)v);
                e.Result = EntryResult.Changed;
                e.Message = $"value {v}";
                return;
            }
        }
    }

    // ------------------------------------------------------------------ undo

    /// <summary>
    /// Puts back the recorded previous state, newest change first.
    /// Pass tweak ids to undo only those tweaks from the run.
    /// </summary>
    public UndoReport Undo(RunRecord run, IReadOnlyCollection<string>? onlyTweakIds = null, DateTimeOffset? now = null)
    {
        int restored = 0, failed = 0, manual = 0;
        var messages = new List<string>();
        var todo = run.Entries.Where(e => e.Result == EntryResult.Changed && !e.Undone && (onlyTweakIds is null || onlyTweakIds.Contains(e.TweakId))).Reverse().ToList();
        foreach (var e in todo)
        {
            try
            {
                if (e.Type == ActionType.SystemRestore)
                {
                    e.Undone = true;
                    e.UndoMessage = "System Restore was left on so any restore points stay available. You can turn it off in System Properties > System Protection if you want.";
                    manual++;
                    messages.Add($"{e.TweakName}: {e.UndoMessage}");
                    continue;
                }
                bool done = UndoEntry(e);
                if (done) { e.Undone = true; restored++; }
                else { manual++; messages.Add($"{e.TweakName}: {e.UndoMessage}"); }
            }
            catch (Exception ex)
            {
                failed++;
                e.UndoMessage = ex.Message;
                messages.Add($"{e.TweakName}: {ex.Message}");
            }
        }
        if (run.Entries.Where(x => x.Result == EntryResult.Changed).All(x => x.Undone || x.Type is ActionType.Appx or ActionType.OneDriveUninstall))
            run.UndoneAt = now ?? DateTimeOffset.Now;
        return new UndoReport(restored, failed, manual, messages);
    }

    bool UndoEntry(BackupEntry e)
    {
        switch (e.Type)
        {
            case ActionType.Registry:
            case ActionType.RegistryDelete:
            case ActionType.RegistryToken:
            {
                var prev = e.PreviousRegistry ?? throw new InvalidDataException("No saved value.");
                if (prev.Exists) _sys.WriteRegistry(e.Hive!.Value, e.Key!, e.Name!, prev.Kind ?? RegKind.DWord, prev.Data ?? "");
                else
                {
                    _sys.DeleteRegistryValue(e.Hive!.Value, e.Key!, e.Name!);
                    if (!prev.KeyExisted) _sys.DeleteKeyIfEmpty(e.Hive!.Value, e.Key!);
                }
                e.UndoMessage = "Restored";
                return true;
            }
            case ActionType.Service:
            {
                var prev = e.PreviousService ?? throw new InvalidDataException("No saved service state.");
                if (prev.Start is { } st) _sys.SetServiceStart(e.ServiceName!, st);
                if (prev.Running)
                {
                    try { _sys.StartService(e.ServiceName!); }
                    catch (Exception ex) { e.UndoMessage = "Startup type restored. The service will start after a restart (" + ex.Message + ")."; return true; }
                }
                e.UndoMessage = "Restored";
                return true;
            }
            case ActionType.Task:
                _sys.SetTaskEnabled(e.TaskPath!, true);
                e.UndoMessage = "Enabled again";
                return true;
            case ActionType.Appx:
                e.UndoMessage = $"Apps cannot be put back offline. Reinstall it from the Microsoft Store (search for {AppxDisplay(e)}).";
                return false;
            case ActionType.OneDriveUninstall:
                if (_sys.ReinstallOneDrive()) { e.UndoMessage = "OneDrive setup started."; return true; }
                e.UndoMessage = "Windows no longer has the OneDrive installer. Download OneDrive from microsoft.com to reinstall it.";
                return false;
            case ActionType.NvidiaDrs:
            {
                var prev = e.PreviousNvidia ?? throw new InvalidDataException("No saved NVIDIA value.");
                if (prev.IsSet && prev.Value is { } v) _sys.SetNvidiaSetting(e.SettingId!.Value, v);
                else _sys.ResetNvidiaSetting(e.SettingId!.Value);
                e.UndoMessage = "Restored";
                return true;
            }
        }
        return false;
    }

    static string AppxDisplay(BackupEntry e) => e.TweakName.StartsWith("Remove ", StringComparison.Ordinal) ? e.TweakName[7..] : e.Package ?? e.TweakName;
}
