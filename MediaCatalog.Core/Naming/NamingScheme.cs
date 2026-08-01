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

    /// <summary>The name for a file using its automatically detected category.</summary>
    public static string GenerateFileName(MediaFile file) =>
        GenerateFileName(file, Classification.CategoryResolver.Auto(file));

    /// <summary>
    /// The name for a file filed under <paramref name="category"/> — the effective one,
    /// so a category the user set by hand decides the shape of the name.
    ///
    /// Built from the title the file actually goes by: a confirmed or hand-typed title
    /// when there is one, the parsed guess otherwise. Correcting a title therefore
    /// changes the name it implies, which is what drives the rename that follows.
    /// </summary>
    public static string GenerateFileName(MediaFile file, string category)
    {
        var title = Sanitize(file.EffectiveTitle);
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty; // nothing reliable to build a name from

        var ext = file.Extension.ToLowerInvariant();

        var stem = category switch
        {
            Classification.CategoryResolver.TvShow when file is { Season: { } s, Episode: { } e }
                => $"{title} - S{s:00}E{e:00}",

            Classification.CategoryResolver.Movie when file.Year is { } y
                => $"{title} ({y})",

            Classification.CategoryResolver.Movie => title,

            Classification.CategoryResolver.Audio => title,

            // Specials and featurettes keep the names they were given: the naming scheme
            // has nothing better to say about "Behind the scenes" than its own name does.
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
