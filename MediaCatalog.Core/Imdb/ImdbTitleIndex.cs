using System.Text;
using System.Text.RegularExpressions;

namespace MediaCatalog.Core.Imdb;

/// <summary>What the IMDb extract knows about one title.</summary>
/// <param name="Title">The primary title as IMDb spells it.</param>
/// <param name="Year">
/// The year to file it under: the most recent one recorded against that name, or null if
/// the extract records none.
/// </param>
/// <param name="Ambiguous">
/// True when the name has been used more than once. A remake, a reboot and the film they
/// were made from all answer to one title, and nothing about a file name says which of them
/// it is — so the newest is taken and this says plainly that it is a guess.
/// </param>
/// <param name="EarliestYear">
/// The other end of the range, so the user can see what the alternatives were rather than
/// only being told there were some.
/// </param>
/// <param name="Genres">The genres recorded against it, spelled out. Empty when it has none.</param>
/// <param name="SeriesId">
/// The identifier of the programme of this name, when one exists — what the episode table is
/// searched by. 0 when the name belongs to no series, which is most of them.
/// </param>
public record ImdbMatch(
    string Title, int? Year, bool Ambiguous = false, int? EarliestYear = null,
    IReadOnlyList<string>? Genres = null, int SeriesId = 0)
{
    /// <summary>The genres, never null, so callers needn't check before enumerating.</summary>
    public IReadOnlyList<string> GenreNames => Genres ?? Array.Empty<string>();
}

/// <summary>
/// Looks titles up in <c>IMDBData.tsv</c>. Two modes, chosen by the user: held in memory
/// (fast, a few hundred megabytes) or read from disk on demand (no memory cost).
///
/// Either way the API is deliberately batch-shaped — <see cref="LookupManyAsync"/> —
/// because the on-disk mode answers a thousand questions in one pass over the file for
/// the price of answering one.
///
/// Two formats are read. The current one carries an identifier, a title type, the years and
/// the genres, with the type and genres held as numbers explained by their own small tables.
/// The two-column extract earlier versions wrote is still understood, so an existing install
/// keeps working until the day it is re-extracted; it simply has no genres and no way to
/// find a programme's episodes, since neither is in the file.
/// </summary>
public sealed class ImdbTitleIndex
{
    private readonly string _path;
    private readonly string _typesPath;
    private readonly string _genresPath;
    private Dictionary<string, ImdbMatch>? _memory;
    private ImdbCodeTable? _types;
    private ImdbCodeTable? _genres;

    public ImdbTitleIndex(string path, string? typesPath = null, string? genresPath = null)
    {
        _path = path;
        _typesPath = typesPath ?? Storage.AppPaths.ImdbTypesPath;
        _genresPath = genresPath ?? Storage.AppPaths.ImdbGenresPath;
    }

    /// <summary>True when the extract exists and can be consulted.</summary>
    public bool IsAvailable => File.Exists(_path);

    /// <summary>True once the whole extract is held in memory.</summary>
    public bool IsLoaded => _memory != null;

    /// <summary>Titles held in memory, or 0 when running from disk.</summary>
    public int Count => _memory?.Count ?? 0;

    /// <summary>
    /// True when the extract carries identifiers, types and genres — i.e. was written by this
    /// version rather than by an earlier one. The features that need any of those say so
    /// rather than quietly finding nothing.
    /// </summary>
    public bool HasRichData => IsAvailable && ImdbExtractFormat.IsCurrentFormat(_path);

    /// <summary>What the title-type numbers in the extract stand for.</summary>
    public ImdbCodeTable Types => _types ??= ImdbCodeTable.Load(_typesPath);

    /// <summary>What the genre numbers stand for.</summary>
    public ImdbCodeTable Genres => _genres ??= ImdbCodeTable.Load(_genresPath);

    /// <summary>Every genre the data holds, in alphabetical order — for the filter box.</summary>
    public IReadOnlyList<string> KnownGenres =>
        Genres.Names.Where(n => n.Length > 0).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Release the in-memory copy (when the user turns the option off).</summary>
    public void Unload()
    {
        _memory = null;
        _types = null;
        _genres = null;
    }

