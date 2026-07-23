namespace MediaCatalog.Core.Tools;

/// <summary>
/// Locates the external binaries (ffmpeg, ffprobe, fpcalc) the advanced features need.
/// Search order per tool:
///   1. explicit user override (ToolSettings)
///   2. a "tools" folder next to the application executable   ← the drop-in-files method
///   3. the system PATH
///   4. a few common install locations (winget/choco/manual)
/// </summary>
public class ExternalTools
{
    public string? FfmpegPath { get; private set; }
    public string? FfprobePath { get; private set; }
    public string? FpcalcPath { get; private set; }

    public bool HasFfmpeg => !string.IsNullOrEmpty(FfmpegPath);
    public bool HasFfprobe => !string.IsNullOrEmpty(FfprobePath);
    public bool HasFpcalc => !string.IsNullOrEmpty(FpcalcPath);

    /// <summary>ffmpeg + ffprobe present — video fingerprinting and deep checks are possible.</summary>
    public bool CanDoVideo => HasFfmpeg && HasFfprobe;

    /// <summary>fpcalc present — audio fingerprinting is possible.</summary>
    public bool CanDoAudio => HasFpcalc;

    public static ExternalTools Resolve(ToolSettings settings)
    {
        return new ExternalTools
        {
            FfmpegPath = Locate("ffmpeg", settings.FfmpegPath),
            FfprobePath = Locate("ffprobe", settings.FfprobePath),
            FpcalcPath = Locate("fpcalc", settings.FpcalcPath)
        };
    }

    public string MissingSummary()
    {
        var missing = new List<string>();
        if (!HasFfmpeg) missing.Add("ffmpeg.exe");
        if (!HasFfprobe) missing.Add("ffprobe.exe");
        if (!HasFpcalc) missing.Add("fpcalc.exe");
        return missing.Count == 0 ? "All tools found." : "Missing: " + string.Join(", ", missing);
    }

    private static string? Locate(string toolName, string overridePath)
    {
        // 1. Manual override wins.
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        var exeName = OperatingSystem.IsWindows() ? toolName + ".exe" : toolName;

        // 2. tools\ next to our own executable.
        var appDir = AppContext.BaseDirectory;
        foreach (var candidate in new[]
        {
            Path.Combine(appDir, "tools", exeName),
            Path.Combine(appDir, exeName)
        })
        {
            if (File.Exists(candidate)) return candidate;
        }

        // 3. PATH.
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }

        // 4. Common install locations.
        foreach (var candidate in CommonLocations(exeName))
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static IEnumerable<string> CommonLocations(string exeName)
    {
        var roots = new List<string>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        // winget shim folder
        roots.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links"));
        // chocolatey
        roots.Add(@"C:\ProgramData\chocolatey\bin");
        // typical manual extracts
        roots.Add(@"C:\ffmpeg\bin");
        roots.Add(Path.Combine(programFiles, "ffmpeg", "bin"));
        roots.Add(@"C:\Chromaprint");

        foreach (var r in roots)
            yield return Path.Combine(r, exeName);
    }
}
