using System.Xml.Serialization;

namespace MediaCatalog.Core.Tools;

/// <summary>
/// User overrides for external-tool locations, persisted to XML alongside the
/// catalogue. Empty entries mean "auto-detect".
/// </summary>
[XmlRoot("ToolSettings")]
public class ToolSettings
{
    public string FfmpegPath { get; set; } = string.Empty;
    public string FfprobePath { get; set; } = string.Empty;
    public string FpcalcPath { get; set; } = string.Empty;

    public static string DefaultPath => Storage.AppPaths.ToolSettingsPath;

    private static readonly XmlSerializer Serializer = new(typeof(ToolSettings));

    public static ToolSettings Load(string path)
    {
        if (!File.Exists(path)) return new ToolSettings();
        try
        {
            using var reader = new StreamReader(path);
            return (ToolSettings?)Serializer.Deserialize(reader) ?? new ToolSettings();
        }
        catch { return new ToolSettings(); }
    }

    public void Save(string path)
    {
        using var writer = new StreamWriter(path);
        Serializer.Serialize(writer, this);
    }
}
