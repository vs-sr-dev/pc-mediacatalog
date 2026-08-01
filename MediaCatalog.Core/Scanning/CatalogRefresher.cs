using MediaCatalog.Core.Classification;
using MediaCatalog.Core.Imdb;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Naming;
using MediaCatalog.Core.Storage;

namespace MediaCatalog.Core.Scanning;

public record RefreshProgress(int Done, int Total, string Current, string Phase = "Refreshing catalogue");

/// <param name="Refreshed">Entries brought up to the current feature set.</param>
/// <param name="Skipped">Entries that already had everything this build knows about.</param>
/// <param name="Linked">Extras attached to the film/episode they belong to.</param>
/// <param name="Pruned">Entries dropped because they now sit under an exclusion.</param>
/// <param name="Shared">Values copied between identical files (titles, season/episode).</param>
/// <param name="Numbered">TV files that gained a season/episode from the re-parse.</param>
/// <param name="Adopted">Files that took a category or title off a folder rule.</param>
/// <param name="RulesRetired">Folder rules removed once their files were labelled outright.</param>
/// <param name="Verified">What the title verification pass managed, when one ran.</param>
public record RefreshReport(
    int Refreshed, int Skipped, int Linked, int Pruned, int Shared = 0,
    int Numbered = 0, int Adopted = 0, int RulesRetired = 0, VerifyReport? Verified = null);

/// <summary>
/// Brings an existing catalogue up to date with features added since it was built —
/// new title parsing, the extras categories, file linking — without re-walking the
/// drives or re-hashing anything. Entries already stamped with the current feature
/// version are left alone, so a refresh over a large library is near-instant.
///
/// Two things are re-derived regardless of the stamp, because both are about the entry
/// still being incomplete rather than about which build wrote it: TV files with no
/// episode number (the parsing rules keep improving) and titles nothing has confirmed.
/// </summary>
public static class CatalogRefresher
{
    /// <summary>
    /// Bumped whenever a release derives something new from data already in the
    /// catalogue. Entries below it are re-derived on the next refresh.
    /// 1 = classification/titles, 2 = extras detection + linking,
    /// 3 = compact 4-digit episode codes, season/title read from the folder path.
    /// </summary>
    public const int CurrentFeatureVersion = 3;

    /// <summary>True when this entry predates the current feature set.</summary>
    public static bool NeedsRefresh(MediaFile file) =>
        file.FeatureVersion < CurrentFeatureVersion || LacksNumbering(file);

    /// <summary>
    /// A programme with no episode number is worth another go every time: the parsing
    /// rules gain cases release by release, and this is what makes a refresh pick them up
    /// without the user having to re-scan the drive.
    /// </summary>
    public static bool LacksNumbering(MediaFile file) =>
        file.Kind == MediaKind.Video &&
        file.VideoCategory is VideoCategory.TvShow &&
        (file.Season is null || file.Episode is null);

    /// <summary>How many catalogue entries a refresh would actually touch.</summary>
    public static int CountStale(Catalog catalog) =>
        catalog.Files.Count(NeedsRefresh);

    /// <summary>How many entries would have their title looked up.</summary>
    public static int CountUnverified(Catalog catalog) =>
        catalog.Files.Count(f => TitleVerifier.NeedsVerification(f) || TitleVerifier.NeedsYear(f));

