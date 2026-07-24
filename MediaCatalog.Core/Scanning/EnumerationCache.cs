using System.Xml.Serialization;

namespace MediaCatalog.Core.Scanning;

/// <summary>
/// A persisted snapshot of the file list produced by walking the drives, so a scan
/// interrupted and resumed across an application restart does not have to re-enumerate
/// the whole tree (which on multi-terabyte volumes is itself slow). Written once when
/// enumeration finishes; the resume position is tracked separately in the scan session.
/// </summary>
[XmlRoot("EnumerationCache")]
public class EnumerationCache
{
    [XmlArray("Roots")]
    [XmlArrayItem("Root")]
    public List<string> Roots { get; set; } = new();

    [XmlArray("Paths")]
    [XmlArrayItem("Path")]
    public List<string> Paths { get; set; } = new();

    public DateTime CreatedUtc { get; set; }

    /// <summary>True if this cache was built for the same set of roots being requested.</summary>
    public bool MatchesRoots(IEnumerable<string> roots)
    {
        var a = new HashSet<string>(Roots, StringComparer.OrdinalIgnoreCase);
        var b = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        return a.SetEquals(b);
    }

    private static readonly XmlSerializer Serializer = new(typeof(EnumerationCache));

    public static EnumerationCache? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var reader = new StreamReader(path);
            return (EnumerationCache?)Serializer.Deserialize(reader);
        }
        catch { return null; }
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
        catch { /* cache is an optimisation; failing to persist it is non-fatal */ }
    }

    public static void Clear(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
