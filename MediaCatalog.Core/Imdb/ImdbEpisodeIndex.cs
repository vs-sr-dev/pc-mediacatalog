using System.Text;

namespace MediaCatalog.Core.Imdb;

/// <summary>Everything the extract knows about one programme's episodes.</summary>
/// <param name="Seasons">
/// Season number to the episodes recorded in it. Episodes with no season or no episode
/// number are left out: a row that cannot say which episode it is answers no question worth
/// asking here.
/// </param>
public record ImdbSeries(int SeriesId, IReadOnlyDictionary<int, IReadOnlyList<ImdbEpisodeRow>> Seasons)
{
    /// <summary>How many episodes IMDb records for a season, or 0 when it records none.</summary>
    public int EpisodeCount(int season) =>
        Seasons.TryGetValue(season, out var episodes) ? episodes.Count : 0;

    /// <summary>The highest episode number recorded for a season, or 0 for an unknown season.</summary>
    public int LastEpisode(int season) =>
        Seasons.TryGetValue(season, out var episodes) && episodes.Count > 0
            ? episodes.Max(e => e.Episode ?? 0)
            : 0;

    /// <summary>The identifier of a particular episode, or 0 when the extract has no such row.</summary>
    public int EpisodeId(int season, int episode) =>
        Seasons.TryGetValue(season, out var episodes)
            ? episodes.FirstOrDefault(e => e.Episode == episode).Id
            : 0;
}

/// <summary>
/// Reads <c>IMDBEpisodes.tsv</c> — which episode of which programme each identifier is.
///
/// This is the file that makes "which episodes am I missing?" a question with an answer. A
/// folder holding episodes 1 to 12 looks complete on its own; only knowing that the season
/// had thirteen says otherwise.
///
/// Like the title index the API is batch-shaped, because the file is answered in one pass
/// whether one programme is asked about or two hundred.
/// </summary>
public sealed class ImdbEpisodeIndex
{
    private readonly string _path;

    public ImdbEpisodeIndex(string? path = null) => _path = path ?? Storage.AppPaths.ImdbEpisodesPath;

    /// <summary>True when the episode extract exists and can be consulted.</summary>
    public bool IsAvailable => File.Exists(_path);

    /// <summary>Where the file is, so the UI can say what is missing and where to put it.</summary>
    public string Path => _path;

    /// <summary>
    /// The episodes of every requested programme, in one pass over the file. Programmes the
    /// extract has never heard of are simply absent from the result.
    /// </summary>
    public async Task<Dictionary<int, ImdbSeries>> LookupSeriesAsync(
        IReadOnlyCollection<int> seriesIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, ImdbSeries>();
        if (seriesIds.Count == 0 || !IsAvailable) return result;

        var wanted = seriesIds as HashSet<int> ?? new HashSet<int>(seriesIds.Where(id => id > 0));
        if (wanted.Count == 0) return result;

        var gathered = new Dictionary<int, Dictionary<int, List<ImdbEpisodeRow>>>();

        await using var stream = new FileStream(
            _path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1 << 20);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (!ImdbExtractFormat.TryParseEpisode(line, out var row)) continue;
            if (!wanted.Contains(row.SeriesId)) continue;
            if (row.Season is not { } season || row.Episode is null) continue;

            if (!gathered.TryGetValue(row.SeriesId, out var seasons))
                gathered[row.SeriesId] = seasons = new Dictionary<int, List<ImdbEpisodeRow>>();
            if (!seasons.TryGetValue(season, out var episodes))
                seasons[season] = episodes = new List<ImdbEpisodeRow>();

            // IMDb occasionally lists one episode twice; counting it twice would invent a
            // missing episode at the end of the season.
            if (episodes.All(e => e.Episode != row.Episode)) episodes.Add(row);
        }

        foreach (var (seriesId, seasons) in gathered)
            result[seriesId] = new ImdbSeries(
                seriesId,
                seasons.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<ImdbEpisodeRow>)kv.Value
                        .OrderBy(e => e.Episode ?? 0).ToList()));

        return result;
    }
}
