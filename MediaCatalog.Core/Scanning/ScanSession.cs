using System.Xml.Serialization;

namespace MediaCatalog.Core.Scanning;

public enum ScanSessionStatus { None = 0, Running, Paused, Completed }

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

    /// <summary>Index into the cached enumeration where the next pass should resume.</summary>
    public int NextIndex { get; set; }

    public int LastDone { get; set; }
    public int LastTotal { get; set; }
    public DateTime UpdatedUtc { get; set; }

    /// <summary>
    /// Resumable when interrupted with roots recorded. A leftover <c>Running</c> session
    /// means the app was closed/crashed mid-scan (a clean finish or cancel clears it),
    /// so that is offered for resume too — not just an explicit pause.
    /// </summary>
    [XmlIgnore]
    public bool IsResumable =>
        (Status is ScanSessionStatus.Paused or ScanSessionStatus.Running) && Roots.Count > 0;

    public static string DefaultPath => Storage.AppPaths.ScanSessionPath;

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
