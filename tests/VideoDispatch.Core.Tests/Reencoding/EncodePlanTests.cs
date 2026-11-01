using VideoDispatch.Core.Media;
using VideoDispatch.Core.Reencoding;

namespace VideoDispatch.Core.Tests.Reencoding;

public class EncodePlanTests
{
    private const long Big = 10L * 1024 * 1024 * 1024;

    private static readonly VideoMetadata Vertical = new(1080, 1920, 0, TimeSpan.FromMinutes(1), 30, "avc1");

    private static EncodePlan Plan(EncodeGoal goal, VideoMetadata? metadata = null, long source = Big, int? target = null) =>
        EncodePlan.For(new EncodeSettings(goal, target), metadata ?? Vertical, source);

    [Fact]
    public void High_quality_keeps_the_picture_size()
    {
        var plan = Plan(EncodeGoal.HighQuality);

        Assert.Equal((1080, 1920), (plan.Width, plan.Height));
        Assert.Equal(30, plan.FramesPerSecond);
        Assert.Equal(18, plan.ConstantQuality);
    }

    [Theory]
    [InlineData(EncodeGoal.Balanced, 3840, 2160, 1920, 1080)]
    [InlineData(EncodeGoal.Balanced, 2160, 3840, 1080, 1920)]
    [InlineData(EncodeGoal.Balanced, 1280, 720, 1280, 720)]
    [InlineData(EncodeGoal.SmallFile, 1920, 1080, 1280, 720)]
    [InlineData(EncodeGoal.SmallFile, 1080, 1920, 720, 1280)]
    [InlineData(EncodeGoal.SmallFile, 640, 360, 640, 360)]
    public void Each_goal_caps_the_longest_side_and_never_makes_it_bigger(EncodeGoal goal, int width, int height, int expectedWidth, int expectedHeight)
    {
        var plan = Plan(goal, new VideoMetadata(width, height, 0, TimeSpan.FromMinutes(1), 30, "avc1"));

        Assert.Equal((expectedWidth, expectedHeight), (plan.Width, plan.Height));
    }

    [Fact]
    public void Sizes_come_out_even_because_H264_needs_that()
    {
        var plan = Plan(EncodeGoal.SmallFile, new VideoMetadata(1921, 1081, 0, TimeSpan.FromMinutes(1), 30, "avc1"));

        Assert.Equal(0, plan.Width % 2);
        Assert.Equal(0, plan.Height % 2);
    }

    [Theory]
    [InlineData(90, true)]
    [InlineData(270, true)]
    [InlineData(0, false)]
    [InlineData(180, false)]
    public void A_sideways_source_is_sized_as_viewers_see_it_and_marked(int rotation, bool sideways)
    {
        var plan = Plan(EncodeGoal.HighQuality, new VideoMetadata(1920, 1080, rotation, TimeSpan.FromMinutes(1), 30, "avc1"));

        Assert.Equal(sideways, plan.SourceIsSideways);
        Assert.Equal(sideways ? (1080, 1920) : (1920, 1080), (plan.Width, plan.Height));
    }

    [Fact]
    public void Lower_goals_ask_for_less()
    {
        var bitrates = new[] { EncodeGoal.HighQuality, EncodeGoal.Balanced, EncodeGoal.SmallFile }.Select(goal => Plan(goal).VideoBitrate).ToList();

        Assert.True(bitrates[0] > bitrates[1]);
        Assert.True(bitrates[1] > bitrates[2]);
    }

    [Fact]
    public void Never_asks_for_more_than_the_source_has()
    {
        // 15 MB in a minute is 2 Mbps, far below what high quality would otherwise ask for.
        var plan = Plan(EncodeGoal.HighQuality, source: 15 * 1024 * 1024);

        Assert.InRange(plan.VideoBitrate, 1_000_000, 2_100_000);
    }

    [Fact]
    public void Works_without_knowing_what_is_in_the_file()
    {
        var plan = EncodePlan.For(new EncodeSettings(EncodeGoal.Balanced), null, Big);

        Assert.Null(plan.Width);
        Assert.Null(plan.Height);
        Assert.Null(plan.EstimatedBytes);
        Assert.False(plan.SourceIsSideways);
        Assert.True(plan.VideoBitrate > 0);
    }

    [Fact]
    public void Fitting_a_size_comes_in_under_it()
    {
        var plan = Plan(EncodeGoal.FitSize, target: 50);

        Assert.Null(plan.ConstantQuality);
        Assert.InRange(plan.EstimatedBytes!.Value, 40 * 1024 * 1024, 50 * 1024 * 1024);
    }

    [Theory]
    [InlineData(500, 1080)]
    [InlineData(10, 720)]
    [InlineData(5, 480)]
    public void Fitting_a_small_size_makes_the_picture_smaller_too(int megabytes, int width)
    {
        var plan = Plan(EncodeGoal.FitSize, target: megabytes);

        Assert.InRange(plan.Width!.Value, width - 2, width + 2);
    }

    [Fact]
    public void Only_fitting_a_size_can_have_a_problem()
    {
        Assert.Null(EncodePlan.ProblemWith(new EncodeSettings(EncodeGoal.HighQuality), null));
        Assert.Null(EncodePlan.ProblemWith(new EncodeSettings(EncodeGoal.FitSize, 100), Vertical));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void Fitting_a_size_needs_a_size(int? megabytes)
    {
        Assert.Contains("Enter the size", EncodePlan.ProblemWith(new EncodeSettings(EncodeGoal.FitSize, megabytes), Vertical));
    }

    [Fact]
    public void Fitting_a_size_needs_a_known_length()
    {
        Assert.Contains("length couldn't be read", EncodePlan.ProblemWith(new EncodeSettings(EncodeGoal.FitSize, 100), null));
        Assert.Contains("length couldn't be read", EncodePlan.ProblemWith(new EncodeSettings(EncodeGoal.FitSize, 100), Vertical with { Duration = null }));
    }

    [Fact]
    public void A_size_too_small_for_the_length_says_what_would_do()
    {
        var problem = EncodePlan.ProblemWith(new EncodeSettings(EncodeGoal.FitSize, 1), Vertical with { Duration = TimeSpan.FromHours(1) });

        Assert.Contains("too small", problem);
        Assert.Contains("at least 123 MB", problem);
    }
}
