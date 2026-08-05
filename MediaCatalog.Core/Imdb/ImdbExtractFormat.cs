namespace MediaCatalog.Core.Imdb;

/// <summary>One row of the optimised title extract.</summary>
/// <param name="Id">IMDb's tconst with the "tt" and the leading zeros taken off.</param>
/// <param name="TypeId">Index into the title-type table — see <see cref="ImdbCodeTable"/>.</param>
/// <param name="Genres">Indexes into the genre table; empty when the row names none.</param>
public readonly record struct ImdbTitleRow(
    int Id, int TypeId, string Title, int? StartYear, int? EndYear, int[] Genres);

/// <summary>One row of the episode extract: which episode of which programme this is.</summary>
public readonly record struct ImdbEpisodeRow(int Id, int SeriesId, int? Season, int? Episode);

/// <summary>
/// The shape of the files this program boils IMDb's downloads down to, in one place, so the
/// half that writes them and the half that reads them cannot drift apart.
///
/// Every file opens with a marker line naming the format and its version. That line is what
/// tells a two-column extract written by an earlier version from the current one, so an
/// existing install keeps working until it is re-extracted rather than reading nonsense.
/// </summary>
public static class ImdbExtractFormat
{
    /// <summary>The version this build writes. Bumped when the columns change.</summary>
    public const int Version = 2;

    /// <summary>What every file this program writes begins with.</summary>
    public const string Marker = "#MediaCatalog";

    public const string TitlesHeader =
        Marker + "\t2\ttitles\tid\ttype\tprimaryTitle\tstartYear\tendYear\tgenres";

    public const string EpisodesHeader =
        Marker + "\t2\tepisodes\tid\tseries\tseason\tepisode";

    /// <summary>IMDb writes a missing value as a backslash-N rather than leaving it blank.</summary>
    public const string NullField = @"\N";

    /// <summary>True when a line is the marker that opens one of our own files.</summary>
    public static bool IsMarker(string? line) =>
        line != null && line.StartsWith(Marker, StringComparison.Ordinal);

    /// <summary>
    /// True when the file at <paramref name="path"/> is in the current format rather than
    /// the two-column extract earlier versions wrote.
    /// </summary>
    public static bool IsCurrentFormat(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            return IsMarker(reader.ReadLine());
        }
        catch { return false; }
    }

    // --- Writing -----------------------------------------------------------

    /// <summary>A title row as it is written out. Empty fields are simply left empty.</summary>
    public static string FormatTitle(
        int id, int typeId, string title, int? startYear, int? endYear, IReadOnlyList<int> genres) =>
        string.Join('\t',
            id.ToString(),
            typeId.ToString(),
            title,
            startYear?.ToString() ?? string.Empty,
            endYear?.ToString() ?? string.Empty,
            genres.Count == 0 ? string.Empty : string.Join(',', genres));

    public static string FormatEpisode(int id, int seriesId, int? season, int? episode) =>
        string.Join('\t',
            id.ToString(),
            seriesId.ToString(),
            season?.ToString() ?? string.Empty,
            episode?.ToString() ?? string.Empty);

    // --- Reading -----------------------------------------------------------

    private static readonly int[] NoGenres = Array.Empty<int>();

    /// <summary>
    /// Read a title row. False for the marker line, a blank line or anything malformed —
    /// a damaged line is skipped rather than allowed to stop a pass over ten million of them.
    /// </summary>
    public static bool TryParseTitle(string line, out ImdbTitleRow row)
    {
        row = default;
        if (line.Length == 0 || line[0] == '#') return false;

        Span<Range> fields = stackalloc Range[6];
        var span = line.AsSpan();
        if (Split(span, fields) < 3) return false;

        var id = ParseInt(span[fields[0]]);
        if (id <= 0) return false;

        var title = span[fields[2]].ToString();
        if (title.Length == 0) return false;

        row = new ImdbTitleRow(
            id,
            ParseInt(span[fields[1]]),
            title,
            ParseYear(span[fields[3]]),
            ParseYear(span[fields[4]]),
            ParseGenres(span[fields[5]]));
        return true;
    }

    /// <summary>Read an episode row. False for the marker line or anything malformed.</summary>
    public static bool TryParseEpisode(string line, out ImdbEpisodeRow row)
    {
        row = default;
        if (line.Length == 0 || line[0] == '#') return false;

        Span<Range> fields = stackalloc Range[4];
        var span = line.AsSpan();
        if (Split(span, fields) < 2) return false;

        var id = ParseInt(span[fields[0]]);
        var series = ParseInt(span[fields[1]]);
        if (id <= 0 || series <= 0) return false;

        row = new ImdbEpisodeRow(
            id, series, ParseNumber(span[fields[2]]), ParseNumber(span[fields[3]]));
        return true;
    }

    /// <summary>
    /// Split on tabs into a fixed number of fields. Missing trailing fields come back empty
    /// rather than absent, so the caller can read them without counting first. Returns how
    /// many were actually present.
    /// </summary>
    private static int Split(ReadOnlySpan<char> line, Span<Range> fields)
    {
        var count = 0;
        var start = 0;
        for (var i = 0; i <= line.Length && count < fields.Length; i++)
        {
            if (i != line.Length && line[i] != '\t') continue;
            fields[count++] = new Range(start, i);
            start = i + 1;
        }

        var present = count;
        while (count < fields.Length) fields[count++] = new Range(line.Length, line.Length);
        return present;
    }

    private static int ParseInt(ReadOnlySpan<char> s) =>
        int.TryParse(s, out var value) ? value : 0;

    private static int? ParseNumber(ReadOnlySpan<char> s) =>
        int.TryParse(s, out var value) ? value : null;

    private static int? ParseYear(ReadOnlySpan<char> s) =>
        int.TryParse(s, out var year) && year is > 1800 and < 2200 ? year : null;

    private static int[] ParseGenres(ReadOnlySpan<char> s)
    {
        if (s.Length == 0) return NoGenres;

        var count = 1;
        foreach (var c in s) if (c == ',') count++;

        var ids = new int[count];
        var n = 0;
        var start = 0;
        for (var i = 0; i <= s.Length; i++)
        {
            if (i != s.Length && s[i] != ',') continue;
            if (int.TryParse(s[start..i], out var id) && id >= 0) ids[n++] = id;
            start = i + 1;
        }

        return n == ids.Length ? ids : n == 0 ? NoGenres : ids[..n];
    }
}
