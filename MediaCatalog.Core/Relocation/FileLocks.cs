using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MediaCatalog.Core.Relocation;

/// <summary>
/// Answers "which application has this file open?" using the Restart Manager — the same
/// mechanism installers use to tell you what to close. Best-effort: an empty list means
/// nothing was found, not that the file is definitely free.
/// </summary>
public static class FileLocks
{
    /// <summary>Friendly names of the processes currently holding <paramref name="path"/> open.</summary>
    public static IReadOnlyList<string> ProcessesUsing(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path))
            return Array.Empty<string>();
        try { return Query(path); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>A ready-made sentence naming the holders, or null when none are known.</summary>
    public static string? DescribeHolders(string path)
    {
        var holders = ProcessesUsing(path);
        if (holders.Count == 0) return null;
        return holders.Count == 1
            ? $"It is open in {holders[0]}."
            : "It is open in " + string.Join(", ", holders.Take(holders.Count - 1)) +
              " and " + holders[^1] + ".";
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> Query(string path)
    {
        var key = Guid.NewGuid().ToString("N");
        if (RmStartSession(out var session, 0, key) != 0) return Array.Empty<string>();

        try
        {
            if (RmRegisterResources(session, 1, new[] { path }, 0, null, 0, null) != 0)
                return Array.Empty<string>();

            uint needed = 0, count = 0, reason = 0;
            // First call reports how many entries there are; zero means nobody holds it.
            var result = RmGetList(session, out needed, ref count, null, ref reason);
            if (result != ERROR_MORE_DATA || needed == 0) return Array.Empty<string>();

            var info = new RM_PROCESS_INFO[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, info, ref reason) != 0)
                return Array.Empty<string>();

            var names = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var name = Describe(info[i]);
                if (name != null && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    names.Add(name);
            }
            return names;
        }
        finally
        {
            RmEndSession(session);
        }
    }

    private static string? Describe(RM_PROCESS_INFO info)
    {
        var friendly = info.strAppName?.Trim();
        try
        {
            using var process = Process.GetProcessById(info.Process.dwProcessId);
            var exe = process.ProcessName;
            return string.IsNullOrWhiteSpace(friendly) || friendly.Equals(exe, StringComparison.OrdinalIgnoreCase)
                ? $"{exe} (PID {info.Process.dwProcessId})"
                : $"{friendly} — {exe} (PID {info.Process.dwProcessId})";
        }
        catch (ArgumentException)
        {
            // The process ended between the query and now.
            return string.IsNullOrWhiteSpace(friendly) ? null : friendly;
        }
        catch (InvalidOperationException) { return friendly; }
    }

    // --- Restart Manager interop -------------------------------------------

    private const int ERROR_MORE_DATA = 234;
    private const int CCH_RM_MAX_APP_NAME = 255;
    private const int CCH_RM_MAX_SVC_NAME = 63;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);
}
