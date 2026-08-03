using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Classification;

/// <summary>
/// Keeps a catalogue entry internally consistent — the corrections that follow from what
/// the entry now says about itself, rather than from anything on disk.
/// </summary>
public static class MetadataNormaliser
{
    /// <summary>
    /// Season and episode numbers only mean anything for television. A film, an album
    /// track or a featurette that carries them picked them up from a number in its name
    /// that happened to look like an episode code — "Ocean's 11", "Apollo 13", a track
    /// numbered 104 — and the number is simply wrong. So the moment a file is categorised
    /// as anything but a programme or a programme's extra, the numbering goes.
    ///
    /// Numbering the user typed in by hand is the one exception, and is left exactly as it
    /// was entered. A film has no season and no episode — so somebody typing one in is
    /// telling us the file was filed as a film wrongly, which is a correction to act on
    /// rather than one to throw away. The category is what wants putting right there, and
    /// the editor offers to do exactly that.
    ///
    /// Returns true when something was cleared, so the caller can persist and say so.
    /// </summary>
    public static bool StripNonTvNumbering(MediaFile file, string category)
    {
        if (KeepsNumbering(category)) return false;
        if (file.NumberingManuallySet) return false;
        if (file.Season is null && file.Episode is null && file.EpisodeEnd is null) return false;

        file.Season = null;
        file.Episode = null;
        file.EpisodeEnd = null;
        return true;
    }

    /// <summary>The same over a whole catalogue; returns how many entries were corrected.</summary>
    public static int StripNonTvNumbering(
        IEnumerable<MediaFile> files, Func<MediaFile, string> categoryOf) =>
        files.Count(f => StripNonTvNumbering(f, categoryOf(f)));

    /// <summary>True for the categories a season/episode number belongs to.</summary>
    public static bool KeepsNumbering(string category) =>
        category is CategoryResolver.TvShow or CategoryResolver.TvExtra;
}