    /// <summary>
    /// Read the whole extract into memory. Titles are keyed by their normalised form, and
    /// rows sharing a name are folded together — see <see cref="Accumulator"/>.
    /// </summary>
    public async Task LoadAsync(IProgress<long>? linesRead = null, CancellationToken ct = default)
    {
        if (_memory != null || !IsAvailable) return;

        var accumulators = new Dictionary<string, Accumulator>(1 << 20, StringComparer.Ordinal);
        long lines = 0;

        await using var stream = OpenRead(_path);
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1 << 20);

        var seriesTypes = SeriesTypeIds();

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryParse(line, out var row)) continue;

            Merge(accumulators, row, seriesTypes);
            if (++lines % 250_000 == 0) linesRead?.Report(lines);
        }

        _memory = accumulators.ToDictionary(
            kv => kv.Key, kv => kv.Value.ToMatch(Genres), StringComparer.Ordinal);
        linesRead?.Report(lines);
    }

    /// <summary>
    /// Look up many titles at once. Every requested title appears in the result; ones
    /// IMDb has never heard of map to null.
    /// </summary>
    public async Task<Dictionary<string, ImdbMatch?>> LookupManyAsync(
        IReadOnlyCollection<string> titles, CancellationToken ct = default)
    {
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);   // key -> original
        foreach (var t in titles)
        {
            var key = Normalize(t);
            if (key.Length > 0) wanted.TryAdd(key, t);
        }

        var found = new Dictionary<string, ImdbMatch?>(StringComparer.OrdinalIgnoreCase);
        foreach (var original in wanted.Values) found[original] = null;
        if (wanted.Count == 0 || !IsAvailable) return found;

        if (_memory is { } map)
        {
            foreach (var (key, original) in wanted)
                if (map.TryGetValue(key, out var match)) found[original] = match;
            return found;
        }

        // One pass over the file answers the whole batch.
        var hits = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        var seriesTypes = SeriesTypeIds();

        await using var stream = OpenRead(_path);
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1 << 20);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryParse(line, out var row)) continue;
            if (!wanted.ContainsKey(Normalize(row.Title))) continue;
            Merge(hits, row, seriesTypes);
        }

        foreach (var (key, original) in wanted)
            if (hits.TryGetValue(key, out var hit)) found[original] = hit.ToMatch(Genres);
        return found;
    }

    /// <summary>Look one title up. Prefer the batch form when asking about many.</summary>
    public async Task<ImdbMatch?> LookupAsync(string title, CancellationToken ct = default)
    {
        var result = await LookupManyAsync(new[] { title }, ct);
        return result.TryGetValue(title, out var match) ? match : null;
    }

    /// <summary>What the extract says about a set of identifiers, in one pass over the file.</summary>
    /// <remarks>
    /// Always read from disk, even when the extract is held in memory: the in-memory map is
    /// keyed by name, and keeping a second one keyed by identifier would double what is a
    /// few hundred megabytes to answer a question asked once, deliberately, by a user who
    /// has just pressed a button and expects it to take a moment.
    /// </remarks>
    public async Task<Dictionary<int, ImdbTitleRow>> LookupByIdAsync(
        IReadOnlyCollection<int> ids, CancellationToken ct = default)
    {
        var found = new Dictionary<int, ImdbTitleRow>();
        if (ids.Count == 0 || !IsAvailable) return found;

        var wanted = ids as HashSet<int> ?? new HashSet<int>(ids);

        await using var stream = OpenRead(_path);
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1 << 20);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryParse(line, out var row)) continue;
            if (!wanted.Contains(row.Id)) continue;

            found[row.Id] = row;
            if (found.Count == wanted.Count) break;
        }

        return found;
    }

    /// <summary>The genres of a row, spelled out.</summary>
    public IReadOnlyList<string> GenresOf(ImdbTitleRow row) => Genres.NamesOf(row.Genres);

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);

    /// <summary>
    /// The type numbers that mean "a programme with episodes". Both are wanted: a six-part
    /// drama is a tvMiniSeries and has seasons and episodes like anything else.
    /// </summary>
    private (int Series, int MiniSeries) SeriesTypeIds() =>
        (Types.IdOf("tvSeries"), Types.IdOf("tvMiniSeries"));

    /// <summary>
    /// What is known about one name so far, folded together as the rows go past.
    ///
    /// One title can belong to several things — the 1969 <i>Italian Job</i> and the 2003 one,
    /// a series and the film it was based on — and a file name almost never says which. The
    /// most recent is taken, because the copy somebody has is far more often the current
    /// release than the fifty-year-old one, and the entry is marked ambiguous so the guess is
    /// visible rather than silent. The earliest is kept alongside it, so the user can see the
    /// span they are choosing between.
    /// </summary>
    private sealed class Accumulator
    {
        public string Title = string.Empty;
        public int? Year;
        public int? EarliestYear;
        public bool Ambiguous;
        public int[] Genres = Array.Empty<int>();
        public int SeriesId;
        public int? SeriesYear;

        public ImdbMatch ToMatch(ImdbCodeTable genres) => new(
            Title, Year, Ambiguous, EarliestYear,
            Genres.Length == 0 ? null : genres.NamesOf(Genres), SeriesId);
    }

    private static void Merge(
        Dictionary<string, Accumulator> map, ImdbTitleRow row, (int Series, int MiniSeries) seriesTypes)
    {
        var key = Normalize(row.Title);
        if (key.Length == 0) return;

        if (!map.TryGetValue(key, out var entry))
            map[key] = entry = new Accumulator
            {
                Title = row.Title, Year = row.StartYear, EarliestYear = row.StartYear,
                Genres = row.Genres
            };
        else
        {
            // Genres from whichever row first had any: a row that names none is silent
            // rather than contradicting one that does.
            if (entry.Genres.Length == 0 && row.Genres.Length > 0) entry.Genres = row.Genres;

            if (row.StartYear is { } y)
            {
                // A row that supplies a year beats one that has none, without that counting
                // as a disagreement: one date and no date are not two dates.
                if (entry.Year is not { } known)
                {
                    entry.Year = y;
                    entry.EarliestYear = y;
                }
                else
                {
                    entry.Ambiguous = entry.Ambiguous || y != known;
                    entry.Year = Math.Max(known, y);
                    entry.EarliestYear = Math.Min(entry.EarliestYear ?? known, y);
                }
            }
        }

        // The programme of this name, for looking its episodes up. The most recent wins,
        // for the same reason the most recent year does.
        if (row.TypeId == seriesTypes.Series || row.TypeId == seriesTypes.MiniSeries)
            if (entry.SeriesId == 0 || (row.StartYear ?? 0) >= (entry.SeriesYear ?? 0))
            {
                entry.SeriesId = row.Id;
                entry.SeriesYear = row.StartYear;
            }
    }

    /// <summary>
    /// Read one line of either format. The current one is six tab-separated fields opening
    /// with an identifier; the two-column extract earlier versions wrote is a title and a
    /// year, and is recognised by having no identifier to open with.
    /// </summary>
    private static bool TryParse(string line, out ImdbTitleRow row)
    {
        if (ImdbExtractFormat.TryParseTitle(line, out row)) return true;

        row = default;
        if (line.Length == 0 || line[0] == '#') return false;

        var tab = line.IndexOf('\t');
        if (tab <= 0) return false;

        var title = line[..tab];
        if (title.Length == 0) return false;

        var rest = line[(tab + 1)..];
        var year = rest.Length > 0 && int.TryParse(rest, out var y) && y is > 1800 and < 2200
            ? y : (int?)null;

        row = new ImdbTitleRow(0, -1, title, year, null, Array.Empty<int>());
        return true;
    }

    // Punctuation, case and spacing vary wildly between file names and IMDb; the
    // comparison ignores all of it so "King Of The Hill" finds "King of the Hill".
    private static readonly Regex NotWord = new(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled);

    /// <summary>The comparison form of a title: lower case, letters and digits only.</summary>
    public static string Normalize(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var s = title.Replace('&', ' ').Replace('+', ' ');
        s = NotWord.Replace(s, " ");
        return s.Trim().ToLowerInvariant().Replace(" ", string.Empty);
    }
}
