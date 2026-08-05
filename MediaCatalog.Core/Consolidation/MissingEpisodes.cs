using MediaCatalog.Core.Classification;
using MediaCatalog.Core.Duplicates;
using MediaCatalog.Core.Imdb;
using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Consolidation;

/// <summary>One episode a season should hold, and whether the library holds it.</summary>
/// <param name="Title">
/// The episode's own name, when the IMDb episode data can supply it. Blank otherwise — a
/// number on its own is still an answer, just a less useful one.
/// </param>
public record EpisodeSlot(int Number, string Title, bool Present)
{
    public string Describe() =>
        Title.Length > 0 ? $"E{Number:00} — {Title}" : $"E{Number:00}";
}

/// <summary>What one season of one programme is missing.</summary>
/// <param name="Expected">
/// How many episodes the season is believed to hold: the number IMDb records, or — with no
/// episode data to consult — the highest number the library actually holds.
/// </param>
/// <param name="ExpectedFromImdb">
/// True when <paramref name="Expected"/> came from IMDb rather than from the files
/// themselves. False means the tail of the season cannot be checked at all: a folder holding
/// episodes 1 to 12 of a thirteen-part season looks complete, and nothing in it says
/// otherwise.
/// </param>
public record SeasonGap(
    string Show, int Season, int Held, int Expected, bool ExpectedFromImdb,
    IReadOnlyList<EpisodeSlot> Missing)
{
    public string Describe() =>
        $"{Show} — Season {Season:00}: {Missing.Count} missing of {Expected} " +
        $"({Held} held){(ExpectedFromImdb ? "" : ", counted from the files themselves")}";
}

/// <summary>Everything one programme is missing.</summary>
/// <param name="MissingSeasons">
/// Seasons IMDb records that the library holds nothing at all of. Kept apart from the gaps,
/// because "you have none of season 4" and "you are missing episode 7 of season 4" are
/// different sizes of problem and somebody collecting one season deliberately does not want
/// the first shouted at them.
/// </param>
public record ShowGaps(
    string Show, int SeriesId, IReadOnlyList<SeasonGap> Seasons, IReadOnlyList<int> MissingSeasons)
{
    public int MissingEpisodes => Seasons.Sum(s => s.Missing.Count);
}

/// <param name="Identified">Programmes the IMDb data recognised, so their tails could be checked.</param>
/// <param name="Named">Episodes given their own title from the IMDb data.</param>
/// <param name="UsedImdb">
/// False when there is no episode data at all, in which case every season was measured
/// against its own highest episode and the report says only where the holes are.
/// </param>
public record MissingEpisodeReport(
    IReadOnlyList<ShowGaps> Shows, int ShowsChecked, int Identified, int Named, bool UsedImdb)
{
    public int TotalMissing => Shows.Sum(s => s.MissingEpisodes);

    public string Describe()
    {
        if (ShowsChecked == 0)
            return "No consolidated programmes with season and episode numbers were found to check.";

        var text = $"{ShowsChecked} programme(s) checked, {TotalMissing} episode(s) missing.";
        if (UsedImdb)
            text += $" {Identified} programme(s) were found in the IMDb episode data, so their " +
                    "seasons were checked against the number of episodes actually broadcast.";
        else
            text += " Without the IMDb episode data each season could only be checked up to the " +
                    "highest episode you hold — so a season missing its last episodes looks complete.";
        return text;
    }
}

/// <summary>
/// Works out which episodes a consolidated programme is missing.
///
/// The easy half needs nothing but the files: episodes 1, 2, 3, 5 of a season are plainly
/// missing episode 4, and that hole can be found with no outside knowledge at all. The hard
/// half is the tail. A folder holding episodes 1 to 12 looks complete from the inside
/// whatever the season actually ran to, and only the IMDb episode data can say that there
/// were thirteen. Without it the tail is simply not checked, and the report says so rather
/// than implying a clean bill of health it cannot give.
///
/// The same pass fills in each held episode's own name — <c>Go Get Mommy's Bra</c> under
/// <c>Two and a Half Men</c> — since it has already looked the season up and the names are
/// sitting in the row beside the numbers.
/// </summary>
public static class MissingEpisodes
{
    /// <summary>True for a file this scan can say anything about.</summary>
    public static bool IsCheckable(MediaFile file, string category) =>
        category == CategoryResolver.TvShow &&
        file is { Season: not null, Episode: not null } &&
        !string.IsNullOrWhiteSpace(file.EffectiveTitle);

    /// <summary>
    /// Scan the catalogue for gaps, and name the episodes it can along the way.
    /// </summary>
    /// <param name="consolidatedOnly">
    /// True to look only at what has been filed. That is the question worth asking: files
    /// scattered across a download folder are half-finished by definition, and reporting
    /// them as an incomplete season would be reporting the obvious.
    /// </param>
    public static async Task<MissingEpisodeReport> ScanAsync(
        IEnumerable<MediaFile> files,
        Func<MediaFile, string> categoryOf,
        ImdbTitleIndex titles,
        ImdbEpisodeIndex episodes,
        bool consolidatedOnly = true,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        // --- What the library holds -----------------------------------------
        var shows = new Dictionary<string, Show>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var category = categoryOf(file);
            if (!IsCheckable(file, category)) continue;
            if (consolidatedOnly && !file.Consolidated) continue;

            var key = TitleDuplicateFinder.Normalise(file.EffectiveTitle);
            if (key.Length == 0) continue;

            if (!shows.TryGetValue(key, out var show))
                shows[key] = show = new Show(file.EffectiveTitle.Trim());

            var season = file.Season!.Value;
            if (!show.Seasons.TryGetValue(season, out var held))
                show.Seasons[season] = held = new Dictionary<int, MediaFile>();

            // A double episode holds both of the episodes it names, so neither counts as
            // missing — that is what Episodes is for.
            foreach (var number in file.Episodes) held.TryAdd(number, file);
        }

