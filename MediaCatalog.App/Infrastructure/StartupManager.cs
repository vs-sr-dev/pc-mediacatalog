using System.Diagnostics;
using Microsoft.Win32;

namespace MediaCatalog.App.Infrastructure;

/// <summary>Registers/unregisters the app to run at Windows sign-in (per-user Run key).</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MediaCatalog";
    private const string TrayArgument = App.TrayArgument;

    /// <param name="startInTray">
    /// Launch hidden in the notification area rather than opening the window — the usual
    /// choice for something that runs at sign-in to watch for new files.
    /// </param>
    public static void Apply(bool enabled, bool startInTray = true)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;

            if (enabled)
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(ValueName,
                        startInTray ? $"\"{exe}\" {TrayArgument}" : $"\"{exe}\"");
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* startup registration is best-effort */ }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }
}
