using VideoDispatch.Core.Model;

namespace VideoDispatch.Core.Tests.Model;

public class PostDetailsValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static readonly YouTubePostDetails Valid = new()
    {
        Title = "A title",
        Description = "A description",
        Privacy = YouTubePrivacy.Private,
        MadeForKids = false,
    };

    private static IReadOnlyList<string> Validate(PostDetails details) => PostDetailsValidator.Validate(details, Now);

    [Fact]
    public void Accepts_valid_details() => Assert.Empty(Validate(Valid));

    [Fact]
    public void Accepts_any_TikTok_caption() => Assert.Empty(Validate(new TikTokPostDetails()));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Requires_a_title(string title)
    {
        Assert.Contains("Enter a title.", Validate(Valid with { Title = title }));
    }

    [Fact]
    public void Limits_the_title_to_100_characters()
    {
        Assert.Empty(Validate(Valid with { Title = new string('a', 100) }));
        Assert.NotEmpty(Validate(Valid with { Title = new string('a', 101) }));
    }

    [Fact]
    public void Counts_an_emoji_as_one_title_character()
    {
        var title = string.Concat(Enumerable.Repeat("😀", 100));

        Assert.Empty(Validate(Valid with { Title = title }));
    }

    [Fact]
    public void Limits_the_description_in_bytes_not_characters()
    {
        Assert.Empty(Validate(Valid with { Description = new string('a', 5000) }));
        Assert.NotEmpty(Validate(Valid with { Description = new string('é', 2501) }));
    }

    [Theory]
    [InlineData("a < b")]
    [InlineData("a > b")]
    public void Rejects_angle_brackets(string text)
    {
        Assert.NotEmpty(Validate(Valid with { Title = text }));
        Assert.NotEmpty(Validate(Valid with { Description = text }));
    }

    [Fact]
    public void Counts_tag_commas_and_implied_quotes()
    {
        Assert.Equal(0, PostDetailsValidator.CountTagCharacters([]));
        Assert.Equal(3, PostDetailsValidator.CountTagCharacters(["abc"]));
        Assert.Equal(7, PostDetailsValidator.CountTagCharacters(["abc", "def"]));

        // "a b" counts as 5 with its quotes, plus a comma and one more character.
        Assert.Equal(7, PostDetailsValidator.CountTagCharacters(["a b", "c"]));
    }

    [Fact]
    public void Limits_tags_to_500_characters_in_total()
    {
        Assert.Empty(Validate(Valid with { Tags = [new string('a', 500)] }));
        Assert.NotEmpty(Validate(Valid with { Tags = [new string('a', 250), new string('b', 250)] }));
    }

    [Fact]
    public void Requires_a_privacy_choice()
    {
        Assert.Contains("Choose a privacy setting.", Validate(Valid with { Privacy = null }));
    }

    [Fact]
    public void Requires_a_made_for_kids_choice()
    {
        Assert.Contains("Choose whether the video is made for kids.", Validate(Valid with { MadeForKids = null }));
    }

    [Fact]
    public void Accepts_a_future_schedule_on_a_private_video()
    {
        Assert.Empty(Validate(Valid with { PublishAt = Now.AddMinutes(1) }));
    }

    [Theory]
    [InlineData(YouTubePrivacy.Public)]
    [InlineData(YouTubePrivacy.Unlisted)]
    public void Rejects_a_schedule_on_a_video_that_is_not_private(YouTubePrivacy privacy)
    {
        var details = Valid with { Privacy = privacy, PublishAt = Now.AddDays(1) };

        Assert.Contains("A scheduled video must be set to private until it publishes.", Validate(details));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public void Rejects_a_schedule_that_would_publish_right_away(int minutes)
    {
        var details = Valid with { PublishAt = Now.AddMinutes(minutes) };

        Assert.Contains("The scheduled time must be in the future.", Validate(details));
    }
}
