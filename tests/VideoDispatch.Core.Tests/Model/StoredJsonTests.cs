using System.Text.Json;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Presets;

namespace VideoDispatch.Core.Tests.Model;

public class StoredJsonTests
{
    private static T RoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, StoredJson.Options);
        return JsonSerializer.Deserialize<T>(json, StoredJson.Options)!;
    }

    private static UploadJob JobWith(PostDetails details) => new()
    {
        Id = Guid.NewGuid(),
        BatchId = Guid.NewGuid(),
        SourcePath = "clip.mp4",
        Details = details,
        CreatedAt = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void Keeps_YouTube_details_through_a_round_trip()
    {
        var job = JobWith(new YouTubePostDetails
        {
            Title = "Title",
            Description = "Text",
            Tags = ["a", "b"],
            CategoryId = "22",
            Privacy = YouTubePrivacy.Unlisted,
            MadeForKids = true,
            PlaylistId = "PL1",
            PublishAt = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(-5)),
        }) with
        {
            State = JobState.Waiting,
            Attempts = 3,
            BytesSent = 1024,
            TotalBytes = 2048,
            UploadSessionUrl = "https://example.test/session",
        };

        var restored = RoundTrip(job);

        var details = Assert.IsType<YouTubePostDetails>(restored.Details);
        var original = (YouTubePostDetails)job.Details;
        Assert.Equal(original.Tags, details.Tags);
        Assert.Equal(original with { Tags = details.Tags }, details);
        Assert.Equal(job with { Details = details }, restored);
    }

    [Fact]
    public void Keeps_TikTok_details_through_a_round_trip()
    {
        var restored = RoundTrip(JobWith(new TikTokPostDetails { Caption = "Hello #fun" }));

        Assert.Equal(Platform.TikTok, restored.Platform);
        Assert.Equal("Hello #fun", Assert.IsType<TikTokPostDetails>(restored.Details).Caption);
    }

    [Fact]
    public void Keeps_an_unset_privacy_choice_unset()
    {
        var restored = RoundTrip(JobWith(new YouTubePostDetails { Title = "Title" }));

        Assert.Null(((YouTubePostDetails)restored.Details).Privacy);
    }

    [Fact]
    public void Writes_enums_as_names_so_saved_files_survive_reordering()
    {
        var json = JsonSerializer.Serialize(JobWith(new TikTokPostDetails()) with { State = JobState.AwaitingCreator }, StoredJson.Options);

        Assert.Contains("\"state\": \"AwaitingCreator\"", json);
    }

    [Fact]
    public void Does_not_save_computed_properties()
    {
        var json = JsonSerializer.Serialize(JobWith(new TikTokPostDetails()), StoredJson.Options);

        Assert.DoesNotContain("publishPath", json);
        Assert.Contains("\"platform\": \"tiktok\"", json);
    }

    [Fact]
    public void PublishPath_prefers_the_rendered_file()
    {
        var job = JobWith(new TikTokPostDetails());

        Assert.Equal("clip.mp4", job.PublishPath);
        Assert.Equal("out.mp4", (job with { RenderedPath = "out.mp4" }).PublishPath);
    }

    [Fact]
    public void Keeps_presets_through_a_round_trip()
    {
        var preset = new YouTubePreset
        {
            Id = Guid.NewGuid(),
            Name = "Vlog",
            Privacy = YouTubePrivacy.Private,
            Schedule = new YouTubeSchedule(2, new TimeOnly(18, 30)),
        };

        var restored = RoundTrip(preset);

        Assert.Equal("Vlog", restored.Name);
        Assert.Equal(preset.Schedule, restored.Schedule);
        Assert.Equal(YouTubePrivacy.Private, restored.Privacy);
    }
}