        if (shows.Count == 0)
            return new MissingEpisodeReport(
                Array.Empty<ShowGaps>(), 0, 0, 0, episodes.IsAvailable);

        // --- What IMDb says they should hold --------------------------------
        var seriesOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var guide = new Dictionary<int, ImdbSeries>();

        if (episodes.IsAvailable && titles.IsAvailable)
        {
            progress?.Report("Looking the programmes up in the IMDb data…");
            var answers = await titles.LookupManyAsync(
                shows.Values.Select(s => s.Title).ToList(), ct);

            foreach (var (key, show) in shows)
                if (answers.TryGetValue(show.Title, out var match) && match is { SeriesId: > 0 })
                    seriesOf[key] = match.SeriesId;

            if (seriesOf.Count > 0)
            {
                progress?.Report($"Reading the episodes of {seriesOf.Count} programme(s)…");
                guide = await episodes.LookupSeriesAsync(seriesOf.Values.Distinct().ToList(), ct);
            }
        }

        // --- Every episode worth naming, looked up in one pass ---------------
        var wantedIds = new HashSet<int>();
        foreach (var (key, show) in shows)
        {
            if (!seriesOf.TryGetValue(key, out var seriesId)) continue;
            if (!guide.TryGetValue(seriesId, out var series)) continue;

            foreach (var (_, seasonEpisodes) in series.Seasons)
                foreach (var episode in seasonEpisodes)
                    wantedIds.Add(episode.Id);
        }

        var episodeTitles = new Dictionary<int, ImdbTitleRow>();
        if (wantedIds.Count > 0)
        {
            progress?.Report($"Reading the names of {wantedIds.Count:N0} episode(s)…");
            episodeTitles = await titles.LookupByIdAsync(wantedIds, ct);
        }

        // --- The report ------------------------------------------------------
        var results = new List<ShowGaps>();
        var named = 0;

        foreach (var (key, show) in shows)
        {
            ct.ThrowIfCancellationRequested();

            seriesOf.TryGetValue(key, out var seriesId);
            guide.TryGetValue(seriesId, out var series);

            var gaps = new List<SeasonGap>();

            foreach (var (season, held) in show.Seasons.OrderBy(s => s.Key))
            {
                var highest = held.Keys.Max();
                var fromImdb = series?.LastEpisode(season) ?? 0;
                var expected = Math.Max(highest, fromImdb);

                var missing = new List<EpisodeSlot>();
                for (var number = 1; number <= expected; number++)
                {
                    if (held.ContainsKey(number)) continue;
                    missing.Add(new EpisodeSlot(
                        number, NameOf(series, season, number, episodeTitles), Present: false));
                }

                // Name what is here as well as what is not: the season has been looked up,
                // and an episode's own title is worth having whether or not anything is
                // missing around it.
                foreach (var (number, file) in held)
                    if (string.IsNullOrWhiteSpace(file.SecondaryTitle) &&
                        NameOf(series, season, number, episodeTitles) is { Length: > 0 } name)
                    {
                        file.SecondaryTitle = name;
                        named++;
                    }

                if (missing.Count > 0)
                    gaps.Add(new SeasonGap(
                        show.Title, season, held.Count, expected, fromImdb > 0, missing));
            }

            var absent = series == null
                ? Array.Empty<int>()
                : series.Seasons.Keys
                    .Where(s => s > 0 && !show.Seasons.ContainsKey(s))
                    .OrderBy(s => s)
                    .ToArray();

            if (gaps.Count > 0 || absent.Length > 0)
                results.Add(new ShowGaps(show.Title, seriesId, gaps, absent));
        }

        return new MissingEpisodeReport(
            results.OrderByDescending(s => s.MissingEpisodes)
                   .ThenBy(s => s.Show, StringComparer.OrdinalIgnoreCase)
                   .ToList(),
            shows.Count, seriesOf.Count, named, episodes.IsAvailable);
    }

    /// <summary>The name IMDb gives one episode, or an empty string when it gives none.</summary>
    private static string NameOf(
        ImdbSeries? series, int season, int episode, IReadOnlyDictionary<int, ImdbTitleRow> titles)
    {
        if (series == null) return string.Empty;
        var id = series.EpisodeId(season, episode);
        return id > 0 && titles.TryGetValue(id, out var row) ? row.Title : string.Empty;
    }

    /// <summary>One programme's episodes as the library holds them, while they are gathered.</summary>
    private sealed class Show
    {
        public Show(string title) => Title = title;
        public string Title { get; }
        public Dictionary<int, Dictionary<int, MediaFile>> Seasons { get; } = new();
    }
}
