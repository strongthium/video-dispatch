using VideoDispatch.Core.Model;
using VideoDispatch.Core.Templates;

namespace VideoDispatch.Core.Presets;

public sealed record ResolvedPost<T>(T Details, IReadOnlyList<string> UnknownVariables)
    where T : PostDetails;

/// <summary>Turns a preset into editable post details for one video.</summary>
public static class PresetResolver
{
    public static ResolvedPost<TikTokPostDetails> Resolve(TikTokPreset preset, TemplateContext context)
    {
        var caption = TemplateRenderer.Render(preset.CaptionTemplate, context);
        var hashtags = string.Join(' ', NormalizeHashtags(preset.Hashtags));
        var text = string.Join(' ', new[] { caption.Text.Trim(), hashtags }.Where(part => part.Length > 0));

        return new ResolvedPost<TikTokPostDetails>(new TikTokPostDetails { Caption = text }, caption.UnknownVariables);
    }

    public static ResolvedPost<YouTubePostDetails> Resolve(
        YouTubePreset preset,
        TemplateContext context,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        var title = TemplateRenderer.Render(preset.TitleTemplate, context);
        var description = TemplateRenderer.Render(preset.DescriptionTemplate, context);

        var details = new YouTubePostDetails
        {
            Title = title.Text.Trim(),
            Description = description.Text.Trim(),
            Tags = NormalizeTags(preset.Tags),
            CategoryId = preset.CategoryId,
            Privacy = preset.Privacy,
            MadeForKids = preset.MadeForKids,
            PlaylistId = preset.PlaylistId,
            PublishAt = preset.Schedule is { } schedule ? ResolveSchedule(schedule, now, timeZone) : null,
        };

        var unknown = title.UnknownVariables
            .Concat(description.UnknownVariables)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ResolvedPost<YouTubePostDetails>(details, unknown);
    }

    public static DateTimeOffset ResolveSchedule(YouTubeSchedule schedule, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);
        var local = today.AddDays(schedule.DaysAhead).ToDateTime(schedule.LocalTime, DateTimeKind.Unspecified);

        // A clock time skipped by daylight saving moves past the gap, and a repeated one uses its first occurrence.
        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);

        return new DateTimeOffset(local, offset);
    }

    public static IReadOnlyList<string> NormalizeHashtags(IEnumerable<string> hashtags) =>
        hashtags
            .Select(tag => tag.Trim().TrimStart('#'))
            .Select(tag => string.Concat(tag.Where(c => !char.IsWhiteSpace(c))))
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(tag => "#" + tag)
            .ToList();

    public static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags) =>
        tags
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
