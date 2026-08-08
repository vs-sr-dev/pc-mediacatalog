using MediaCatalog.Core.Classification;
using MediaCatalog.Core.Duplicates;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Storage;

namespace MediaCatalog.Core.Consolidation;

/// <summary>What sort of decision a job needs before anything can move.</summary>
public enum AutoJobKind
{
    /// <summary>One file, no other copy of it anywhere. Nothing to decide.</summary>
    Single,

    /// <summary>Several copies, all byte-for-byte identical. Keep one; the choice is free.</summary>
    ExactCopies,

    /// <summary>
    /// Several genuinely different files all claiming to be the same content — two rips of
    /// one film. Which to keep is a real question, and answering it costs work.
    /// </summary>
    Rivals
}

/// <summary>
/// One piece of content and every catalogued file claiming to be it.
/// </summary>
/// <param name="Distinct">
/// The files grouped by what they actually are: one list per distinct set of bytes. A file
/// with no hash is its own group, since not knowing what it is is not evidence that it is
/// the same as its neighbour.
/// </param>
public record AutoJob(
    string Key, string Display, string Category, IReadOnlyList<MediaFile> Files,
    IReadOnlyList<IReadOnlyList<MediaFile>> Distinct)
{
    public AutoJobKind Kind =>
        Files.Count == 1 ? AutoJobKind.Single
        : Distinct.Count == 1 ? AutoJobKind.ExactCopies
        : AutoJobKind.Rivals;

    /// <summary>One file standing for each distinct piece of content.</summary>
    public IReadOnlyList<MediaFile> Representatives => Distinct.Select(d => d[0]).ToList();

    /// <summary>Everything in the job that is not <paramref name="keeper"/>.</summary>
    public List<MediaFile> Others(MediaFile keeper) =>
        Files.Where(f => !ReferenceEquals(f, keeper)).ToList();
}

/// <summary>A file the run will not touch, and the plain reason why.</summary>
public record AutoReview(MediaFile File, string Reason);

/// <summary>
/// Works out what a hands-off consolidation run would do, without doing any of it.
///
/// The rules are the ones a person would follow. A file that does not yet say what it is
/// cannot be filed anywhere and is set aside for the user rather than guessed at. A file
/// with no other copy is simply filed. Copies that are byte-for-byte identical decide
/// themselves — any of them will do, so the one already in the library wins and the rest go.
/// Only genuinely different files claiming to be the same thing are a question, and that
/// question is settled by looking: fingerprints to confirm they really are the same content,
/// quality and size to choose between them, and a decode to make sure the survivor is not
/// the damaged one.
///
/// Everything here is a decision about what *should* happen. Carrying it out — moving,
/// fingerprinting, decoding, deleting — belongs to the caller, which owns the progress bar
/// and the undo stack.
/// </summary>
public static class AutoConsolidator
{
    /// <summary>
    /// Sort the catalogue into jobs that can be done and files that cannot, giving a reason
    /// for every one of the latter.
    /// </summary>
    /// <param name="categoryOf">The effective category, so a category the user set decides.</param>
    public static (List<AutoJob> Jobs, List<AutoReview> Review) Plan(
        IEnumerable<MediaFile> files, AppSettings settings, Func<MediaFile, string> categoryOf)
    {
        var review = new List<AutoReview>();
        var byKey = new Dictionary<string, List<MediaFile>>(StringComparer.OrdinalIgnoreCase);
        var categoryOfKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var category = categoryOf(file);

            // Extras are never filed on their own account: they belong beside the film or
            // episode they are an extra of, and travel with it when that moves.
            if (CategoryResolver.IsExtra(category) || file.IsExtra) continue;
            // Audio, video, and whatever a plugin has taught the program to catalogue. What
            // is left out is only the genuinely unknown: a file nothing claims to understand
            // cannot be filed anywhere, because nothing knows what it is.
            if (file.Kind is not (MediaKind.Audio or MediaKind.Video or MediaKind.Other)) continue;
            if (!File.Exists(file.FullPath)) continue;

            if (MissingInformation(file, category, settings) is { } problem)
            {
                review.Add(new AutoReview(file, problem));
                continue;
            }

            var key = ContentKey(file, category, settings.MatchForCategory(category));
            if (!byKey.TryGetValue(key, out var members))
            {
                byKey[key] = members = new List<MediaFile>();
                categoryOfKey[key] = category;
            }
            members.Add(file);
        }

