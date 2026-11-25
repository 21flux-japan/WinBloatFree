using System.Reflection;
using WinBloatFree.Core;
using WinBloatFree.Core.Tests;

int passed = 0, failed = 0;
foreach (var m in typeof(Tests).GetMethods(BindingFlags.Public | BindingFlags.Static))
{
    try { m.Invoke(null, null); passed++; Console.WriteLine($"  ok    {m.Name}"); }
    catch (TargetInvocationException ex) { failed++; Console.WriteLine($"  FAIL  {m.Name}: {ex.InnerException?.Message}"); }
}
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

static class Assert
{
    public static void True(bool c, string msg = "expected true") { if (!c) throw new Exception(msg); }
    public static void Equal<T>(T expected, T actual, string what = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what} expected <{expected}> but got <{actual}>");
    }
}

static class Tests
{
    static readonly TweakCatalog Catalog = TweakCatalog.LoadEmbedded();

    public static void CatalogLoadsAndIsValid()
    {
        Assert.Equal(8, Catalog.Categories.Count, "categories");
        var errors = CatalogValidator.Validate(Catalog);
        Assert.True(errors.Count == 0, string.Join("; ", errors));
        Assert.True(Catalog.Tweaks.All(t => t.RawJson.Contains(t.Id)), "raw json kept");
    }

    public static void DiagTrackAndDmwappushAreSeparate()
    {
        var a = Catalog.Find("telemetry.diagtrack")!;
        var b = Catalog.Find("telemetry.dmwappush")!;
        Assert.Equal(Risk.Safe, a.Risk);
        Assert.Equal(Risk.Moderate, b.Risk);
        Assert.True(a.Actions.All(x => x.Service != "dmwappushservice"), "dmwappush not in DiagTrack tweak");
    }

    public static void VrrAndGsyncAreSeparate()
    {
        var vrr = Catalog.Find("performance.windows-vrr")!;
        var gs = Catalog.Find("performance.nvidia-gsync")!;
        Assert.Equal(ActionType.RegistryToken, vrr.Actions.Single().Type);
        Assert.Equal(null, vrr.Requires);
        Assert.Equal("nvidia", gs.Requires);
    }

    public static void TokensKeepOtherSettings()
    {
        Assert.Equal("SwapEffectUpgradeEnable=1;VRROptimizeEnable=0;AutoHDREnable=1;",
            RegistryTokens.Set("SwapEffectUpgradeEnable=1;VRROptimizeEnable=1;AutoHDREnable=1;", "VRROptimizeEnable", "0"));
        Assert.Equal("VRROptimizeEnable=0;", RegistryTokens.Set(null, "VRROptimizeEnable", "0"));
        Assert.Equal("A=1;VRROptimizeEnable=0;", RegistryTokens.Set("A=1", "VRROptimizeEnable", "0"));
        Assert.Equal("1", RegistryTokens.Get("a=2; vrroptimizeenable=1;", "VRROptimizeEnable"));
        Assert.Equal(null, RegistryTokens.Get("", "X"));
    }

    public static void PlaceholdersResolve()
    {
        var ctx = new ResolveContext(new SystemInfo(19045, 1, "22H2", "Home"), new DateTime(2026, 11, 25, 1, 0, 0, DateTimeKind.Utc), "2");
        Assert.Equal("Windows 10 22H2", Placeholders.Resolve("{productVersion} {displayVersion}", ctx));
        Assert.Equal("2026-11-25T01:00:00Z", Placeholders.Resolve("{nowUtc}", ctx));
        Assert.Equal("2026-12-09T01:00:00Z", Placeholders.Resolve("{untilUtc}", ctx));
        Assert.Equal("Windows 11", new SystemInfo(22631, 0, "23H2", "Pro").ProductVersion);
    }