    /// <summary>
    /// Re-derive what can be re-derived. When <paramref name="verifier"/> is supplied,
    /// unconfirmed titles are also checked against IMDb (and TMDb as a fallback) and
    /// missing years filled in.
    /// </summary>
    public static async Task<RefreshReport> RefreshAsync(
        Catalog catalog,
        AppSettings settings,
        TitleVerifier? verifier = null,
        IProgress<RefreshProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Entries now covered by an exclusion (a newly enabled system-folder rule, say)
        // are dropped rather than re-derived.
        var pruned = catalog.Files.RemoveAll(f =>
            settings.IsPathExcluded(f.FullPath) || settings.IsExtensionIgnored(f.Extension));

        var stale = catalog.Files.Where(NeedsRefresh).ToList();
        var skipped = catalog.Files.Count - stale.Count;
        var numbered = 0;

        for (var i = 0; i < stale.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = stale[i];
            progress?.Report(new RefreshProgress(i, stale.Count, file.FileName));

            var hadNumbering = file is { Season: not null, Episode: not null };

            // Re-derive everything that comes from the name and path. User overrides,
            // TMDb results, hashes and fingerprints are all stored elsewhere on the
            // entry and survive untouched.
            MediaClassifier.Classify(file);
            DuplicateMetadata.ApplyFolderTitle(file, settings);
            file.FeatureVersion = CurrentFeatureVersion;

            if (!hadNumbering && file is { Season: not null, Episode: not null }) numbered++;
        }

        // Folder rules become plain facts on each file, and retire once they have been.
        var (adopted, retired) = MigrateFolderRules(catalog, settings);

        // Both of these need the whole catalogue: an extra's owner — or the copy that
        // knows which episode this is — may be an entry that was already up to date and
        // therefore skipped above.
        var shared = DuplicateMetadata.Propagate(catalog.Files);
        var linked = ExtraLinker.Link(catalog.Files);

        VerifyReport? verified = null;
        if (verifier != null)
        {
            var verifyProgress = new Progress<VerifyProgress>(p =>
                progress?.Report(new RefreshProgress(p.Done, p.Total, p.Current, p.Phase)));
            verified = await verifier.VerifyAsync(catalog.Files, verifyProgress, ct);
        }

        catalog.RebuildIndex();
        progress?.Report(new RefreshProgress(stale.Count, stale.Count, string.Empty));
        return new RefreshReport(stale.Count, skipped, linked, pruned, shared,
            numbered, adopted, retired, verified);
    }

    /// <summary>
    /// Write what the folder rules say onto the files themselves, then drop the rules
    /// that have been fully absorbed.
    ///
    /// Everything known about a file belongs in the catalogue, so a rule is a migration
    /// step rather than a permanent fixture. A rule matching nothing yet — a folder that
    /// has not been scanned — is kept, since removing it would lose the instruction.
    /// </summary>
    private static (int Adopted, int Retired) MigrateFolderRules(Catalog catalog, AppSettings settings)
    {
        var adopted = 0;
        var retired = 0;

        foreach (var rule in settings.FolderCategoryRules.ToList())
        {
            if (string.IsNullOrWhiteSpace(rule.Path) || string.IsNullOrWhiteSpace(rule.Category))
            {
                settings.FolderCategoryRules.Remove(rule);
                retired++;
                continue;
            }

            var covered = catalog.Files.Where(f => Covers(rule.Path, rule.IncludeSubdirectories, f.FullPath))
                .ToList();
            if (covered.Count == 0) continue;

            foreach (var file in covered)
            {
                // A category set on the file itself is more specific than the folder's,
                // and was chosen later, so it stands.
                if (!string.IsNullOrWhiteSpace(file.CategoryOverride)) continue;
                file.CategoryOverride = rule.Category;
                adopted++;
            }

            settings.FolderCategoryRules.Remove(rule);
            retired++;
        }

        foreach (var rule in settings.FolderTitleRules.ToList())
        {
            if (string.IsNullOrWhiteSpace(rule.Path) || string.IsNullOrWhiteSpace(rule.Title))
            {
                settings.FolderTitleRules.Remove(rule);
                retired++;
                continue;
            }

            var covered = catalog.Files.Where(f => Covers(rule.Path, rule.IncludeSubdirectories, f.FullPath))
                .ToList();
            if (covered.Count == 0) continue;

            adopted += TitleUpdater.Set(covered, rule.Title, manual: true);
            settings.FolderTitleRules.Remove(rule);
            retired++;
        }

        return (adopted, retired);
    }

    private static bool Covers(string rulePath, bool includeSubdirectories, string fullPath)
    {
        var root = rulePath.TrimEnd('\\', '/');
        if (root.Length == 0) return false;

        if (!includeSubdirectories)
            return string.Equals(Path.GetDirectoryName(fullPath), root, StringComparison.OrdinalIgnoreCase);

        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
