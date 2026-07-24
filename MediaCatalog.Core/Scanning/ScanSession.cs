using System.Xml.Serialization;

namespace MediaCatalog.Core.Scanning;

public enum ScanSessionStatus { None = 0, Paused, Completed }

/// <summary>
/// Records the state of an interrupted scan so it can be resumed in a later run
/// (persisted to XML, like everything else). Resume relies on the catalogue's
/// incremental hashing to skip files already processed — the session just remembers
/// which roots to walk and how far it had got, for the UI.
/// </summary>
[XmlRoot("ScanSession")]
public class ScanSession
{
    public ScanSessionStatus Status { get; set; } = ScanSessionStatus.None;

    [XmlArray("Roots")]
    [XmlArrayItem("Root")]
    public List<string> Roots { get; set; } = new();

    public int LastDone { get; set; }
    public int LastTotal { get; set; }
    public DateTime UpdatedUtc { get; set; }

    [XmlIgnore]
    public bool IsResumable => Status == ScanSessionStatus.Paused && Roots.Count > 0;

    public static string DefaultPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MediaCatalog");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "scan-session.xml");
        }
    }

    private static readonly XmlSerializer Serializer = new(typeof(ScanSession));

    public static ScanSession Load(string path)
    {
        if (!File.Exists(path)) return new ScanSession();
        try
        {
            using var reader = new StreamReader(path);
            return (ScanSession?)Serializer.Deserialize(reader) ?? new ScanSession();
        }
        catch { return new ScanSession(); }
    }

    public void Save(string path)
    {
        try
        {
            using var writer = new StreamWriter(path);
            Serializer.Serialize(writer, this);
        }
        catch { /* session state is best-effort */ }
    }

    /// <summary>Remove the session file (scan completed or cancelled outright).</summary>
    public static void Clear(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