    public static void WildcardMatches()
    {
        Assert.True(Wildcard.IsMatch("king.com.CandyCrushSaga", "king.com.*"));
        Assert.True(Wildcard.IsMatch("4DF9E0F8.Netflix", "*.Netflix"));
        Assert.True(!Wildcard.IsMatch("Microsoft.WindowsStore", "Microsoft.WindowsStor"));
        Assert.True(Wildcard.IsMatch("microsoft.bingnews", "Microsoft.BingNews"));
    }

    public static void PresetsPickOneLevelAndSkipDangerous()
    {
        foreach (var p in new[] { Preset.Recommended, Preset.Privacy, Preset.Everything })
        {
            var sel = Presets.Select(Catalog, p);
            Assert.Equal(1, sel.Count(id => Catalog.Find(id)!.Group == "wu-level"), $"{p} levels");
            Assert.True(!sel.Contains("updates.level.off"), $"{p} must not turn updates fully off");
            Assert.True(!sel.Contains("apps.calculator") && !sel.Contains("apps.photos") && !sel.Contains("apps.snipping-tool"), $"{p} keeps essentials");
            Assert.True(!sel.Contains("services.print-spooler"), $"{p} keeps printing");
            Assert.Equal(p, Presets.Match(Catalog, sel), "round trip");
        }
        var rec = Presets.Select(Catalog, Preset.Recommended);
        Assert.True(rec.All(id => Catalog.Find(id)!.Risk != Risk.Advanced), "recommended has no Advanced");
        Assert.True(Presets.Select(Catalog, Preset.Privacy).IsSupersetOf(rec.Where(id => Catalog.Find(id)!.Category != "performance")), "privacy includes recommended");
        Assert.Equal(Preset.Custom, Presets.Match(Catalog, new HashSet<string> { "telemetry.diagtrack" }));
    }

