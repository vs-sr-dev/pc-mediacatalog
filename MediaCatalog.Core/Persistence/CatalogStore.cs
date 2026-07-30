using System.Xml;
using System.Xml.Serialization;
using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Persistence;

/// <summary>Loads and saves the whole <see cref="Catalog"/> as XML, per the brief.</summary>
public static class CatalogStore
{
    private static readonly XmlSerializer Serializer = new(typeof(Catalog));

    /// <summary>Default catalogue location: catalog.xml in the app's own folder.</summary>
    public static string DefaultPath => Storage.AppPaths.CatalogPath;

    public static Catalog Load(string path)
    {
        if (!File.Exists(path))
            return new Catalog();

        try
        {
            Catalog catalog;
            using (var reader = XmlReader.Create(path))
                catalog = (Catalog?)Serializer.Deserialize(reader) ?? new Catalog();
            catalog.RebuildIndex();

            // Bring an older file up to the current schema, and persist the upgrade so it
            // is only migrated once.
            if (CatalogMigrator.Migrate(catalog))
            {
                try { Save(catalog, path); } catch { /* migration re-save is best-effort */ }
            }
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
