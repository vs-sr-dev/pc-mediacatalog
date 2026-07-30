using MediaCatalog.Core.Models;
using MediaCatalog.Core.Storage;

namespace MediaCatalog.Core.Classification;

/// <summary>
/// Resolves the effective category of a file, combining the automatic classification
/// with user overrides (per-file, then folder rules). Also supplies the category list
/// shown in the UI (the built-ins plus any custom categories the user has added).
/// </summary>
public static class CategoryResolver
{
    public const string Movie = "Movie";
    public const string TvShow = "TvShow";
    public const string Other = "Other";
    public const string Unknown = "Unknown";
    public const string Audio = "Audio";

    public static readonly string[] BuiltIn = { Movie, TvShow, Other, Unknown, Audio };

    /// <summary>The effective category string: per-file override wins, then a folder rule,
    /// otherwise the auto-detected value.</summary>
    public static string Effective(MediaFile file, AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(file.CategoryOverride))
            return file.CategoryOverride;

        var byFolder = settings.CategoryForPath(file.FullPath);
        if (!string.IsNullOrWhiteSpace(byFolder))
            return byFolder;

        return Auto(file);
    }

    /// <summary>The auto-detected category, ignoring any user override.</summary>
    public static string Auto(MediaFile file) => file.Kind switch
    {
        MediaKind.Video => file.VideoCategory.ToString(),
        MediaKind.Audio => Audio,
        _ => Unknown
    };

    /// <summary>Built-in categories plus the user's custom ones, de-duplicated.</summary>
    public static IReadOnlyList<string> All(AppSettings settings)
    {
        var list = new List<string>(BuiltIn);
        foreach (var c in settings.CustomCategories)
            if (!string.IsNullOrWhiteSpace(c) &&
                !list.Contains(c, StringComparer.OrdinalIgnoreCase))
                list.Add(c);
        return list;
    }
}
