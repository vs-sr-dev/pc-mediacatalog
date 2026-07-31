using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MediaCatalog.Core.Relocation;

/// <summary>
/// Works out which physical volume a path lives on. Drive letters are not enough: the
/// same volume can be mounted at several letters or inside a folder (<c>D:\Mount\Data</c>),
/// and two letters can be different volumes on one disk. Windows answers this properly
/// through the volume GUID, which is what this asks for.
/// </summary>
public static class VolumeInfo
{
    /// <summary>
    /// A stable identifier for the volume holding <paramref name="path"/> — the volume
    /// GUID name where Windows will give one, otherwise the mount point or path root.
    /// </summary>
    public static string? VolumeOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var full = Path.GetFullPath(path);
            if (!OperatingSystem.IsWindows()) return Path.GetPathRoot(full);

            var mountPoint = MountPointOf(full);
            if (mountPoint == null) return Path.GetPathRoot(full);

            var guid = new StringBuilder(64);
            return GetVolumeNameForVolumeMountPoint(mountPoint, guid, guid.Capacity)
                ? guid.ToString()
                : mountPoint;
        }
        catch { return null; }
    }

    /// <summary>
    /// True when both paths are on the same volume, so a move between them is a rename
    /// rather than a copy. Unknown answers are treated as "not the same", which only
    /// costs a copy — the safe direction to be wrong in.
    /// </summary>
    public static bool SameVolume(string a, string b)
    {
        var volumeA = VolumeOf(a);
        var volumeB = VolumeOf(b);
        return volumeA != null && volumeB != null &&
               string.Equals(volumeA, volumeB, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The mount point a path sits under — "D:\" for most things, but "C:\Mount\Data\" for
    /// a volume mounted into a folder. Walks up to an existing directory first, since the
    /// destination folder may not have been created yet.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? MountPointOf(string fullPath)
    {
        var probe = fullPath;
        while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe) && !File.Exists(probe))
        {
            var parent = Path.GetDirectoryName(probe);
            if (parent == null || parent == probe) break;
            probe = parent;
        }
        if (string.IsNullOrEmpty(probe)) return null;

        var buffer = new StringBuilder(512);
        return GetVolumePathName(probe, buffer, buffer.Capacity) ? buffer.ToString() : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(
        string lpszFileName, StringBuilder lpszVolumePathName, int cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string lpszVolumeMountPoint, StringBuilder lpszVolumeName, int cchBufferLength);
}
