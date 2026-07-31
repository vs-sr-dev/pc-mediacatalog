using System.Runtime.Versioning;

namespace MediaCatalog.Core.Relocation;

/// <summary>
/// Puts back what the Recycle Bin holds. Windows exposes no plain API for this, so the
/// shell's own "restore" verb is invoked through the Shell Automation object. Best-effort
/// by nature: the caller is told which files could not be put back so it can point the
/// user at the bin.
/// </summary>
public static class RecycleBin
{
    private const int RecycleBinFolder = 10;   // ssfBITBUCKET

    // Canonical verb first; the rest cover localised menus where the canonical name is
    // not accepted (accelerator ampersands are stripped before comparing).
    private static readonly string[] RestoreVerbs =
    {
        "undelete", "restore", "ripristina", "wiederherstellen", "restaurer",
        "restaurar", "herstellen", "przywróć", "восстановить", "还原", "復元"
    };

    /// <summary>
    /// Restore files by the path they had when they were deleted. Returns the paths that
    /// are back in place; anything missing from the result is still in the bin.
    /// </summary>
    public static IReadOnlyList<string> Restore(IEnumerable<string> originalPaths)
    {
        var wanted = new HashSet<string>(originalPaths, StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0 || !OperatingSystem.IsWindows()) return Array.Empty<string>();

        try { return RestoreCore(wanted); }
        catch { return wanted.Where(File.Exists).ToList(); }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> RestoreCore(HashSet<string> wanted)
    {
        var restored = new List<string>();

        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType == null) return restored;

        dynamic? shell = Activator.CreateInstance(shellType);
        if (shell == null) return restored;

        dynamic bin = shell.NameSpace(RecycleBinFolder);
        if (bin == null) return restored;

        foreach (dynamic item in bin.Items())
        {
            // The bin lists items under their original name; the folder they came from is
            // the item's parent path, which the shell exposes as its "path" column.
            string name = item.Name;
            string from = bin.GetDetailsOf(item, 1);   // "Original Location"
            if (string.IsNullOrEmpty(from)) continue;

            var original = Path.Combine(from, name);
            var match = wanted.FirstOrDefault(w =>
                string.Equals(w, original, StringComparison.OrdinalIgnoreCase) ||
                // The bin hides known extensions, so fall back to matching without one.
                string.Equals(Path.GetDirectoryName(w), from, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileNameWithoutExtension(w), name, StringComparison.OrdinalIgnoreCase));
            if (match == null) continue;

            if (InvokeRestore(item) && File.Exists(match))
            {
                restored.Add(match);
                wanted.Remove(match);
            }
            if (wanted.Count == 0) break;
        }

        return restored;
    }

    [SupportedOSPlatform("windows")]
    private static bool InvokeRestore(dynamic item)
    {
        foreach (dynamic verb in item.Verbs())
        {
            string name = ((string)verb.Name).Replace("&", "").Trim();
            if (!RestoreVerbs.Any(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            try { verb.DoIt(); return true; } catch { return false; }
        }

        // No verb matched (an unusual locale): try the canonical name directly.
        try { item.InvokeVerb("undelete"); return true; } catch { return false; }
    }
}
