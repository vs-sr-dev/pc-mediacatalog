using System.Xml;
using System.Xml.Serialization;
using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Persistence;

/// <summary>Loads and saves the whole <see cref="Catalog"/> as XML, per the brief.</summary>
public static class CatalogStore
{
    private static readonly XmlSerializer Serializer = new(typeof(Catalog));

    /// <summary>
    /// Default catalogue location under the user's local app data, e.g.
    /// %LOCALAPPDATA%\MediaCatalog\catalog.xml
    /// </summary>
    public static string DefaultPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MediaCatalog");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "catalog.xml");
        }
    }

    public static Catalog Load(string path)
    {
        if (!File.Exists(path))
            return new Catalog();

        try
        {
            using var reader = XmlReader.Create(path);
            var catalog = (Catalog?)Serializer.Deserialize(reader) ?? new Catalog();
            catalog.RebuildIndex();
            return catalog;
        }
        catch (Exception ex) when (ex is InvalidOperationException or XmlException or IOException)
        {
            // Corrupt/old catalogue: start fresh rather than crash the app.
            return new Catalog();
        }
    }

    /// <summary>
    /// Saves atomically: write to a temp file then replace, so a crash mid-write
    /// can never leave a truncated catalogue.
    /// </summary>
    public static void Save(Catalog catalog, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        var settings = new XmlWriterSettings { Indent = true };
        using (var writer = XmlWriter.Create(tmp, settings))
        {
            Serializer.Serialize(writer, catalog);
        }

        if (File.Exists(path))
            File.Replace(tmp, path, null);
        else
            File.Move(tmp, path);
    }
}
