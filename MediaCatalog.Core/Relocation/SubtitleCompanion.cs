namespace MediaCatalog.Core.Relocation;

/// <summary>One subtitle file that belongs to a video, and where it would go.</summary>
/// <param name="Path">Where the subtitle is now.</param>
/// <param name="Suffix">
/// Whatever the subtitle's name carries beyond the video's — ".eng", ".en.forced", or
/// nothing at all. Kept as it is, so a rename changes the half that names the film and
/// leaves the half that names the language alone.
/// </param>
public record SubtitleFile(string Path, string Suffix)
{
    public string Extension => System.IO.Path.GetExtension(Path);

    /// <summary>What this subtitle is called once its video is called <paramref name="videoName"/>.</summary>
    public string NameBeside(string videoName) =>
        System.IO.Path.GetFileNameWithoutExtension(videoName) + Suffix + Extension;

    public long SizeBytes
    {
        get { try { return new FileInfo(Path).Length; } catch { return 0; } }
    }
}

/// <summary>
/// The subtitles sitting beside a video file.
///
/// A film downloaded with its subtitles has them in the same folder under the same name —
/// <c>The Film.mkv</c> and <c>The Film.eng.srt</c> — and they are only useful while that
/// remains true. So a rename takes them with it, and a move either brings them along or
/// clears them away: a subtitle left behind after its film has gone is a file that can
/// never be matched to anything again.
///
/// Subtitles are not catalogued (they are not media), which is exactly why this exists:
/// nothing else in the program would ever notice them.
/// </summary>
public static class SubtitleCompanion
{
    /// <summary>Sidecar subtitle formats. Container-embedded subtitles are not files and are not here.</summary>
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".sub", ".idx", ".ssa", ".ass", ".vtt", ".smi", ".sami",
        ".sbv", ".usf", ".ttml", ".dfxp", ".lrc", ".mpl2", ".pjs", ".rt", ".stl"
    };

    public static bool IsSubtitle(string extension) =>
        !string.IsNullOrEmpty(extension) && Extensions.Contains(extension);

    // What may sit between the video's name and a language tag: "Film.eng.srt",
    // "Film - eng.srt", "Film_en.srt".
    private static readonly char[] Separators = { '.', '-', '_', ' ' };

    /// <summary>
    /// The longest tail a subtitle may carry beyond its video's name. A language tag is
    /// short — "en", "eng", "English (SDH) forced" at the very outside — and anything longer
    /// is far more likely to be a different title that happens to begin the same way.
    /// </summary>
    private const int LongestSuffix = 32;

    /// <summary>
    /// True when what a subtitle's name carries beyond the video's is a language tag rather
    /// than the rest of a different title.
    ///
    /// This is the whole difficulty. "The Film.mkv" and "The Film.eng.srt" belong together;
    /// "The Film.mkv" and "The Film 2.srt" emphatically do not — the second is the subtitle
    /// of "The Film 2.mkv", and renaming or deleting it along with the first film would be
    /// destroying somebody's file on the strength of a shared prefix.
    ///
    /// So a tail has to look like a tag: nothing at all (the same name), or a separator
    /// followed by something that starts with a letter. A tail that starts with a number is
    /// how sequels, parts and episodes are written, and is refused.
    /// </summary>
    private static bool IsLanguageTag(string suffix)
    {
        if (suffix.Length == 0) return true;                 // exactly the same name
        if (suffix.Length > LongestSuffix) return false;
        if (!Separators.Contains(suffix[0])) return false;   // "Films.srt" is not "Film"'s

        var tag = suffix.TrimStart(Separators);
        return tag.Length > 0 && char.IsLetter(tag[0]);
    }

    /// <summary>
    /// The subtitles belonging to <paramref name="videoPath"/>: same folder, and either the
    /// same name or the same name followed by a language tag.
    /// </summary>
    public static List<SubtitleFile> For(string videoPath)
    {
        var found = new List<SubtitleFile>();
        if (string.IsNullOrWhiteSpace(videoPath)) return found;

        var dir = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return found;

        var stem = Path.GetFileNameWithoutExtension(videoPath);
        if (stem.Length == 0) return found;

        string[] entries;
        try { entries = Directory.GetFiles(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return found; }

        foreach (var path in entries)
        {
            if (!IsSubtitle(Path.GetExtension(path))) continue;

            var name = Path.GetFileNameWithoutExtension(path);
            if (!name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;

            var suffix = name[stem.Length..];
            if (!IsLanguageTag(suffix)) continue;

            found.Add(new SubtitleFile(path, suffix));
        }

        return found;
    }

    /// <summary>True when the video has anything sitting beside it.</summary>
    public static bool AnyFor(string videoPath) => For(videoPath).Count > 0;

    /// <summary>
    /// Put the subtitles beside the video at its new name and place. Works for a rename in
    /// the same folder and for a move to another one alike, since to a subtitle they are
    /// the same thing happening. Returns what was moved, so the caller can put it back.
    /// </summary>
    /// <param name="companions">
    /// The subtitles as they were *before* the video moved — read them first, because by
    /// the time the video has gone there is no longer anything to find them by.
    /// </param>
    public static List<(string From, string To)> MoveBeside(
        IReadOnlyList<SubtitleFile> companions, string newVideoPath)
    {
        var moved = new List<(string, string)>();
        var dir = Path.GetDirectoryName(newVideoPath);
        if (string.IsNullOrEmpty(dir) || companions.Count == 0) return moved;

        var videoName = Path.GetFileName(newVideoPath);

        foreach (var subtitle in companions)
        {
            if (!File.Exists(subtitle.Path)) continue;

            var target = Path.Combine(dir, subtitle.NameBeside(videoName));
            if (string.Equals(target, subtitle.Path, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(target, subtitle.Path, StringComparison.Ordinal))
                continue;                                    // already exactly there

            try
            {
                Directory.CreateDirectory(dir);
                if (File.Exists(target) &&
                    !string.Equals(target, subtitle.Path, StringComparison.OrdinalIgnoreCase))
                    target = MakeUniquePath(target);

                File.Move(subtitle.Path, target, overwrite: false);
                moved.Add((subtitle.Path, target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                           NotSupportedException or PathTooLongException)
            {
                // A subtitle that will not move is not a reason to fail the film's move.
            }
        }

        return moved;
    }

    /// <summary>
    /// The same, reading the companions itself — for the ordinary case where the video has
    /// not moved yet.
    /// </summary>
    public static List<(string From, string To)> MoveBeside(string oldVideoPath, string newVideoPath) =>
        MoveBeside(For(oldVideoPath), newVideoPath);

    /// <summary>
    /// Put a copy of each subtitle beside the video's copy, for an operation that copied
    /// rather than moved. Returns what was written, so a rollback can remove it.
    /// </summary>
    public static List<string> CopyBeside(
        IReadOnlyList<SubtitleFile> companions, string newVideoPath)
    {
        var written = new List<string>();
        var dir = Path.GetDirectoryName(newVideoPath);
        if (string.IsNullOrEmpty(dir) || companions.Count == 0) return written;

        var videoName = Path.GetFileName(newVideoPath);

        foreach (var subtitle in companions)
        {
            if (!File.Exists(subtitle.Path)) continue;

            var target = Path.Combine(dir, subtitle.NameBeside(videoName));
            if (string.Equals(target, subtitle.Path, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                Directory.CreateDirectory(dir);
                if (File.Exists(target)) target = MakeUniquePath(target);
                File.Copy(subtitle.Path, target, overwrite: false);
                written.Add(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                           NotSupportedException or PathTooLongException)
            {
                // As above: a subtitle is never worth failing the film for.
            }
        }

        return written;
    }

    /// <summary>Undo a companion move, putting each subtitle back where it was.</summary>
    public static void MoveBack(IEnumerable<(string From, string To)> moved)
    {
        foreach (var (from, to) in moved)
        {
            try
            {
                if (File.Exists(to) && !File.Exists(from)) File.Move(to, from, overwrite: false);
            }
            catch { /* best effort: the film is back, which is the part that matters */ }
        }
    }

    /// <summary>
    /// Remove subtitles whose video has gone elsewhere without them. Recycled rather than
    /// destroyed: they are small, and the user may yet want them.
    /// </summary>
    public static int Delete(IReadOnlyList<SubtitleFile> companions, bool toRecycleBin = true)
    {
        if (companions.Count == 0) return 0;
        var result = FileDeleter.Delete(companions.Select(c => c.Path), toRecycleBin);
        return result.Deleted;
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
