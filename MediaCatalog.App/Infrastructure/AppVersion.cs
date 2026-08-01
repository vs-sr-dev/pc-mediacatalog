using System.Diagnostics;
using System.Reflection;

namespace MediaCatalog.App.Infrastructure;

/// <summary>
/// The running build's version numbers, read from the assembly rather than written out
/// anywhere a second time — so bumping the version in one place is enough.
/// </summary>
public static class AppVersion
{
    /// <summary>The version people talk about, e.g. "1.8".</summary>
    public static string Product =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            .Split('+')[0]                       // drop any source-control suffix
        ?? File;

    /// <summary>The Windows file version, e.g. "0.0.1.8".</summary>
    public static string File
    {
        get
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    if (!string.IsNullOrWhiteSpace(info.FileVersion)) return info.FileVersion;
                }
                catch { /* fall through to the assembly's own number */ }
            }
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        }
    }

    /// <summary>Who built it, for the About box.</summary>
    public const string Credits = "Collaborative effort between Samuele, Phreak, and Claude";
}
