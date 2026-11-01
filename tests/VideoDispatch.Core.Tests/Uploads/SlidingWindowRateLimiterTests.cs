using VideoDispatch.Core.Tests.TikTok;
using VideoDispatch.Core.Uploads;

namespace VideoDispatch.Core.Tests.Uploads;

public class SlidingWindowRateLimiterTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Lets_the_limit_through_at_once_and_holds_the_next_until_the_window_passes()
    {
        var time = new JumpingTime(Start);
        var limiter = new SlidingWindowRateLimiter(3, TimeSpan.FromMinutes(1), time);

        for (var i = 0; i < 3; i++)
        {
            await limiter.WaitAsync();
        }

        Assert.Equal(Start, time.GetUtcNow());

        await limiter.WaitAsync();

        Assert.Equal(Start.AddMinutes(1), time.GetUtcNow());
    }

    [Fact]
    public async Task Holds_for_only_as_long_as_the_oldest_call_counts()
    {
        var time = new JumpingTime(Start);
        var limiter = new SlidingWindowRateLimiter(2, TimeSpan.FromMinutes(1), time);

        await limiter.WaitAsync();
        await Task.Delay(TimeSpan.FromSeconds(45), time);
        await limiter.WaitAsync();
        await limiter.WaitAsync();

        Assert.Equal(Start.AddMinutes(1), time.GetUtcNow());
    }

    [Fact]
    public async Task Canceling_stops_the_wait()
    {
        var limiter = new SlidingWindowRateLimiter(1, TimeSpan.FromHours(1), TimeProvider.System);
        await limiter.WaitAsync();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.WaitAsync(cancel.Token));
    }
}
