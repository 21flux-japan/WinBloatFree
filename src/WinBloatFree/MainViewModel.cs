using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Input;
using WinBloatFree.Core;
using WinBloatFree.Platform;

namespace WinBloatFree;

public sealed class MainViewModel : Observable
{
    public const string AppVersion = "0.1.3";
    public const string RepoUrl = "https://github.com/21flux-japan/WinBloatFree";

    readonly TweakCatalog _catalog;
    readonly ISystemAccess _sys;
    readonly Engine _engine;
    readonly HistoryStore _store;
    readonly Dictionary<string, TweakVM> _byId = new();
    bool _suppress;

    public MainViewModel(ISystemAccess sys, TweakCatalog catalog, HistoryStore store)
    {
        _sys = sys;
        _catalog = catalog;
        _engine = new Engine(sys);
        _store = store;
        foreach (var c in catalog.Categories)
        {
            var vms = c.Tweaks.Select(t => new TweakVM(t, c.Name, this)).ToList();
            foreach (var v in vms) _byId[v.Id] = v;
            Categories.Add(new CategoryVM(c, vms));
        }
        OsText = sys.Info.FriendlyName;
        using (var id = WindowsIdentity.GetCurrent()) IsAdmin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);

        SelectCategoryCommand = new RelayCommand(p => { if (p is CategoryVM c) ShowCategory(c); });
        ShowHistoryCommand = new RelayCommand(ShowHistory);
        SelectTweakCommand = new RelayCommand(p => { if (p is TweakVM t) Focus(t); });
        SetPresetCommand = new RelayCommand(p => { if (p is string s && Enum.TryParse<Preset>(s, out var pr)) ApplyPreset(pr); });
        SelectAllCommand = new RelayCommand(() => SetAllVisible(true));
        ClearVisibleCommand = new RelayCommand(() => SetAllVisible(false));
        ClearSelectionCommand = new RelayCommand(ClearSelection);
        OpenReviewCommand = new RelayCommand(OpenReview, () => PendingCount > 0 && !IsBusy);
        CloseReviewCommand = new RelayCommand(() => IsReviewOpen = false);
        ApplyCommand = new RelayCommand(async () => await ApplyAsync(), () => !IsBusy);
        CloseResultCommand = new RelayCommand(() => IsResultOpen = false);
        ShowJsonCommand = new RelayCommand(() => { if (FocusedTweak is not null) IsJsonOpen = true; });
        CloseJsonCommand = new RelayCommand(() => IsJsonOpen = false);
        UndoRunCommand = new RelayCommand(p => { if (p is RunVM r) AskUndo(r); }, p => p is RunVM r && r.CanUndo && !IsBusy);
        ConfirmOkCommand = new RelayCommand(() => CompleteConfirm(true));
        ConfirmCancelCommand = new RelayCommand(() => CompleteConfirm(false));
        RestartNowCommand = new RelayCommand(AskRestart, () => !IsBusy && IsResultDone && ResultNeedsRestart);
        OpenRepoCommand = new RelayCommand(() => OpenUrl(RepoUrl));
        OpenIssuesCommand = new RelayCommand(() => OpenUrl(RepoUrl + "/issues"));
        OpenAboutCommand = new RelayCommand(() => IsAboutOpen = true);
        CloseAboutCommand = new RelayCommand(() => IsAboutOpen = false);
        OpenStoreCommand = new RelayCommand(p => { if (p is string u && StoreLinks.IsStoreUri(u)) OpenUrl(u); });
        OpenHistoryFolderCommand = new RelayCommand(() => OpenFolder(_store.Directory));
        RefreshCommand = new RelayCommand(async () => await DetectAllAsync(), () => !IsBusy);
        ToggleThemeCommand = new RelayCommand(() => IsDark = !IsDark);