    public static void ApplyAndUndoRegistryRoundTrip()
    {
        var sys = new FakeSystem();
        var eng = new Engine(sys);
        var t = Catalog.Find("telemetry.advertising-id")!;
        Assert.Equal(TweakStatus.On, eng.Detect(t).Status);
        sys.WriteRegistry(RegHive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", RegKind.DWord, "1");
        var run = eng.Apply(new[] { t }, null, "test");
        Assert.Equal(2, run.ChangedCount);
        Assert.Equal(TweakStatus.Off, eng.Detect(t).Status);
        var rep = eng.Undo(run);
        Assert.Equal(2, rep.Restored);
        Assert.Equal("1", sys.ReadRegistry(RegHive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled").Data);
        Assert.True(!sys.ReadRegistry(RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy").Exists, "policy value removed");
        Assert.True(!sys.Keys.Contains(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo"), "created key removed");
        Assert.True(run.UndoneAt is not null, "run marked undone");
    }

    public static void ApplySkipsWhatIsAlreadyDone()
    {
        var sys = new FakeSystem();
        var eng = new Engine(sys);
        var t = Catalog.Find("telemetry.feedback")!;
        eng.Apply(new[] { t }, null, "test");
        var second = eng.Apply(new[] { t }, null, "test");
        Assert.Equal(0, second.ChangedCount);
        Assert.True(!second.CanUndo, "nothing to undo");
    }

    public static void ServiceRoundTripRestartsService()
    {
        var sys = new FakeSystem();
        sys.Services["DiagTrack"] = new ServiceSnapshot(true, ServiceStart.Automatic, true);
        var eng = new Engine(sys);
        var t = Catalog.Find("telemetry.diagtrack")!;
        var run = eng.Apply(new[] { t }, null, "test");
        Assert.Equal(ServiceStart.Disabled, sys.Services["DiagTrack"].Start);
        Assert.True(!sys.Services["DiagTrack"].Running, "stopped");
        eng.Undo(run);
        Assert.Equal(ServiceStart.Automatic, sys.Services["DiagTrack"].Start);
        Assert.True(sys.Services["DiagTrack"].Running, "started again");
    }

    public static void MissingServicesAndTasksAreSkipped()
    {
        var sys = new FakeSystem();
        var eng = new Engine(sys);
        Assert.Equal(TweakStatus.NotPresent, eng.Detect(Catalog.Find("services.fax")!).Status);
        sys.Tasks[@"\Microsoft\Windows\Autochk\Proxy"] = true;
        var t = Catalog.Find("telemetry.tasks")!;
        Assert.Equal(TweakStatus.On, eng.Detect(t).Status);
        var run = eng.Apply(new[] { t }, null, "test");
        Assert.Equal(1, run.ChangedCount);
        Assert.Equal(0, run.FailedCount);
        eng.Undo(run);
        Assert.True(sys.Tasks[@"\Microsoft\Windows\Autochk\Proxy"], "task enabled again");
    }

    public static void VrrTokenUndoRestoresExactString()
    {
        var sys = new FakeSystem();
        const string key = @"Software\Microsoft\DirectX\UserGpuPreferences";
        sys.WriteRegistry(RegHive.HKCU, key, "DirectXUserGlobalSettings", RegKind.String, "SwapEffectUpgradeEnable=1;AutoHDREnable=1;");
        var eng = new Engine(sys);
        var t = Catalog.Find("performance.windows-vrr")!;
        Assert.Equal(TweakStatus.On, eng.Detect(t).Status);
        var run = eng.Apply(new[] { t }, null, "test");
        Assert.Equal("SwapEffectUpgradeEnable=1;AutoHDREnable=1;VRROptimizeEnable=0;", sys.ReadRegistry(RegHive.HKCU, key, "DirectXUserGlobalSettings").Data);
        Assert.Equal(TweakStatus.Off, eng.Detect(t).Status);
        eng.Undo(run);
        Assert.Equal("SwapEffectUpgradeEnable=1;AutoHDREnable=1;", sys.ReadRegistry(RegHive.HKCU, key, "DirectXUserGlobalSettings").Data);

        var sys2 = new FakeSystem();
        var eng2 = new Engine(sys2);
        var run2 = eng2.Apply(new[] { t }, null, "test");
        eng2.Undo(run2);
        Assert.True(!sys2.ReadRegistry(RegHive.HKCU, key, "DirectXUserGlobalSettings").Exists, "value removed when it did not exist");
    }

    public static void NvidiaOnlyWhenPresent()
    {
        var t = Catalog.Find("performance.nvidia-gsync")!;
        var none = new FakeSystem();
        Assert.Equal(TweakStatus.NotApplicable, new Engine(none).Detect(t).Status);
        Assert.Equal(0, new Engine(none).Apply(new[] { t }, null, "test").ChangedCount);

        var sys = new FakeSystem { Nvidia = true };
        var eng = new Engine(sys);
        Assert.Equal(TweakStatus.On, eng.Detect(t).Status);
        var run = eng.Apply(new[] { t }, null, "test");
        Assert.Equal(0u, sys.Nv[0x1194F158]);
        eng.Undo(run);
        Assert.True(!sys.Nv.ContainsKey(0x1194F158), "override removed, driver default again");

        sys.Nv[0x1194F158] = 2;
        run = eng.Apply(new[] { t }, null, "test");
        eng.Undo(run);
        Assert.Equal(2u, sys.Nv[0x1194F158]);
    }

    public static void AppRemovalIsHonestAboutUndo()
    {
        var sys = new FakeSystem();
        sys.Packages.Add("Microsoft.BingNews");
        var eng = new Engine(sys);
        var t = Catalog.Find("apps.bing-news")!;
        var run = eng.Apply(new[] { t }, null, "test");
        Assert.True(!sys.Packages.Contains("Microsoft.BingNews"), "removed");
        Assert.Equal(TweakStatus.NotPresent, eng.Detect(t).Status);
        var rep = eng.Undo(run);
        Assert.Equal(1, rep.Manual);
        Assert.True(rep.Messages[0].Contains("Microsoft Store"), "points to the Store");
    }

    public static void TargetReleaseUsesCurrentVersion()
    {
        var sys = new FakeSystem();
        var eng = new Engine(sys);
        eng.Apply(new[] { Catalog.Find("updates.level.block")! }, null, "test");
        const string wu = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
        Assert.Equal("Windows 11", sys.ReadRegistry(RegHive.HKLM, wu, "ProductVersion").Data);
        Assert.Equal("25H2", sys.ReadRegistry(RegHive.HKLM, wu, "TargetReleaseVersionInfo").Data);
        Assert.Equal(RegKind.String, sys.ReadRegistry(RegHive.HKLM, wu, "ProductVersion").Kind);
    }

    public static void PauseUsesChosenWeeks()
    {
        var sys = new FakeSystem();
        var eng = new Engine(sys);
        var t = Catalog.Find("updates.level.pause")!;
        eng.Apply(new[] { t }, new Dictionary<string, string> { [t.Id] = "3" }, "test");
        var end = sys.ReadRegistry(RegHive.HKLM, @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "PauseUpdatesExpiryTime").Data!;
        var days = (DateTime.Parse(end, null, System.Globalization.DateTimeStyles.AdjustToUniversal) - DateTime.UtcNow).TotalDays;
        Assert.True(days > 20.9 && days < 21.1, $"3 weeks, got {days}");
    }

    public static void ReviewCountsRiskAndRestart()
    {
        var sel = Presets.Select(Catalog, Preset.Recommended);
        var pending = Review.Pending(Catalog, sel, _ => TweakStatus.On);
        Assert.True(pending.All(t => t.Actions.Count > 0), "leave as is not counted");
        var sum = Review.Summarize(Catalog, pending);
        Assert.Equal(sum.Total, sum.Safe + sum.Moderate + sum.Advanced);
        Assert.Equal(sum.Total, sum.Groups.Sum(g => g.Tweaks.Count));
        Assert.True(sum.NeedRestart > 0, "some need restart");
        var none = Review.Pending(Catalog, sel, _ => TweakStatus.Off);
        Assert.Equal(0, none.Count);
    }

    public static void HistoryRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbf-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sys = new FakeSystem();
            sys.Services["DiagTrack"] = new ServiceSnapshot(true, ServiceStart.Automatic, true);
            var store = new HistoryStore(dir);
            var run = new Engine(sys).Apply(new[] { Catalog.Find("telemetry.diagtrack")! }, null, "0.1.0", r => store.Save(r));
            var loaded = store.LoadAll().Single();
            Assert.Equal(run.Id, loaded.Id);
            Assert.Equal(ServiceStart.Automatic, loaded.Entries[0].PreviousService!.Start);
            new Engine(sys).Undo(loaded);
            Assert.Equal(ServiceStart.Automatic, sys.Services["DiagTrack"].Start);
            File.WriteAllText(Path.Combine(dir, "run-broken.json"), "{ not json");
            Assert.Equal(1, store.LoadAll().Count);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    public static void UndoTextIsGenerated()
    {
        Assert.True(Review.UndoText(Catalog.Find("telemetry.activity-history")!).Contains("registry values"));
        Assert.True(Review.UndoText(Catalog.Find("services.fax")!).Contains("starts services"));
        Assert.True(Review.UndoText(Catalog.Find("apps.tips")!).Contains("Microsoft Store"));
    }

    public static void NoDashesOrNetworkCodeInRepo()
    {
        var root = FindRoot();
        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}mockup{Path.DirectorySeparatorChar}"))
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml") || f.EndsWith(".json") || f.EndsWith(".md") || f.EndsWith(".yml") || f.EndsWith(".csproj")).ToList();
        Assert.True(files.Count > 10, "found source files");
        var self = Path.GetFullPath(Path.Combine(root, "tests", "WinBloatFree.Core.Tests", "Program.cs"));
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            Assert.True(!text.Contains('\u2014') && !text.Contains('\u2013'), $"dash in {f}");
            if (f.Contains($"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}") && f.EndsWith(".cs"))
                foreach (var bad in new[] { "HttpClient", "WebClient", "WebRequest", "System.Net.Sockets", "TcpClient", "DownloadString" })
                    Assert.True(!text.Contains(bad), $"{bad} in {f}");
        }
    }

    public static void UnreadableStateIsNotSelectable()
    {
        var states = new[] { new ActionState(0, ActionType.Appx, "APP PACKAGE", "x", true, false, "could not read", "", Failed: true) };
        Assert.Equal(TweakStatus.CheckFailed, Engine.Summarize(states));
    }

    public static void UninstallStringSplits()
    {
        Assert.Equal((@"C:\Users\m\AppData\Local\Microsoft\OneDrive\25.1\OneDriveSetup.exe", "/uninstall"),
            CommandLine.Split("\"C:\\Users\\m\\AppData\\Local\\Microsoft\\OneDrive\\25.1\\OneDriveSetup.exe\"  /uninstall "));
        Assert.Equal((@"C:\Program Files\Microsoft OneDrive\OneDriveSetup.exe", "/uninstall /allusers"),
            CommandLine.Split(@"C:\Program Files\Microsoft OneDrive\OneDriveSetup.exe /uninstall /allusers"));
    }

    public static void XamlResourcesAndBindingsResolve()
    {
        var root = FindRoot();
        var app = Path.Combine(root, "src", "WinBloatFree");
        var xamlFiles = Directory.GetFiles(app, "*.xaml", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")).ToList();
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        HashSet<string> Keys(string file) => System.Xml.Linq.XDocument.Load(file).Descendants().Select(e => (string?)e.Attribute(x + "Key")).Where(k => k is not null).Select(k => k!).ToHashSet();
        var dark = Keys(Path.Combine(app, "Themes", "Dark.xaml"));
        var light = Keys(Path.Combine(app, "Themes", "Light.xaml"));
        Assert.True(dark.SetEquals(light), "Dark and Light define the same keys");
        var all = new HashSet<string>(dark);
        foreach (var f in xamlFiles) all.UnionWith(Keys(f));

        var csText = string.Join("\n", Directory.GetFiles(app, "*.cs", SearchOption.AllDirectories).Concat(Directory.GetFiles(Path.Combine(root, "src", "WinBloatFree.Core"), "*.cs")).Select(File.ReadAllText));
        var props = System.Text.RegularExpressions.Regex.Matches(csText, @"public\s+(?:static\s+)?(?:const\s+)?[\w<>\[\]?,. ]+\s+(\w+)\s*(?:\{|=>|=|\()").Select(m => m.Groups[1].Value)
            .Concat(System.Text.RegularExpressions.Regex.Matches(csText, @"record\s+\w+\(([^)]*)\)").SelectMany(m => m.Groups[1].Value.Split(',').Select(p => p.Trim().Split(' ').Last())))
            .ToHashSet();
        props.UnionWith(new[] { "Count", "ActualWidth", "IsDropDownOpen", "IsExpanded", "DataContext" });

        foreach (var f in xamlFiles)
        {
            var text = File.ReadAllText(f);
            System.Xml.Linq.XDocument.Parse(text); // well-formed
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\{(?:Dynamic|Static)Resource\s+([\w.]+)\}"))
                Assert.True(all.Contains(m.Groups[1].Value), $"{Path.GetFileName(f)}: missing resource {m.Groups[1].Value}");
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\{Binding\s+(?:Path=)?([\w.]+)(\s*=)?"))
                if (!m.Groups[2].Success) foreach (var part in m.Groups[1].Value.Split('.'))
                    Assert.True(props.Contains(part), $"{Path.GetFileName(f)}: binding to unknown property {part}");
        }
    }

    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "WinBloatFree.sln"))) d = d.Parent;
        return d?.FullName ?? throw new Exception("repo root not found");
    }
}
