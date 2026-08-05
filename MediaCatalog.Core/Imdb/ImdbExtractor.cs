using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using MediaCatalog.Core.Storage;

namespace MediaCatalog.Core.Imdb;

/// <param name="Phase">Which of the two source files is being read, for the status line.</param>
public record ImdbExtractProgress(long BytesRead, long BytesTotal, long LinesKept, string Phase = "titles");

/// <param name="Kept">Titles written to the extract.</param>
/// <param name="Skipped">Rows dropped — placeholders, timestamps, or outside the year window.</param>
/// <param name="OutsideYears">Of those, how many were dropped for their year alone.</param>
/// <param name="Episodes">Episode rows written, when the episode dataset was there to read.</param>
/// <param name="Types">How many distinct title types the data turned out to hold.</param>
/// <param name="Genres">How many distinct genres.</param>
public record ImdbExtractReport(
    long Kept, long Skipped, string Path,
    long OutsideYears = 0, long Episodes = 0, int Types = 0, int Genres = 0);

/// <summary>
/// Boils IMDb's downloads down to the small, fast files this program actually reads.
///
/// The two sources together are well over a gigabyte of text, most of which is repetition:
/// every identifier written as "tt0369179" when the number alone would do, every row naming
/// its type as "tvEpisode" in full, every row naming its genres in full, the same title
/// written out twice as primaryTitle and originalTitle, and columns this program has no use
/// for at all. So:
///
/// <list type="bullet">
/// <item>identifiers keep their number and lose the "tt" and the leading zeros;</item>
/// <item>the title type becomes a number, with a table saying what the numbers mean;</item>
/// <item>genres become numbers the same way, built from the data rather than from a fixed
/// list in this program, since IMDb may add one at any time;</item>
/// <item>originalTitle, isAdult and runtimeMinutes are dropped — the first is the primary
/// title again on all but a handful of rows, the second is of no interest here, and the
/// third is better read from the file itself than believed from a database;</item>
/// <item>anything released outside the year window the user has set is left out, because
/// very few people are cataloguing films from the 1890s and the whole file is faster for
/// every row that is not in it.</item>
/// </list>
///
/// Generic episode rows ("Episode #1.4", "Episode dated 3 May 1999") are dropped: they are
/// placeholders for untitled episodes and would only ever match by accident. So are the
/// broadcast timestamps that sit in the same column for some feeds — rows whose "title"
/// reads "22. sep. 2016 kl. 07:30" — which are a transmission slot rather than a name.
///
/// Everything is read a line at a time and never held in memory, bar the set of identifiers
/// kept, which is what decides the episodes worth writing out.
/// </summary>
public static class ImdbExtractor
{
    /// <summary>Placeholder names IMDb gives episodes that were never titled.</summary>
    private static readonly Regex GenericEpisode = new(
        @"^Episode\s*(?:#|dated\b|\d{1,4}(?!\d))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// A date, or a date and a time, standing where a title should be. Two forms are
    /// enough to catch the lot: a clock reference ("kl. 17:00", "12:30"), and a
    /// day-month-year opening ("21. jan. 2015", "4 feb 2015"). Nothing anyone would call a
    /// film matches either.
    /// </summary>
    private static readonly Regex Timestamp = new(
        @"^\d{1,2}\s*[.\-/]\s*\p{L}{3,}\.?\s*[.\-/]?\s*\d{4}\b" +   // 21. jan. 2015
        @"|^\d{1,2}\s*[.\-/]\s*\d{1,2}\s*[.\-/]\s*\d{4}\b" +        // 21.01.2015
        @"|\bkl\.?\s*\d{1,2}[:.]\d{2}\b" +                          // kl. 17:00
        @"|^\d{1,2}[:.]\d{2}(?::\d{2})?$",                          // 17:00
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>True when a row's primary title is a placeholder rather than a real name.</summary>
    public static bool IsGenericEpisodeTitle(string title) =>
        !string.IsNullOrWhiteSpace(title) && GenericEpisode.IsMatch(title.TrimStart());

    /// <summary>
    /// True when a row's primary title is a broadcast date or time rather than a name.
    /// These are worse than useless in the extract: they are numerous, they match nothing
    /// anyone searches for, and they make the file bigger for no purpose.
    /// </summary>
    public static bool IsTimestampTitle(string title) =>
        !string.IsNullOrWhiteSpace(title) && Timestamp.IsMatch(title.Trim());

    /// <summary>
    /// The source file to extract from: the plain TSV if it is there, otherwise the
    /// gzipped download, which is read without unpacking it first. Null when neither
    /// exists.
    /// </summary>
    public static string? FindSource(string tsvPath, string gzPath)
    {
        if (File.Exists(tsvPath)) return tsvPath;
        if (File.Exists(gzPath)) return gzPath;
        return null;
    }

    /// <summary>Where each of the extract's files goes, so callers needn't assemble them.</summary>
    public record Destinations(string Titles, string Types, string Genres, string Episodes)
    {
        public static Destinations Default => new(
            AppPaths.ImdbDataPath, AppPaths.ImdbTypesPath,
            AppPaths.ImdbGenresPath, AppPaths.ImdbEpisodesPath);
    }

    /// <summary>
    /// Read the IMDb downloads and write the extract. Everything is written to temporary
    /// files and moved into place at the end, so an interrupted run never leaves a half
    /// written extract for the next lookup to read.
    /// </summary>
    /// <param name="episodeSourcePath">
    /// <c>title.episode.tsv</c> or its gzip, when the user has it. Null simply means no
    /// episode table is written: everything else works exactly as before, and the only thing
    /// lost is knowing how many episodes a season is supposed to have.
    /// </param>
    public static async Task<ImdbExtractReport> ExtractAsync(
        string sourcePath,
        Destinations destinations,
        AppSettings settings,
        string? episodeSourcePath = null,
        IProgress<ImdbExtractProgress>? progress = null,
        CancellationToken ct = default)
    {
        var types = new ImdbCodeTable();
        var genres = new ImdbCodeTable();

        // The identifiers that made it into the extract. The episode table is only worth
        // the rows whose episode we actually kept — an episode we dropped for its year is
        // not one we can say anything about later.
        var kept = new HashSet<int>();

        var tmp = destinations.Titles + ".tmp";
        long keptCount = 0, skipped = 0, outsideYears = 0, episodes = 0;

        try
        {
            await using (var raw = Open(sourcePath))
            {
                var total = new FileInfo(sourcePath).Length;

                // Progress is measured on the compressed stream, which is the only length
                // we know up front; it still moves smoothly from 0 to 100.
                await using var body = Decompressed(raw, sourcePath);
                using var reader = new StreamReader(body, Encoding.UTF8, false, 1 << 20);
                await using var writer = new StreamWriter(tmp, false, new UTF8Encoding(false), 1 << 20);

                await writer.WriteLineAsync(ImdbExtractFormat.TitlesHeader);

                var columns = BasicsColumns(await reader.ReadLineAsync(ct));
                var sinceReport = 0;
                var genreIds = new List<int>(4);

                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    ct.ThrowIfCancellationRequested();

                    var fields = line.Split('\t');
                    if (fields.Length <= columns.Max) { skipped++; continue; }

                    var title = fields[columns.Title];
                    if (title.Length == 0 || title == ImdbExtractFormat.NullField) { skipped++; continue; }
                    if (IsGenericEpisodeTitle(title)) { skipped++; continue; }
                    if (IsTimestampTitle(title)) { skipped++; continue; }

                    var startYear = Year(fields[columns.StartYear]);
                    if (!settings.IsYearExtracted(startYear)) { skipped++; outsideYears++; continue; }

                    var id = ImdbIds.Parse(fields[columns.Id]);
                    if (id <= 0) { skipped++; continue; }

                    var typeId = types.Intern(Value(fields[columns.Type]) ?? "unknown");

                    genreIds.Clear();
                    if (Value(fields[columns.Genres]) is { } list)
                        foreach (var genre in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var genreId = genres.Intern(genre);
                            if (genreId >= 0) genreIds.Add(genreId);
                        }

                    await writer.WriteLineAsync(ImdbExtractFormat.FormatTitle(
                        id, typeId, title, startYear, Year(fields[columns.EndYear]), genreIds));
                    kept.Add(id);
                    keptCount++;

                    if (++sinceReport >= 100_000)
                    {
                        sinceReport = 0;
                        progress?.Report(new ImdbExtractProgress(raw.Position, total, keptCount));
                    }
                }
            }

            if (episodeSourcePath != null)
                episodes = await ExtractEpisodesAsync(
                    episodeSourcePath, destinations.Episodes, kept, progress, ct);

            // The tables and the titles go into place together, and last. The numbers in the
            // titles file mean whatever these two tables say they mean, so replacing one
            // without the other would leave every genre on every row pointing at the wrong
            // name — worse than an extraction that simply failed and left the old files be.
            await types.SaveAsync(destinations.Types, "types", ct);
            await genres.SaveAsync(destinations.Genres, "genres", ct);
            Replace(tmp, destinations.Titles);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }

        progress?.Report(new ImdbExtractProgress(1, 1, keptCount, "done"));
        return new ImdbExtractReport(
            keptCount, skipped, destinations.Titles, outsideYears, episodes,
            types.Count, genres.Count);
    }

    /// <summary>
    /// Write the episode table: which episode of which programme each identifier is.
    ///
    /// Only rows whose episode survived the title extraction are written. An episode left out
    /// for its year is one nothing can be said about later, so a row pointing at it would be
    /// a reference to nothing.
    /// </summary>
    private static async Task<long> ExtractEpisodesAsync(
        string sourcePath, string destPath, HashSet<int> keptTitles,
        IProgress<ImdbExtractProgress>? progress, CancellationToken ct)
    {
        var tmp = destPath + ".tmp";
        long written = 0;

        try
        {
            await using (var raw = Open(sourcePath))
            {
                var total = new FileInfo(sourcePath).Length;
                await using var body = Decompressed(raw, sourcePath);
                using var reader = new StreamReader(body, Encoding.UTF8, false, 1 << 20);
                await using var writer = new StreamWriter(tmp, false, new UTF8Encoding(false), 1 << 20);

                await writer.WriteLineAsync(ImdbExtractFormat.EpisodesHeader);

                var columns = EpisodeColumns(await reader.ReadLineAsync(ct));
                var sinceReport = 0;

                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    ct.ThrowIfCancellationRequested();

                    var fields = line.Split('\t');
                    if (fields.Length <= columns.Max) continue;

                    var id = ImdbIds.Parse(fields[columns.Id]);
                    var series = ImdbIds.Parse(fields[columns.Parent]);
                    if (id <= 0 || series <= 0) continue;
                    if (keptTitles.Count > 0 && !keptTitles.Contains(id)) continue;

                    // A row that says neither which season nor which episode it is cannot
                    // answer the one question this table exists to answer, and there are a
                    // great many of them — every episode of every chat show IMDb has never
                    // been told the numbering of.
                    var season = Number(fields[columns.Season]);
                    var episode = Number(fields[columns.Episode]);
                    if (season is null && episode is null) continue;

                    await writer.WriteLineAsync(
                        ImdbExtractFormat.FormatEpisode(id, series, season, episode));
                    written++;

                    if (++sinceReport >= 100_000)
                    {
                        sinceReport = 0;
                        progress?.Report(new ImdbExtractProgress(
                            raw.Position, total, written, "episodes"));
                    }
                }
            }

            Replace(tmp, destPath);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }

        return written;
    }

