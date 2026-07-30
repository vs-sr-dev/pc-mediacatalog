using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Naming;

/// <summary>
/// Applies a confirmed title — typed by the user or returned by TMDb — to a file and to
/// every other catalogue entry that shared its previous title, so a single correction
/// fixes a whole show or film in one go.
/// </summary>
public static class TitleUpdater
{
    /// <summary>
    /// Set <paramref name="newTitle"/> on <paramref name="targets"/> and on every file in
    /// <paramref name="catalogue"/> whose current title matches a target's previous one.
    /// Returns the number of entries changed.
    /// </summary>
    /// <param name="manual">True when the user typed the title rather than TMDb supplying it.</param>
    /// <param name="scope">Optional restriction on which other files may be updated.</param>
    public static int Apply(
        IEnumerable<MediaFile> catalogue,
        IReadOnlyList<MediaFile> targets,
        string newTitle,
        bool manual,
        Func<MediaFile, bool>? scope = null)
    {
        var title = (newTitle ?? string.Empty).Trim();
        if (title.Length == 0 || targets.Count == 0) return 0;

        // Snapshot the titles being replaced *before* touching anything, otherwise the
        // first update would change what the rest are compared against.
        var previous = new HashSet<string>(
            targets.Select(t => t.EffectiveTitle.Trim()).Where(t => t.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        var chosen = new HashSet<MediaFile>(targets);

        var changed = 0;
        foreach (var file in catalogue)
        {
            var isTarget = chosen.Contains(file);
            if (!isTarget)
            {
                if (!previous.Contains(file.EffectiveTitle.Trim())) continue;
                if (scope != null && !scope(file)) continue;
            }
            if (Set(file, title, manual)) changed++;
        }
        return changed;
    }

    /// <summary>
    /// Spread a title already set on <paramref name="source"/> to every other file that
    /// still carries <paramref name="previousTitle"/>. Used after a TMDb lookup, where the
    /// source has been updated before we get a chance to compare.
    /// </summary>
    public static int Propagate(
        IEnumerable<MediaFile> catalogue,
        MediaFile source,
        string previousTitle,
        bool manual,
        Func<MediaFile, bool>? scope = null)
    {
        var title = source.EffectiveTitle.Trim();
        var previous = (previousTitle ?? string.Empty).Trim();
        if (title.Length == 0 || previous.Length == 0) return 0;
        if (string.Equals(title, previous, StringComparison.OrdinalIgnoreCase)) return 0;

        var changed = 0;
        foreach (var file in catalogue)
        {
            if (ReferenceEquals(file, source)) continue;
            if (!string.Equals(file.EffectiveTitle.Trim(), previous, StringComparison.OrdinalIgnoreCase))
                continue;
            if (scope != null && !scope(file)) continue;
            if (Set(file, title, manual)) changed++;
        }
        return changed;
    }

    /// <summary>Files that would be swept up by editing this file's title.</summary>
    public static List<MediaFile> SameTitleAs(
        IEnumerable<MediaFile> catalogue, MediaFile file)
    {
        var title = file.EffectiveTitle.Trim();
        if (title.Length == 0) return new List<MediaFile>();
        return catalogue
            .Where(f => !ReferenceEquals(f, file) &&
                        string.Equals(f.EffectiveTitle.Trim(), title, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static bool Set(MediaFile file, string title, bool manual)
    {
        var changed = !string.Equals(file.TmdbName, title, StringComparison.Ordinal) ||
                      !file.TmdbVerified ||
                      file.TitleManuallySet != manual;
        file.TmdbName = title;
        file.TmdbVerified = true;
        file.TitleManuallySet = manual;
        return changed;
    }
}
