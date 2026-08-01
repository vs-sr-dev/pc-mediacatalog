using System.Net.Http;

namespace MediaCatalog.Core.Imdb;

/// <param name="BytesTotal">Zero when the server declines to say how big the file is.</param>
public record ImdbDownloadProgress(long BytesRead, long BytesTotal);

/// <summary>
/// Fetches IMDb's <c>title.basics.tsv.gz</c> so the user needn't go and find it by hand.
/// The download is well over a hundred megabytes, so it is streamed straight to disk —
/// never held in memory — into a <c>.part</c> file that is only moved into place once it
/// has arrived whole. An interrupted download therefore leaves nothing to trip over.
/// </summary>
public static class ImdbDownloader
{
    // One client for the process: a new HttpClient per download exhausts sockets.
    private static readonly HttpClient Http = new()
    {
        // The file is large and some connections are slow; the cancellation token is what
        // stops this, not a stopwatch.
        Timeout = Timeout.InfiniteTimeSpan
    };

    /// <summary>
    /// Download <paramref name="url"/> to <paramref name="destPath"/>. Returns the path
    /// written. Throws on anything that means the file did not arrive intact.
    /// </summary>
    public static async Task<string> DownloadAsync(
        string url,
        string destPath,
        IProgress<ImdbDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("No download address is configured.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException($"'{url}' is not a usable http(s) address.");

        var partPath = destPath + ".part";
        try
        {
            using var response = await Http.GetAsync(
                uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? 0;
            progress?.Report(new ImdbDownloadProgress(0, total));

            await using (var body = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(partPath, FileMode.Create, FileAccess.Write,
                             FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                long read = 0;
                int n;
                while ((n = await body.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    progress?.Report(new ImdbDownloadProgress(read, total));
                }
            }

            // A proxy or a moved dataset answers with an HTML page rather than an error,
            // and a "successful" download of a login form is worse than a failed one.
            if (IsGzipName(destPath) && !await LooksGzippedAsync(partPath, ct))
                throw new InvalidOperationException(
                    "What arrived is not a gzip file — the address may have moved, or a proxy " +
                    "answered instead. Check the download address in Settings.");

            if (File.Exists(destPath)) File.Replace(partPath, destPath, null);
            else File.Move(partPath, destPath);

            progress?.Report(new ImdbDownloadProgress(
                new FileInfo(destPath).Length, new FileInfo(destPath).Length));
            return destPath;
        }
        catch
        {
            Relocation.FileDeleter.TryDeleteQuietly(partPath);
            throw;
        }
    }

    private static bool IsGzipName(string path) =>
        path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gzip's two-byte magic number, 0x1F 0x8B.</summary>
    private static async Task<bool> LooksGzippedAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var header = new byte[2];
            return await stream.ReadAsync(header, ct) == 2 && header[0] == 0x1F && header[1] == 0x8B;
        }
        catch { return false; }
    }
}
