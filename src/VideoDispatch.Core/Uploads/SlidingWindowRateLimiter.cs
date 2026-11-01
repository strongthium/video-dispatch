namespace VideoDispatch.Core.Uploads;

/// <summary>Holds calls back so that no more than the limit start in any stretch of the window.</summary>
public sealed class SlidingWindowRateLimiter(int limit, TimeSpan window, TimeProvider time)
{
    private readonly Queue<DateTimeOffset> _starts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                var now = time.GetUtcNow();
                while (_starts.TryPeek(out var oldest) && now - oldest >= window)
                {
                    _starts.Dequeue();
                }

                if (_starts.Count < limit)
                {
                    _starts.Enqueue(now);
                    return;
                }

                await Task.Delay(_starts.Peek() + window - now, time, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
