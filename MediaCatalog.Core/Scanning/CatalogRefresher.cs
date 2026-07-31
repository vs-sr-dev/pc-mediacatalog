using MediaCatalog.Core.Classification;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Storage;

namespace MediaCatalog.Core.Scanning;

public record RefreshProgress(int Done, int Total, string Current);

/// <param name="Refreshed">Entries brought up to the current feature set.</param>
/// <param name="Skipped">Entries that already had everything this build knows about.</param>
/// <param name="Linked">Extras attached to the film/episode they belong to.</param>
/// <param name="Pruned">Entries dropped because they now sit under an exclusion.</param>
/// <param name="Shared">Values copied between identical files (titles, season/episode).</param>
public record RefreshReport(int Refreshed, int Skipped, int Linked, int Pruned, int Shared = 0);

/// <summary>
/// Brings an existing catalogue up to date with features added since it was built —
/// new title parsing, the extras categories, file linking — without re-walking the
/// drives or re-hashing anything. Entries already stamped with the current feature
/// version are left alone, so a refresh over a large library is near-instant.
/// </summary>
public static class CatalogRefresher
{
    /// <summary>
    /// Bumped whenever a release derives something new from data already in the
    /// catalogue. Entries below it are re-derived on the next refresh.
    /// 1 = classification/titles, 2 = extras detection + linking.
    /// </summary>
    public const int CurrentFeatureVersion = 2;

    /// <summary>True when this entry predates the current feature set.</summary>
    public static bool NeedsRefresh(MediaFile file) =>
        file.FeatureVersion < CurrentFeatureVersion;

    /// <summary>How many catalogue entries a refresh would actually touch.</summary>
    public static int CountStale(Catalog catalog) =>
        catalog.Files.Count(NeedsRefresh);

    public static RefreshReport Refresh(
        Catalog catalog,
        AppSettings settings,
        IProgress<RefreshProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Entries now covered by an exclusion (a newly enabled system-folder rule, say)
        // are dropped rather than re-derived.
        var pruned = catalog.Files.RemoveAll(f =>
            settings.IsPathExcluded(f.FullPath) || settings.IsExtensionIgnored(f.Extension));

        var stale = catalog.Files.Where(NeedsRefresh).ToList();
        var skipped = catalog.Files.Count - stale.Count;

        for (var i = 0; i < stale.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = stale[i];
            progress?.Report(new RefreshProgress(i, stale.Count, file.FileName));

            // Re-derive everything that comes from the name and path. User overrides,
            // TMDb results, hashes and fingerprints are all stored elsewhere on the
            // entry and survive untouched.
            MediaClassifier.Classify(file);
            DuplicateMetadata.ApplyFolderTitle(file, settings);
            file.FeatureVersion = CurrentFeatureVersion;
        }

        // Both of these need the whole catalogue: an extra's owner — or the copy that
        // knows which episode this is — may be an entry that was already up to date and
        // therefore skipped above.
        var shared = DuplicateMetadata.Propagate(catalog.Files);
        var linked = ExtraLinker.Link(catalog.Files);

        catalog.RebuildIndex();
        progress?.Report(new RefreshProgress(stale.Count, stale.Count, string.Empty));
        return new RefreshReport(stale.Count, skipped, linked, pruned, shared);
    }
}