    // --- Reading the source ------------------------------------------------

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);

    private static Stream Decompressed(FileStream raw, string path) =>
        IsGzip(path) ? new GZipStream(raw, CompressionMode.Decompress) : raw;

    /// <summary>A field's value, or null when IMDb says it has none.</summary>
    private static string? Value(string field)
    {
        var s = field.Trim();
        return s.Length == 0 || s == ImdbExtractFormat.NullField ? null : s;
    }

    private static int? Year(string field) =>
        Value(field) is { } s && int.TryParse(s, out var year) && year is > 1800 and < 2200
            ? year : null;

    private static int? Number(string field) =>
        Value(field) is { } s && int.TryParse(s, out var n) ? n : null;

    /// <param name="Max">The rightmost column read, so a short row can be skipped in one test.</param>
    private record BasicsLayout(int Id, int Type, int Title, int StartYear, int EndYear, int Genres)
    {
        public int Max => Math.Max(Math.Max(Math.Max(Id, Type), Math.Max(Title, StartYear)),
            Math.Max(EndYear, Genres));
    }

    private record EpisodeLayout(int Id, int Parent, int Season, int Episode)
    {
        public int Max => Math.Max(Math.Max(Id, Parent), Math.Max(Season, Episode));
    }

    /// <summary>
    /// Which columns of <c>title.basics.tsv</c> hold what. Read from the header so a future
    /// column reshuffle doesn't silently extract the wrong fields; falls back to the
    /// documented positions if the header is missing or unfamiliar.
    /// </summary>
    private static BasicsLayout BasicsColumns(string? header)
    {
        var names = header?.Split('\t') ?? Array.Empty<string>();
        int At(string name, int fallback)
        {
            var index = Array.FindIndex(names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 ? index : fallback;
        }

        return new BasicsLayout(
            At("tconst", 0), At("titleType", 1), At("primaryTitle", 2),
            At("startYear", 5), At("endYear", 6), At("genres", 8));
    }

    private static EpisodeLayout EpisodeColumns(string? header)
    {
        var names = header?.Split('\t') ?? Array.Empty<string>();
        int At(string name, int fallback)
        {
            var index = Array.FindIndex(names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 ? index : fallback;
        }

        return new EpisodeLayout(
            At("tconst", 0), At("parentTconst", 1), At("seasonNumber", 2), At("episodeNumber", 3));
    }

    private static bool IsGzip(string path) =>
        path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);

    private static void Replace(string tmp, string destPath)
    {
        if (File.Exists(destPath)) File.Replace(tmp, destPath, null);
        else File.Move(tmp, destPath);
    }

    private static void TryDelete(string path) =>
        Relocation.FileDeleter.TryDeleteQuietly(path);
}
