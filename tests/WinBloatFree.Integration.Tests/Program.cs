using System.Diagnostics;
using WinBloatFree.Core;
using WinBloatFree.Platform;

int failed = 0, covered = 0, skipped = 0, alreadyOff = 0;
var catalog = TweakCatalog.LoadEmbedded();
var sys = new WindowsSystem();
var eng = new Engine(sys);

void Log(string s) => Console.WriteLine(s);

Log("Windows: " + sys.Info.FriendlyName);
Log("Tweaks in catalog: " + catalog.Tweaks.Count);

var seqWatch = Stopwatch.StartNew();
var sequential = eng.DetectAll(catalog.Tweaks, parallel: false);
seqWatch.Stop();
var parWatch = Stopwatch.StartNew();
var parallel = eng.DetectAll(catalog.Tweaks, parallel: true);
parWatch.Stop();
Log($"Status scan (sequential): {seqWatch.ElapsedMilliseconds} ms");
Log($"Status scan (parallel, app list loaded once): {parWatch.ElapsedMilliseconds} ms");

for (int i = 0; i < sequential.Count; i++)
{
    if (sequential[i].Tweak.Id != parallel[i].Tweak.Id || sequential[i].Detection.Status != parallel[i].Detection.Status)
    {
        Log($"FAIL  parallel detect mismatch for {sequential[i].Tweak.Id}: {sequential[i].Detection.Status} vs {parallel[i].Detection.Status}");
        failed++;
    }
}

foreach (var (tweak, first) in sequential)
{
    var permanent = SkipReason(tweak);
    if (permanent is not null)
    {
        skipped++;
        Log($"SKIP  {tweak.Id}  {permanent}");
        continue;
    }
    if (first.Status == TweakStatus.NotApplicable)
    {
        skipped++;
        Log($"SKIP  {tweak.Id}  Not applicable on this runner" + (string.IsNullOrEmpty(tweak.Requires) ? "." : " (requires " + tweak.Requires + ")."));
        continue;
    }
    if (first.Status == TweakStatus.NotPresent)
    {
        skipped++;
        Log($"SKIP  {tweak.Id}  Not present on this runner, so there is nothing to apply.");
        continue;
    }
    if (first.Status == TweakStatus.CheckFailed)
    {
        skipped++;
        Log($"SKIP  {tweak.Id}  Could not read the current state.");
        continue;
    }

    try
    {
        var before = Capture(sys, tweak);
        var run = eng.Apply(new[] { tweak }, null, "integration");
        var afterApply = eng.Detect(tweak);
        if (run.FailedCount > 0)
        {
            failed++;
            Log($"FAIL  {tweak.Id}  apply failed: {string.Join("; ", run.Entries.Where(e => e.Result == EntryResult.Failed).Select(e => e.Message))}");
            if (run.ChangedCount > 0) TryUndo(eng, run, tweak.Id);
            continue;
        }
        if (run.ChangedCount == 0)
        {
            if (afterApply.Status is TweakStatus.Off or TweakStatus.NotPresent)
            {
                alreadyOff++;
                covered++;
                Log($"OK    {tweak.Id}  already off, apply made no change");
            }
            else
            {
                failed++;
                Log($"FAIL  {tweak.Id}  apply made no change, but status is {afterApply.Status}");
            }
            continue;
        }
        if (afterApply.Status != TweakStatus.Off)
        {
            failed++;
            Log($"FAIL  {tweak.Id}  expected Off after apply, got {afterApply.Status}");
            TryUndo(eng, run, tweak.Id);
            continue;
        }
        var undo = eng.Undo(run);
        if (undo.Failed > 0)
        {
            failed++;
            Log($"FAIL  {tweak.Id}  undo failed: {string.Join("; ", undo.Messages)}");
            continue;
        }
        var diff = CompareAfterUndo(sys, tweak, before);
        if (diff is not null)
        {
            failed++;
            Log($"FAIL  {tweak.Id}  state after undo did not match the original: {diff}");
            continue;
        }
        covered++;
        Log($"OK    {tweak.Id}  applied, status Off, original state restored");
    }
    catch (Exception ex)
    {
        failed++;
        Log($"FAIL  {tweak.Id}  {ex.Message}");
    }
}

