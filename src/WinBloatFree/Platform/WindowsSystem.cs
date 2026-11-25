using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Management.Deployment;
using WinBloatFree.Core;

namespace WinBloatFree.Platform;

/// <summary>
/// The real Windows implementation of <see cref="ISystemAccess"/>. Uses the registry API,
/// the Service Control Manager, the Task Scheduler COM API, the WinRT PackageManager and
/// NVAPI from the installed NVIDIA driver. Nothing here uses the network.
/// </summary>
public sealed class WindowsSystem : ISystemAccess
{
    readonly object _appxLock = new();
    List<(string Name, string FullName, string Family)>? _installed;
    List<(string Name, string FullName, string Family)>? _provisioned;
    PackageManager? _pmInstance;
    PackageManager _pm => _pmInstance ??= new PackageManager();

    public SystemInfo Info { get; } = ReadInfo();

    static SystemInfo ReadInfo()
    {
        using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                                 .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        int build = int.TryParse(k?.GetValue("CurrentBuildNumber") as string, out var b) ? b : Environment.OSVersion.Version.Build;
        int ubr = k?.GetValue("UBR") is int u ? u : 0;
        string disp = k?.GetValue("DisplayVersion") as string ?? k?.GetValue("ReleaseId") as string ?? "";
        string edition = (k?.GetValue("EditionID") as string ?? "") switch
        {
            "Core" or "CoreSingleLanguage" or "CoreCountrySpecific" => "Home",
            "Professional" => "Pro",
            "ProfessionalWorkstation" => "Pro for Workstations",
            "Enterprise" => "Enterprise",
            "Education" => "Education",
            "IoTEnterprise" => "IoT Enterprise",
            var other => other,
        };
        return new SystemInfo(build, ubr, disp, edition);
    }

    // ------------------------------------------------------------------ registry

    static RegistryKey Root(RegHive hive) =>
        RegistryKey.OpenBaseKey(hive == RegHive.HKLM ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);

    public RegSnapshot ReadRegistry(RegHive hive, string key, string name)
    {
        using var root = Root(hive);
        using var k = root.OpenSubKey(key);
        if (k is null) return RegSnapshot.Missing(false);
        var v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (v is null) return RegSnapshot.Missing(true);
        var kind = k.GetValueKind(name);
        return kind switch
        {
            RegistryValueKind.DWord => new RegSnapshot(true, RegKind.DWord, unchecked((uint)(int)v).ToString(CultureInfo.InvariantCulture), true),
            RegistryValueKind.QWord => new RegSnapshot(true, RegKind.QWord, unchecked((ulong)(long)v).ToString(CultureInfo.InvariantCulture), true),
            RegistryValueKind.ExpandString => new RegSnapshot(true, RegKind.ExpandString, (string)v, true),
            RegistryValueKind.MultiString => new RegSnapshot(true, RegKind.MultiString, string.Join('\n', (string[])v), true),
            RegistryValueKind.Binary => new RegSnapshot(true, RegKind.Binary, Convert.ToHexString((byte[])v), true),
            _ => new RegSnapshot(true, RegKind.String, v.ToString(), true),
        };
    }

    public void WriteRegistry(RegHive hive, string key, string name, RegKind kind, string data) =>
        RegistryIO.SetValue(hive, key, name, kind, data);

    public void DeleteRegistryValue(RegHive hive, string key, string name) =>
        RegistryIO.DeleteValue(hive, key, name);

    public void DeleteKeyIfEmpty(RegHive hive, string key) =>
        RegistryIO.DeleteKeyIfEmpty(hive, key);

        // ------------------------------------------------------------------ services

    public ServiceSnapshot GetService(string name) => Services.Get(name);

    public void SetServiceStart(string name, ServiceStart start) => Services.SetStart(name, start);

    public void StopService(string name) => Services.Stop(name);

    public void StartService(string name) => Services.Start(name);

    // ------------------------------------------------------------------ scheduled tasks

    static (string Folder, string Name) SplitTask(string path)
    {
        int i = path.LastIndexOf('\\');
        return (i <= 0 ? "\\" : path[..i], path[(i + 1)..]);
    }

    static dynamic Scheduler()
    {
        var t = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler is not available.");
        dynamic s = Activator.CreateInstance(t)!;
        s.Connect();
        return s;
    }

    static bool IsNotFound(Exception ex) =>
        ex is FileNotFoundException or DirectoryNotFoundException ||
        (ex is COMException c && (c.HResult == unchecked((int)0x80070002) || c.HResult == unchecked((int)0x80070003)));

    public TaskSnapshot GetTask(string path)
    {
        var (folder, name) = SplitTask(path);
        try
        {
            dynamic s = Scheduler();
            dynamic f = s.GetFolder(folder);
            dynamic task = f.GetTask(name);
            bool enabled = task.Enabled;
            return new TaskSnapshot(true, enabled);
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            return new TaskSnapshot(false, false);
        }
    }

    public void SetTaskEnabled(string path, bool enabled)
    {
        var (folder, name) = SplitTask(path);
        try
        {
            dynamic s = Scheduler();
            dynamic f = s.GetFolder(folder);
            dynamic task = f.GetTask(name);
            task.Enabled = enabled;
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException("Windows protects this task and does not allow changing it, even as administrator.");
        }
    }

    // ------------------------------------------------------------------ appx

    void LoadPackages()
    {
        lock (_appxLock)
        {
            if (_installed is not null) return;
            var inst = new List<(string, string, string)>();
            foreach (var p in _pm.FindPackages())
            {
                try { inst.Add((p.Id.Name, p.Id.FullName, p.Id.FamilyName)); } catch (COMException) { }
            }
            var prov = new List<(string, string, string)>();
            try
            {
                foreach (var p in _pm.FindProvisionedPackages())
                {
                    try { prov.Add((p.Id.Name, p.Id.FullName, p.Id.FamilyName)); } catch (COMException) { }
                }
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or NotImplementedException) { }
            _installed = inst;
            _provisioned = prov;
        }
    }

