using System.Text.RegularExpressions;
using MediaCatalog.Core.Classification;
using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Duplicates;

/// <summary>
/// Files that describe the same thing without being the same file.
/// </summary>
/// <param name="Key">What they have in common, for display: "Blade Runner (1982)".</param>
public record TitleDuplicateGroup(string Key, List<MediaFile> Files)
{
    public long TotalBytes => Files.Sum(f => f.SizeBytes);

    /// <summary>What keeping only one of them would give back — the largest is assumed kept.</summary>
    public long ReclaimableBytes => TotalBytes - (Files.Count > 0 ? Files.Max(f => f.SizeBytes) : 0);

    /// <summary>True when the copies differ in size, which is the usual sign of two encodes.</summary>
    public bool DiffersInSize => Files.Select(f => f.SizeBytes).Distinct().Count() > 1;
}

/// <summary>
/// Finds the duplicates a content hash cannot see: the same film downloaded twice, from
/// two different releases, so the files differ byte for byte while being the same thing.
///
/// For a film the evidence is the title and the year. For a programme it is the title and
/// the numbering, and nothing else will do: two episodes of one series share a title, a
/// year and very nearly a name without being remotely the same thing, so an episode is
/// only a possible duplicate of another episode carrying the same show title *and* the
/// same season and episode number. An episode with no numbering at all cannot be judged on
/// that basis and is left out rather than guessed at.
///
/// Byte-identical copies are left out throughout: they are exact duplicates, which the hash
/// already found and which have their own manager.
/// </summary>
public static class TitleDuplicateFinder
{
    /// <summary>
    /// Group the catalogue by what each file says it is. Groups of one — the normal case —
    /// are dropped, leaving only the sets worth a second look.
    /// </summary>
    /// <param name="categoryOf">
    /// The effective category of a file, so a file the user has filed as a programme is
    /// judged by a programme's rules. Without one the automatic classification is used.
    /// </param>
    public static List<TitleDuplicateGroup> Find(
        IEnumerable<MediaFile> files, Func<MediaFile, string>? categoryOf = null)
    {
        var groups = new List<TitleDuplicateGroup>();

        var keyed = files
            .Select(f => (File: f, Key: KeyOf(f, categoryOf?.Invoke(f) ?? CategoryResolver.Auto(f))))
            .Where(x => x.Key != null);

        foreach (var group in keyed.GroupBy(x => x.Key!, StringComparer.OrdinalIgnoreCase))
        {
            var members = group.Select(x => x.File).ToList();
            if (members.Count < 2) continue;

            // A set whose members are all the same bytes is an exact-duplicate set, which
            // the hash found and the duplicate manager handles. Only a set holding more
            // than one distinct piece of content is a question for the user.
            if (DistinctContentCount(members) < 2) continue;

            groups.Add(new TitleDuplicateGroup(
                DisplayKey(members[0]),
                members.OrderByDescending(f => f.SizeBytes).ThenBy(f => f.FullPath).ToList()));
        }

        return groups
            .OrderByDescending(g => g.ReclaimableBytes)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The group a particular file belongs to, or null when it has no twin.</summary>
    public static TitleDuplicateGroup? GroupFor(
        IEnumerable<MediaFile> files, MediaFile file, Func<MediaFile, string>? categoryOf = null) =>
        Find(files, categoryOf).FirstOrDefault(g => g.Files.Any(f => ReferenceEquals(f, file)));

    /// <summary>
    /// What makes two entries the same content, or null when the file cannot be compared
    /// on this basis at all.
    ///
    /// Something has to be known about it: a file with no title has nothing to match on,
    /// and an extra ("Behind the scenes") shares its name with every other extra in the
    /// library without being the same thing. A programme additionally has to say which
    /// episode it is — see the class summary.
    /// </summary>
    private static string? KeyOf(MediaFile file, string category)
    {
        if (file.IsExtra || CategoryResolver.IsExtra(category)) return null;
        if (file.Kind is not (MediaKind.Audio or MediaKind.Video)) return null;

        var title = Normalise(file.EffectiveTitle);
        if (title.Length == 0) return null;

        if (IsProgramme(file, category))
        {
            // Same show, same season, same episode — and a double episode is a different
            // thing again from either of the episodes it holds.
            if (file is not { Season: { } s, Episode: { } e }) return null;
            var run = file.EpisodeEnd is { } last && last > e ? $"-{last:000}" : "";

            // No year: the year read off an episode's file name is the show's, the
            // season's or the broadcast's depending on who named it, and two copies of one
            // episode routinely disagree about it.
            return $"tv|{title}|S{s:000}E{e:000}{run}";
        }

        return $"{file.Kind}|{title}|{file.Year}";
    }

    /// <summary>
    /// True when the file should be judged as an episode: it is filed as a programme, or
    /// it carries the numbering that only a programme has.
    /// </summary>
    private static bool IsProgramme(MediaFile file, string category) =>
        category is CategoryResolver.TvShow ||
        file.VideoCategory is VideoCategory.TvShow ||
        file is { Season: not null, Episode: not null };

    private static string DisplayKey(MediaFile file)
    {
        var title = file.EffectiveTitle.Trim();
        if (file.NumberingDisplay is { Length: > 0 } numbering) return $"{title} — {numbering}";
        return file.Year is { } year ? $"{title} ({year})" : title;
    }

    /// <summary>
    /// How many genuinely different files are in the set. Anything without a hash counts as
    /// distinct: not knowing what it is is not evidence that it is the same as its neighbour.
    /// </summary>
    private static int DistinctContentCount(IReadOnlyList<MediaFile> files)
    {
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unhashed = 0;
        foreach (var file in files)
            if (file.HasHash) hashes.Add(file.Sha256);
            else unhashed++;
        return hashes.Count + unhashed;
    }

    private static readonly Regex Punctuation = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <summary>
    /// A title stripped down to what it says: case, punctuation and spacing set aside, so
    /// "The Italian Job" and "the italian job." land in the same group.
    /// </summary>
    public static string Normalise(string? title) =>
        Punctuation.Replace((title ?? string.Empty).Trim(), " ").Trim().ToLowerInvariant();
}
