using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Scanning;

/// <summary>Central registry of which file extensions we treat as media.</summary>
public static class MediaExtensions
{
    public static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".m4v", ".mpg", ".mpeg",
        ".webm", ".ts", ".m2ts", ".mts", ".vob", ".divx", ".3gp", ".ogv", ".rm", ".rmvb"
    };

    public static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".aac", ".m4a", ".wav", ".ogg", ".opus", ".wma",
        ".aiff", ".aif", ".ape", ".alac", ".m4b", ".wv", ".mka"
    };

    /// <summary>Markers left by browsers/torrent clients for in-progress downloads.</summary>
    public static readonly HashSet<string> IncompleteMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        ".part", ".crdownload", ".partial", ".!ut", ".bc!", ".download", ".tmp"
    };

    public static MediaKind Classify(string extension)
    {
        if (Video.Contains(extension)) return MediaKind.Video;
        if (Audio.Contains(extension)) return MediaKind.Audio;
        // Anything a plugin has claimed. Asked last, so a plugin cannot take .mp4 away from
        // the program that already knows what an .mp4 is.
        if (Plugins.MediaPlugins.Handles(extension)) return MediaKind.Other;
        return MediaKind.Unknown;
    }

    /// <summary>
    /// True when a scan should pick this file up: audio, video, or a file type some plugin
    /// has taught the program about.
    /// </summary>
    public static bool IsMedia(string extension) =>
        Video.Contains(extension) || Audio.Contains(extension) ||
        Plugins.MediaPlugins.Handles(extension);

    /// <summary>True when it is a plugin, rather than this program, that knows what this is.</summary>
    public static bool IsPluginMedia(string extension) =>
        !Video.Contains(extension) && !Audio.Contains(extension) &&
        Plugins.MediaPlugins.Handles(extension);

    public static bool IsIncompleteMarker(string extension) =>
        IncompleteMarkers.Contains(extension);
}
