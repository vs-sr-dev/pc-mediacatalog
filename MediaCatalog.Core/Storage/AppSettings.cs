using System.Xml.Serialization;

namespace MediaCatalog.Core.Storage;

/// <summary>A folder the user has excluded from results/scans.</summary>
public class ExcludedFolder
{
    public string Path { get; set; } = string.Empty;
    /// <summary>When true, everything beneath the folder is excluded too.</summary>
    public bool IncludeSubdirectories { get; set; } = true;
}

/// <summary>A rule assigning a category to everything under a folder.</summary>
public class FolderCategoryRule
{
    public string Path { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool IncludeSubdirectories { get; set; } = true;
}

/// <summary>
/// User preferences persisted to <c>settings.xml</c> in the app folder. Separate from
/// <see cref="Tools.ToolSettings"/> (tools.xml) so existing installs keep working; this
/// file is simply created on first use.
/// </summary>
[XmlRoot("AppSettings")]
public class AppSettings
{
    // --- TMDb ---
    public string TmdbApiKey { get; set; } = string.Empty;

    // --- Consolidation targets ---
    public string TvConsolidationDir { get; set; } = string.Empty;
    public string FilmConsolidationDir { get; set; } = string.Empty;

    // --- Exclusions ---
    [XmlArray("IgnoredExtensions"), XmlArrayItem("Extension")]
    public List<string> IgnoredExtensions { get; set; } = new();

    [XmlArray("ExcludedFolders"), XmlArrayItem("Folder")]
    public List<ExcludedFolder> ExcludedFolders { get; set; } = new();

    // --- Categories ---
    [XmlArray("CustomCategories"), XmlArrayItem("Category")]
    public List<string> CustomCategories { get; set; } = new();

    [XmlArray("FolderCategoryRules"), XmlArrayItem("Rule")]
    public List<FolderCategoryRule> FolderCategoryRules { get; set; } = new();

    // --- Helpers -----------------------------------------------------------

    public bool IsExtensionIgnored(string extension) =>
        IgnoredExtensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));

    public void IgnoreExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return;
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        if (!IsExtensionIgnored(ext)) IgnoredExtensions.Add(ext.ToLowerInvariant());
    }

    /// <summary>True if <paramref name="fullPath"/> falls under an excluded folder.</summary>
    public bool IsPathExcluded(string fullPath)
    {
        foreach (var ex in ExcludedFolders)
        {
            if (string.IsNullOrEmpty(ex.Path)) continue;
            if (ex.IncludeSubdirectories)
            {
                if (IsUnder(fullPath, ex.Path)) return true;
            }
            else
            {
                var dir = System.IO.Path.GetDirectoryName(fullPath);
                if (string.Equals(dir, ex.Path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>True if descent into <paramref name="dir"/> should be pruned (subtree excluded).</summary>
    public bool IsDescentBlocked(string dir) =>
        ExcludedFolders.Any(e => e.IncludeSubdirectories &&
            !string.IsNullOrEmpty(e.Path) && IsUnder(dir, e.Path));

    /// <summary>Category rule matching a path, preferring the deepest (most specific) folder.</summary>
    public string? CategoryForPath(string fullPath)
    {
        string? best = null;
        var bestLen = -1;
        foreach (var rule in FolderCategoryRules)
        {
            if (string.IsNullOrEmpty(rule.Path)) continue;
            var matches = rule.IncludeSubdirectories
                ? IsUnder(fullPath, rule.Path)
                : string.Equals(System.IO.Path.GetDirectoryName(fullPath),
                    rule.Path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            if (matches && rule.Path.Length > bestLen)
            {
                best = rule.Category;
                bestLen = rule.Path.Length;
            }
        }
        return best;
    }

    private static bool IsUnder(string path, string folder)
    {
        var root = folder.TrimEnd('\\', '/');
        return path.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + System.IO.Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, root, StringComparison.OrdinalIgnoreCase);
    }

    // --- Persistence -------------------------------------------------------

    public static string DefaultPath => AppPaths.SettingsPath;

    private static readonly XmlSerializer Serializer = new(typeof(AppSettings));

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            using var reader = new StreamReader(path);
            return (AppSettings?)Serializer.Deserialize(reader) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public void Save(string path)
    {
        try
        {
            var tmp = path + ".tmp";
            using (var writer = new StreamWriter(tmp))
                Serializer.Serialize(writer, this);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch { /* settings are best-effort */ }
    }
}
