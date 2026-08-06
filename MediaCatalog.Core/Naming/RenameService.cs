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

/// <param name="Subtitles">
/// Subtitle files renamed along with the video, oldest name first. They are matched to
/// their film by name and by nothing else, so a rename that left them behind would break
/// the only link there is.
/// </param>
public record RenameResult(
    bool Success, string Message, string NewPath,
    IReadOnlyList<(string From, string To)>? Subtitles = null);

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
            var proposal = BuildAnyProposal(f, categoryOf?.Invoke(f));
            if (proposal != null) proposals.Add(proposal);
        }
        return proposals;
    }

    /// <summary>
    /// The best rename this file can be offered, whatever it is filed as: the naming scheme
    /// when it has something to say, and failing that a name built from the title in front of
    /// what the file is already called.
    ///
    /// The fallback is the point. A featurette, a file still filed as Other, a programme with
    /// no episode number — the scheme declines to name all of them, and until now that meant
    /// correcting such a file's title changed the catalogue and left the name on disk saying
    /// the old one. Anything can be renamed; nothing is beyond it for want of a category.
    /// </summary>
    /// <param name="previousTitle">
    /// What the file went by before, when the caller knows. Given one, a title that appears
    /// in the name is swapped for the new one in place, which keeps far more of what the file
    /// was called than putting the new title in front of it would.
    /// </param>
    public static RenameProposal? BuildAnyProposal(
        MediaFile file, string? category = null, string? previousTitle = null) =>
        BuildProposal(file, category)
        ?? BuildTitleSwap(file, previousTitle, file.EffectiveTitle)
        ?? BuildTitledName(file);

    /// <summary>
    /// A name that puts the file's title in front of what it is already called:
    /// "bts.mkv" under the title *Yes Minister* becomes "Yes Minister - bts.mkv".
    ///
    /// Null when there is no title to lead with, or when the name already opens with it —
    /// which is what stops a second run adding the title a second time.
    /// </summary>
    public static RenameProposal? BuildTitledName(MediaFile file)
    {
        var title = Sanitize((file.EffectiveTitle ?? string.Empty).Trim());
        if (title.Length == 0) return null;

        var stem = Path.GetFileNameWithoutExtension(file.FileName);
        if (stem.StartsWith(title, StringComparison.OrdinalIgnoreCase)) return null;

        // Whatever the name says beyond the title — the featurette's own name, usually —
        // with the title taken out of the middle of it if that is where it was.
        var rest = stem;
        var at = rest.IndexOf(title, StringComparison.OrdinalIgnoreCase);
        if (at >= 0) rest = rest[..at] + rest[(at + title.Length)..];
        rest = rest.Trim(' ', '-', '–', '—', '_', '.');

        var proposed = Sanitize(rest.Length == 0 ? title : $"{title} - {rest}") +
                       Path.GetExtension(file.FileName);

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

        // Read before the move: once the video has gone there is nothing left to find its
        // subtitles by, since the only thing tying them together is the name.
        var companions = Relocation.SubtitleCompanion.For(file.FullPath);

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

            var subtitles = Relocation.SubtitleCompanion.MoveBeside(companions, target);
            return new RenameResult(true, "Renamed.", target, subtitles);
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