        RefreshRestoreState(setDefault: true);
        ApplyPreset(Preset.Recommended);
        ShowCategory(Categories[0]);
        LoadHistory();
    }

    // ------------------------------------------------------------------ header

    public string OsText { get; }
    public bool IsAdmin { get; }
    public string AdminText => IsAdmin ? "Running as administrator" : "Not running as administrator";
    public string VersionText => "v" + AppVersion;
    public string HistoryDirectory => _store.Directory;

    bool _isDark = true;
    public bool IsDark
    {
        get => _isDark;
        set { if (Set(ref _isDark, value)) App.ApplyTheme(value); }
    }

    // ------------------------------------------------------------------ navigation

    public List<CategoryVM> Categories { get; } = new();
    public ObservableList<TweakVM> VisibleTweaks { get; } = new();
    public ObservableList<RunVM> Runs { get; } = new();

    CategoryVM? _current;
    public CategoryVM? CurrentCategory { get => _current; private set => Set(ref _current, value); }

    string _view = "list";
    public bool ShowList => _view is "list" or "search";
    public bool ShowUpdates => _view == "updates";
    public bool ShowHistoryView => _view == "history";
    public bool IsHistoryActive => _view == "history";
    public bool ShowListActions => _view == "list";

    string _title = "", _subtitle = "";
    public string PageTitle { get => _title; private set => Set(ref _title, value); }
    public string PageSubtitle { get => _subtitle; private set => Set(ref _subtitle, value); }

    void SetView(string v)
    {
        _view = v;
        Raise(nameof(ShowList), nameof(ShowUpdates), nameof(ShowHistoryView), nameof(IsHistoryActive), nameof(ShowListActions), nameof(ShowDetail), nameof(ShowEmptySearch));
    }

    void ShowCategory(CategoryVM c)
    {
        if (_searchText.Length > 0) { _searchText = ""; Raise(nameof(SearchText)); }
        foreach (var x in Categories) x.IsActive = x == c;
        CurrentCategory = c;
        PageTitle = c.Name;
        PageSubtitle = c.Description;
        VisibleTweaks.ReplaceAll(c.IsUpdates ? Enumerable.Empty<TweakVM>() : c.Tweaks.Where(t => t.IsListed));
        SetView(c.IsUpdates ? "updates" : "list");
        var listed = c.Tweaks.Where(t => t.IsListed).ToList();
        Focus(c.IsUpdates ? c.Levels.FirstOrDefault(t => t.IsChecked) ?? c.Tweaks.First() : listed.FirstOrDefault(t => t.IsAvailable) ?? listed.FirstOrDefault() ?? c.Tweaks.First());
    }

    void ShowHistory()
    {
        foreach (var x in Categories) x.IsActive = false;
        CurrentCategory = null;
        PageTitle = "Undo / History";
        PageSubtitle = "Every run is saved on this PC before anything changes. Undo puts back what was there before, newest change first.";
        LoadHistory();
        SetView("history");
    }

    public string RunsText => Runs.Count == 1 ? "1 run" : $"{Runs.Count} runs";
    public bool HasNoRuns => Runs.Count == 0;

    void LoadHistory()
    {
        Runs.ReplaceAll(_store.LoadAll().Select(r => new RunVM(r)));
        Raise(nameof(RunsText), nameof(HasNoRuns));
    }

    // ------------------------------------------------------------------ search

    string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value ?? "")) return;
            if (_searchText.Trim().Length == 0)
            {
                ShowCategory(CurrentCategory ?? Categories[0]);
                return;
            }
            var q = _searchText.Trim();
            var hits = _byId.Values.Where(t => t.IsListed && !t.IsLeaveAsIs && (Has(t.Name, q) || Has(t.Summary, q) || Has(t.Description, q) || t.Tweak.Actions.Any(a => Has(Engine.Target(a), q)))).ToList();
            foreach (var x in Categories) x.IsActive = false;
            VisibleTweaks.ReplaceAll(hits);
            PageTitle = "Search results";
            PageSubtitle = hits.Count == 1 ? $"1 tweak matches \"{q}\"." : $"{hits.Count} tweaks match \"{q}\".";
            SetView("search");
            if (hits.Count > 0) Focus(hits[0]);
        }
    }

    public bool ShowEmptySearch => _view == "search" && VisibleTweaks.Count == 0;

    static bool Has(string s, string q) => s.Contains(q, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ detail pane

    TweakVM? _focused;
    public TweakVM? FocusedTweak { get => _focused; private set { if (Set(ref _focused, value)) Raise(nameof(ShowDetail)); } }
    public bool ShowDetail => FocusedTweak is not null && _view != "history";

    void Focus(TweakVM t)
    {
        if (FocusedTweak is not null) FocusedTweak.IsFocused = false;
        t.IsFocused = true;
        FocusedTweak = t;
    }

    // ------------------------------------------------------------------ selection and presets

    Preset _preset;
    public Preset Preset
    {
        get => _preset;
        private set { if (Set(ref _preset, value)) Raise(nameof(IsRecommended), nameof(IsPrivacy), nameof(IsEverything), nameof(IsCustom)); }
    }
    public bool IsRecommended { get => Preset == Preset.Recommended; set { if (value) ApplyPreset(Preset.Recommended); } }
    public bool IsPrivacy { get => Preset == Preset.Privacy; set { if (value) ApplyPreset(Preset.Privacy); } }
    public bool IsEverything { get => Preset == Preset.Everything; set { if (value) ApplyPreset(Preset.Everything); } }
    public bool IsCustom { get => Preset == Preset.Custom; set { if (value) Preset = Preset.Custom; } }

    void ApplyPreset(Preset p)
    {
        if (p == Preset.Custom) { Preset = p; return; }
        var ids = Presets.Select(_catalog, p, IncludeInPresets);
        _suppress = true;
        foreach (var t in _byId.Values) t.SetChecked(ids.Contains(t.Id));
        _suppress = false;
        Preset = p;
        OnSelectionChanged();
    }

    bool IncludeInPresets(Tweak t) => !_byId.TryGetValue(t.Id, out var vm) || vm.IsListed;

    internal void OnChecked(TweakVM t)
    {
        if (t.IsLevel && t.IsChecked)
            foreach (var other in _byId.Values.Where(o => o.Tweak.Group == t.Tweak.Group && o != t)) other.SetChecked(false);
        Focus(t);
        if (_suppress) return;
        Preset = Presets.Match(_catalog, SelectedIds(), IncludeInPresets);
        OnSelectionChanged();
    }

    HashSet<string> SelectedIds() => _byId.Values.Where(t => t.IsChecked && t.IsListed).Select(t => t.Id).ToHashSet();

    void SetAllVisible(bool on)
    {
        _suppress = true;
        foreach (var t in VisibleTweaks.Where(t => t.IsAvailable && !t.IsLevel)) t.SetChecked(on);
        _suppress = false;
        Preset = Presets.Match(_catalog, SelectedIds(), IncludeInPresets);
        OnSelectionChanged();
    }

    void ClearSelection()
    {
        _suppress = true;
        foreach (var t in _byId.Values) t.SetChecked(t.IsLeaveAsIs);
        _suppress = false;
        Preset = Preset.Custom;
        OnSelectionChanged();
    }

    IReadOnlyList<Tweak> PendingTweaks() =>
        Review.Pending(_catalog, SelectedIds(), t => _byId[t.Id].Status, PauseNeedsRefresh);

    bool PauseNeedsRefresh(Tweak t)
    {
        if (t.Id != "updates.level.pause") return true;
        var vm = _byId[t.Id];
        var expiry = vm.Detection.Actions.FirstOrDefault(a => a.Current.Contains("PauseUpdatesExpiryTime", StringComparison.OrdinalIgnoreCase))?.Current;
        var weeks = PauseLength.WeeksRemaining(expiry, DateTime.UtcNow);
        if (weeks is null) return false;
        return weeks.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) != (vm.ParameterValue ?? t.Parameter?.Default);
    }

    public int PendingCount { get; private set; }
    public string PendingText => PendingCount == 1 ? "1 change" : $"{PendingCount} changes";
    public string PendingCategoriesText { get; private set; } = "";

    internal void OnSelectionChanged()
    {
        if (_suppress) return;
        var pending = PendingTweaks();
        PendingCount = pending.Count;
        int cats = pending.Select(t => t.Category).Distinct().Count();
        PendingCategoriesText = cats == 0 ? "selected" : cats == 1 ? "selected in 1 category" : $"selected in {cats} categories";
        foreach (var c in Categories) c.Refresh();
        Raise(nameof(PendingCount), nameof(PendingText), nameof(PendingCategoriesText));
        CommandManager.InvalidateRequerySuggested();
    }

    bool _restoreEnabled = true;
    bool _restorePoint = true;
    public bool CreateRestorePoint
    {
        get => _restorePoint;
        set { if (Set(ref _restorePoint, value)) Raise(nameof(ReviewBackupText)); }
    }
    public string RestoreCheckLabel => BackupCopy.CheckLabel(_restoreEnabled);
    public string RestoreCheckHint => BackupCopy.CheckHint(_restoreEnabled);
    public string ReviewBackupText => BackupCopy.ReviewText(CreateRestorePoint, _restoreEnabled);

    void RefreshRestoreState(bool setDefault = false)
    {
        _restoreEnabled = RestorePoint.IsEnabled();
        if (setDefault) _restorePoint = _restoreEnabled;
        Raise(nameof(RestoreCheckLabel), nameof(RestoreCheckHint), nameof(ReviewBackupText), nameof(CreateRestorePoint));
    }

    // ------------------------------------------------------------------ detection

    bool _busy;
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }

    string _statusLine = "Checking this PC";
    public string StatusLine { get => _statusLine; private set => Set(ref _statusLine, value); }

    public async Task DetectAllAsync()
    {
        StatusLine = "Checking this PC";
        var list = _byId.Values.ToList();
        var results = await Task.Run(() => list.Select(t => (t, _engine.Detect(t.Tweak, t.ParameterValue))).ToList());
        foreach (var (vm, det) in results)
        {
            vm.Detection = det;
            if (!vm.IsAvailable && vm.IsChecked && !vm.IsLevel) vm.SetChecked(false);
        }
        if (!_appliedStartupSelection)
        {
            ApplyStartupSelection();
            _appliedStartupSelection = true;
        }
        RefreshRestoreState();
        RefreshVisibleList();
        StatusLine = "Already applied changes are skipped";
        OnSelectionChanged();
    }

    void RefreshVisibleList()
    {
        if (_view == "search" && _searchText.Trim().Length > 0)
        {
            var q = _searchText.Trim();
            VisibleTweaks.ReplaceAll(_byId.Values.Where(t => t.IsListed && !t.IsLeaveAsIs && (Has(t.Name, q) || Has(t.Summary, q) || Has(t.Description, q) || t.Tweak.Actions.Any(a => Has(Engine.Target(a), q)))));
            return;
        }
        if (_view == "list" && CurrentCategory is { IsUpdates: false } c)
        {
            VisibleTweaks.ReplaceAll(c.Tweaks.Where(t => t.IsListed));
            if (FocusedTweak is { IsListed: false })
            {
                var next = VisibleTweaks.FirstOrDefault(t => t.IsAvailable) ?? VisibleTweaks.FirstOrDefault();
                if (next is not null) Focus(next);
            }
        }
    }

    bool _appliedStartupSelection;

    void ApplyStartupSelection()
    {
        SyncPauseLengthFromDetection();
        var ids = Presets.SelectDefault(_catalog, Preset.Recommended, t => _byId[t.Id].Status, IncludeInPresets);
        _suppress = true;
        foreach (var t in _byId.Values) t.SetChecked(ids.Contains(t.Id));
        foreach (var t in _byId.Values.Where(t => !t.IsAvailable && t.IsChecked && !t.IsLevel)) t.SetChecked(false);
        _suppress = false;
        Preset = Presets.Match(_catalog, SelectedIds(), IncludeInPresets);
    }

    void SyncPauseLengthFromDetection()
    {
        if (!_byId.TryGetValue("updates.level.pause", out var vm)) return;
        var expiry = vm.Detection.Actions.FirstOrDefault(a => a.Current.Contains("PauseUpdatesExpiryTime", StringComparison.OrdinalIgnoreCase))?.Current;
        var weeks = PauseLength.WeeksRemaining(expiry, DateTime.UtcNow);
        if (weeks is null) return;
        var opt = vm.Options.FirstOrDefault(o => o.Value == weeks.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (opt is not null) vm.SelectedOption = opt;
    }

    // ------------------------------------------------------------------ review dialog

    bool _reviewOpen;
    public bool IsReviewOpen { get => _reviewOpen; set => Set(ref _reviewOpen, value); }
    public string ReviewTitle { get; private set; } = "";
    public string ReviewSafe { get; private set; } = "";
    public string ReviewModerate { get; private set; } = "";
    public string ReviewAdvanced { get; private set; } = "";
    public bool ReviewHasModerate { get; private set; }
    public bool ReviewHasAdvanced { get; private set; }
    public string ReviewRestart { get; private set; } = "";
    public bool ReviewHasRestart { get; private set; }
    public string ApplyText { get; private set; } = "";
    public List<ReviewGroupVM> ReviewGroups { get; private set; } = new();
    public List<string> ReviewWarnings { get; private set; } = new();
    public bool ReviewHasWarnings => ReviewWarnings.Count > 0;

    void OpenReview()
    {
        var pending = PendingTweaks();
        if (pending.Count == 0) return;
        var sum = Review.Summarize(_catalog, pending);
        ReviewTitle = sum.Total == 1 ? "Review 1 change" : $"Review {sum.Total} changes";
        ReviewSafe = $"{sum.Safe} Safe";
        ReviewModerate = $"{sum.Moderate} Moderate";
        ReviewAdvanced = $"{sum.Advanced} Advanced";
        ReviewHasModerate = sum.Moderate > 0;
        ReviewHasAdvanced = sum.Advanced > 0;
        ReviewRestart = sum.NeedRestart == 1 ? "1 needs a restart" : $"{sum.NeedRestart} need a restart";
        ReviewHasRestart = sum.NeedRestart > 0;
        ApplyText = sum.Total == 1 ? "Apply 1 change" : $"Apply {sum.Total} changes";
        ReviewGroups = sum.Groups.Select((g, i) => new ReviewGroupVM(g.CategoryName, g.Tweaks.Select(t => _byId[t.Id]).ToList(), i == 0)).ToList();
        ReviewWarnings = pending.Where(t => t.Warning is not null).Select(t => $"{t.Name}: {t.Warning}").ToList();
        Raise(nameof(ReviewTitle), nameof(ReviewSafe), nameof(ReviewModerate), nameof(ReviewAdvanced), nameof(ReviewHasModerate), nameof(ReviewHasAdvanced),
              nameof(ReviewRestart), nameof(ReviewHasRestart), nameof(ApplyText), nameof(ReviewGroups), nameof(ReviewWarnings), nameof(ReviewHasWarnings),
              nameof(ReviewBackupText), nameof(RestoreCheckLabel), nameof(RestoreCheckHint));
        IsReviewOpen = true;
    }

    // ------------------------------------------------------------------ apply

    bool _resultOpen;
    public bool IsResultOpen { get => _resultOpen; set => Set(ref _resultOpen, value); }
    bool _resultDone;
    public bool IsResultDone { get => _resultDone; private set { if (Set(ref _resultDone, value)) Raise(nameof(ShowRestartButtons), nameof(ShowResultClose)); } }
    string _resultTitle = "", _resultText = "", _progressText = "";
    public string ResultTitle { get => _resultTitle; private set => Set(ref _resultTitle, value); }
    public string ResultText { get => _resultText; private set => Set(ref _resultText, value); }
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
    double _progress;
    public double ProgressValue { get => _progress; private set => Set(ref _progress, value); }
    public ObservableList<string> ResultLines { get; } = new();
    public ObservableList<string> ResultAlreadyDone { get; } = new();
    public ObservableList<string> ResultDetails { get; } = new();
    public ObservableList<StoreLink> ResultStoreLinks { get; } = new();
    public bool HasResultFailures => ResultLines.Count > 0;
    public bool HasResultAlreadyDone => ResultAlreadyDone.Count > 0;
    public bool HasResultDetails => ResultDetails.Count > 0;
    public bool HasResultStoreLinks => ResultStoreLinks.Count > 0;
    bool _resultNeedsRestart;
    public bool ResultNeedsRestart
    {
        get => _resultNeedsRestart;
        private set
        {
            if (Set(ref _resultNeedsRestart, value))
                Raise(nameof(ShowRestartButtons), nameof(ShowResultClose));
        }
    }
    public bool ShowRestartButtons => IsResultDone && ResultNeedsRestart;
    public bool ShowResultClose => IsResultDone && !ResultNeedsRestart;

    void ClearResultLists()
    {
        ResultLines.ReplaceAll(Array.Empty<string>());
        ResultAlreadyDone.ReplaceAll(Array.Empty<string>());
        ResultDetails.ReplaceAll(Array.Empty<string>());
        ResultStoreLinks.ReplaceAll(Array.Empty<StoreLink>());
        Raise(nameof(HasResultFailures), nameof(HasResultAlreadyDone), nameof(HasResultDetails), nameof(HasResultStoreLinks));
    }

    void SetResultLists(IEnumerable<string> failures, IEnumerable<string>? alreadyDone = null, IEnumerable<string>? details = null, IEnumerable<StoreLink>? storeLinks = null)
    {
        ResultLines.ReplaceAll(failures);
        ResultAlreadyDone.ReplaceAll(alreadyDone ?? Array.Empty<string>());
        ResultDetails.ReplaceAll(details ?? Array.Empty<string>());
        ResultStoreLinks.ReplaceAll(storeLinks ?? Array.Empty<StoreLink>());
        Raise(nameof(HasResultFailures), nameof(HasResultAlreadyDone), nameof(HasResultDetails), nameof(HasResultStoreLinks));
    }

    async Task ApplyAsync()
    {
        var pending = PendingTweaks();
        if (pending.Count == 0) return;
        IsReviewOpen = false;
        IsBusy = true;
        IsResultDone = false;
        ClearResultLists();
        ResultTitle = "Applying changes";
        ResultText = "Please keep this window open.";
        ProgressValue = 0;
        IsResultOpen = true;

        var parameters = pending.Where(t => t.Parameter is not null).ToDictionary(t => t.Id, t => _byId[t.Id].ParameterValue ?? t.Parameter!.Default);
        ClearResultLists();
        ResultNeedsRestart = false;
        bool wantRestorePoint = CreateRestorePoint;
        bool restoreWasOn = _restoreEnabled;
        bool enabledRestore = false;
        bool createdRestore = false;
        string? restoreError = null;
        string drive = RestorePoint.SystemDrive;
        try
        {
            if (wantRestorePoint)
            {
                if (!restoreWasOn)
                {
                    ProgressText = "Turning on System Restore for the system drive.";
                    restoreError = await Task.Run(RestorePoint.Enable);
                    if (restoreError is null)
                    {
                        enabledRestore = true;
                        _restoreEnabled = true;
                        Raise(nameof(RestoreCheckLabel), nameof(RestoreCheckHint), nameof(ReviewBackupText));
                    }
                }
                if (restoreError is null)
                {
                    ProgressText = "Creating a restore point. This can take a minute.";
                    restoreError = await Task.Run(() => RestorePoint.Create($"WinBloatFree {AppVersion}, before {pending.Count} changes"));
                }
                if (restoreError is null) createdRestore = true;
                else
                {
                    bool cont = await ConfirmAsync("Could not create a restore point", BackupCopy.CreateFailedText(restoreError), "Continue without it");
                    if (!cont)
                    {
                        if (enabledRestore) SaveRestoreEnableOnly(drive, restoreError);
                        IsResultOpen = false;
                        return;
                    }
                }
            }

            var progress = new Progress<ApplyProgress>(p =>
            {
                ProgressText = p.Text;
                ProgressValue = p.Total == 0 ? 1 : (double)p.Done / p.Total;
            });
            bool first = true;
            var run = await Task.Run(() => _engine.Apply(pending, parameters, AppVersion, r =>
            {
                if (first)
                {
                    _store.EnsureUniqueId(r);
                    r.RestorePointCreated = createdRestore;
                    r.RestorePointNote = createdRestore ? null : (restoreError is not null ? "No restore point: " + restoreError : "No restore point (not requested)");
                    if (enabledRestore) r.Entries.Insert(0, Engine.SystemRestoreEnabledEntry(drive));
                    first = false;
                }
                _store.Save(r);
            }, progress));

            ShowApplyResult(run);
        }
        catch (Exception ex)
        {
            ResultTitle = "Something went wrong";
            ResultText = ex.Message + " Changes made before the problem are saved in Undo / History.";
            ResultNeedsRestart = false;
            IsResultDone = true;
        }
        finally
        {
            IsBusy = false;
            LoadHistory();
            await DetectAllAsync();
        }
    }

    // ------------------------------------------------------------------ undo

    bool _confirmOpen;
    Action? _confirmAction;
    public bool IsConfirmOpen { get => _confirmOpen; set => Set(ref _confirmOpen, value); }
    public string ConfirmTitle { get; private set; } = "";
    public string ConfirmText { get; private set; } = "";
    public string ConfirmOkText { get; private set; } = "";

    void Confirm(string title, string text, string ok, Action action)
    {
        ConfirmTitle = title;
        ConfirmText = text;
        ConfirmOkText = ok;
        _confirmTcs = null;
        _confirmAction = action;
        Raise(nameof(ConfirmTitle), nameof(ConfirmText), nameof(ConfirmOkText));
        IsConfirmOpen = true;
    }

    void AskUndo(RunVM r)
    {
        int n = r.Run.Entries.Count(e => e.Result == EntryResult.Changed && !e.Undone);
        bool apps = r.Run.Entries.Any(e => e.Result == EntryResult.Changed && !e.Undone && e.Type == ActionType.Appx);
        Confirm($"Undo the run from {r.Title}?",
            $"This puts back {n} saved value{(n == 1 ? "" : "s")} and settings as they were before that run." +
            (apps ? " Removed apps cannot be put back offline. Use Get it from the Store to open the Microsoft Store page for each one." : ""),
            "Undo", async () => await UndoAsync(r));
    }

    async Task UndoAsync(RunVM r)
    {
        IsBusy = true;
        IsResultDone = false;
        ResultTitle = "Undoing changes";
        ResultText = "Please keep this window open.";
        ProgressText = "";
        ProgressValue = 0.5;
        ClearResultLists();
        ResultNeedsRestart = false;
        IsResultOpen = true;
        try
        {
            var rep = await Task.Run(() => { var x = _engine.Undo(r.Run); _store.Save(r.Run); return x; });
            ResultTitle = rep.Failed == 0 ? "Undo finished" : "Undo finished, with some problems";
            bool needsRestart = RunSummary.NeedsRestartAfterUndo(r.Run.Entries, id => _catalog.Find(id)?.Restart == true);
            ResultNeedsRestart = needsRestart;
            ResultText = RunSummary.AfterUndo(rep, needsRestart);
            SetResultLists(rep.Messages, storeLinks: StoreLinks.ForEntries(r.Run.Entries));
        }
        catch (Exception ex)
        {
            ResultTitle = "Something went wrong";
            ResultText = ex.Message;
            ResultNeedsRestart = false;
        }
        finally
        {
            ProgressValue = 1;
            IsResultDone = true;
            IsBusy = false;
            r.Refresh();
            LoadHistory();
            await DetectAllAsync();
        }
    }

    void ShowApplyResult(RunRecord run)
    {
        var failed = run.Entries.Where(e => e.Result == EntryResult.Failed).ToList();
        // Only list skips that would previously have looked like failures (for example a deprovision that was already gone).
        var already = run.Entries.Where(e => e.Result == EntryResult.Skipped && AppxErrors.IsAlreadyDone(0, e.Message)).ToList();
        bool needsRestart = RunSummary.NeedsRestart(run, id => _catalog.Find(id)?.Restart == true);
        ResultNeedsRestart = needsRestart;
        ResultTitle = failed.Count == 0 ? "Done" : "Done, with some problems";
        ResultText = RunSummary.AfterApply(run, needsRestart, RunSummary.AppRemovalPendingRestart(run));
        SetResultLists(
            failed.Select(RunSummary.FailureLine),
            already.Count == 0 ? null : already.Select(RunSummary.AlreadyDoneLine),
            failed.Where(e => !string.IsNullOrEmpty(e.Details) || RunSummary.IsTechnical(e.Message)).Select(e => e.Details ?? e.Message ?? ""));
        IsResultDone = true;
    }

    void SaveRestoreEnableOnly(string drive, string restoreError)
    {
        var now = DateTimeOffset.Now;
        var run = new RunRecord
        {
            Id = now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture),
            StartedAt = now,
            FinishedAt = now,
            Windows = _sys.Info.FriendlyName,
            AppVersion = AppVersion,
            RestorePointCreated = false,
            RestorePointNote = "System Restore was turned on. The restore point could not be created and no tweaks were applied: " + restoreError,
        };
        run.Entries.Add(Engine.SystemRestoreEnabledEntry(drive));
        _store.EnsureUniqueId(run);
        _store.Save(run);
        LoadHistory();
    }

    TaskCompletionSource<bool>? _confirmTcs;

    void CompleteConfirm(bool ok)
    {
        IsConfirmOpen = false;
        var action = _confirmAction;
        var tcs = _confirmTcs;
        _confirmAction = null;
        _confirmTcs = null;
        if (ok) action?.Invoke();
        tcs?.TrySetResult(ok);
    }

    Task<bool> ConfirmAsync(string title, string text, string ok)
    {
        var tcs = new TaskCompletionSource<bool>();
        _confirmTcs = tcs;
        _confirmAction = null;
        ConfirmTitle = title;
        ConfirmText = text;
        ConfirmOkText = ok;
        Raise(nameof(ConfirmTitle), nameof(ConfirmText), nameof(ConfirmOkText));
        IsConfirmOpen = true;
        return tcs.Task;
    }

    void AskRestart()
    {
        Confirm("Restart now?", "Save your work in other apps first.", "Restart now", () =>
        {
            try { SystemRestart.Reboot(); }
            catch (Exception ex)
            {
                ResultText = "Windows could not be restarted: " + ex.Message + " You can restart from the Start menu.";
            }
        });
    }

    // ------------------------------------------------------------------ JSON view and links

    bool _jsonOpen;
    public bool IsJsonOpen { get => _jsonOpen; set => Set(ref _jsonOpen, value); }

    bool _aboutOpen;
    public bool IsAboutOpen { get => _aboutOpen; set => Set(ref _aboutOpen, value); }
    public string AboutTitle => About.Title;
    public string AboutBody => About.Body;
    public string AboutVersion => "Version " + AppVersion;
    public string AboutLicense => "Open source under the MIT license";

    /// <summary>
    /// Opens a link through Explorer, so the browser does not start with administrator rights.
    /// Only happens when the user clicks a link; the app itself never connects anywhere.
    /// </summary>
    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + url + "\"") { UseShellExecute = false })?.Dispose(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "WinBloatFree"); }
    }

    static void OpenFolder(string dir)
    {
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "WinBloatFree"); }
    }

    // ------------------------------------------------------------------ commands

    public ICommand SelectCategoryCommand { get; }
    public ICommand ShowHistoryCommand { get; }
    public ICommand SelectTweakCommand { get; }
    public ICommand SetPresetCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ClearVisibleCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand OpenReviewCommand { get; }
    public ICommand CloseReviewCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand CloseResultCommand { get; }
    public ICommand ShowJsonCommand { get; }
    public ICommand CloseJsonCommand { get; }
    public ICommand UndoRunCommand { get; }
    public ICommand ConfirmOkCommand { get; }
    public ICommand ConfirmCancelCommand { get; }
    public ICommand OpenRepoCommand { get; }
    public ICommand OpenIssuesCommand { get; }
    public ICommand OpenHistoryFolderCommand { get; }
    public ICommand OpenAboutCommand { get; }
    public ICommand CloseAboutCommand { get; }
    public ICommand OpenStoreCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand RestartNowCommand { get; }

    /// <summary>Closes the top-most overlay. Returns false when none was open.</summary>
    public bool CloseTopOverlay()
    {
        if (IsConfirmOpen) { CompleteConfirm(false); return true; }
        if (IsAboutOpen) { IsAboutOpen = false; return true; }
        if (IsJsonOpen) { IsJsonOpen = false; return true; }
        if (IsResultOpen && IsResultDone) { IsResultOpen = false; return true; }
        if (IsReviewOpen) { IsReviewOpen = false; return true; }
        return false;
    }
}
