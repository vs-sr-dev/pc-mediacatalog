using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaCatalog.Core.Imdb;

public record ImdbExtractProgress(long BytesRead, long BytesTotal, long LinesKept);

/// <param name="Kept">Titles written to the extract.</param>
/// <param name="Skipped">Rows dropped as generic, unnamed episodes.</param>
public record ImdbExtractReport(long Kept, long Skipped, string Path);

/// <summary>
/// Boils IMDb's <c>title.basics.tsv</c> down to the two columns this program actually
/// uses — primary title and year. The source is well over a gigabyte, so it is read a
/// line at a time and never held in memory; the result is a fraction of the size and
/// cheap to search or load.
///
/// Generic episode rows ("Episode #1.4", "Episode dated 3 May 1999") are dropped: they
/// are placeholders for untitled episodes and would only ever match by accident. So are
/// the broadcast timestamps that sit in the same column for some feeds — rows whose
/// "title" reads "22. sep. 2016 kl. 07:30" — which are a transmission slot rather than
/// the name of anything.
/// </summary>
public static class ImdbExtractor
{
    /// <summary>Placeholder names IMDb gives episodes that were never titled.</summary>
    private static readonly Regex GenericEpisode = new(
        @"^Episode\s*(?:#|dated\b|\d{1,4}(?!\d))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// A date, or a date and a time, standing where a title should be. Two forms are
    /// enough to catch the lot: a clock reference ("kl. 17:00", "12:30"), and a
    /// day-month-year opening ("21. jan. 2015", "4 feb 2015"). Nothing anyone would call a
    /// film matches either.
    /// </summary>
    private static readonly Regex Timestamp = new(
        @"^\d{1,2}\s*[.\-/]\s*\p{L}{3,}\.?\s*[.\-/]?\s*\d{4}\b" +   // 21. jan. 2015
        @"|^\d{1,2}\s*[.\-/]\s*\d{1,2}\s*[.\-/]\s*\d{4}\b" +        // 21.01.2015
        @"|\bkl\.?\s*\d{1,2}[:.]\d{2}\b" +                          // kl. 17:00
        @"|^\d{1,2}[:.]\d{2}(?::\d{2})?$",                          // 17:00
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>True when a row's primary title is a placeholder rather than a real name.</summary>
    public static bool IsGenericEpisodeTitle(string title) =>
        !string.IsNullOrWhiteSpace(title) && GenericEpisode.IsMatch(title.TrimStart());

    /// <summary>
    /// True when a row's primary title is a broadcast date or time rather than a name.
    /// These are worse than useless in the extract: they are numerous, they match nothing
    /// anyone searches for, and they make the file bigger for no purpose.
    /// </summary>
    public static bool IsTimestampTitle(string title) =>
        !string.IsNullOrWhiteSpace(title) && Timestamp.IsMatch(title.Trim());

    /// <summary>
    /// The source file to extract from: the plain TSV if it is there, otherwise the
    /// gzipped download, which is read without unpacking it first. Null when neither
    /// exists.
    /// </summary>
    public static string? FindSource(string tsvPath, string gzPath)
    {
        if (File.Exists(tsvPath)) return tsvPath;
        if (File.Exists(gzPath)) return gzPath;
        return null;
    }

    /// <summary>
    /// Read <paramref name="sourcePath"/> and write "title&lt;tab&gt;year" lines to
    /// <paramref name="destPath"/>. Writes to a temporary file and moves it into place,
    /// so an interrupted run never leaves a half-written extract behind.
    /// </summary>
    public static async Task<ImdbExtractReport> ExtractAsync(
        string sourcePath,
        string destPath,
        IProgress<ImdbExtractProgress>? progress = null,
        CancellationToken ct = default)
    {
        var total = new FileInfo(sourcePath).Length;
        var tmp = destPath + ".tmp";
        long kept = 0, skipped = 0;

        try
        {
            await using (var raw = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                             FileShare.Read, 1 << 20, useAsync: true))
            {
                // Progress is measured on the compressed stream, which is the only length
                // we know up front; it still moves smoothly from 0 to 100.
                await using var body = IsGzip(sourcePath)
                    ? new GZipStream(raw, CompressionMode.Decompress)
                    : (Stream)raw;

                using var reader = new StreamReader(body, Encoding.UTF8, false, 1 << 20);
                await using var writer = new StreamWriter(tmp, false, new UTF8Encoding(false), 1 << 20);

                var header = await reader.ReadLineAsync(ct);
                var (titleCol, yearCol) = ColumnsOf(header);

                var sinceReport = 0;
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    ct.ThrowIfCancellationRequested();

                    var fields = line.Split('\t');
                    if (fields.Length <= Math.Max(titleCol, yearCol)) { skipped++; continue; }

                    var title = fields[titleCol];
                    if (title.Length == 0 || title == NullField) { skipped++; continue; }
                    if (IsGenericEpisodeTitle(title)) { skipped++; continue; }
                    if (IsTimestampTitle(title)) { skipped++; continue; }

                    var year = fields[yearCol];
                    if (year == NullField) year = string.Empty;

                    await writer.WriteAsync(title);
                    await writer.WriteAsync('\t');
                    await writer.WriteLineAsync(year);
                    kept++;

                    if (++sinceReport >= 100_000)
                    {
                        sinceReport = 0;
                        progress?.Report(new ImdbExtractProgress(raw.Position, total, kept));
                    }
                }
            }

            if (File.Exists(destPath)) File.Replace(tmp, destPath, null);
            else File.Move(tmp, destPath);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }

        progress?.Report(new ImdbExtractProgress(total, total, kept));
        return new ImdbExtractReport(kept, skipped, destPath);
    }

    /// <summary>IMDb writes a missing value as a backslash-N rather than leaving it blank.</summary>
    private const string NullField = @"\N";

    /// <summary>
    /// Which columns hold the primary title and the start year. Read from the header so a
    /// future column reshuffle doesn't silently extract the wrong fields; falls back to
    /// the documented positions if the header is missing or unfamiliar.
    /// </summary>
    private static (int Title, int Year) ColumnsOf(string? header)
    {
        const int defaultTitle = 2, defaultYear = 5;
        if (string.IsNullOrEmpty(header)) return (defaultTitle, defaultYear);

        var names = header.Split('\t');
        var title = Array.FindIndex(names, n =>
            n.Equals("primaryTitle", StringComparison.OrdinalIgnoreCase));
        var year = Array.FindIndex(names, n =>
            n.Equals("startYear", StringComparison.OrdinalIgnoreCase));

        return (title >= 0 ? title : defaultTitle, year >= 0 ? year : defaultYear);
    }

    private static bool IsGzip(string path) =>
        path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path) =>
        Relocation.FileDeleter.TryDeleteQuietly(path);
}