Log("");
Log($"Covered: {covered} (of which already off: {alreadyOff})");
Log($"Skipped: {skipped}");
Log($"Failed: {failed}");
if (failed > 0)
{
    Log("Integration tests failed.");
    return 1;
}
Log("Integration tests passed.");
return 0;

static string? SkipReason(Tweak t)
{
    if (t.Id == "updates.level.leave") return "No actions (leave as is).";
    if (t.Id == "updates.level.pause") return "Pausing Windows Update on the runner is risky.";
    if (t.Id == "updates.level.off") return "Turning Windows Update fully off would break the runner.";
    if (t.Id == "onedrive.uninstall") return "Uninstalling OneDrive is not reliably restored on a CI image.";
    if (t.Actions.Any(a => a.Type == ActionType.Appx))
        return "App removal cannot be undone offline and could break the runner.";
    return null;
}

static List<(string Key, string Value)> Capture(ISystemAccess sys, Tweak t)
{
    var rows = new List<(string, string)>();
    foreach (var a in t.Actions)
    {
        switch (a.Type)
        {
            case ActionType.Registry:
            case ActionType.RegistryDelete:
            case ActionType.RegistryToken:
            {
                var cur = sys.ReadRegistry(a.Hive, a.Key!, a.Name!);
                rows.Add(($"{a.Hive}\\{a.Key}\\{a.Name}", cur.Exists ? $"{cur.Kind}:{cur.Data}" : "absent"));
                break;
            }
            case ActionType.Service:
            {
                var s = sys.GetService(a.Service!);
                rows.Add(("service:" + a.Service, s.Exists ? $"{s.Start}:{(s.Running ? "running" : "stopped")}" : "absent"));
                break;
            }
            case ActionType.Task:
            {
                var task = sys.GetTask(a.Task!);
                rows.Add(("task:" + a.Task, task.Exists ? (task.Enabled ? "enabled" : "disabled") : "absent"));
                break;
            }
            case ActionType.NvidiaDrs:
            {
                var nv = sys.GetNvidiaSetting(a.SettingId);
                rows.Add(("nvidia:" + a.SettingId, nv.Available ? (nv.IsSet ? nv.Value!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unset") : "none"));
                break;
            }
            case ActionType.OneDriveUninstall:
                rows.Add(("onedrive", sys.IsOneDriveInstalled() ? "installed" : "absent"));
                break;
        }
    }
    return rows;
}

/// <summary>
/// Compares captured state after undo. Service running state is retried briefly,
/// because a just-started service may not show as running immediately.
/// </summary>
static string? CompareAfterUndo(ISystemAccess sys, Tweak t, List<(string Key, string Value)> before)
{
    var deadline = DateTime.UtcNow.AddSeconds(8);
    string? last = "not compared";
    do
    {
        var after = Capture(sys, t);
        if (before.Count != after.Count) return "different number of recorded values";
        var parts = new List<string>();
        for (int i = 0; i < before.Count; i++)
        {
            if (before[i].Key != after[i].Key || before[i].Value != after[i].Value)
                parts.Add($"{before[i].Key} was {before[i].Value}, now {after[i].Value}");
        }
        if (parts.Count == 0) return null;
        last = string.Join("; ", parts);
        bool onlyServiceRun = parts.All(p => p.StartsWith("service:", StringComparison.Ordinal) && (p.Contains("running") || p.Contains("stopped")));
        if (!onlyServiceRun) return last;
        Thread.Sleep(250);
    }
    while (DateTime.UtcNow < deadline);
    return last;
}

static void TryUndo(Engine eng, RunRecord run, string id)
{
    try { eng.Undo(run); }
    catch (Exception ex) { Console.WriteLine("  undo after failure also had a problem for " + id + ": " + ex.Message); }
}
