using System.Net.Http;
using System.Text.Json;

namespace MediaCatalog.Core.Tmdb;

/// <summary>
/// Minimal TMDb (themoviedb.org) v3 client for validating TV show names. Uses the
/// shared <see cref="RateLimiter"/> and <see cref="TmdbCache"/>: cached queries never
/// hit the network, and live queries are spaced by the rate limit.
/// </summary>
public class TmdbClient
{
    private const string SearchTvUrl = "https://api.themoviedb.org/3/search/tv";

    private readonly string _apiKey;
    private readonly TmdbCache _cache;
    private readonly RateLimiter _limiter;
    private readonly HttpClient _http;

    public TmdbClient(string apiKey, TmdbCache cache, RateLimiter limiter, HttpClient? http = null)
    {
        _apiKey = apiKey ?? string.Empty;
        _cache = cache;
        _limiter = limiter;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    /// <summary>
    /// Validate a TV show name. Returns the cached answer if present; otherwise queries
    /// TMDb (rate-limited) and caches the outcome. Network/HTTP errors are returned as a
    /// non-result and *not* cached, so they can be retried later.
    /// </summary>
    public async Task<TmdbResult> ValidateTvAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new TmdbResult(false, string.Empty);

        if (_cache.TryGet(name, out var cached))
            return cached;

        if (!IsConfigured)
            return new TmdbResult(false, string.Empty);

        await _limiter.WaitAsync(ct);

        try
        {
            var url = $"{SearchTvUrl}?api_key={Uri.EscapeDataString(_apiKey)}" +
                      $"&query={Uri.EscapeDataString(name)}";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return new TmdbResult(false, string.Empty); // transient — don't cache

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            TmdbResult result;
            if (doc.RootElement.TryGetProperty("results", out var results) &&
                results.ValueKind == JsonValueKind.Array &&
                results.GetArrayLength() > 0)
            {
                var canonical = results[0].TryGetProperty("name", out var n)
                    ? n.GetString() ?? name
                    : name;
                result = new TmdbResult(true, canonical);
            }
            else
            {
                result = new TmdbResult(false, string.Empty);
            }

            _cache.Put(name, result); // cache definitive hits and misses
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new TmdbResult(false, string.Empty); // network error — don't cache
        }
    }
}
