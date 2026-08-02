using System.Text.RegularExpressions;
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
/// The evidence is the title and the year — plus the season and episode for a programme,
/// so a whole series is not swept into one group. Byte-identical copies are left out: they
/// are exact duplicates, which the hash already found and which have their own manager.
/// </summary>
public static class TitleDuplicateFinder
{
    /// <summary>
    /// Group the catalogue by what each file says it is. Groups of one — the normal case —
    /// are dropped, leaving only the sets worth a second look.
    /// </summary>
    public static List<TitleDuplicateGroup> Find(IEnumerable<MediaFile> files)
    {
        var groups = new List<TitleDuplicateGroup>();

        foreach (var group in files.Where(Qualifies).GroupBy(KeyOf, StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToList();
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
    public static TitleDuplicateGroup? GroupFor(IEnumerable<MediaFile> files, MediaFile file) =>
        Find(files).FirstOrDefault(g => g.Files.Any(f => ReferenceEquals(f, file)));

    /// <summary>
    /// Files worth comparing at all: something has to be known about them. A file with no
    /// title has nothing to match on, and an extra ("Behind the scenes") shares its name
    /// with every other extra in the library without being the same thing.
    /// </summary>
    private static bool Qualifies(MediaFile file) =>
        !file.IsExtra &&
        file.Kind is MediaKind.Audio or MediaKind.Video &&
        Normalise(file.EffectiveTitle).Length > 0;

    /// <summary>
    /// What makes two entries the same content: kind, title, year, and — for anything with
    /// numbering — the season and episode, so episode 1 and episode 2 of one series are not
    /// taken for copies of each other.
    /// </summary>
    private static string KeyOf(MediaFile file)
    {
        var numbering = file is { Season: { } s, Episode: { } e } ? $"|S{s:000}E{e:000}" : "";
        return $"{file.Kind}|{Normalise(file.EffectiveTitle)}|{file.Year}{numbering}";
    }

    private static string DisplayKey(MediaFile file)
    {
        var title = file.EffectiveTitle.Trim();
        if (file is { Season: { } s, Episode: { } e }) return $"{title} — S{s:00}E{e:00}";
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
    private static string Normalise(string? title) =>
        Punctuation.Replace((title ?? string.Empty).Trim(), " ").Trim().ToLowerInvariant();
}
