using System.Text;

namespace MediaCatalog.Core.Imdb;

/// <summary>
/// A set of short strings that repeat on nearly every row — the title types (movie,
/// tvSeries, tvEpisode, …) and the genres — held once and referred to by number.
///
/// "tvEpisode" is nine bytes, and it is on eight million rows. "Comedy,Romance" is fourteen,
/// and something like it is on most of the rest. Writing a number instead and keeping one
/// table of what the numbers mean costs a file of a few hundred bytes and saves a couple of
/// hundred megabytes, without losing anything: the names are still there to be read back.
///
/// The table is built afresh from the data on every extraction rather than being fixed in
/// this program, because IMDb may add a type or a genre at any time and a fixed list would
/// quietly file the new one as unknown.
/// </summary>
public sealed class ImdbCodeTable
{
    private readonly List<string> _names = new();
    private readonly Dictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The names, in id order — index 0 is id 0.</summary>
    public IReadOnlyList<string> Names => _names;

    public int Count => _names.Count;

    /// <summary>The id for a name, adding it to the table if it is new.</summary>
    public int Intern(string name)
    {
        var key = name.Trim();
        if (key.Length == 0) return -1;
        if (_ids.TryGetValue(key, out var id)) return id;

        id = _names.Count;
        _names.Add(key);
        _ids[key] = id;
        return id;
    }

    /// <summary>What an id stands for, or an empty string when nothing does.</summary>
    public string NameOf(int id) => id >= 0 && id < _names.Count ? _names[id] : string.Empty;

    /// <summary>The id of a name, or -1 when the table has never seen it.</summary>
    public int IdOf(string name) =>
        !string.IsNullOrWhiteSpace(name) && _ids.TryGetValue(name.Trim(), out var id) ? id : -1;

    /// <summary>The names for a run of ids, skipping any the table cannot explain.</summary>
    public List<string> NamesOf(IEnumerable<int> ids) =>
        ids.Select(NameOf).Where(n => n.Length > 0).ToList();

    // --- Persistence -------------------------------------------------------

    private const string Marker = "#MediaCatalog";

    public async Task SaveAsync(string path, string kind, CancellationToken ct = default)
    {
        var tmp = path + ".tmp";
        await using (var writer = new StreamWriter(tmp, false, new UTF8Encoding(false)))
        {
            await writer.WriteLineAsync($"{Marker}\t{ImdbExtractFormat.Version}\t{kind}");
            for (var id = 0; id < _names.Count; id++)
                await writer.WriteLineAsync($"{id}\t{_names[id]}");
        }

        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Read a table back. A missing file is an empty table rather than an error: the program
    /// works without the names, it simply has nothing to show in place of the numbers.
    /// </summary>
    public static ImdbCodeTable Load(string path)
    {
        var table = new ImdbCodeTable();
        if (!File.Exists(path)) return table;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                if (!int.TryParse(line.AsSpan(0, tab), out var id)) continue;

                var name = line[(tab + 1)..].Trim();
                if (name.Length == 0) continue;

                // The ids are positions in the list, so a gap has to be filled rather than
                // skipped — otherwise every id after it means something else.
                while (table._names.Count < id) table._names.Add(string.Empty);
                if (table._names.Count == id) table._names.Add(name);
                else table._names[id] = name;
                table._ids[name] = id;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return table;
    }
}
