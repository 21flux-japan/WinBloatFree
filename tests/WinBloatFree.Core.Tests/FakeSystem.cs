using WinBloatFree.Core;

namespace WinBloatFree.Core.Tests;

/// <summary>In-memory Windows, used to test the engine on any OS.</summary>
sealed class FakeSystem : ISystemAccess
{
    public SystemInfo Info { get; set; } = new(26200, 7171, "25H2", "Pro");
    public readonly Dictionary<string, (RegKind Kind, string Data)> Values = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> Keys = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, ServiceSnapshot> Services = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, bool> Tasks = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> Packages = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> Provisioned = new(StringComparer.OrdinalIgnoreCase);
    public bool OneDrive = true;
    public bool Nvidia;
    public readonly Dictionary<uint, uint> Nv = new();
    public readonly List<string> Log = new();

    static string K(RegHive h, string key) => $"{h}\\{key}";
    static string V(RegHive h, string key, string name) => $"{K(h, key)}::{name}";

    public RegSnapshot ReadRegistry(RegHive hive, string key, string name) =>
        Values.TryGetValue(V(hive, key, name), out var v) ? new RegSnapshot(true, v.Kind, v.Data, true) : RegSnapshot.Missing(Keys.Contains(K(hive, key)));

    public void WriteRegistry(RegHive hive, string key, string name, RegKind kind, string data)
    {
        Keys.Add(K(hive, key));
        Values[V(hive, key, name)] = (kind, data);
        Log.Add($"write {V(hive, key, name)}={data}");
    }

    public void DeleteRegistryValue(RegHive hive, string key, string name) { Values.Remove(V(hive, key, name)); Log.Add($"delete {V(hive, key, name)}"); }

    public void DeleteKeyIfEmpty(RegHive hive, string key)
    {
        var k = K(hive, key);
        if (!Values.Keys.Any(v => v.StartsWith(k + "::", StringComparison.OrdinalIgnoreCase))) Keys.Remove(k);
    }

    public ServiceSnapshot GetService(string name) => Services.TryGetValue(name, out var s) ? s : new ServiceSnapshot(false, null, false);
    public void SetServiceStart(string name, ServiceStart start) => Services[name] = Services[name] with { Start = start };
    public void StopService(string name) => Services[name] = Services[name] with { Running = false };
    public void StartService(string name) => Services[name] = Services[name] with { Running = true };

    public TaskSnapshot GetTask(string path) => Tasks.TryGetValue(path, out var e) ? new TaskSnapshot(true, e) : new TaskSnapshot(false, false);
    public void SetTaskEnabled(string path, bool enabled) => Tasks[path] = enabled;

    public AppxSnapshot GetAppx(string pattern)
    {
        var inst = Packages.Where(p => Wildcard.IsMatch(p, pattern)).ToList();
        var prov = Provisioned.Where(p => Wildcard.IsMatch(p, pattern)).ToList();
        if (inst.Count == 0 && prov.Count == 0) return AppxSnapshot.None;
        var names = inst.Concat(prov).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new AppxSnapshot(inst.Count > 0, prov.Count > 0, names, names.Select(x => x + "_8wekyb3d8bbwe").ToList());
    }
    public void RemoveAppx(string pattern)
    {
        Packages.RemoveWhere(p => Wildcard.IsMatch(p, pattern));
        Provisioned.RemoveWhere(p => Wildcard.IsMatch(p, pattern));
    }

    public bool IsOneDriveInstalled() => OneDrive;
    public void UninstallOneDrive() => OneDrive = false;
    public bool ReinstallOneDrive() { OneDrive = true; return true; }

    public NvSnapshot GetNvidiaSetting(uint settingId) =>
        !Nvidia ? new NvSnapshot(false, false, null) : Nv.TryGetValue(settingId, out var v) ? new NvSnapshot(true, true, v) : new NvSnapshot(true, false, null);
    public void SetNvidiaSetting(uint settingId, uint value) => Nv[settingId] = value;
    public void ResetNvidiaSetting(uint settingId) => Nv.Remove(settingId);
}
