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
    /// <param name="categoryOf">
    /// The effective category of a file, when the caller knows it. Without one the
    /// automatically detected category is used, which ignores anything the user has set.
    /// </param>
    public static List<RenameProposal> BuildProposals(
        IEnumerable<MediaFile> files, Func<MediaFile, string>? categoryOf = null)
    {
        var proposals = new List<RenameProposal>();
        foreach (var f in files)
        {
            var proposal = BuildProposal(f, categoryOf?.Invoke(f));
            if (proposal != null) proposals.Add(proposal);
        }
        return proposals;
    }

    /// <summary>
    /// The proposal for one file, or null when the naming scheme has nothing better to
    /// offer than the name it already has.
    /// </summary>
    public static RenameProposal? BuildProposal(MediaFile file, string? category = null)
    {
        var proposed = category == null
            ? NamingScheme.GenerateFileName(file)
            : NamingScheme.GenerateFileName(file, category);
        if (string.IsNullOrEmpty(proposed)) return null;

        var dir = Path.GetDirectoryName(file.FullPath) ?? string.Empty;
        return new RenameProposal
        {
            File = file,
            CurrentName = file.FileName,
            ProposedName = proposed,
            ProposedPath = Path.Combine(dir, proposed)
        };
    }

    /// <summary>
    /// A rename that swaps one title for another inside the name the file already has:
    /// "the italian job 1969.avi" becomes "The Italian Job 1969.avi".
    ///
    /// For the categories the naming scheme declines to name — an extra, a file still
    /// filed as Other, a programme with no episode number — this is what "rename to match
    /// the corrected title" can honestly mean. It changes the part of the name that was
    /// wrong and leaves everything else the file was called alone, rather than inventing a
    /// name nobody asked for. Null when the old title isn't in the name to begin with, or
    /// when either title is missing.
    /// </summary>
    public static RenameProposal? BuildTitleSwap(MediaFile file, string? oldTitle, string? newTitle)
    {
        var previous = (oldTitle ?? string.Empty).Trim();
        var current = (newTitle ?? string.Empty).Trim();
        if (previous.Length == 0 || current.Length == 0) return null;
        if (string.Equals(previous, current, StringComparison.Ordinal)) return null;

        var stem = Path.GetFileNameWithoutExtension(file.FileName);
        var at = stem.IndexOf(previous, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        var swapped = stem[..at] + current + stem[(at + previous.Length)..];
        var proposed = Sanitize(swapped) + Path.GetExtension(file.FileName);
        if (proposed.Length <= Path.GetExtension(file.FileName).Length) return null;

        var dir = Path.GetDirectoryName(file.FullPath) ?? string.Empty;
        return new RenameProposal
        {
            File = file,
            CurrentName = file.FileName,
            ProposedName = proposed,
            ProposedPath = Path.Combine(dir, proposed)
        };
    }

    private static string Sanitize(string stem)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(stem.Select(c => invalid.Contains(c) ? ' ' : c).ToArray());
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned.TrimEnd('.', ' ');
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
