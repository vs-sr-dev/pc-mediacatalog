using System.Text.RegularExpressions;
using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Naming;

/// <summary>
/// Produces a consistent target file name from a file's parsed metadata:
///   Movie:  "Title (Year).ext"
///   TV:     "Title - S01E02.ext"
///   Audio:  "Title.ext"
/// Returns an empty string when there isn't enough metadata to improve on the
/// current name, so callers can simply skip those files.
/// </summary>
public static class NamingScheme
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    public static string GenerateFileName(MediaFile file)
    {
        var title = Sanitize(file.ParsedTitle);
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty; // nothing reliable to build a name from

        var ext = file.Extension.ToLowerInvariant();

        string stem = file switch
        {
            { Kind: MediaKind.Video, VideoCategory: VideoCategory.TvShow, Season: { } s, Episode: { } e }
                => $"{title} - S{s:00}E{e:00}",

            { Kind: MediaKind.Video, VideoCategory: VideoCategory.Movie, Year: { } y }
                => $"{title} ({y})",

            { Kind: MediaKind.Video, VideoCategory: VideoCategory.Movie }
                => title,

            { Kind: MediaKind.Audio }
                => title,

            _ => string.Empty
        };

        return string.IsNullOrEmpty(stem) ? string.Empty : stem + ext;
    }

    /// <summary>Strip characters Windows won't allow in a file name and tidy spacing.</summary>
    private static string Sanitize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var cleaned = new string(input.Select(c => InvalidChars.Contains(c) ? ' ' : c).ToArray());
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned.TrimEnd('.', ' '); // Windows dislikes trailing dots/spaces
    }
}
