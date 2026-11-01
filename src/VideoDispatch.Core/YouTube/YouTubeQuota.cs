namespace VideoDispatch.Core.YouTube;

public static class YouTubeQuota
{
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

    /// <summary>YouTube's daily quotas reset at midnight Pacific time.</summary>
    public static DateTimeOffset NextReset(DateTimeOffset now)
    {
        var midnight = TimeZoneInfo.ConvertTime(now, Pacific).Date.AddDays(1);
        return new DateTimeOffset(midnight, Pacific.GetUtcOffset(midnight));
    }
}
