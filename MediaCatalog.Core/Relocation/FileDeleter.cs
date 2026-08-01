using System.Runtime.InteropServices;

namespace MediaCatalog.Core.Relocation;

/// <param name="LockedBy">Applications holding the file open, when that is why it failed.</param>
/// <param name="AccessDenied">True when the failure looks like a permissions problem, so
/// retrying with administrative rights is worth offering.</param>
public record DeleteFailure(string Path, string Reason, IReadOnlyList<string> LockedBy, bool AccessDenied)
{
    /// <summary>A full explanation, including who is holding the file and what to try.</summary>
    public string Describe()
    {
        var text = $"{System.IO.Path.GetFileName(Path)} — {Reason}";
        if (LockedBy.Count > 0)
            text += "\n    Open in: " + string.Join(", ", LockedBy);
        text += "\n    " + Path;
        return text;
    }
}

/// <summary>Outcome of a delete request.</summary>
public record DeleteResult(int Deleted, List<DeleteFailure> Failures)
{
    public int Failed => Failures.Count;

    /// <summary>True when at least one failure might be solved by elevating.</summary>
    public bool AnyAccessDenied => Failures.Any(f => f.AccessDenied);

    public IReadOnlyList<string> Errors => Failures.Select(f => f.Describe()).ToList();
}

/// <summary>
/// The one place in the program that removes a file from disk. Everything that deletes —
/// the results grid, the duplicate manager, a verified move discarding its original, a
/// rolled-back copy — comes through here, so a fix to how deleting behaves is made once.
///
/// Files go to the Recycle Bin (recoverable) or permanently, as asked. A refusal is not
/// taken at face value: the read-only attribute is cleared before the attempt and again
/// after a failure, and anything still refusing is reported with the reason and — when the
/// file is locked — the applications holding it open, so the caller can say exactly what
/// to close or offer to retry with administrative rights.
/// </summary>
public static class FileDeleter
{
    public static DeleteResult Delete(IEnumerable<string> paths, bool toRecycleBin) =>
        Delete(paths, toRecycleBin, clearReadOnly: true);

    public static DeleteResult Delete(IEnumerable<string> paths, bool toRecycleBin, bool clearReadOnly)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var deleted = 0;
        var failures = new List<DeleteFailure>();

        foreach (var path in list)
        {
            var failure = DeleteOne(path, toRecycleBin, clearReadOnly);
            if (failure == null) deleted++;
            else failures.Add(failure);
        }

        return new DeleteResult(deleted, failures);
    }

    /// <summary>
    /// Delete one file. Returns null when it is gone (including when it was never there,
    /// which is the outcome the caller wanted), or the failure explaining why not.
    /// </summary>
    public static DeleteFailure? DeleteOne(string path, bool toRecycleBin, bool clearReadOnly = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!File.Exists(path)) return null;   // already gone is a success

        // Read-only is the commonest reason a delete refuses, and the easiest to undo, so
        // the attribute goes before the attempt rather than after the first failure.
        if (clearReadOnly) ClearReadOnly(path);

        var failure = TryDelete(path, toRecycleBin);

        // The attribute may have been set again between clearing it and deleting, or the
        // file may have been unreadable a moment ago; one more go costs nothing.
        if (failure != null && clearReadOnly && ClearReadOnly(path))
            failure = TryDelete(path, toRecycleBin);

        return failure;
    }

    /// <summary>
    /// Convenience for housekeeping deletes — temporary files, a half-written copy being
    /// rolled back — where there is nothing useful to say about a failure.
    /// </summary>
    public static bool TryDeleteQuietly(string path) =>
        DeleteOne(path, toRecycleBin: false) == null;

    /// <summary>Delete one file; returns null on success or a failure describing why not.</summary>
    private static DeleteFailure? TryDelete(string path, bool toRecycleBin)
    {
        try
        {
            if (toRecycleBin && OperatingSystem.IsWindows())
            {
                var code = Recycle(path);
                if (code != 0 || File.Exists(path))
                    return Failure(path, ShellError(code), accessDenied: code == DE_ACCESSDENIEDSRC);
                return null;
            }

            File.Delete(path);
            return File.Exists(path) ? Failure(path, "the file is still there after deleting it.", false) : null;
        }
        catch (UnauthorizedAccessException ex)
        {
            return Failure(path, $"access denied ({ex.Message.TrimEnd('.')}).", accessDenied: true);
        }
        catch (IOException ex)
        {
            return Failure(path, $"the file is in use ({ex.Message.TrimEnd('.')}).", accessDenied: false);
        }
        catch (Exception ex)
        {
            return Failure(path, ex.Message.TrimEnd('.') + ".", accessDenied: false);
        }
    }

    private static DeleteFailure Failure(string path, string reason, bool accessDenied)
    {
        var holders = FileLocks.ProcessesUsing(path);
        // Something holding the file explains the refusal better than "access denied" does.
        if (holders.Count > 0 && !reason.Contains("in use", StringComparison.OrdinalIgnoreCase))
            reason = "the file is open in another application.";
        return new DeleteFailure(path, reason, holders, accessDenied && holders.Count == 0);
    }

    /// <summary>
    /// Clear the read-only attribute. True only when it was set and has now gone, so the
    /// caller can tell "worth retrying" from "that was never the problem".
    /// </summary>
    public static bool ClearReadOnly(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if (!attributes.HasFlag(FileAttributes.ReadOnly)) return false;
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            return true;
        }
        catch { return false; }
    }

    private static string ShellError(int code) => code switch
    {
        0 => "the Recycle Bin reported success but the file is still there.",
        DE_ACCESSDENIEDSRC => "access denied.",
        DE_PATHTOODEEP => "the path is too long for the Recycle Bin.",
        DE_ROOTDIR => "the item is a root directory.",
        ERROR_DISK_FULL => "there is not enough room in the Recycle Bin.",
        _ => $"the Recycle Bin refused it (shell error {code})."
    };

    // --- Shell interop ----------------------------------------------------

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    private const int DE_ACCESSDENIEDSRC = 0x78;
    private const int DE_PATHTOODEEP = 0x79;
    private const int DE_ROOTDIR = 0x7A;
    private const int ERROR_DISK_FULL = 0x70;

    private static int Recycle(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",   // double-null-terminated list
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT
        };
        return SHFileOperation(ref op);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
