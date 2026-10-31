using VideoDispatch.Core.Model;
using VideoDispatch.Core.Presets;
using VideoDispatch.Core.Templates;

namespace VideoDispatch.Core.Tests.Presets;

public class PresetResolverTests
{
    private static readonly TemplateContext Context = new("My Trip", "trip_01", new DateOnly(2026, 10, 31));
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Fact]
    public void TikTok_caption_gets_template_text_and_hashtags()
    {
        var preset = new TikTokPreset
        {
            Id = Guid.NewGuid(),
            Name = "Travel",
            CaptionTemplate = "{title} from {date}",
            Hashtags = ["travel", "#fun", " #Travel ", "two words", "#", ""],
        };

        var resolved = PresetResolver.Resolve(preset, Context);

        Assert.Equal("My Trip from 2026-10-31 #travel #fun #twowords", resolved.Details.Caption);
        Assert.Empty(resolved.UnknownVariables);
    }

    [Fact]
    public void TikTok_caption_has_no_stray_space_without_text_or_hashtags()
    {
        var hashtagsOnly = new TikTokPreset { Id = Guid.NewGuid(), Name = "A", CaptionTemplate = "", Hashtags = ["a"] };
        var textOnly = new TikTokPreset { Id = Guid.NewGuid(), Name = "B", CaptionTemplate = "{title}" };

        Assert.Equal("#a", PresetResolver.Resolve(hashtagsOnly, Context).Details.Caption);
        Assert.Equal("My Trip", PresetResolver.Resolve(textOnly, Context).Details.Caption);
    }

    [Fact]
    public void TikTok_preset_types_have_no_fields_TikTok_requires_the_user_to_set()
    {
        string[] banned = ["privacy", "comment", "duet", "stitch", "commercial", "brand", "disclosure"];

        var properties = typeof(TikTokPreset).GetProperties()
            .Concat(typeof(TikTokPostDetails).GetProperties())
            .Select(property => property.Name);

        Assert.DoesNotContain(properties, name => banned.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void YouTube_details_copy_the_preset()
    {
        var preset = new YouTubePreset
        {
            Id = Guid.NewGuid(),
            Name = "Vlog",
            TitleTemplate = "{title}",
            DescriptionTemplate = "  Filmed {date}\n",
            Tags = [" vlog ", "VLOG", "", "travel tips"],
            CategoryId = "19",
            Privacy = YouTubePrivacy.Unlisted,
            MadeForKids = false,
            PlaylistId = "PL123",
        };

        var resolved = PresetResolver.Resolve(preset, Context, Now, NewYork);

        Assert.Equal("My Trip", resolved.Details.Title);
        Assert.Equal("Filmed 2026-10-31", resolved.Details.Description);
        Assert.Equal(["vlog", "travel tips"], resolved.Details.Tags);
        Assert.Equal("19", resolved.Details.CategoryId);
        Assert.Equal(YouTubePrivacy.Unlisted, resolved.Details.Privacy);
        Assert.False(resolved.Details.MadeForKids);
        Assert.Equal("PL123", resolved.Details.PlaylistId);
        Assert.Null(resolved.Details.PublishAt);
    }

    [Fact]
    public void YouTube_leaves_unset_choices_empty_instead_of_defaulting()
    {
        var preset = new YouTubePreset { Id = Guid.NewGuid(), Name = "Blank" };

        var details = PresetResolver.Resolve(preset, Context, Now, NewYork).Details;

        Assert.Null(details.Privacy);
        Assert.Null(details.MadeForKids);
    }

    [Fact]
    public void YouTube_reports_unknown_variables_from_both_templates_once()
    {
        var preset = new YouTubePreset
        {
            Id = Guid.NewGuid(),
            Name = "Typos",
            TitleTemplate = "{titel}",
            DescriptionTemplate = "{TITEL} {desc}",
        };

        var resolved = PresetResolver.Resolve(preset, Context, Now, NewYork);

        Assert.Equal(["titel", "desc"], resolved.UnknownVariables);
    }

    [Fact]
    public void Schedule_uses_the_local_day_in_the_user_time_zone()
    {
        // 02:00 UTC on November 1 is still October 31 in New York.
        var now = new DateTimeOffset(2026, 11, 1, 2, 0, 0, TimeSpan.Zero);

        var publishAt = PresetResolver.ResolveSchedule(new YouTubeSchedule(1, new TimeOnly(18, 0)), now, NewYork);

        // November 1, 2026 is after clocks go back, so the offset is -5.
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 18, 0, 0, TimeSpan.FromHours(-5)), publishAt);
    }

    [Fact]
    public void Schedule_moves_a_skipped_clock_time_past_the_gap()
    {
        // Clocks in New York jump from 02:00 to 03:00 on March 8, 2026.
        var now = new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero);

        var publishAt = PresetResolver.ResolveSchedule(new YouTubeSchedule(1, new TimeOnly(2, 30)), now, NewYork);

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 3, 0, 0, TimeSpan.FromHours(-4)), publishAt);
    }

    [Fact]
    public void Schedule_uses_the_first_occurrence_of_a_repeated_clock_time()
    {
        // Clocks in New York go back from 02:00 to 01:00 on November 1, 2026.
        var now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

        var publishAt = PresetResolver.ResolveSchedule(new YouTubeSchedule(1, new TimeOnly(1, 30)), now, NewYork);

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-4)), publishAt);
    }
}
