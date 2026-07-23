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
}
