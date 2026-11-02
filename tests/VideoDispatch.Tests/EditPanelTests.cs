using VideoDispatch.App;
using VideoDispatch.Core.Editing;
using VideoDispatch.Core.Media;
using VideoDispatch.Core.Reencoding;

namespace VideoDispatch.Tests;

public sealed class EditPanelTests : IDisposable
{
    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "edit-panel-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, VideoMetadata?> _videos = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TimeSpan?> _lengths = new(StringComparer.OrdinalIgnoreCase);
    private readonly FakeEncoder _encoder = new();
    private readonly EditStore _store;

    public EditPanelTests()
    {
        Directory.CreateDirectory(_folder);
        _store = new EditStore(PathOf("edit.json"));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);

    private string PathOf(string name) => Path.Combine(_folder, name);

    private string Video(string name, double seconds, int width = 1080, int height = 1920)
    {
        var path = PathOf(name);
        File.WriteAllText(path, "video");
        _videos[path] = new VideoMetadata(width, height, 0, Seconds(seconds), 30, "avc1");
        return path;
    }

    private EditPanel NewPanel(EditStore? store = null) => new(
        store ?? _store,
        path => _videos.GetValueOrDefault(path),
        (path, _) => Task.FromResult(_lengths.GetValueOrDefault(path)),
        () => _encoder,
        File.Exists);

