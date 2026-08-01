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
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.xml");
    public static string ScanSessionPath => Path.Combine(DataDirectory, "scan-session.xml");
    public static string TmdbCachePath => Path.Combine(DataDirectory, "tmdb-cache.xml");

    /// <summary>Cached file-enumeration snapshot, so a resume needn't re-walk the drives.</summary>
    public static string EnumerationPath => Path.Combine(DataDirectory, "enumeration.xml");

    /// <summary>
    /// IMDb's raw <c>title.basics.tsv</c> as downloaded (or still gzipped). Over a
    /// gigabyte, and only ever read a line at a time to boil it down to
    /// <see cref="ImdbDataPath"/>.
    /// </summary>
    public static string ImdbSourcePath => Path.Combine(DataDirectory, "title.basics.tsv");

    /// <summary>The gzipped form, accepted as-is so the download needn't be unpacked first.</summary>
    public static string ImdbSourceGzPath => Path.Combine(DataDirectory, "title.basics.tsv.gz");

    /// <summary>Our own two-column extract: primary title and year, tab separated.</summary>
    public static string ImdbDataPath => Path.Combine(DataDirectory, "IMDBData.tsv");
}
