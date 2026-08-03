namespace MediaCatalog.Core.Relocation;

/// <summary>
/// Turns a failed move into something the user can act on.
///
/// "Relocation failed: Access to the path is denied" tells somebody with a full drive, an
/// unplugged disk and a read-only folder exactly nothing about which of the three they are
/// looking at. Every one of those has a different answer, so each is named — along with the
/// path it is about, and, when a file is being held open, the applications holding it.
/// </summary>
public static class RelocationDiagnosis
{
    // The Win32 codes worth telling apart, as they arrive inside an IOException's HResult.
    private const int ErrorAccessDenied = 0x05;
    private const int ErrorNotReady = 0x15;         // drive not ready — no disc, disconnected
    private const int ErrorWriteProtect = 0x13;
    private const int ErrorSharingViolation = 0x20;
    private const int ErrorLockViolation = 0x21;
    private const int ErrorHandleDiskFull = 0x27;
    private const int ErrorDiskFull = 0x70;
    private const int ErrorFilenameExcedRange = 0xCE;
    private const int ErrorNetworkUnreachable = 0x40;   // network name no longer available

    /// <summary>
    /// What went wrong, in a sentence the user can do something about.
    /// </summary>
    /// <param name="destination">Where the file was going — a folder or a full path.</param>
    public static string Explain(Exception exception, string source, string destination)
    {
        // A destination that is not there at all explains itself better than any exception
        // message, and is far and away the commonest reason a move fails outright.
        if (RootProblem(destination) is { } rootProblem) return rootProblem;

        return exception switch
        {
            PathTooLongException => $"the path would be too long for Windows: {destination}",

            UnauthorizedAccessException => IsReadOnly(source)
                ? $"'{Path.GetFileName(source)}' is read-only and the read-only flag could not " +
                  "be cleared — check the file's properties, or run as an administrator."
                : $"there is no permission to write to {DirectoryOf(destination)}. Check the " +
                  "folder's security settings, or run as an administrator.",

            IOException io => ExplainIo(io, source, destination),

            _ => exception.Message.TrimEnd('.') + "."
        };
    }

    private static string ExplainIo(IOException exception, string source, string destination)
    {
        switch (exception.HResult & 0xFFFF)
        {
            case ErrorDiskFull:
            case ErrorHandleDiskFull:
                return $"there is not enough free space on {DriveOf(destination)}" +
                       FreeSpaceNote(destination) + ".";

            case ErrorNotReady:
                return $"{DriveOf(destination)} is not ready — the drive may be disconnected, " +
                       "asleep, or have no disc in it.";

            case ErrorWriteProtect:
                return $"{DriveOf(destination)} is write-protected.";

            case ErrorSharingViolation:
            case ErrorLockViolation:
                var holders = FileLocks.ProcessesUsing(source);
                return holders.Count > 0
                    ? $"'{Path.GetFileName(source)}' is open in {string.Join(", ", holders)} — " +
                      "close it and try again."
                    : $"'{Path.GetFileName(source)}' is open in another application.";

            case ErrorFilenameExcedRange:
                return $"the path would be too long for Windows: {destination}";

            case ErrorNetworkUnreachable:
                return $"the network location {DriveOf(destination)} is no longer available.";

            case ErrorAccessDenied:
                return $"there is no permission to write to {DirectoryOf(destination)}.";

            default:
                return exception.Message.TrimEnd('.') + ".";
        }
    }

    /// <summary>
    /// Why the destination cannot be written to at all — an unplugged drive, a share that
    /// has gone — or null when there is nothing wrong with it. Checked before a move as
    /// well as after a failure, since it is worth saying before any bytes are copied.
    /// </summary>
    public static string? RootProblem(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) return "no destination folder was given.";

        string root;
        try { root = Path.GetPathRoot(Path.GetFullPath(destination)) ?? string.Empty; }
        catch (Exception ex) { return $"'{destination}' is not a usable path: {ex.Message}"; }

        if (root.Length == 0)
            return $"'{destination}' has no drive or share to sit on.";

        try
        {
            if (Directory.Exists(root)) return null;
        }
        catch { /* fall through to the message below */ }

        var trimmed = root.TrimEnd('\\', '/');
        return trimmed.StartsWith(@"\\", StringComparison.Ordinal)
            ? $"the network location {trimmed} is not available — check it is reachable."
            : $"drive {trimmed} is not available — connect it, or choose another destination.";
    }

    /// <summary>
    /// True when there is plainly not enough room for <paramref name="bytes"/> at the
    /// destination. Only reports what it is sure of: an unanswerable question is not a
    /// reason to refuse a copy that might have worked.
    /// </summary>
    public static string? SpaceProblem(string destination, long bytes)
    {
        if (bytes <= 0) return null;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destination));
            if (string.IsNullOrEmpty(root)) return null;

            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free >= bytes) return null;
            return $"there is not enough free space on {root.TrimEnd('\\', '/')}: " +
                   $"{Bytes(bytes)} needed, {Bytes(free)} free.";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string FreeSpaceNote(string destination)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destination));
            if (string.IsNullOrEmpty(root)) return string.Empty;
            return $" ({Bytes(new DriveInfo(root).AvailableFreeSpace)} free)";
        }
        catch { return string.Empty; }
    }

    private static bool IsReadOnly(string path)
    {
        try { return File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly); }
        catch { return false; }
    }

    private static string DirectoryOf(string destination)
    {
        try
        {
            return Directory.Exists(destination)
                ? destination
                : Path.GetDirectoryName(destination) ?? destination;
        }
        catch { return destination; }
    }

    private static string DriveOf(string destination)
    {
        try { return (Path.GetPathRoot(Path.GetFullPath(destination)) ?? destination).TrimEnd('\\', '/'); }
        catch { return destination; }
    }

    private static string Bytes(long value)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{value} B" : $"{size:0.##} {units[unit]}";
    }
}