        var jobs = new List<AutoJob>();
        foreach (var (key, members) in byKey)
        {
            var category = categoryOfKey[key];
            jobs.Add(new AutoJob(
                key, Describe(members[0]), category, members, GroupByContent(members)));
        }

        // Easiest first: the files that need no decision at all are the ones where stopping
        // part-way still leaves the library better than it was.
        return (jobs.OrderBy(j => (int)j.Kind)
                    .ThenBy(j => j.Display, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                review.OrderBy(r => r.Reason, StringComparer.Ordinal)
                      .ThenBy(r => r.File.FullPath, StringComparer.OrdinalIgnoreCase)
                      .ToList());
    }

    /// <summary>
    /// What a file is still missing before it can be filed, or null when it has everything.
    ///
    /// These are not warnings to be overridden — each of them names something that decides
    /// where the file goes, so filing without it means filing it in the wrong place and
    /// having to do it again.
    /// </summary>
    public static string? MissingInformation(MediaFile file, string category, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(category) || category == CategoryResolver.Unknown)
            return "no category — nothing says what this file is";

        if (string.IsNullOrWhiteSpace(settings.ConsolidationDirFor(category)))
            return $"no consolidation folder is set for '{category}'";

        if (string.IsNullOrWhiteSpace(file.EffectiveTitle))
            return "no title — a title is what decides the folder it goes in";

        if (category == CategoryResolver.TvShow && file is not { Season: not null, Episode: not null })
            return "no season/episode — an episode with no number cannot be filed in a season";

        if (category == CategoryResolver.Movie && file.Year is null)
            return "no year — a film's folder is named \"Title (Year)\"";

        if (file.Integrity == IntegrityStatus.IncompleteDownload)
            return "still downloading";

