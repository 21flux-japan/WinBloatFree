using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using WinBloatFree.Core;

namespace WinBloatFree;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }

    protected void Raise(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

public sealed class RelayCommand : ICommand
{
    readonly Action<object?> _run;
    readonly Func<object?, bool>? _can;

    public RelayCommand(Action<object?> run, Func<object?, bool>? can = null) { _run = run; _can = can; }
    public RelayCommand(Action run, Func<bool>? can = null) : this(_ => run(), can is null ? null : _ => can()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _can?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _run(parameter);
}

/// <summary>One line in the "What changes" box.</summary>
public sealed record ChangeLine(string Label, string Target, string From, string To)
{
    public bool HasFrom => From.Length > 0;
}

public sealed class TweakVM : Observable
{
    readonly MainViewModel _owner;
    bool _isChecked, _isFocused;
    Detection _detection = Detection.Unknown;
    ParamOption? _selectedOption;

    public TweakVM(Tweak tweak, string categoryName, MainViewModel owner)
    {
        Tweak = tweak;
        CategoryName = categoryName;
        _owner = owner;
        if (tweak.Parameter is { } p) _selectedOption = p.Options.FirstOrDefault(o => o.Value == p.Default);
    }

    public Tweak Tweak { get; }
    public string Id => Tweak.Id;
    public string CategoryName { get; }
    public string Name => Tweak.Name;
    public string Summary => Tweak.Summary;
    public string Description => Tweak.Description;
    public Risk Risk => Tweak.Risk;
    public string RiskText => Tweak.Risk.ToString();
    public string? GoodToKnow => Tweak.GoodToKnow;
    public bool HasGoodToKnow => !string.IsNullOrEmpty(Tweak.GoodToKnow);
    public string? Warning => Tweak.Warning;
    public bool HasWarning => !string.IsNullOrEmpty(Tweak.Warning);
    public string UndoText => Review.UndoText(Tweak);
    public bool IsLevel => Tweak.Group is not null;
    public bool IsLeaveAsIs => IsLevel && Tweak.Actions.Count == 0;
    public bool NeedsRestart => Tweak.Restart;
    public bool HasParameter => Tweak.Parameter is not null;
    public IReadOnlyList<ParamOption> Options => Tweak.Parameter?.Options ?? (IReadOnlyList<ParamOption>)Array.Empty<ParamOption>();
    public string JsonText => Tweak.RawJson;

    public ParamOption? SelectedOption
    {
        get => _selectedOption;
        set { if (Set(ref _selectedOption, value)) _owner.OnSelectionChanged(); }
    }

    public string? ParameterValue => _selectedOption?.Value;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            if (IsLevel && !value) return; // radio: a level is unchecked only by picking another
            _isChecked = value;
            Raise(nameof(IsChecked));
            _owner.OnChecked(this);
        }
    }

    /// <summary>Set by the owner without the side effects of <see cref="IsChecked"/>.</summary>
    internal void SetChecked(bool value)
    {
        if (_isChecked == value) return;
        _isChecked = value;
        Raise(nameof(IsChecked));
    }

    public bool IsFocused
    {
        get => _isFocused;
        set => Set(ref _isFocused, value);
    }

    public Detection Detection
    {
        get => _detection;
        set
        {
            _detection = value;
            Raise(nameof(Detection), nameof(Status), nameof(StatusText), nameof(StatusMuted), nameof(IsAvailable), nameof(Changes), nameof(ShowLevelStatus));
        }
    }

    public TweakStatus Status => _detection.Status;

    /// <summary>Show "Active" on a Windows Update level that is already in place.</summary>
    public bool ShowLevelStatus => IsLevel && !IsLeaveAsIs && Status is TweakStatus.Off or TweakStatus.Partial;

    public bool ShowLevelRisk => IsLevel && !IsLeaveAsIs && !HasParameter;

    public bool IsAvailable => Status is not (TweakStatus.NotPresent or TweakStatus.NotApplicable or TweakStatus.CheckFailed);

    public bool StatusMuted => Status is TweakStatus.Off or TweakStatus.NotPresent or TweakStatus.NotApplicable or TweakStatus.CheckFailed;

    public string StatusText => Status switch
    {
        TweakStatus.Unknown => "Checking",
        TweakStatus.On when IsLevel => "Not set",
        TweakStatus.On => "Currently on",
        TweakStatus.Partial when IsLevel => "Partly set",
        TweakStatus.Partial => "Partly on",
        TweakStatus.Off when IsLeaveAsIs => "",
        TweakStatus.Off => Tweak.AppliedLabel ?? "Already off",
        TweakStatus.NotPresent => Tweak.Actions.Any(a => a.Type is ActionType.Appx or ActionType.OneDriveUninstall) ? "Not installed" : "Not on this PC",
        TweakStatus.NotApplicable => "Not applicable",
        TweakStatus.CheckFailed => "Could not check",
        _ => "",
    };

    /// <summary>Detail pane lines: what is there now and what it becomes.</summary>
    public IReadOnlyList<ChangeLine> Changes
    {
        get
        {
            if (_detection.Actions.Count > 0)
                return _detection.Actions.Select(a => new ChangeLine(a.Label, a.Target, a.Present && !a.Failed ? a.Current : "", a.Failed ? a.Current : a.Present ? a.Wanted : "Skipped: " + a.Current)).ToList();
            return Tweak.Actions.Select(a => new ChangeLine(Engine.Label(a.Type), Engine.Target(a), "", Describe(a))).ToList();
        }
    }

    static string Describe(TweakAction a) => a.Type switch
    {
        ActionType.Registry => $"{a.Name} = {a.Value}",
        ActionType.RegistryDelete => $"{a.Name} removed",
        ActionType.RegistryToken => $"{a.Name}: {a.Token}={a.Value}",
        ActionType.Service => "Startup type: " + StartTypes.Display(a.Startup),
        ActionType.Task => "Disabled",
        ActionType.Appx => "Removed for all users",
        ActionType.OneDriveUninstall => "Uninstalled",
        ActionType.NvidiaDrs => "value " + a.Value,
        _ => "",
    };
}