    public AppxSnapshot GetAppx(string pattern)
    {
        LoadPackages();
        var inst = _installed!.Where(p => Wildcard.IsMatch(p.Name, pattern)).ToList();
        var prov = _provisioned!.Where(p => Wildcard.IsMatch(p.Name, pattern)).ToList();
        if (inst.Count == 0 && prov.Count == 0) return AppxSnapshot.None;
        return new AppxSnapshot(inst.Count > 0, prov.Count > 0,
            inst.Concat(prov).Select(p => p.FullName).Distinct().ToList(),
            inst.Concat(prov).Select(p => p.Family).Distinct().ToList());
    }

    public void RemoveAppx(string pattern)
    {
        LoadPackages();
        var errors = new List<string>();
        var details = new List<string>();
        foreach (var p in _installed!.Where(p => Wildcard.IsMatch(p.Name, pattern)).ToList())
        {
            try
            {
                var r = _pm.RemovePackageAsync(p.FullName, RemovalOptions.RemoveForAllUsers).AsTask().GetAwaiter().GetResult();
                int hr = r.ExtendedErrorCode?.HResult ?? 0;
                if (hr != 0 && !AppxErrors.IsAlreadyDone(hr, r.ErrorText))
                {
                    errors.Add(AppxErrors.ShortRemoveFailure(p.Name, forNewUsers: false));
                    if (!string.IsNullOrEmpty(r.ErrorText)) details.Add(p.FullName + ": " + r.ErrorText);
                }
            }
            catch (Exception ex) when (AppxErrors.IsAlreadyDone(ex)) { }
            catch (Exception ex)
            {
                errors.Add(AppxErrors.ShortRemoveFailure(p.Name, forNewUsers: false));
                details.Add(p.FullName + ": " + ex.Message);
            }
        }
        // Only deprovision packages that are actually provisioned for new users.
        foreach (var fam in _provisioned!.Where(p => Wildcard.IsMatch(p.Name, pattern)).Select(p => p.Family).Distinct().ToList())
        {
            try
            {
                var r = _pm.DeprovisionPackageForAllUsersAsync(fam).AsTask().GetAwaiter().GetResult();
                int hr = r.ExtendedErrorCode?.HResult ?? 0;
                if (hr != 0 && !AppxErrors.IsAlreadyDone(hr, r.ErrorText))
                {
                    errors.Add(AppxErrors.ShortRemoveFailure(fam, forNewUsers: true));
                    if (!string.IsNullOrEmpty(r.ErrorText)) details.Add(fam + " (new users): " + r.ErrorText);
                }
            }
            catch (Exception ex) when (AppxErrors.IsAlreadyDone(ex)) { }
            catch (Exception ex)
            {
                errors.Add(AppxErrors.ShortRemoveFailure(fam, forNewUsers: true));
                details.Add(fam + " (new users): " + ex.Message);
            }
        }
        lock (_appxLock) { _installed = null; _provisioned = null; }
        if (errors.Count > 0)
            throw new DetailedException(string.Join(" ", errors), details.Count > 0 ? string.Join("\n", details) : null);
    }

    // ------------------------------------------------------------------ OneDrive

    static string? OneDriveUninstallString()
    {
        foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var k = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\OneDriveSetup.exe");
            if (k?.GetValue("UninstallString") is string s && s.Length > 0) return s;
        }
        return null;
    }

    static IEnumerable<string> OneDriveExePaths()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "OneDrive", "OneDrive.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft OneDrive", "OneDrive.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft OneDrive", "OneDrive.exe");
    }

    public bool IsOneDriveInstalled() => OneDriveUninstallString() is not null || OneDriveExePaths().Any(File.Exists);

    static string? WindowsOneDriveSetup()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var p in new[] { Path.Combine(win, "System32", "OneDriveSetup.exe"), Path.Combine(win, "SysWOW64", "OneDriveSetup.exe") })
            if (File.Exists(p)) return p;
        return null;
    }

    public void UninstallOneDrive()
    {
        foreach (var p in Process.GetProcessesByName("OneDrive"))
        {
            try { p.Kill(); p.WaitForExit(5000); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            finally { p.Dispose(); }
        }
        var cmd = OneDriveUninstallString();
        var (file, args) = cmd is not null ? CommandLine.Split(cmd) : (WindowsOneDriveSetup() ?? throw new FileNotFoundException("The OneDrive uninstaller was not found."), "/uninstall");
        if (!args.Contains("/uninstall", StringComparison.OrdinalIgnoreCase)) args = "/uninstall " + args;
        using var proc = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true })
                         ?? throw new InvalidOperationException("Could not start the OneDrive uninstaller.");
        if (!proc.WaitForExit(180_000)) throw new TimeoutException("The OneDrive uninstaller did not finish within 3 minutes.");
        if (proc.ExitCode != 0) throw new IOException($"The OneDrive uninstaller ended with code {proc.ExitCode}.");
    }

    public bool ReinstallOneDrive()
    {
        var setup = WindowsOneDriveSetup();
        if (setup is null) return false;
        Process.Start(new ProcessStartInfo(setup) { UseShellExecute = false })?.Dispose();
        return true;
    }

    // ------------------------------------------------------------------ NVIDIA

    public NvSnapshot GetNvidiaSetting(uint settingId) => NvApi.Get(settingId);

    public void SetNvidiaSetting(uint settingId, uint value) => NvApi.Set(settingId, value);

    public void ResetNvidiaSetting(uint settingId) => NvApi.Reset(settingId);
}