    [Fact]
    public async Task Adding_videos_puts_each_whole_at_the_end_and_moves_the_playhead_to_the_first()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 10)]);
        await panel.AddVideosAsync([Video("ridge.mp4", 4), Video("summit.mp4", 2)]);

        Assert.Equal(["lake.mp4", "ridge.mp4", "summit.mp4"], panel.Project.Clips.Select(clip => clip.Name));
        Assert.Equal(Seconds(16), panel.Length);
        Assert.Equal(Seconds(10), panel.Position);
        Assert.Equal(1, panel.SelectedIndex);
    }

    [Fact]
    public async Task Adding_before_a_clip_puts_them_there()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 10), Video("ridge.mp4", 4)]);

        await panel.AddVideosAsync([Video("summit.mp4", 2)], at: 1);

        Assert.Equal(["lake.mp4", "summit.mp4", "ridge.mp4"], panel.Project.Clips.Select(clip => clip.Name));
    }

    [Fact]
    public async Task A_video_whose_metadata_cant_be_read_is_measured_another_way()
    {
        var panel = NewPanel();
        var mkv = PathOf("clip.mkv");
        File.WriteAllText(mkv, "video");
        _lengths[mkv] = Seconds(7);

        await panel.AddVideosAsync([mkv]);

        Assert.Equal(Seconds(7), Assert.Single(panel.Project.Clips).Length);
    }

    [Fact]
    public async Task Something_that_isnt_a_video_is_named_and_left_out()
    {
        var panel = NewPanel();
        var text = PathOf("notes.txt");
        File.WriteAllText(text, "notes");

        await panel.AddVideosAsync([Video("lake.mp4", 10), text]);

        Assert.Single(panel.Project.Clips);
        Assert.True(panel.MessageIsError);
        Assert.Contains("notes.txt couldn't be read as a video", panel.Message);
    }

    [Fact]
    public async Task Splitting_cuts_at_the_playhead_and_chooses_the_second_half()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 10)]);
        panel.Position = Seconds(4);

        Assert.True(panel.CanSplit);
        panel.Split();

        Assert.Equal([Seconds(4), Seconds(6)], panel.Project.Clips.Select(clip => clip.Length));
        Assert.Equal(1, panel.SelectedIndex);
        Assert.Equal(Seconds(4), panel.Position);
    }

    [Fact]
    public async Task Splitting_at_an_edge_isnt_offered()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 10)]);
        panel.Position = TimeSpan.Zero;

        Assert.False(panel.CanSplit);
        panel.Split();

        Assert.Single(panel.Project.Clips);
    }

    [Fact]
    public async Task Every_change_can_be_undone_and_redone_in_turn()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 10), Video("ridge.mp4", 4)]);
        var added = panel.Project;
        panel.Position = Seconds(5);
        panel.Split();
        var split = panel.Project;
        panel.Move(2, 0);
        var moved = panel.Project;
        panel.TrimStart(0, Seconds(1));
        panel.SelectedIndex = 1;
        panel.DeleteSelected();

        panel.Undo();
        Assert.Equal(3, panel.Project.Clips.Count);
        Assert.Equal(Seconds(1), panel.Project.Clips[0].Start);
        panel.Undo();
        Assert.Same(moved, panel.Project);
        panel.Undo();
        Assert.Same(split, panel.Project);
        panel.Undo();
        Assert.Same(added, panel.Project);
        panel.Undo();
        Assert.Empty(panel.Project.Clips);
        Assert.False(panel.CanUndo);

        panel.Redo();
        panel.Redo();
        Assert.Same(split, panel.Project);
        Assert.True(panel.CanRedo);
    }

    [Fact]
    public async Task A_new_change_after_undoing_ends_what_could_be_redone()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 10)]);
        panel.Position = Seconds(5);
        panel.Split();
        panel.Undo();

        panel.TrimEnd(0, Seconds(8));

        Assert.False(panel.CanRedo);
    }

    [Fact]
    public async Task Trimming_to_where_it_already_is_isnt_a_change()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 10)]);
        panel.Undo();
        panel.Redo();

        panel.TrimStart(0, Seconds(-5));
        panel.TrimEnd(0, Seconds(50));

        panel.Undo();
        Assert.Empty(panel.Project.Clips);
    }

    [Fact]
    public async Task Deleting_chooses_the_clip_that_took_its_place()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 10), Video("ridge.mp4", 4), Video("summit.mp4", 2)]);
        panel.SelectedIndex = 2;

        panel.DeleteSelected();
        Assert.Equal(1, panel.SelectedIndex);

        panel.SelectedIndex = 0;
        panel.DeleteSelected();
        Assert.Equal(0, panel.SelectedIndex);
        Assert.Equal("ridge.mp4", panel.Project.Clips[0].Name);

        panel.DeleteSelected();
        Assert.Equal(-1, panel.SelectedIndex);
        Assert.False(panel.CanDelete);
    }

    [Fact]
    public async Task The_playhead_stays_inside_the_edit()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 10)]);

        panel.Position = Seconds(99);
        Assert.Equal(Seconds(10), panel.Position);

        panel.TrimEnd(0, Seconds(6));
        Assert.Equal(Seconds(6), panel.Position);

        panel.Position = Seconds(-1);
        Assert.Equal(TimeSpan.Zero, panel.Position);
        Assert.Equal("0:00.0 / 0:06.0", panel.TimeText);
    }

    [Fact]
    public async Task The_edit_is_kept_for_next_time_and_a_missing_video_is_named()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 10), Video("ridge.mp4", 4)]);
        File.Delete(PathOf("ridge.mp4"));

        var next = NewPanel(new EditStore(PathOf("edit.json")));
        await next.LoadAsync();

        Assert.Equal(2, next.Project.Clips.Count);
        Assert.Contains("ridge.mp4 can't be found", next.Message);
    }

    [Fact]
    public async Task Starting_from_New_post_replaces_the_edit_in_one_step_back()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 10), Video("ridge.mp4", 4)]);
        var before = panel.Project;

        await panel.StartWithAsync(Video("summit.mp4", 2));

        Assert.Equal(["summit.mp4"], panel.Project.Clips.Select(clip => clip.Name));
        Assert.Equal(TimeSpan.Zero, panel.Position);
        panel.Undo();
        Assert.Same(before, panel.Project);
    }

    [Fact]
    public async Task Saving_makes_one_video_at_the_first_clips_size_and_offers_it_to_post()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 10, 1080, 1920), Video("ridge.mp4", 4, 1920, 1080)]);
        string? posted = null;
        panel.UseVideo += path => posted = path;
        var output = PathOf("edited.mp4");

        await panel.SaveAsync(output);

        Assert.Equal("made", File.ReadAllText(output));
        Assert.Equal(output, panel.SavedPath);
        Assert.True(panel.HasSaved);
        Assert.False(panel.MessageIsError);
        Assert.Equal(1080, _encoder.Plan!.Width);
        Assert.Equal(1920, _encoder.Plan.Height);
        Assert.Equal(Seconds(14), _encoder.Plan.Duration);
        Assert.Same(panel.Project, _encoder.Project);
        Assert.Empty(Directory.GetFiles(_folder, "*.part.mp4"));

        panel.UseSaved();
        Assert.Equal(output, posted);
    }

    [Fact]
    public async Task Saving_never_writes_over_one_of_its_own_videos()
    {
        var panel = NewPanel();
        var lake = Video("lake.mp4", 10);
        await panel.AddVideosAsync([lake]);

        await panel.SaveAsync(lake.ToUpperInvariant());

        Assert.Equal("video", File.ReadAllText(lake));
        Assert.Null(_encoder.Project);
        Assert.Contains("one of the videos in this edit", panel.Message);
    }

    [Fact]
    public async Task Saving_with_a_video_gone_says_which_and_makes_nothing()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 10), Video("ridge.mp4", 4)]);
        File.Delete(PathOf("ridge.mp4"));

        await panel.SaveAsync(PathOf("edited.mp4"));

        Assert.Null(_encoder.Project);
        Assert.Contains("ridge.mp4 can't be found", panel.Message);
        Assert.False(File.Exists(PathOf("edited.mp4")));
    }

    [Fact]
    public async Task A_failed_save_says_why_and_leaves_nothing_half_made()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 10)]);
        _encoder.Fails = new EncodeException("Windows' tools couldn't save this edit (Unknown).");

        await panel.SaveAsync(PathOf("edited.mp4"));

        Assert.True(panel.MessageIsError);
        Assert.Equal("Windows' tools couldn't save this edit (Unknown).", panel.Message);
        Assert.False(panel.HasSaved);
        Assert.Empty(Directory.GetFiles(_folder, "edited*"));
    }

    [Fact]
    public async Task Stopping_a_save_leaves_nothing_and_says_so()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 10)]);
        var started = new TaskCompletionSource();
        _encoder.Work = started;

        var saving = panel.SaveAsync(PathOf("edited.mp4"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(panel.IsSaving);
        Assert.False(panel.CanEdit);
        panel.CancelSave();
        await saving.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Stopped. Nothing was saved.", panel.Message);
        Assert.False(panel.IsSaving);
        Assert.Empty(Directory.GetFiles(_folder, "edited*"));
    }

    [Fact]
    public async Task Suggests_a_name_beside_the_first_video()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 10)]);

        Assert.Equal("lake edited.mp4", panel.SuggestedName);
        Assert.Equal(_folder, panel.SuggestedFolder);
    }

    [Theory]
    [InlineData(0, "0:00.0")]
    [InlineData(65.34, "1:05.3")]
    [InlineData(3725.5, "1:02:05.5")]
    public void Times_read_as_minutes_and_seconds(double seconds, string text) => Assert.Equal(text, EditPanel.Format(Seconds(seconds)));

    private sealed class FakeEncoder : IVideoEncoder
    {
        public EditProject? Project { get; private set; }

        public EncodePlan? Plan { get; private set; }

        public Exception? Fails { get; set; }

        /// <summary>Set to make it wait, once it has started, until it's stopped.</summary>
        public TaskCompletionSource? Work { get; set; }

        public Task EncodeAsync(string input, string output, EncodePlan plan, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The editor only saves edits.");

        public async Task RenderAsync(EditProject project, string output, EncodePlan plan, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            Project = project;
            Plan = plan;
            Assert.EndsWith(".part.mp4", output);
            await File.WriteAllTextAsync(output, "made", cancellationToken);

            if (Work is { } work)
            {
                work.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (Fails is not null)
            {
                throw Fails;
            }
        }
    }
}
