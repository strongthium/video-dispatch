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
        Assert.Contains("one of the files in this edit", panel.Message);
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
        _encoder.Waits = started;

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

    [Fact]
    public async Task Dragging_a_slider_is_one_change_to_undo_and_another_setting_is_another()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 10)]);
        var added = panel.Project;

        foreach (var percent in new[] { 90d, 70, 50, 30 })
        {
            panel.ClipVolume = percent;
        }

        var quieter = panel.Project;
        panel.ClipFadeIn = 0.5;
        panel.ClipFadeIn = 1.2;

        Assert.Equal(0.3, panel.Project.Clips[0].Volume, 3);
        Assert.Equal(Seconds(1.2), panel.Project.Clips[0].FadeIn);
        Assert.Equal("30%", panel.ClipVolumeText);
        Assert.Equal("1.2 s", panel.ClipFadeInText);
        panel.Undo();
        Assert.Same(quieter, panel.Project);
        panel.Undo();
        Assert.Same(added, panel.Project);
    }

    [Fact]
    public async Task Choosing_something_else_between_drags_makes_them_separate_changes()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 10)]);
        panel.ClipZoom = 150;
        panel.SelectedIndex = -1;
        panel.SelectedIndex = 0;
        panel.ClipZoom = 200;

        panel.Undo();

        Assert.Equal(1.5, panel.Project.Clips[0].Zoom);
    }

    [Fact]
    public async Task Only_a_clip_with_one_before_it_can_crossfade()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 10), Video("ridge.mp4", 4)]);

        panel.SelectedIndex = 0;
        Assert.False(panel.CanCrossfade);
        panel.SelectedIndex = 1;
        Assert.True(panel.CanCrossfade);
        panel.ClipCrossfade = 1;

        Assert.Equal(Seconds(13), panel.Length);
        Assert.Equal("1.0 s", panel.ClipCrossfadeText);
        Assert.Equal("Off", panel.ClipFadeOutText);
    }

    [Fact]
    public async Task Zooming_and_moving_what_is_in_view_change_the_chosen_clip()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 10)]);
        Assert.False(panel.ClipIsZoomed);

        panel.ClipZoom = 250;
        panel.SetZoomCentre(0.3, 0.7);

        var clip = panel.Project.Clips[0];
        Assert.True(panel.ClipIsZoomed);
        Assert.Equal("250%", panel.ClipZoomText);
        Assert.Equal((2.5, 0.3, 0.7), (clip.Zoom, clip.ZoomX, clip.ZoomY));
    }

    [Fact]
    public async Task Sounds_go_from_the_playhead_one_after_another_and_the_last_is_chosen()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 30)]);
        panel.Position = Seconds(5);

        await panel.AddSoundsAsync([Video("song.mp3", 8), Video("birds.wav", 3)]);

        Assert.Equal([Seconds(5), Seconds(13)], panel.Project.Sounds.Select(sound => sound.At));
        Assert.Equal(1, panel.SelectedSound);
        Assert.Equal(-1, panel.SelectedIndex);
        Assert.True(panel.HasSoundChosen);
        Assert.Equal("birds.wav", panel.ChosenName);
        panel.Undo();
        Assert.Empty(panel.Project.Sounds);
    }

    [Fact]
    public async Task A_sound_that_cant_be_read_is_named_and_left_out()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 30)]);
        var broken = PathOf("broken.mp3");
        File.WriteAllText(broken, "not sound");

        await panel.AddSoundsAsync([broken], Seconds(2));

        Assert.Empty(panel.Project.Sounds);
        Assert.Contains("broken.mp3 couldn't be read as a sound", panel.Message);
    }

    [Fact]
    public async Task A_sounds_settings_and_its_place_can_be_changed_and_it_can_be_taken_out()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 30)]);
        await panel.AddSoundsAsync([Video("song.mp3", 20)], Seconds(2));

        panel.SoundVolume = 40;
        panel.SoundFadeOut = 3;
        panel.MoveSound(0, Seconds(6));
        panel.TrimSoundStart(0, Seconds(4));

        var sound = panel.Project.Sounds[0];
        Assert.Equal(0.4, sound.Volume, 3);
        Assert.Equal(Seconds(3), sound.FadeOut);
        Assert.Equal((Seconds(4), Seconds(10)), (sound.Start, sound.At));
        Assert.True(panel.CanDelete);
        panel.DeleteSelected();
        Assert.Empty(panel.Project.Sounds);
        Assert.False(panel.HasSelection);
    }

    [Fact]
    public async Task New_words_go_at_the_playhead_or_back_from_the_end_and_typing_is_one_change()
    {
        var panel = NewPanel();
        Assert.False(panel.CanAddText);
        await panel.AddVideosAsync([Video("lake.mp4", 10)]);
        panel.Position = Seconds(9);

        panel.AddText();
        var added = panel.Project;
        foreach (var typed in new[] { "H", "Hi", "Hi there" })
        {
            panel.TextContent = typed;
        }

        var text = Assert.Single(panel.Project.Texts);
        Assert.Equal(Seconds(7), text.At);
        Assert.Equal("Hi there", text.Text);
        Assert.True(panel.HasTextChosen);
        panel.Undo();
        Assert.Same(added, panel.Project);
    }

    [Fact]
    public async Task How_words_look_can_be_chosen()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 10)]);
        panel.AddText();

        panel.TextPlaceIndex = (int)TextPlace.Top;
        panel.TextSizeIndex = (int)TextSize.Large;
        panel.TextColourIndex = (int)TextColour.Yellow;
        panel.TextHasBox = true;
        panel.TextSeconds = 5;
        panel.TextPlaceIndex = 9;

        var text = panel.Project.Texts[0];
        Assert.Equal((TextPlace.Top, TextSize.Large, TextColour.Yellow, true, Seconds(5)), (text.Place, text.Size, text.Colour, text.Box, text.Length));
    }

    [Fact]
    public async Task Saving_draws_the_words_at_the_frames_size_and_tidies_up_after()
    {
        var drawn = new List<(string, int, int)>();
        var panel = new EditPanel(
            _store,
            path => _videos.GetValueOrDefault(path),
            (path, _) => Task.FromResult(_lengths.GetValueOrDefault(path)),
            () => _encoder,
            File.Exists,
            (text, width, height) =>
            {
                drawn.Add((text.Text, width, height));
                return [1, 2, 3];
            });
        await panel.AddVideosAsync([Video("lake.mp4", 10, 720, 1280)]);
        panel.AddText();
        panel.TextContent = "Hello";

        await panel.SaveAsync(PathOf("edited.mp4"));

        Assert.Equal([("Hello", 720, 1280)], drawn);
        Assert.Equal([[1, 2, 3]], _encoder.Pictures);
        Assert.False(Directory.Exists(_encoder.Work!.Folder));
    }

    [Fact]
    public async Task A_sound_gone_missing_stops_saving_too()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 10)]);
        await panel.AddSoundsAsync([Video("song.mp3", 5)]);
        File.Delete(PathOf("song.mp3"));

        await panel.SaveAsync(PathOf("edited.mp4"));

        Assert.Null(_encoder.Project);
        Assert.Contains("song.mp3 can't be found", panel.Message);
    }

    [Fact]
    public async Task Saying_how_long_a_clip_lasts_changes_its_speed_to_fit()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 30)]);
        Assert.Equal("0:30.0", panel.ClipLasts);
        Assert.Equal("Normal", panel.ClipSpeedText);

        panel.ClipLasts = "60";

        Assert.Equal(0.5, panel.Project.Clips[0].Speed);
        Assert.Equal(Seconds(60), panel.Length);
        Assert.Equal("1:00.0", panel.ClipLasts);
        Assert.Equal("50%", panel.ClipSpeedText);
        Assert.True(panel.ClipHasSpeed);
    }

    [Fact]
    public async Task A_length_that_cant_be_read_or_reached_is_put_back_or_held_in_range()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 30)]);

        panel.ClipLasts = "a minute";
        Assert.Equal(1, panel.Project.Clips[0].Speed);

        panel.ClipLasts = "1:00:00";
        Assert.Equal(EditClip.SlowestSpeed, panel.Project.Clips[0].Speed);
    }

    [Fact]
    public async Task Once_its_speed_changes_a_clip_with_sound_asks_about_its_sound_until_answered()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 30)]);
        Assert.False(panel.NeedsSoundChoice);

        panel.ClipSpeed = 50;
        Assert.True(panel.NeedsSoundChoice);
        Assert.Contains("deeper", panel.SoundChoiceQuestion);
        Assert.True(panel.ClipStretchesSound);

        panel.ClipKeepsSoundSpeed = true;
        Assert.False(panel.NeedsSoundChoice);
        Assert.False(panel.Project.Clips[0].StretchSound);

        panel.ClipSpeed = 200;
        Assert.False(panel.NeedsSoundChoice);
        Assert.Contains("higher", panel.SoundChoiceQuestion);
    }

    [Fact]
    public async Task A_muted_clip_isnt_asked_about_its_sound()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 30)]);
        panel.ClipVolume = 0;

        panel.ClipSpeed = 50;

        Assert.False(panel.NeedsSoundChoice);
    }

    [Fact]
    public async Task Sound_can_be_turned_up_four_times_and_evened_out()
    {
        var panel = NewPanel();
        await panel.AddVideosAsync([Video("lake.mp4", 30)]);
        await panel.AddSoundsAsync([Video("song.mp3", 10)]);

        panel.SoundVolume = 400;
        panel.SoundLevelIndex = (int)SoundLevel.Evened;
        panel.SelectedIndex = 0;
        panel.ClipVolume = 250;
        panel.ClipLevelIndex = (int)SoundLevel.Loudest;
        panel.ClipLevelIndex = 9;

        Assert.Equal((4.0, SoundLevel.Evened), (panel.Project.Sounds[0].Volume, panel.Project.Sounds[0].Level));
        Assert.Equal((2.5, SoundLevel.Loudest), (panel.Project.Clips[0].Volume, panel.Project.Clips[0].Level));
        Assert.Equal("250%", panel.ClipVolumeText);
    }

    [Theory]
    [InlineData("60", 60)]
    [InlineData("12.5", 12.5)]
    [InlineData("1:00", 60)]
    [InlineData("1:02:05", 3725)]
    [InlineData(" 45 s ", 45)]
    public void Lengths_are_read_as_seconds_or_minutes_and_seconds(string text, double seconds) =>
        Assert.Equal(Seconds(seconds), EditPanel.ParseLength(text));

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("-5")]
    [InlineData("1:2:3:4")]
    public void What_isnt_a_length_is_nothing(string text) => Assert.Null(EditPanel.ParseLength(text));

    [Theory]
    [InlineData("song.MP3", true)]
    [InlineData("birds.wav", true)]
    [InlineData("voice.m4a", true)]
    [InlineData("lake.mp4", false)]
    public void Sounds_are_told_from_videos_by_their_names(string name, bool sound) => Assert.Equal(sound, EditPanel.IsSoundFile(name));

    private sealed class FakeEncoder : IVideoEncoder
    {
        public EditProject? Project { get; private set; }

        public EncodePlan? Plan { get; private set; }

        public EditWork? Work { get; private set; }

        public List<byte[]> Pictures { get; } = [];

        public Exception? Fails { get; set; }

        /// <summary>Set to make it wait, once it has started, until it's stopped.</summary>
        public TaskCompletionSource? Waits { get; set; }

        public Task EncodeAsync(string input, string output, EncodePlan plan, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The editor only saves edits.");

        public async Task RenderAsync(EditProject project, EditWork work, string output, EncodePlan plan, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            Project = project;
            Plan = plan;
            Work = work;
            Assert.EndsWith(".part.mp4", output);
            Assert.True(Directory.Exists(work.Folder));
            Pictures.AddRange(work.TextPictures.Select(File.ReadAllBytes));
            await File.WriteAllTextAsync(output, "made", cancellationToken);

            if (Waits is { } waits)
            {
                waits.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (Fails is not null)
            {
                throw Fails;
            }
        }
    }
}
