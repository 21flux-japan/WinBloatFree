namespace WinBloatFree.Core;

/// <summary>Registry value snapshot. Data is a decimal string for DWord/QWord.</summary>
public sealed record RegSnapshot(bool Exists, RegKind? Kind, string? Data, bool KeyExisted)
{
    public static RegSnapshot Missing(bool keyExisted) => new(false, null, null, keyExisted);
}

public sealed record ServiceSnapshot(bool Exists, ServiceStart? Start, bool Running);

public sealed record TaskSnapshot(bool Exists, bool Enabled);

public sealed record AppxSnapshot(bool Installed, bool Provisioned, IReadOnlyList<string> Names, IReadOnlyList<string> FamilyNames)
{
    public static readonly AppxSnapshot None = new(false, false, Array.Empty<string>(), Array.Empty<string>());
    public bool Present => Installed || Provisioned;
}

public sealed record NvSnapshot(bool Available, bool IsSet, uint? Value);

public sealed record SystemInfo(int Build, int Ubr, string DisplayVersion, string Edition)
{
    public bool IsWindows11 => Build >= 22000;
    public string ProductVersion => IsWindows11 ? "Windows 11" : "Windows 10";
    public string FriendlyName => $"{ProductVersion} {Edition} {DisplayVersion} (build {Build}.{Ubr})".Replace("  ", " ");
}

/// <summary>
/// Everything that touches the operating system goes through this interface,
/// so the engine itself stays pure and testable on any platform.
/// </summary>
public interface ISystemAccess
{
    SystemInfo Info { get; }

    RegSnapshot ReadRegistry(RegHive hive, string key, string name);
    void WriteRegistry(RegHive hive, string key, string name, RegKind kind, string data);
    void DeleteRegistryValue(RegHive hive, string key, string name);
    /// <summary>Deletes the key only if it has no values and no subkeys.</summary>
    void DeleteKeyIfEmpty(RegHive hive, string key);

    ServiceSnapshot GetService(string name);
    void SetServiceStart(string name, ServiceStart start);
    void StopService(string name);
    void StartService(string name);

    TaskSnapshot GetTask(string path);
    void SetTaskEnabled(string path, bool enabled);

    /// <summary>
    /// Reloads the installed and provisioned app list once so later GetAppx calls in this scan
    /// can reuse it. Call at the start of a status scan.
    /// </summary>
    void RefreshAppxInventory();
    AppxSnapshot GetAppx(string pattern);
    void RemoveAppx(string pattern);

    bool IsOneDriveInstalled();
    void UninstallOneDrive();
    /// <summary>Runs the OneDrive installer that ships with Windows, if present.</summary>
    bool ReinstallOneDrive();

    NvSnapshot GetNvidiaSetting(uint settingId);
    void SetNvidiaSetting(uint settingId, uint value);
    /// <summary>Removes the global override so the driver default applies again.</summary>
    void ResetNvidiaSetting(uint settingId);
}
