using System.Text;

namespace VideoDispatch.Core.Model;

/// <summary>Checks details before queueing. Limits are from the YouTube videos resource docs.</summary>
public static class PostDetailsValidator
{
    public const int MaxTitleCharacters = 100;
    public const int MaxDescriptionBytes = 5000;
    public const int MaxTagCharacters = 500;

    public static IReadOnlyList<string> Validate(PostDetails details, DateTimeOffset now) => details switch
    {
        TikTokPostDetails => [],
        YouTubePostDetails youTube => ValidateYouTube(youTube, now),
        _ => throw new ArgumentException($"Unknown details type {details.GetType().Name}.", nameof(details)),
    };

    /// <summary>
    /// Counts tags the way YouTube does: commas between tags count, and a tag
    /// with a space counts as if wrapped in quotation marks.
    /// </summary>
    public static int CountTagCharacters(IEnumerable<string> tags)
    {
        var total = 0;
        var count = 0;
        foreach (var tag in tags)
        {
            total += tag.Length + (tag.Contains(' ') ? 2 : 0);
            count++;
        }

        return count == 0 ? 0 : total + count - 1;
    }

    private static List<string> ValidateYouTube(YouTubePostDetails details, DateTimeOffset now)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(details.Title))
        {
            problems.Add("Enter a title.");
        }
        else if (details.Title.EnumerateRunes().Count() > MaxTitleCharacters)
        {
            problems.Add($"The title can be at most {MaxTitleCharacters} characters.");
        }

        if (HasAngleBracket(details.Title))
        {
            problems.Add("The title can't contain < or >.");
        }

        if (Encoding.UTF8.GetByteCount(details.Description) > MaxDescriptionBytes)
        {
            problems.Add($"The description can be at most {MaxDescriptionBytes} bytes.");
        }

        if (HasAngleBracket(details.Description))
        {
            problems.Add("The description can't contain < or >.");
        }

        if (CountTagCharacters(details.Tags) > MaxTagCharacters)
        {
            problems.Add($"The tags can be at most {MaxTagCharacters} characters in total.");
        }

        if (details.Privacy is null)
        {
            problems.Add("Choose a privacy setting.");
        }

        if (details.MadeForKids is null)
        {
            problems.Add("Choose whether the video is made for kids.");
        }

        if (details.PublishAt is { } publishAt)
        {
            if (details.Privacy is not (null or YouTubePrivacy.Private))
            {
                problems.Add("A scheduled video must be set to private until it publishes.");
            }

            // YouTube publishes right away when the scheduled time is in the past.
            if (publishAt <= now)
            {
                problems.Add("The scheduled time must be in the future.");
            }
        }

        return problems;
    }

    private static bool HasAngleBracket(string text) => text.AsSpan().IndexOfAny('<', '>') >= 0;
}
