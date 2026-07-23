using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Naming;

/// <summary>A proposed rename for one file, within its current folder.</summary>
public class RenameProposal
{
    public required MediaFile File { get; init; }
    public required string CurrentName { get; init; }
    public required string ProposedName { get; init; }

    /// <summary>Full destination path the rename would produce.</summary>
    public required string ProposedPath { get; init; }

    public bool WillChange =>
        !string.Equals(CurrentName, ProposedName, StringComparison.Ordinal);
}

public record RenameResult(bool Success, string Message, string NewPath);

/// <summary>
/// Builds and applies rename proposals. Renames happen in place (same folder); the
/// file is never moved to another directory here — that's what relocation is for.
/// </summary>
public static class RenameService
{
    /// <summary>
    /// Compute a proposal for each file. Files with no confident scheme, or already
    /// matching the scheme, come back with <see cref="RenameProposal.WillChange"/> = false.
    /// </summary>
    public static List<RenameProposal> BuildProposals(IEnumerable<MediaFile> files)
    {
        var proposals = new List<RenameProposal>();
        foreach (var f in files)
        {
            var proposed = NamingScheme.GenerateFileName(f);
            if (string.IsNullOrEmpty(proposed))
                continue; // not enough metadata — leave it alone

            var dir = Path.GetDirectoryName(f.FullPath) ?? string.Empty;
            proposals.Add(new RenameProposal
            {
                File = f,
                CurrentName = f.FileName,
                ProposedName = proposed,
                ProposedPath = Path.Combine(dir, proposed)
            });
        }
        return proposals;
    }

    /// <summary>
    /// Rename a single file. Handles case-only renames on Windows and avoids
    /// clobbering an unrelated existing file by disambiguating the name.
    /// </summary>
    public static RenameResult Apply(RenameProposal proposal)
    {
        var file = proposal.File;
        if (!File.Exists(file.FullPath))
            return new RenameResult(false, "File no longer exists.", file.FullPath);

        if (!proposal.WillChange)
            return new RenameResult(true, "Already named correctly.", file.FullPath);

        var dir = Path.GetDirectoryName(file.FullPath) ?? string.Empty;

        try
        {
            var target = proposal.ProposedPath;

            // A pure case change ("film.MKV" -> "film.mkv") looks like a collision with
            // itself on Windows; handle it directly rather than disambiguating.
            var caseOnly = string.Equals(file.FullPath, target, StringComparison.OrdinalIgnoreCase)
                           && !string.Equals(file.FullPath, target, StringComparison.Ordinal);

            if (!caseOnly && File.Exists(target))
                target = MakeUniquePath(target);

            File.Move(file.FullPath, target, overwrite: false);

            file.FullPath = target;
            file.FileName = Path.GetFileName(target);
            return new RenameResult(true, "Renamed.", target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RenameResult(false, $"Rename failed: {ex.Message}", file.FullPath);
        }
    }

    private static string MakeUniquePath(string desired)
    {
        var dir = Path.GetDirectoryName(desired)!;
        var stem = Path.GetFileNameWithoutExtension(desired);
        var ext = Path.GetExtension(desired);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
