using System.Globalization;
using System.Text.RegularExpressions;
using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Consolidation;

/// <summary>
/// Builds the name a consolidated file is filed under from a pattern the user writes, so a
/// library can be laid out the way its owner reads it rather than the way this program
/// happens to write names.
///
/// A pattern is ordinary text with fields in braces — <c>{title} - {numbering}</c> — and
/// each field may carry a .NET number format after a colon: <c>{episode:000}</c>. Fields
/// that have nothing to say come out empty, and the spacing and punctuation left stranded
/// around them is tidied away, so one pattern copes with a film that has no year and an
/// episode that has no second number.
///
/// The extension is never part of a pattern and never changes: nothing here re-encodes a
/// file, so a name that changed its extension would simply be lying about the contents.
/// </summary>
public static class ConsolidationNaming
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    private static readonly Regex Field = new(
        @"\{(?<name>[a-zA-Z]+)(?::(?<format>[^}]*))?\}", RegexOptions.Compiled);

    /// <summary>Every field a pattern may use, with what it stands for — shown in Settings.</summary>
    public static readonly (string Field, string Means)[] Fields =
    {
        ("{title}", "the primary title — the programme's or the film's name"),
        ("{secondarytitle}", "the second name, when there is one: the episode's own title, or a film's tag line"),
        ("{year}", "year of release, blank when unknown"),
        ("{season}", "season number — {season:00} pads it to two digits"),
        ("{episode}", "episode number — {episode:00} pads it to two digits"),
        ("{episodeend}", "last episode of a double episode, blank otherwise"),
        ("{numbering}", "the whole code: S01E02, or S06E11-E12 for a double"),
        ("{quality}", "1080p for video, 320 kbps for audio, blank when unmeasured"),
        ("{name}", "the file's current name, without its extension")
    };

    /// <summary>
    /// The name <paramref name="pattern"/> gives this file, extension included, or an empty
    /// string when the pattern is blank or produces nothing usable — in which case the
    /// caller keeps whatever name it would have used anyway.
    /// </summary>
    public static string Apply(MediaFile file, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return string.Empty;

        var stem = Field.Replace(pattern, match => Value(file,
            match.Groups["name"].Value.ToLowerInvariant(),
            match.Groups["format"].Success ? match.Groups["format"].Value : null));

        stem = Tidy(stem);
        return stem.Length == 0 ? string.Empty : stem + file.Extension.ToLowerInvariant();
    }

    private static string Value(MediaFile file, string name, string? format) => name switch
    {
        "title" => file.EffectiveTitle.Trim(),
        "secondarytitle" => file.SecondaryTitle.Trim(),
        "year" => Number(file.Year, format),
        "season" => Number(file.Season, format),
        "episode" => Number(file.Episode, format),
        "episodeend" => Number(file.EpisodeEnd is { } end && end > file.Episode ? end : null, format),
        "numbering" => file.NumberingDisplay,
        "quality" => file.QualityDisplay,
        "name" => Path.GetFileNameWithoutExtension(file.FileName),
        // An unknown field is left as the user wrote it rather than silently swallowed:
        // a typo you can see is a typo you can fix.
        _ => "{" + name + (format == null ? "" : ":" + format) + "}"
    };

    private static string Number(int? value, string? format) =>
        value is not { } n ? string.Empty
        : string.IsNullOrEmpty(format) ? n.ToString(CultureInfo.InvariantCulture)
        : Format(n, format);

    private static string Format(int value, string format)
    {
        try { return value.ToString(format, CultureInfo.InvariantCulture); }
        catch (FormatException) { return value.ToString(CultureInfo.InvariantCulture); }
    }

    // What an empty field leaves behind: a bracket with nothing in it, and two separators
    // that were meant to have something between them.
    private static readonly Regex EmptyBrackets = new(@"[\(\[\{]\s*[\)\]\}]", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex RunOfSeparators = new(
        @"[-–—]\s*(?:[-–—]\s*)+", RegexOptions.Compiled);

    /// <summary>
    /// Make a name out of what the pattern produced: drop what an empty field left behind,
    /// and take out the characters Windows will not have in a file name.
    ///
    /// Separators between fields are the user's and are left alone — "11 - Burn Notice"
    /// keeps its dash. Only the ones an empty field stranded go: a run of two where one was
    /// meant, and any left hanging off either end.
    /// </summary>
    private static string Tidy(string stem)
    {
        var cleaned = new string(stem.Select(c => InvalidChars.Contains(c) ? ' ' : c).ToArray());
        cleaned = EmptyBrackets.Replace(cleaned, " ");
        cleaned = Whitespace.Replace(cleaned, " ").Trim();
        cleaned = RunOfSeparators.Replace(cleaned, "- ");
        cleaned = Whitespace.Replace(cleaned, " ").Trim();
        return cleaned.Trim('-', '–', '—', '_', '.', ' ');
    }
}
