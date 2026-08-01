using System.Text;
using System.Text.RegularExpressions;

namespace MediaCatalog.Core.Imdb;

/// <summary>What the IMDb extract knows about one title.</summary>
/// <param name="Title">The primary title as IMDb spells it.</param>
/// <param name="Year">The earliest year recorded for that name, or null if it has none.</param>
public record ImdbMatch(string Title, int? Year);

/// <summary>
/// Looks titles up in <c>IMDBData.tsv</c>. Two modes, chosen by the user: held in memory
/// (fast, a few hundred megabytes) or read from disk on demand (no memory cost).
///
/// Either way the API is deliberately batch-shaped — <see cref="LookupManyAsync"/> —
/// because the on-disk mode answers a thousand questions in one pass over the file for
/// the price of answering one.
/// </summary>
public sealed class ImdbTitleIndex
{
    private readonly string _path;
    private Dictionary<string, ImdbMatch>? _memory;

    public ImdbTitleIndex(string path) => _path = path;

    /// <summary>True when the extract exists and can be consulted.</summary>
    public bool IsAvailable => File.Exists(_path);

    /// <summary>True once the whole extract is held in memory.</summary>
    public bool IsLoaded => _memory != null;

    /// <summary>Titles held in memory, or 0 when running from disk.</summary>
    public int Count => _memory?.Count ?? 0;

    /// <summary>Release the in-memory copy (when the user turns the option off).</summary>
    public void Unload() => _memory = null;

    /// <summary>
    /// Read the whole extract into memory. Titles are keyed by their normalised form, and
    /// the earliest year wins where a name has been used more than once — the original
    /// rather than the remake.
    /// </summary>
    public async Task LoadAsync(IProgress<long>? linesRead = null, CancellationToken ct = default)
    {
        if (_memory != null || !IsAvailable) return;

        var map = new Dictionary<string, ImdbMatch>(1 << 20, StringComparer.Ordinal);
        long lines = 0;

        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1 << 20, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1 << 20);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryParse(line, out var title, out var year)) continue;

            Merge(map, title, year);
            if (++lines % 250_000 == 0) linesRead?.Report(lines);
        }

        _memory = map;
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
        var hits = new Dictionary<string, ImdbMatch>(StringComparer.Ordinal);
        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1 << 20, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1 << 20);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryParse(line, out var title, out var year)) continue;
            if (!wanted.ContainsKey(Normalize(title))) continue;
            Merge(hits, title, year);
        }

        foreach (var (key, original) in wanted)
            if (hits.TryGetValue(key, out var match)) found[original] = match;
        return found;
    }

    /// <summary>Look one title up. Prefer the batch form when asking about many.</summary>
    public async Task<ImdbMatch?> LookupAsync(string title, CancellationToken ct = default)
    {
        var result = await LookupManyAsync(new[] { title }, ct);
        return result.TryGetValue(title, out var match) ? match : null;
    }

    /// <summary>Keep the earliest year for a name: the original, not the remake.</summary>
    private static void Merge(Dictionary<string, ImdbMatch> map, string title, int? year)
    {
        var key = Normalize(title);
        if (key.Length == 0) return;

        if (!map.TryGetValue(key, out var existing))
        {
            map[key] = new ImdbMatch(title, year);
            return;
        }

        // A row that supplies a year beats one that has none; otherwise the older wins.
        if (year is { } y && (existing.Year is null || y < existing.Year))
            map[key] = existing with { Year = y };
    }

    private static bool TryParse(string line, out string title, out int? year)
    {
        title = string.Empty;
        year = null;

        var tab = line.IndexOf('\t');
        if (tab <= 0) return false;

        title = line[..tab];
        var rest = line[(tab + 1)..];
        if (rest.Length > 0 && int.TryParse(rest, out var y) && y is > 1800 and < 2200)
            year = y;
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
