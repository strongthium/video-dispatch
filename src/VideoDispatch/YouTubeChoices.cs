using System.Globalization;
using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Accounts.Secrets;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Reencoding;
using VideoDispatch.Core.Uploads;
using VideoDispatch.Core.YouTube;

namespace VideoDispatch.App;

/// <summary>
/// One entry in a list to choose from. An empty ID means nothing is chosen, because a list can't show an entry for
/// null: the views turn it into null on the way through.
/// </summary>
public sealed record Choice(string Id, string Title)
{
    public const string None = "";

    /// <summary>What a screen reader says for this entry in a list.</summary>
    public override string ToString() => Title;

    /// <summary>The ID as it's kept: nothing chosen is null.</summary>
    public static string? Clean(string? id) => string.IsNullOrEmpty(id) ? null : id;
}

/// <summary>What the YouTube fields offer: the fixed lists, and the channel's playlists and categories, asked for once.</summary>
public sealed class YouTubeChoices(IYouTubeApi api, Func<Platform, AccountStatus> accountOf)
{
    public const string PrivateId = nameof(YouTubePrivacy.Private);
    public const string YesId = "yes";
    public const string NoId = "no";

    private bool _loaded;

    public event EventHandler? Changed;

    public IReadOnlyList<Choice> Categories { get; private set; } = [new(Choice.None, "People & Blogs (the default)")];

    public IReadOnlyList<Choice> Playlists { get; private set; } = [new(Choice.None, "No playlist")];

    /// <summary>Why the channel's lists couldn't be read, if they couldn't.</summary>
    public string? Problem { get; private set; }

    /// <summary>Asks YouTube, if the account is connected and it hasn't answered yet.</summary>
    public async Task LoadAsync()
    {
        if (_loaded)
        {
            return;
        }

        try
        {
            var account = await Task.Run(() => accountOf(Platform.YouTube));
            if (account.State != ConnectionState.Connected)
            {
                return;
            }

            var categories = await api.ListCategoriesAsync(RegionInfo.CurrentRegion.TwoLetterISORegionName);
            var playlists = account.Scopes.Contains(GoogleOAuthProvider.PlaylistScope)
                ? await api.ListPlaylistsAsync()
                : [];

            Categories = [new(Choice.None, "People & Blogs (the default)"), .. categories.Select(category => new Choice(category.Id, category.Title))];
            Playlists = [new(Choice.None, "No playlist"), .. playlists.Select(playlist => new Choice(playlist.Id, playlist.Title))];
            Problem = null;
            _loaded = true;
        }
        catch (Exception exception) when (exception is UploadException or SecretStoreException)
        {
            Problem = exception.Message;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The account changed, so the next look at the lists asks again.</summary>
    public void Forget() => _loaded = false;

    /// <summary>What can be done to a video before sending it. Nothing is re-encoded unless it's asked for.</summary>
    public static IReadOnlyList<Choice> EncodeChoices { get; } =
    [
        new(Choice.None, "Send the file as it is"),
        new(nameof(EncodeGoal.HighQuality), "Re-encode to MP4: high quality"),
        new(nameof(EncodeGoal.Balanced), "Re-encode to MP4: balanced, up to 1080p"),
        new(nameof(EncodeGoal.SmallFile), "Re-encode to MP4: smaller file, up to 720p"),
        new(nameof(EncodeGoal.FitSize), "Re-encode to MP4: fit a size"),
    ];

    public static IReadOnlyList<Choice> Privacies(string noChoice) =>
    [
        new(Choice.None, noChoice),
        new(nameof(YouTubePrivacy.Private), "Private"),
        new(nameof(YouTubePrivacy.Unlisted), "Unlisted"),
        new(nameof(YouTubePrivacy.Public), "Public"),
    ];

    public static IReadOnlyList<Choice> MadeForKids(string noChoice) =>
    [
        new(Choice.None, noChoice),
        new(YesId, "Yes"),
        new(NoId, "No"),
    ];

    public static YouTubePrivacy? PrivacyFrom(string? id) =>
        Enum.TryParse<YouTubePrivacy>(id, out var privacy) ? privacy : null;

    public static bool? MadeForKidsFrom(string? id) => id switch
    {
        YesId => true,
        NoId => false,
        _ => null,
    };

    public static string? IdOf(bool? madeForKids) => madeForKids switch
    {
        true => YesId,
        false => NoId,
        null => null,
    };
}
