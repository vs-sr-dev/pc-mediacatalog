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
    public const string TvExtra = "TvExtra";
    public const string MovieExtra = "MovieExtra";
    public const string Other = "Other";
    public const string Unknown = "Unknown";
    public const string Audio = "Audio";

    public static readonly string[] BuiltIn =
        { Movie, TvShow, TvExtra, MovieExtra, Other, Unknown, Audio };

    /// <summary>The extras category belonging to a main category, or null.</summary>
    public static string? ExtraOf(string category) => category switch
    {
        TvShow => TvExtra,
        Movie => MovieExtra,
        _ => null
    };

    /// <summary>The main category an extras category belongs to, or null.</summary>
    public static string? MainOf(string category) => category switch
    {
        TvExtra => TvShow,
        MovieExtra => Movie,
        _ => null
    };

    /// <summary>True for the specials/featurettes categories.</summary>
    public static bool IsExtra(string category) =>
        category is TvExtra or MovieExtra;

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
    public static string Auto(MediaFile file)
    {
        // A file a plugin handles is whatever that plugin says it is, and nothing else gets
        // a say. An e-book called "Discworld S01E02" is not an episode of anything: the
        // extension is the plugin's, so the category is the plugin's too.
        if (file.Kind == MediaKind.Other &&
            Plugins.MediaPlugins.CategoryOf(file) is { Length: > 0 } fromPlugin)
            return fromPlugin;

        // A season/episode code is the strongest signal there is: whatever the extension
        // suggests, a file that says S02E05 is an episode of something.
        if (file is { Season: not null, Episode: not null } && !file.IsExtra)
            return TvShow;

        return file.Kind switch
        {
            MediaKind.Video => file.VideoCategory.ToString(),
            MediaKind.Audio => Audio,
            _ => Unknown
        };
    }

    /// <summary>
    /// Built-in categories plus the user's custom ones, de-duplicated and in the order the
    /// user has put them in.
    ///
    /// The order is the user's because the menu is theirs: somebody whose library is nine
    /// tenths television should not have to walk past Movie every time. Anything not
    /// mentioned in the ordering follows in its built-in position, so a category added later
    /// turns up at the bottom rather than vanishing.
    /// </summary>
    public static IReadOnlyList<string> All(AppSettings settings)
    {
        var list = new List<string>(BuiltIn);

        // Whatever the plugins brought. They come before the user's own so that a category a
        // plugin owns is never mistaken for one somebody typed and can be removed.
        foreach (var c in Plugins.MediaPlugins.Categories)
            if (!list.Contains(c, StringComparer.OrdinalIgnoreCase))
                list.Add(c);

        foreach (var c in settings.CustomCategories)
            if (!string.IsNullOrWhiteSpace(c) &&
                !list.Contains(c, StringComparer.OrdinalIgnoreCase))
                list.Add(c);

        return Ordered(list, settings.CategoryOrder);
    }

    /// <summary>
    /// <paramref name="categories"/> rearranged to follow <paramref name="order"/>. Names in
    /// the order that no longer exist are ignored; names missing from it keep their relative
    /// positions at the end.
    /// </summary>
    public static IReadOnlyList<string> Ordered(
        IReadOnlyList<string> categories, IReadOnlyList<string> order)
    {
        if (order.Count == 0) return categories;

        var remaining = new List<string>(categories);
        var result = new List<string>(categories.Count);

        foreach (var wanted in order)
        {
            var index = remaining.FindIndex(c =>
                string.Equals(c, wanted, StringComparison.OrdinalIgnoreCase));
            if (index < 0) continue;
            result.Add(remaining[index]);
            remaining.RemoveAt(index);
        }

        result.AddRange(remaining);
        return result;
    }

    /// <summary>
    /// The categories worth giving a consolidation folder of their own.
    ///
    /// Extras are not among them, and never were: a special belongs beside the film or the
    /// episode it is a special of, in an Extras subfolder of that, so a separate destination
    /// for them would be a setting that could only ever be ignored. Unknown and Other have
    /// nothing in common with each other and are left to the user to configure or not.
    /// </summary>
    public static IReadOnlyList<string> Consolidatable(AppSettings settings) =>
        All(settings).Where(c => !IsExtra(c)).ToList();
}
