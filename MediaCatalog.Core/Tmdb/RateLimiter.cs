namespace MediaCatalog.Core.Tmdb;

/// <summary>
/// Serialises calls and guarantees a minimum spacing between them (TMDb is limited to
/// one query every two seconds here). Thread-safe; callers await <see cref="WaitAsync"/>
/// immediately before each request.
/// </summary>
public class RateLimiter
{
    private readonly TimeSpan _minInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastUtc = DateTime.MinValue;

    public RateLimiter(TimeSpan minInterval) => _minInterval = minInterval;

    public async Task WaitAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _lastUtc + _minInterval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
            _lastUtc = DateTime.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }
}
