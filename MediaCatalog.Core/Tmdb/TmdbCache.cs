using System.Xml.Serialization;

namespace MediaCatalog.Core.Tmdb;

public record TmdbResult(bool Found, string CanonicalName);

public class TmdbCacheEntry
{
    public string Query { get; set; } = string.Empty;
    public bool Found { get; set; }
    public string CanonicalName { get; set; } = string.Empty;
}

/// <summary>
/// Persistent cache of TMDb lookups (both hits and misses) so a validated — or
/// known-invalid — name is never queried twice. Backed by XML in the app folder.
/// </summary>
[XmlRoot("TmdbCache")]
public class TmdbCache
{
    [XmlArray("Entries"), XmlArrayItem("Entry")]
    public List<TmdbCacheEntry> Entries { get; set; } = new();

    private readonly Dictionary<string, TmdbResult> _index =
        new(StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string query) =>
        (query ?? string.Empty).Trim();

    public bool TryGet(string query, out TmdbResult result)
    {
        if (_index.TryGetValue(Normalize(query), out var r))
        {
            result = r;
            return true;
        }
        result = new TmdbResult(false, string.Empty);
        return false;
    }

    public void Put(string query, TmdbResult result)
    {
        var key = Normalize(query);
        if (_index.ContainsKey(key)) return;
        _index[key] = result;
        Entries.Add(new TmdbCacheEntry
        {
            Query = key,
            Found = result.Found,
            CanonicalName = result.CanonicalName
        });
    }

    private void RebuildIndex()
    {
        _index.Clear();
        foreach (var e in Entries)
            _index[Normalize(e.Query)] = new TmdbResult(e.Found, e.CanonicalName);
    }

    private static readonly XmlSerializer Serializer = new(typeof(TmdbCache));

    public static TmdbCache Load(string path)
    {
        if (!File.Exists(path)) return new TmdbCache();
        try
        {
            using var reader = new StreamReader(path);
            var cache = (TmdbCache?)Serializer.Deserialize(reader) ?? new TmdbCache();
            cache.RebuildIndex();
            return cache;
        }
        catch { return new TmdbCache(); }
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
        catch { /* cache is an optimisation */ }
    }
}
