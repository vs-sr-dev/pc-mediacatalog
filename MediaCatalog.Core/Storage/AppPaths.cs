namespace MediaCatalog.Core.Storage;

/// <summary>
/// Central definition of where the app stores its data. Everything lives in the
/// folder the application runs from (portable-app style), NOT under %LOCALAPPDATA%.
/// </summary>
public static class AppPaths
{
    /// <summary>The directory the executable is running from.</summary>
    public static string DataDirectory => AppContext.BaseDirectory;

    public static string CatalogPath => Path.Combine(DataDirectory, "catalog.xml");
    public static string ToolSettingsPath => Path.Combine(DataDirectory, "tools.xml");
    public static string ScanSessionPath => Path.Combine(DataDirectory, "scan-session.xml");

    /// <summary>Cached file-enumeration snapshot, so a resume needn't re-walk the drives.</summary>
    public static string EnumerationPath => Path.Combine(DataDirectory, "enumeration.xml");
}
