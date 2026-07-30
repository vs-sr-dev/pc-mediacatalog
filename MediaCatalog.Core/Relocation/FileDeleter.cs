using System.Runtime.InteropServices;

namespace MediaCatalog.Core.Relocation;

/// <summary>Outcome of a delete request.</summary>
public record DeleteResult(int Deleted, int Failed, List<string> Errors);

/// <summary>
/// Deletes files either to the Recycle Bin (recoverable, the default) or permanently.
/// Recycling goes through the shell so the files land in the bin exactly as they would
/// from Explorer; permanent deletion is a plain unlink.
/// </summary>
public static class FileDeleter
{
    public static DeleteResult Delete(IEnumerable<string> paths, bool toRecycleBin)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var errors = new List<string>();
        if (list.Count == 0) return new DeleteResult(0, 0, errors);

        if (toRecycleBin && OperatingSystem.IsWindows())
            return Recycle(list, errors);

        var deleted = 0;
        foreach (var path in list)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                deleted++;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
        return new DeleteResult(deleted, list.Count - deleted, errors);
    }

    /// <summary>Send everything to the Recycle Bin in one shell operation.</summary>
    private static DeleteResult Recycle(List<string> paths, List<string> errors)
    {
        var existing = paths.Where(File.Exists).ToList();
        if (existing.Count > 0)
        {
            // pFrom is a double-null-terminated list of null-separated paths.
            var from = string.Join('\0', existing) + "\0\0";
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = from,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT
            };

            var code = SHFileOperation(ref op);
            if (code != 0)
                errors.Add($"The Recycle Bin refused the operation (shell error {code}).");
        }

        // Whatever is still on disk did not make it to the bin.
        var failed = paths.Where(File.Exists).ToList();
        foreach (var f in failed.Take(10))
            errors.Add($"{Path.GetFileName(f)}: could not be recycled.");
        return new DeleteResult(paths.Count - failed.Count, failed.Count, errors);
    }

    // --- Shell interop ----------------------------------------------------

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

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
