namespace WinBloatFree.Core;

/// <summary>
/// Builds Microsoft Store product-page URIs for a removed app.
/// Uses the ms-windows-store protocol so Windows can open the Store app when the user clicks.
/// The URI is not fetched by this program.
/// </summary>
public sealed record StoreLink(string Name, string Uri);

public static class StoreLinks
{
    public const string Scheme = "ms-windows-store://";

    /// <summary>Product detail page by package family name, as documented by Microsoft.</summary>
    public static string PdpByPfn(string packageFamilyName) =>
        Scheme + "pdp/?PFN=" + Uri.EscapeDataString(packageFamilyName);

    /// <summary>Product detail page by Store product ID, as documented by Microsoft.</summary>
    public static string PdpByProductId(string productId) =>
        Scheme + "pdp/?ProductId=" + Uri.EscapeDataString(productId);

    public static bool IsStoreUri(string? uri) =>
        !string.IsNullOrEmpty(uri) && uri.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Links for a history entry that Undo cannot put back. Prefers PFN from the saved package family names.
    /// </summary>
    public static IReadOnlyList<StoreLink> ForEntry(BackupEntry e)
    {
        if (e.Type != ActionType.Appx || e.Result != EntryResult.Changed) return Array.Empty<StoreLink>();
        var families = e.PreviousAppx?.FamilyNames;
        if (families is null || families.Count == 0) return Array.Empty<StoreLink>();

        var links = new List<StoreLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < families.Count; i++)
        {
            var pfn = families[i];
            if (string.IsNullOrWhiteSpace(pfn) || !seen.Add(pfn)) continue;
            links.Add(new StoreLink(DisplayName(e, pfn, i, families.Count), PdpByPfn(pfn)));
        }
        return links;
    }

    public static IReadOnlyList<StoreLink> ForEntries(IEnumerable<BackupEntry> entries) =>
        entries.SelectMany(ForEntry).ToList();

    static string DisplayName(BackupEntry e, string pfn, int index, int total)
    {
        if (total == 1)
        {
            var title = e.TweakName.StartsWith("Remove ", StringComparison.Ordinal) ? e.TweakName[7..] : e.TweakName;
            if (!string.IsNullOrWhiteSpace(title)) return title;
        }
        var names = e.PreviousAppx?.Names;
        if (names is not null && index < names.Count && !string.IsNullOrWhiteSpace(names[index]))
            return names[index]!;
        int cut = pfn.LastIndexOf('_');
        return cut > 0 ? pfn[..cut] : pfn;
    }
}
