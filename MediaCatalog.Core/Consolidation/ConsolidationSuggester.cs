using MediaCatalog.Core.Classification;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Storage;

namespace MediaCatalog.Core.Consolidation;

/// <summary>A proposed consolidation move for one file.</summary>
public class ConsolidationSuggestion
{
    public required MediaFile File { get; init; }
    public required string Category { get; init; }
    public required string CurrentPath { get; init; }
    public required string ProposedPath { get; init; }

    /// <summary>A different file already exists at the destination path.</summary>
    public bool NameCollision { get; set; }

    /// <summary>
    /// This exact file is already in the consolidation location, so the source is a
    /// redundant copy that can be deleted rather than moved.
    /// </summary>
    public bool DuplicateAtDestination { get; set; }

    /// <summary>Recommended to include in the operation by default.</summary>
    public bool Recommended { get; set; } = true;

    /// <summary>Human-readable reasons/warnings (quality, validation, collisions).</summary>
    public string Note { get; set; } = string.Empty;

    public void AddNote(string note) =>
        Note = string.IsNullOrEmpty(Note) ? note : Note + "; " + note;
}

/// <summary>
/// Scans catalogue entries and proposes consolidation moves: where each file is now,
/// where it would go, and any problems (name collisions, or a lower-quality copy that a
/// better one supersedes). TV files must be TMDb-validated and have a season/episode to
/// be recommended.
/// </summary>
public static class ConsolidationSuggester
{
    public static List<ConsolidationSuggestion> Suggest(
        IEnumerable<MediaFile> files,
        AppSettings settings,
        Func<MediaFile, string> categoryOf)
    {
        var candidates = files
            .Where(f => settings.ConsolidationDirFor(categoryOf(f)) is { Length: > 0 })
            .ToList();

        var suggestions = new List<ConsolidationSuggestion>();

        // Group by content identity so we can prefer the best-quality copy of each item.
        foreach (var group in candidates.GroupBy(f => IdentityKey(f, categoryOf(f))))
        {
            var members = group.ToList();
            var best = members.Count > 1 ? QualityRanker.Best(members) : members[0];

            foreach (var file in members)
            {
                var category = categoryOf(file);
                var destDir = ConsolidationPlanner.PlanDirectory(file, category, settings);
                if (destDir == null) continue;

                var proposed = System.IO.Path.Combine(destDir,
                    ConsolidationPlanner.PlanFileName(file, category, settings));
                var s = new ConsolidationSuggestion
                {
                    File = file,
                    Category = category,
                    CurrentPath = file.FullPath,
                    ProposedPath = proposed
                };

                if (members.Count > 1 && !ReferenceEquals(file, best))
                {
                    s.Recommended = false;
                    s.AddNote("lower quality than preferred copy");
                }

                if (category == CategoryResolver.TvShow)
                {
                    if (!file.TitleVerified) { s.Recommended = false; s.AddNote("TV title not validated"); }
                    if (file.Season is null || file.Episode is null)
                    { s.Recommended = false; s.AddNote("missing season/episode"); }
                }

                if (CategoryResolver.IsExtra(category))
                {
                    if (string.IsNullOrEmpty(file.LinkedFileId))
                    {
                        // Without an owner we only have the file's own name to file it
                        // under, which would invent a folder — leave it to the user.
                        s.Recommended = false;
                        s.AddNote("extra with no linked film/show");
                    }
                    else s.AddNote("extra — files with its film/show");
                }

                // Something already occupies the target path. If it is the same file, this
                // copy is redundant and can be deleted instead of moved.
                if (!PathsEqual(proposed, file.FullPath) && System.IO.File.Exists(proposed))
                {
                    s.Recommended = false;
                    if (SameContent(proposed, file))
                    {
                        s.DuplicateAtDestination = true;
                        s.AddNote("already in the consolidation location");
                    }
                    else
                    {
                        s.NameCollision = true;
                        s.AddNote("name collision at destination");
                    }
                }

                // Already at the destination — nothing to do.
                if (PathsEqual(proposed, file.FullPath))
                {
                    s.Recommended = false;
                    s.AddNote("already in place");
                }

                suggestions.Add(s);
            }
        }

        return suggestions
            .OrderByDescending(s => s.Recommended)
            .ThenBy(s => s.ProposedPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string IdentityKey(MediaFile f, string category)
    {
        var title = (!string.IsNullOrWhiteSpace(f.TmdbName) ? f.TmdbName : f.ParsedTitle)
            .Trim().ToLowerInvariant();
        return category switch
        {
            CategoryResolver.TvShow => $"tv|{title}|{f.Season}|{f.Episode}",
            CategoryResolver.Movie => $"mv|{title}|{f.Year}",
            _ => "id|" + f.Id // no grouping for other categories
        };
    }

    /// <summary>
    /// Cheap "is this the same file" test for the listing: identical length. Anything
    /// actually deleted is hash-verified against the destination copy first.
    /// </summary>
    private static bool SameContent(string path, MediaFile file)
    {
        try
        {
            var info = new System.IO.FileInfo(path);
            return info.Exists && info.Length == file.SizeBytes;
        }
        catch { return false; }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
            StringComparison.OrdinalIgnoreCase);
}