        return null;
    }

    /// <summary>
    /// The copies grouped by what they actually are. Byte-identical files land together;
    /// anything unhashed stands alone, because an unknown is not a match.
    /// </summary>
    private static List<IReadOnlyList<MediaFile>> GroupByContent(IReadOnlyList<MediaFile> files)
    {
        var byHash = new Dictionary<string, List<MediaFile>>(StringComparer.OrdinalIgnoreCase);
        var alone = new List<IReadOnlyList<MediaFile>>();

        foreach (var file in files)
        {
            if (!file.HasHash) { alone.Add(new[] { file }); continue; }
            if (!byHash.TryGetValue(file.Sha256, out var set))
                byHash[file.Sha256] = set = new List<MediaFile>();
            set.Add(file);
        }

        return byHash.Values.Cast<IReadOnlyList<MediaFile>>().Concat(alone).ToList();
    }

    /// <summary>
    /// What makes two files the same content: the title with the numbering for a programme,
    /// the title with the year for anything else. Deliberately the same judgement the
    /// possible-duplicates list makes, so the two never disagree about what is a duplicate.
    /// </summary>
    public static string ContentKey(MediaFile file, string category)
    {
        var title = TitleDuplicateFinder.Normalise(file.EffectiveTitle);

        if (category == CategoryResolver.TvShow && file is { Season: { } s, Episode: { } e })
        {
            var run = file.EpisodeEnd is { } last && last > e ? $"-{last:000}" : "";
            return $"tv|{title}|S{s:000}E{e:000}{run}";
        }

        return $"{category}|{title}|{file.Year}";
    }

    /// <summary>
    /// The same, under whatever the user has said counts as two copies of one thing for this
    /// category. The built-in judgement — the title with its numbering — is the default and
    /// what every catalogue used before there was anything to say.
    /// </summary>
    public static string ContentKey(MediaFile file, string category, DuplicateMatch match) =>
        match switch
        {
            // Name alone, wherever the two files are. For somebody who files by hand and
            // trusts their own naming, this is the whole of the question.
            DuplicateMatch.SameName =>
                $"name|{Path.GetFileNameWithoutExtension(file.FileName).ToLowerInvariant()}",

            // The strictest reading: the same bytes and nothing else. A file nobody has
            // hashed is not known to match anything, so it stands alone.
            DuplicateMatch.SameContent =>
                file.HasHash ? $"sha|{file.Sha256.ToLowerInvariant()}" : $"unhashed|{file.FullPath}",

            _ => ContentKey(file, category)
        };

    private static string Describe(MediaFile file)
    {
        var title = file.EffectiveTitle.Trim();
        if (file.NumberingDisplay is { Length: > 0 } numbering) return $"{title} — {numbering}";
        return file.Year is { } year ? $"{title} ({year})" : title;
    }

    // --- Choosing between copies --------------------------------------------

    /// <summary>
    /// Which of several copies to keep when they are all the same bytes: the one already in
    /// the library if there is one, since keeping that means moving nothing at all.
    /// Failing that, one on the drive the library lives on, so the move is a rename rather
    /// than a copy. Failing that, any of them — they are identical, and any further test
    /// would be answering a question that has no answer.
    /// </summary>
    public static MediaFile PreferLibraryCopy(
        IReadOnlyList<MediaFile> copies, string? destinationDir, AppSettings settings)
    {
        var filed = copies.FirstOrDefault(c =>
            ConsolidationPlanner.IsCorrectlyFiled(
                c, CategoryResolver.Effective(c, settings), settings));
        if (filed != null) return filed;

        var underRoot = copies.FirstOrDefault(c =>
            ConsolidationPlanner.IsUnderConsolidationRoot(c, settings));
        if (underRoot != null) return underRoot;

        if (!string.IsNullOrWhiteSpace(destinationDir))
        {
            var sameDrive = copies.FirstOrDefault(c => OnSameDrive(c.FullPath, destinationDir));
            if (sameDrive != null) return sameDrive;
        }

        return copies[0];
    }

    private static bool OnSameDrive(string path, string other)
    {
        try
        {
            return string.Equals(Path.GetPathRoot(Path.GetFullPath(path)),
                Path.GetPathRoot(Path.GetFullPath(other)), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// The order to try genuinely different copies in.
    ///
    /// Best picture first. Then, among copies of the same quality, the <em>longest</em>: two
    /// copies of one film that differ in length differ by the credits, an ident or a scene,
    /// and the longer one is the copy that has them — it holds everything the shorter one
    /// holds and something besides. That only applies at equal quality, which is why it comes
    /// second: a longer copy at a worse resolution is not the better copy, it is a worse copy
    /// with more of itself.
    ///
    /// Then, among copies of the same quality <em>and</em> the same length, the smallest: at
    /// one resolution and one running time the extra bytes are padding rather than detail, and
    /// the smaller one is the cheaper thing to be wrong about.
    ///
    /// Anything a deep check has already condemned goes to the back rather than being
    /// dropped: if every copy is damaged the user still needs to be shown something.
    /// </summary>
    public static List<MediaFile> RankCandidates(IEnumerable<MediaFile> copies) =>
        copies
            .OrderBy(f => f.Integrity == IntegrityStatus.Corrupt ? 1 : 0)
            .ThenByDescending(QualityOf)
            .ThenByDescending(LengthRank)
            .ThenBy(f => f.SizeBytes)
            .ThenBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// A file's length in whole seconds, for ordering. Rounded so that two copies which are
    /// the same length to the second are not separated by a rounding difference, which would
    /// take the choice away from the size rule below it for no reason.
    /// </summary>
    private static long LengthRank(MediaFile file) =>
        file.DurationSeconds > 0 ? (long)Math.Round(file.DurationSeconds) : 0;

    /// <summary>
    /// A file's picture quality: the height ffprobe measured when something has looked, and
    /// otherwise whatever the name claims. The measurement is worth far more than the claim
    /// — a file called "1080p" is only ever a file called "1080p" — so it is preferred.
    /// </summary>
    public static int QualityOf(MediaFile file) =>
        file.Quality > 0 ? file.Quality : QualityRanker.ResolutionScore(file.FileName);
}
