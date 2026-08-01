using System.Xml.Serialization;

namespace MediaCatalog.Core.Tmdb;

public record TmdbResult(bool Found, string CanonicalName);

public class TmdbCacheEntry
{
    public string Query { get; set; } = string.Empty;
    public bool Found { get; set; }
    public string CanonicalName { get; set; } = string.Empty;

    /// <summary>
    /// Which search answered this: "tv" or "movie". Blank in caches written before films
    /// were searched for at all, where every entry was a TV lookup.
    /// </summary>
    public string Kind { get; set; } = string.Empty;
}

/// <summary>
/// Persistent cache of TMDb lookups (both hits and misses) so a validated — or
/// known-invalid — name is never queried twice. Backed by XML in the app folder.
///
/// Entries are keyed by the search as well as the name: *Fargo* is both a film and a
/// programme, and one answer must not be handed back in place of the other.
/// </summary>
[XmlRoot("TmdbCache")]
public class TmdbCache
{
    public const string Tv = "tv";
    public const string Movie = "movie";

    [XmlArray("Entries"), XmlArrayItem("Entry")]
    public List<TmdbCacheEntry> Entries { get; set; } = new();

    private readonly Dictionary<string, TmdbResult> _index =
        new(StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string query) =>
        (query ?? string.Empty).Trim();

    private static string Key(string query, string kind) => kind + "|" + Normalize(query);

    public bool TryGet(string query, string kind, out TmdbResult result)
    {
        if (_index.TryGetValue(Key(query, kind), out var r))
        {
            result = r;
            return true;
        }

        // Caches written before films were searched for hold bare keys, and every one of
        // them was a TV lookup — so they still answer TV questions and nothing else.
        if (kind == Tv && _index.TryGetValue(Key(query, string.Empty), out var legacy))
        {
            result = legacy;
            return true;
        }

        result = new TmdbResult(false, string.Empty);
        return false;
    }

    public void Put(string query, string kind, TmdbResult result)
    {
        var key = Key(query, kind);
        if (_index.ContainsKey(key)) return;
        _index[key] = result;
        Entries.Add(new TmdbCacheEntry
        {
            Query = Normalize(query),
            Kind = kind,
            Found = result.Found,
            CanonicalName = result.CanonicalName
        });
    }

    private void RebuildIndex()
    {
        _index.Clear();
        foreach (var e in Entries)
            _index[Key(e.Query, e.Kind ?? string.Empty)] = new TmdbResult(e.Found, e.CanonicalName);
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
