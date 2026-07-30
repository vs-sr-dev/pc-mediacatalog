using System.IO;
using MediaCatalog.Core.Scanning;

namespace MediaCatalog.App.Infrastructure;

/// <summary>
/// Watches a set of root folders for newly created/renamed media files and reports them.
/// The callback fires on a background thread; callers marshal to the UI thread themselves.
/// </summary>
public sealed class NewFileWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Action<string> _onNewMediaFile;

    public NewFileWatcher(IEnumerable<string> roots, Action<string> onNewMediaFile)
    {
        _onNewMediaFile = onNewMediaFile;
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024
                };
                w.Created += (_, e) => Handle(e.FullPath);
                w.Renamed += (_, e) => Handle(e.FullPath);
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or FileNotFoundException)
            {
                // Skip roots we can't watch (removed drive, permissions, …).
            }
        }
    }

    public bool IsWatching => _watchers.Count > 0;

    private void Handle(string path)
    {
        var ext = Path.GetExtension(path);
        if (MediaExtensions.IsMedia(ext))
            _onNewMediaFile(path);
    }

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
        }
        _watchers.Clear();
    }
}
