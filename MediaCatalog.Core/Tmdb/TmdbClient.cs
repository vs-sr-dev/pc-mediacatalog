using System.Net.Http;
using System.Text.Json;

namespace MediaCatalog.Core.Tmdb;

/// <summary>
/// Minimal TMDb (themoviedb.org) v3 client for validating programme and film names. Uses
/// the shared <see cref="RateLimiter"/> and <see cref="TmdbCache"/>: cached queries never
/// hit the network, and live queries are spaced by the rate limit.
/// </summary>
public class TmdbClient
{
    private const string SearchTvUrl = "https://api.themoviedb.org/3/search/tv";
    private const string SearchMovieUrl = "https://api.themoviedb.org/3/search/movie";

    private readonly string _apiKey;
    private readonly string _readToken;
    private readonly TmdbCache _cache;
    private readonly RateLimiter _limiter;
    private readonly HttpClient _http;

    /// <param name="apiKey">TMDb v3 API Key (query parameter).</param>
    /// <param name="readAccessToken">TMDb v4 Read Access Token (Bearer header); preferred.</param>
    public TmdbClient(string apiKey, string readAccessToken, TmdbCache cache, RateLimiter limiter, HttpClient? http = null)
    {
        _apiKey = apiKey ?? string.Empty;
        _readToken = readAccessToken ?? string.Empty;
        _cache = cache;
        _limiter = limiter;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_apiKey) || !string.IsNullOrWhiteSpace(_readToken);

    /// <summary>
    /// Validate a TV show name. Returns the cached answer if present; otherwise queries
    /// TMDb (rate-limited) and caches the outcome. Network/HTTP errors are returned as a
    /// non-result and *not* cached, so they can be retried later.
    /// </summary>
    public Task<TmdbResult> ValidateTvAsync(string name, CancellationToken ct = default) =>
        SearchAsync(SearchTvUrl, TmdbCache.Tv, "name", name, ct);

    /// <summary>
    /// The same for a film. TMDb keeps films and programmes in separate indexes and calls
    /// the field <c>title</c> rather than <c>name</c>, which is the only real difference.
    /// </summary>
    public Task<TmdbResult> ValidateMovieAsync(string name, CancellationToken ct = default) =>
        SearchAsync(SearchMovieUrl, TmdbCache.Movie, "title", name, ct);

    /// <param name="nameField">Which field of a result carries the canonical name.</param>
    private async Task<TmdbResult> SearchAsync(
        string endpoint, string kind, string nameField, string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new TmdbResult(false, string.Empty);

        if (_cache.TryGet(name, kind, out var cached))
            return cached;

        if (!IsConfigured)
            return new TmdbResult(false, string.Empty);

        await _limiter.WaitAsync(ct);

        try
        {
            // Prefer the v4 Bearer token; otherwise fall back to the v3 api_key query param.
            var useBearer = !string.IsNullOrWhiteSpace(_readToken);
            var url = $"{endpoint}?query={Uri.EscapeDataString(name)}";
            if (!useBearer)
                url += $"&api_key={Uri.EscapeDataString(_apiKey)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("accept", "application/json");
            if (useBearer)
                request.Headers.Add("Authorization", "Bearer " + _readToken);

            using var resp = await _http.SendAsync(request, ct);
            if (!resp.IsSuccessStatusCode)
                return new TmdbResult(false, string.Empty); // transient — don't cache

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            TmdbResult result;
            if (doc.RootElement.TryGetProperty("results", out var results) &&
                results.ValueKind == JsonValueKind.Array &&
                results.GetArrayLength() > 0)
            {
                var canonical = results[0].TryGetProperty(nameField, out var n)
                    ? n.GetString() ?? name
                    : name;
                result = new TmdbResult(true, canonical);
            }
            else
            {
                result = new TmdbResult(false, string.Empty);
            }

            _cache.Put(name, kind, result); // cache definitive hits and misses
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
