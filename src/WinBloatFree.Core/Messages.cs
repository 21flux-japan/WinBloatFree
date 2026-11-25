namespace WinBloatFree.Core;

/// <summary>An apply failure with a short message and optional technical details.</summary>
public sealed class DetailedException : IOException
{
    public string? Details { get; }
    public DetailedException(string message, string? details = null) : base(message) => Details = details;
}

/// <summary>Checkbox and review wording for System Restore and other backups.</summary>
public static class BackupCopy
{
    public static string CheckLabel(bool restoreEnabled) =>
        restoreEnabled
            ? "Create restore point first"
            : "Turn on System Restore and create a restore point first";

    public static string CheckHint(bool restoreEnabled) =>
        restoreEnabled
            ? "Takes about a minute. A registry backup and an undo history file are always saved."
            : "System Restore is off on this PC. Ticking this turns it on for the system drive, then creates a restore point. A registry backup and an undo history file are still saved either way.";

    public static string ReviewText(bool wantRestorePoint, bool restoreEnabled)
    {
        if (!wantRestorePoint)
            return "A registry backup and an undo history file will be saved. No System Restore point will be created.";
        if (!restoreEnabled)
            return "System Restore will be turned on for the system drive, then a restore point will be created. A registry backup and an undo history file will also be saved.";
        return "A System Restore point will be created first. A registry backup and an undo history file will also be saved.";
    }

    public static string CreateFailedText(string reason)
    {
        var r = reason.Trim().TrimEnd('.');
        return "Could not create a restore point (" + r + "). Your registry backup and undo history will still be saved.";
    }
}

/// <summary>Recognizes Appx deprovision errors that mean the package was already gone.</summary>
public static class AppxErrors
{
    public const int NotFound = unchecked((int)0x80070490);

    public static bool IsAlreadyDone(int hresult, string? message)
    {
        if (hresult == NotFound || hresult == 0x490) return true;
        if (string.IsNullOrEmpty(message)) return false;
        return message.Contains("0x80070490", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Element not found", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAlreadyDone(Exception ex) => IsAlreadyDone(ex.HResult, ex.Message);

    public static string ShortRemoveFailure(string package, bool forNewUsers) =>
        forNewUsers ? package + " could not be removed for new users." : package + " could not be removed.";
}

/// <summary>Plain result-dialog wording after Apply or Undo.</summary>
public static class RunSummary
{
    public static bool NeedsRestart(RunRecord run, Func<string, bool> tweakNeedsRestart) =>
        run.Entries.Any(e => e.Result == EntryResult.Changed && !string.IsNullOrEmpty(e.TweakId) && tweakNeedsRestart(e.TweakId));

    public static bool NeedsRestartAfterUndo(IEnumerable<BackupEntry> entries, Func<string, bool> tweakNeedsRestart) =>
        entries.Any(e => e.Undone && tweakNeedsRestart(e.TweakId));

    public static string? AppRemovalPendingRestart(RunRecord run) =>
        run.Entries.Any(e => e.Type == ActionType.Appx && e.Result == EntryResult.Changed)
            ? "Windows will finish removing some apps during the next restart. It may show a progress screen for a few minutes."
            : null;

    public static string AfterApply(RunRecord run, bool needsRestart, string? extra = null)
    {
        int skipped = run.Entries.Count(e => e.Result == EntryResult.Skipped);
        var s = $"{run.ChangedCount} changed, {skipped} already in place or not on this PC, {run.FailedCount} failed.";
        if (!string.IsNullOrEmpty(extra)) s += " " + extra;
        if (needsRestart) s += " Restart the PC to finish some changes.";
        else if (run.ChangedCount > 0) s += " Some changes show after you sign out and back in.";
        s += " You can undo this run in Undo / History.";
        return s;
    }

    public static string AfterUndo(UndoReport rep, bool needsRestart)
    {
        var s = $"{rep.Restored} restored, {rep.Manual} need a manual step, {rep.Failed} failed.";
        if (needsRestart) s += " Restart the PC to finish some changes.";
        return s;
    }

    public static string FailureLine(BackupEntry e)
    {
        var msg = PlainError(e.Message);
        return string.IsNullOrEmpty(e.TweakName) ? msg : $"{e.TweakName}: {msg}";
    }

    public static string AlreadyDoneLine(BackupEntry e)
    {
        var name = string.IsNullOrEmpty(e.TweakName) ? e.Target : e.TweakName;
        return string.IsNullOrEmpty(name) ? "Already done." : name + ": already done.";
    }

    public static string PlainError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "The change could not be applied.";
        if (AppxErrors.IsAlreadyDone(0, message)) return "Already done.";
        if (message.Contains("unauthorized operation", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("protects this", StringComparison.OrdinalIgnoreCase))
            return "Windows protects this setting and would not allow the change.";
        if (message.Contains("Deployment ", StringComparison.OrdinalIgnoreCase)
            || message.Contains("DeprovisionPackageOperation", StringComparison.OrdinalIgnoreCase)
            || message.Contains("RemovePackageOperation", StringComparison.OrdinalIgnoreCase)
            || message.Contains("0x8007", StringComparison.OrdinalIgnoreCase))
            return "The app could not be fully removed.";
        return message;
    }

    public static bool IsTechnical(string? text) =>
        !string.IsNullOrEmpty(text) && (
            text.Contains("Deployment ", StringComparison.OrdinalIgnoreCase)
            || text.Contains("0x", StringComparison.OrdinalIgnoreCase)
            || text.Contains("HRESULT", StringComparison.OrdinalIgnoreCase)
            || text.Contains("DeprovisionPackageOperation", StringComparison.OrdinalIgnoreCase));
}
