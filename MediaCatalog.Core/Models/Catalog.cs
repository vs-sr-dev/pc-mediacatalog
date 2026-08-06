using System.Xml.Serialization;

namespace MediaCatalog.Core.Models;

/// <summary>Root object persisted to XML. Holds every catalogued file.</summary>
[XmlRoot("MediaCatalog")]
public class Catalog
{
    /// <summary>Schema version so future changes can migrate old files.</summary>
    public int Version { get; set; } = 1;

    public DateTime LastScanUtc { get; set; }

    [XmlArray("Files")]
    [XmlArrayItem("File")]
    public List<MediaFile> Files { get; set; } = new();

    /// <summary>Fast lookup by full path; rebuilt on load, not serialised.</summary>
    [XmlIgnore]
    public Dictionary<string, MediaFile> ByPath { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void RebuildIndex()
    {
        ByPath.Clear();
        foreach (var f in Files)
            ByPath[f.FullPath] = f;
    }

    /// <summary>
    /// Put one entry's new path into the index, taking its old one out.
    ///
    /// A batch that moves or renames a thousand files cannot rebuild the whole index a
    /// thousand times, and must not wait until the end either: the files still to come are
    /// looked up by path as they go — for a collision, for a folder that is finished with —
    /// and an index that still holds the name a file had ten seconds ago answers those
    /// questions about a file that is no longer there.
    /// </summary>
    public void Note(MediaFile file, string? previousPath = null)
    {
        if (!string.IsNullOrEmpty(previousPath) &&
            !string.Equals(previousPath, file.FullPath, StringComparison.OrdinalIgnoreCase) &&
            ByPath.TryGetValue(previousPath, out var held) && ReferenceEquals(held, file))
            ByPath.Remove(previousPath);

        if (!string.IsNullOrEmpty(file.FullPath)) ByPath[file.FullPath] = file;
    }
}
