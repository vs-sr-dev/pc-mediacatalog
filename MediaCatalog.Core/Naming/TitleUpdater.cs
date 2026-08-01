using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Naming;

/// <summary>
/// Applies a confirmed title — typed by the user or returned by TMDb — to the files it
/// belongs to.
///
/// A correction is written to exactly the files chosen, plus whatever the caller has
/// worked out is the *same content* (byte-identical copies). It is deliberately **not**
/// spread to everything that happened to carry the same title: two files can both be
/// called "xyz" and still be two different things, and one of them may already be right.
/// Sharing a name is not evidence of sharing an identity; sharing a hash is.
/// </summary>
public static class TitleUpdater
{
    /// <summary>
    /// Set <paramref name="newTitle"/> on exactly these files and nothing else. Returns
    /// how many entries actually changed.
    /// </summary>
    /// <param name="manual">True when the user typed the title rather than a source supplying it.</param>
    public static int Set(IEnumerable<MediaFile> files, string newTitle, bool manual)
    {
        var title = (newTitle ?? string.Empty).Trim();
        if (title.Length == 0) return 0;

        var changed = 0;
        foreach (var file in files)
            if (SetOne(file, title, manual)) changed++;
        return changed;
    }

    /// <summary>
    /// Spread a title already set on <paramref name="source"/> to every other file that
    /// still carries <paramref name="previousTitle"/>. Used after a TMDb lookup, where the
    /// source has been updated before we get a chance to compare.
    ///
    /// This one *is* title-based, and deliberately so: it replaces a guessed spelling with
    /// the canonical spelling of the same name — "king of the hill" becoming *King of the
    /// Hill* — which is a correction every file carrying that guess wants. It is not a way
    /// to give a file a different title, which is why editing a title by hand does not use it.
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
            if (SetOne(file, title, manual)) changed++;
        }
        return changed;
    }

    private static bool SetOne(MediaFile file, string title, bool manual)
    {
        var changed = !string.Equals(file.TmdbName, title, StringComparison.Ordinal) ||
                      !file.TitleVerified ||
                      file.TitleManuallySet != manual;
        file.TmdbName = title;
        file.TitleManuallySet = manual;

        // A hand-typed title replaces whatever a lookup had decided, and is recorded as
        // the user's own rather than borrowing the credit of a source that never saw it.
        if (manual) { file.TmdbVerified = false; file.ImdbVerified = false; }
        else file.TmdbVerified = true;

        return changed;
    }
}
