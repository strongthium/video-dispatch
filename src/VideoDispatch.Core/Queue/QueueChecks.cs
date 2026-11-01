using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Media;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Reencoding;
using VideoDispatch.Core.Templates;
using VideoDispatch.Core.TikTok;
using VideoDispatch.Core.Uploads;
using VideoDispatch.Core.YouTube;

namespace VideoDispatch.Core.Queue;

/// <param name="Problems">Why this can't be queued.</param>
/// <param name="Warnings">What the user should know, though it can be queued.</param>
public sealed record QueueCheck(IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings)
{
    public bool CanQueue => Problems.Count == 0;
}

/// <summary>Everything that must be right before a video joins the queue.</summary>
public static class QueueChecks
{
    public static QueueCheck Check(
        PostDetails details,
        string path,
        long size,
        VideoMetadata? metadata,
        AccountStatus account,
        DateTimeOffset now)
    {
        var problems = new List<string>(PostDetailsValidator.Validate(details, now));
        var warnings = new List<string>();

        foreach (var name in VariablesLeftIn(details))
        {
            problems.Add($"{{{name}}} isn't a variable this app can fill in, so it would be posted as written.");
        }

        // A re-encoded video is judged as what it will be. The uploaders check the real file again before sending it.
        var sentPath = path;
        var sentSize = size;
        if (details.Encode is { } encode)
        {
            if (EncodePlan.ProblemWith(encode, metadata) is { } encodeProblem)
            {
                problems.Add(encodeProblem);
            }
            else
            {
                sentPath = Path.ChangeExtension(path, ".mp4");
                sentSize = EncodePlan.For(encode, metadata, size).EstimatedBytes is { } estimate ? Math.Min(estimate, size) : size;
            }
        }

        IReadOnlyList<VideoLimitFinding> findings = details switch
        {
            TikTokPostDetails => TikTokVideoLimits.Check(sentPath, sentSize, metadata),
            _ => YouTubeVideoLimits.Check(sentSize),
        };

        foreach (var finding in findings)
        {
            (finding.Blocks ? problems : warnings).Add(finding.Message);
        }

        switch (account.State)
        {
            case ConnectionState.NotConnected:
                problems.Add($"Connect your {account.Platform} account first.");
                break;

            case ConnectionState.NeedsReconnect:
                problems.Add($"Your {account.Platform} sign-in has ended. Connect the account again.");
                break;

            case ConnectionState.Connected when details is YouTubePostDetails { PlaylistId: not null } &&
                                                !account.Scopes.Contains(GoogleOAuthProvider.PlaylistScope):
                problems.Add("Adding to a playlist needs a permission this sign-in doesn't have. Turn on playlists under Accounts and connect again.");
                break;
        }

        return new QueueCheck(problems, warnings);
    }

    private static IEnumerable<string> VariablesLeftIn(PostDetails details) => details switch
    {
        TikTokPostDetails tikTok => TemplateRenderer.FindVariables(tikTok.Caption),
        YouTubePostDetails youTube => TemplateRenderer.FindVariables(youTube.Title)
            .Concat(TemplateRenderer.FindVariables(youTube.Description))
            .Distinct(StringComparer.OrdinalIgnoreCase),
        _ => [],
    };
}
