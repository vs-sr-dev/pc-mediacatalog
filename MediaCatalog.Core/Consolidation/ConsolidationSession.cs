using System.Xml.Serialization;

namespace MediaCatalog.Core.Consolidation;

public enum ConsolidationSessionStatus { None = 0, Running, Paused, Completed }

/// <summary>
/// A consolidation run that has not finished, written down so it can be picked up again —
/// after a pause, or after the application has been closed and opened.
///
/// Filing a library of thousands is an hours-long job that moves real bytes, and until now
/// the only way to stop one was to cancel it and start again from the top. What is recorded
/// is the *work left*, by catalogue id: the files already filed are filed, and a resume that
/// re-examined them would only ask the same questions again. Ids rather than paths, because
/// the path of a file this job has moved is precisely the thing that has changed.
/// </summary>
[XmlRoot("ConsolidationSession")]
public class ConsolidationSession
{
    public ConsolidationSessionStatus Status { get; set; } = ConsolidationSessionStatus.None;

    /// <summary>Catalogue ids still to be filed, in the order the run would take them.</summary>
    [XmlArray("Pending"), XmlArrayItem("Id")]
    public List<string> PendingIds { get; set; } = new();

    /// <summary>
    /// True when the run is a move — the original goes once the copy is verified — which is
    /// what consolidating means and what a resume has to go on doing.
    /// </summary>
    public bool DeleteOriginal { get; set; } = true;

    /// <summary>How many files the job started with, so a resume can say where it is.</summary>
    public int Total { get; set; }

    public DateTime UpdatedUtc { get; set; }

    /// <summary>How many have been dealt with: everything the job began with, less what is left.</summary>
    [XmlIgnore]
    public int Done => Math.Max(0, Total - PendingIds.Count);

    /// <summary>
    /// Resumable when it stopped with work outstanding. A <c>Running</c> session left on
    /// disk means the application went away mid-job — a crash, a power cut, somebody closing
    /// the window — which is exactly the case this exists for, so it is offered too.
    /// </summary>
    [XmlIgnore]
    public bool IsResumable =>
        Status is ConsolidationSessionStatus.Paused or ConsolidationSessionStatus.Running &&
        PendingIds.Count > 0;

    /// <summary>How far the job got, for the status bar and the resume prompt.</summary>
    public string Describe() =>
        $"{Done} of {Total} filed, {PendingIds.Count} still to go";

    public static string DefaultPath => Storage.AppPaths.ConsolidationSessionPath;

    private static readonly XmlSerializer Serializer = new(typeof(ConsolidationSession));

    public static ConsolidationSession Load(string path)
    {
        if (!File.Exists(path)) return new ConsolidationSession();
        try
        {
            using var reader = new StreamReader(path);
            return (ConsolidationSession?)Serializer.Deserialize(reader) ?? new ConsolidationSession();
        }
        catch { return new ConsolidationSession(); }
    }

    public void Save(string path)
    {
        try
        {
            UpdatedUtc = DateTime.UtcNow;
            using var writer = new StreamWriter(path);
            Serializer.Serialize(writer, this);
        }
        catch { /* session state is best-effort */ }
    }

    /// <summary>Remove the session file — the job finished, or was given up on.</summary>
    public static void Clear(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