public sealed class CategoryVM : Observable
{
    bool _isActive;

    public CategoryVM(Category c, IEnumerable<TweakVM> tweaks)
    {
        Category = c;
        Tweaks = tweaks.ToList();
    }

    public Category Category { get; }
    public string Id => Category.Id;
    public string Name => Category.Name;
    public string Description => Category.Description;
    public string Glyph => char.ConvertFromUtf32(int.Parse(Category.Icon, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    public bool IsUpdates => Category.Kind == "updates";
    public List<TweakVM> Tweaks { get; }
    public IEnumerable<TweakVM> Levels => Tweaks.Where(t => t.IsLevel);
    public IEnumerable<TweakVM> Options => Tweaks.Where(t => !t.IsLevel);

    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    IEnumerable<TweakVM> Countable => Tweaks.Where(t => !t.IsLeaveAsIs);
    public int Selected => Countable.Count(t => t.IsChecked && t.IsAvailable);
    public string CountSelected => Selected.ToString(CultureInfo.InvariantCulture);
    public string CountTotal => "/" + Countable.Count().ToString(CultureInfo.InvariantCulture);

    public void Refresh() => Raise(nameof(Selected), nameof(CountSelected), nameof(CountTotal));
}

public sealed class EntryVM
{
    public EntryVM(BackupEntry e) => Entry = e;
    public BackupEntry Entry { get; }
    public string Name => Entry.TweakName;
    public string Target => Entry.Target;
    public string Result => Entry.Undone ? "Undone" : Entry.Result switch
    {
        EntryResult.Changed => "Changed",
        EntryResult.Skipped => "Skipped",
        _ => "Failed",
    };
    public string Message => Entry.Undone ? Entry.UndoMessage ?? "" : (Entry.UndoMessage ?? Entry.Message ?? "");
    public bool IsFailed => Entry.Result == EntryResult.Failed;
}

public sealed class RunVM : Observable
{
    public RunVM(RunRecord run) { Run = run; }
    public RunRecord Run { get; }
    public string Title => Run.StartedAt.ToLocalTime().ToString("MMM d, yyyy  HH:mm", CultureInfo.CurrentCulture);
    public string Summary
    {
        get
        {
            var tweaks = Run.TweakIds.Count;
            var s = $"{tweaks} tweak{(tweaks == 1 ? "" : "s")}, {Run.ChangedCount} change{(Run.ChangedCount == 1 ? "" : "s")}";
            if (Run.FailedCount > 0) s += $", {Run.FailedCount} failed";
            return s;
        }
    }
    public string RestoreNote => Run.RestorePointCreated ? "Restore point created" : (Run.RestorePointNote ?? "No restore point");
    public string StateText => Run.UndoneAt is { } u ? "Undone " + u.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.CurrentCulture) : (Run.CanUndo ? "" : "Nothing to undo");
    public bool CanUndo => Run.CanUndo;
    public List<EntryVM> Entries => Run.Entries.Where(e => e.Result != EntryResult.Skipped).Select(e => new EntryVM(e)).ToList();
    public void Refresh() => Raise(nameof(Summary), nameof(StateText), nameof(CanUndo), nameof(Entries));
}

public sealed class ReviewGroupVM
{
    public ReviewGroupVM(string name, List<TweakVM> items, bool expanded) { Name = name; Items = items; IsExpanded = expanded; }
    public string Name { get; }
    public List<TweakVM> Items { get; }
    public bool IsExpanded { get; set; }
    public string CountText => Items.Count == 1 ? "1 change" : $"{Items.Count} changes";
}

public sealed class ObservableList<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var i in items) Items.Add(i);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}
