using VideoDispatch.Core.Editing;
using VideoDispatch.Core.Media;
using VideoDispatch.Core.Reencoding;

namespace VideoDispatch.Core.Tests.Editing;

public sealed class EditProjectTests
{
    private static readonly EditClip Lake = EditClip.Whole(@"C:\Videos\lake.mp4", Seconds(10));
    private static readonly EditClip Ridge = EditClip.Whole(@"C:\Videos\ridge.mp4", Seconds(4));

    private static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);

    private static EditProject Two() => EditProject.Empty.Insert(Lake).Insert(Ridge);

    [Fact]
    public void Is_as_long_as_its_clips_one_after_another()
    {
        var project = Two();

        Assert.Equal(Seconds(14), project.Length);
        Assert.Equal(Seconds(10), project.StartOf(1));
        Assert.Equal(TimeSpan.Zero, EditProject.Empty.Length);
    }

    [Fact]
    public void Finds_which_clip_plays_at_a_moment_and_where_that_is_in_its_video()
    {
        var project = Two().TrimStart(1, Seconds(1));

        Assert.Equal(new EditMoment(0, TimeSpan.Zero, Seconds(9.5)), project.Find(Seconds(9.5)));
        Assert.Equal(new EditMoment(1, Seconds(10), Seconds(1)), project.Find(Seconds(10)));
        Assert.Equal(new EditMoment(1, Seconds(10), Seconds(3)), project.Find(Seconds(12)));
    }

    [Fact]
    public void The_very_end_and_beyond_belong_to_the_last_clip()
    {
        var project = Two();

        Assert.Equal(new EditMoment(1, Seconds(10), Seconds(4)), project.Find(Seconds(14)));
        Assert.Equal(new EditMoment(1, Seconds(10), Seconds(4)), project.Find(Seconds(99)));
        Assert.Equal(new EditMoment(0, TimeSpan.Zero, TimeSpan.Zero), project.Find(Seconds(-1)));
        Assert.Null(EditProject.Empty.Find(TimeSpan.Zero));
    }

    [Fact]
    public void Splitting_makes_two_clips_that_play_the_same_as_the_one()
    {
        var project = Two().SplitAt(Seconds(3.5))!;

        Assert.Equal(3, project.Clips.Count);
        Assert.Equal(Lake with { End = Seconds(3.5) }, project.Clips[0]);
        Assert.Equal(Lake with { Start = Seconds(3.5) }, project.Clips[1]);
        Assert.Equal(Ridge, project.Clips[2]);
        Assert.Equal(Seconds(14), project.Length);
    }

    [Fact]
    public void Splitting_a_trimmed_clip_cuts_at_the_same_moment_of_its_video()
    {
        var project = Two().TrimStart(1, Seconds(1)).SplitAt(Seconds(11))!;

        Assert.Equal(Ridge with { Start = Seconds(1), End = Seconds(2) }, project.Clips[1]);
        Assert.Equal(Ridge with { Start = Seconds(2) }, project.Clips[2]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.05)]
    [InlineData(9.95)]
    [InlineData(10)]
    [InlineData(14)]
    public void Splitting_right_at_an_edge_does_nothing(double at)
    {
        Assert.Null(Two().SplitAt(Seconds(at)));
    }

    [Fact]
    public void Splitting_nothing_does_nothing() => Assert.Null(EditProject.Empty.SplitAt(Seconds(1)));

    [Fact]
    public void Trimming_stays_inside_the_video_and_leaves_something_to_see()
    {
        var project = Two();

        Assert.Equal(TimeSpan.Zero, project.TrimStart(0, Seconds(-3)).Clips[0].Start);
        Assert.Equal(Seconds(10) - EditProject.ShortestClip, project.TrimStart(0, Seconds(30)).Clips[0].Start);
        Assert.Equal(Seconds(10), project.TrimEnd(0, Seconds(30)).Clips[0].End);
        Assert.Equal(EditProject.ShortestClip, project.TrimEnd(0, Seconds(-1)).Clips[0].End);

        var trimmed = project.TrimStart(0, Seconds(2)).TrimEnd(0, Seconds(7));
        Assert.Equal(Seconds(5), trimmed.Clips[0].Length);
        Assert.Equal(Seconds(9), trimmed.Length);
    }

    [Fact]
    public void A_trimmed_end_can_be_pulled_back_out_to_the_end_of_its_video()
    {
        var project = Two().TrimEnd(0, Seconds(5)).TrimEnd(0, Seconds(10));

        Assert.Equal(Lake, project.Clips[0]);
    }

    [Fact]
    public void Moving_puts_a_clip_where_it_lands()
    {
        var third = EditClip.Whole(@"C:\Videos\summit.mp4", Seconds(2));
        var project = Two().Insert(third);

        Assert.Equal([third, Lake, Ridge], project.Move(2, 0).Clips);
        Assert.Equal([Ridge, third, Lake], project.Move(0, 2).Clips);
        Assert.Equal([Lake, Ridge, third], project.Move(1, 1).Clips);
    }

    [Fact]
    public void Adding_goes_at_the_end_or_where_asked_and_removing_takes_out_one()
    {
        var third = EditClip.Whole(@"C:\Videos\summit.mp4", Seconds(2));

        Assert.Equal([third, Lake, Ridge], Two().Insert(third, 0).Clips);
        Assert.Equal([Lake, Ridge, third], Two().Insert(third, 99).Clips);
        Assert.Equal([Ridge], Two().Remove(0).Clips);
    }

    [Fact]
    public void Edits_leave_the_project_they_were_made_from_as_it_was()
    {
        var before = Two();

        before.SplitAt(Seconds(5));
        before.TrimStart(0, Seconds(1));
        before.Move(0, 1);
        before.Remove(1);

        Assert.Equal([Lake, Ridge], before.Clips);
    }

    [Fact]
    public void Lists_each_file_once_however_many_clips_and_sounds_come_from_it()
    {
        var project = Two().SplitAt(Seconds(5))!
            .Insert(Lake with { SourcePath = @"c:\videos\LAKE.mp4" })
            .AddSound(new EditSound(@"C:\Music\song.mp3", Seconds(30), TimeSpan.Zero, Seconds(30), TimeSpan.Zero))
            .AddSound(new EditSound(@"C:\Videos\ridge.mp4", Seconds(4), TimeSpan.Zero, Seconds(4), TimeSpan.Zero));

        Assert.Equal([@"C:\Videos\lake.mp4", @"C:\Videos\ridge.mp4", @"C:\Music\song.mp3"], project.Sources);
    }

    private static EditProject Crossfaded(double seconds) => Two().ChangeClip(1, Ridge with { Crossfade = Seconds(seconds) });

    [Fact]
    public void A_crossfade_starts_the_next_clip_early_so_the_edit_is_shorter()
    {
        var project = Crossfaded(1.5);

        Assert.Equal(Seconds(1.5), project.OverlapBefore(1));
        Assert.Equal(Seconds(8.5), project.StartOf(1));
        Assert.Equal(Seconds(12.5), project.Length);
    }

    [Fact]
    public void A_crossfade_is_never_more_than_half_of_either_clip_and_the_first_clip_has_none()
    {
        Assert.Equal(Seconds(2), Crossfaded(9).OverlapBefore(1));
        Assert.Equal(TimeSpan.Zero, Two().ChangeClip(0, Lake with { Crossfade = Seconds(3) }).OverlapBefore(0));
        Assert.Equal(Seconds(14), Two().ChangeClip(0, Lake with { Crossfade = Seconds(3) }).Length);
    }

    [Fact]
    public void Where_clips_overlap_the_moment_belongs_to_the_one_coming_in()
    {
        var project = Crossfaded(2);

        Assert.Equal(new EditMoment(0, TimeSpan.Zero, Seconds(7.9)), project.Find(Seconds(7.9)));
        Assert.Equal(new EditMoment(1, Seconds(8), Seconds(0)), project.Find(Seconds(8)));
        Assert.Equal(new EditMoment(1, Seconds(8), Seconds(1)), project.Find(Seconds(9)));
        Assert.Equal(new EditMoment(1, Seconds(8), Seconds(4)), project.Find(Seconds(12)));
    }

    [Fact]
    public void Where_clips_overlap_both_are_seen_the_next_fading_in_over_the_first()
    {
        var project = Crossfaded(2);

        var view = project.ViewAt(Seconds(9))!;

        Assert.Equal(new EditLook(0, Seconds(9), 1), view.Under);
        Assert.Equal(new EditLook(1, Seconds(1), 0.5), view.Over);
        Assert.Null(project.ViewAt(Seconds(5))!.Over);
        Assert.Null(EditProject.Empty.ViewAt(TimeSpan.Zero));
    }

    [Fact]
    public void Fades_darken_the_start_and_end_of_a_clip()
    {
        var project = Two().ChangeClip(0, Lake with { FadeIn = Seconds(2), FadeOut = Seconds(4) });

        Assert.Equal(1, project.ViewAt(TimeSpan.Zero)!.Black);
        Assert.Equal(0.5, project.ViewAt(Seconds(1))!.Black, 6);
        Assert.Equal(0, project.ViewAt(Seconds(4))!.Black);
        Assert.Equal(0.75, project.ViewAt(Seconds(9))!.Black, 6);
        Assert.Equal(0, project.ViewAt(Seconds(11))!.Black);
    }

    [Fact]
    public void A_fade_is_never_more_than_half_a_clip()
    {
        var project = Two().ChangeClip(1, Ridge with { FadeIn = Seconds(30) });

        Assert.Equal(Seconds(2), project.FadeInOf(1));
    }

    [Fact]
    public void A_clips_sound_follows_its_volume_its_fades_and_its_crossfades()
    {
        var project = Two()
            .ChangeClip(0, Lake with { Volume = 0.5, FadeIn = Seconds(2) })
            .ChangeClip(1, Ridge with { Crossfade = Seconds(2) });

        Assert.Equal(0.25, project.ClipGain(0, Seconds(1)), 6);
        Assert.Equal(0.5, project.ClipGain(0, Seconds(5)), 6);

        // Going out as the next comes in.
        Assert.Equal(0.25, project.ClipGain(0, Seconds(9)), 6);
        Assert.Equal(0.5, project.ClipGain(1, Seconds(1)), 6);
        Assert.Equal(1, project.ClipGain(1, Seconds(3)), 6);
    }

    [Fact]
    public void Splitting_keeps_how_the_clip_starts_on_the_first_half_and_how_it_ends_on_the_second()
    {
        var project = Two().ChangeClip(1, Ridge with { FadeIn = Seconds(1), FadeOut = Seconds(1), Crossfade = Seconds(1), Volume = 0.3, Zoom = 2 });

        var split = project.SplitAt(Seconds(11))!;

        Assert.Equal((Seconds(1), TimeSpan.Zero, Seconds(1)), (split.Clips[1].FadeIn, split.Clips[1].FadeOut, split.Clips[1].Crossfade));
        Assert.Equal((TimeSpan.Zero, Seconds(1), TimeSpan.Zero), (split.Clips[2].FadeIn, split.Clips[2].FadeOut, split.Clips[2].Crossfade));
        Assert.All(split.Clips.Skip(1), clip => Assert.Equal((0.3, 2.0), (clip.Volume, clip.Zoom)));
        Assert.Equal(project.Length, split.Length);
    }

    [Fact]
    public void Changing_a_clip_keeps_its_settings_in_range()
    {
        var clip = Two().ChangeClip(0, Lake with { Volume = 3, Zoom = 9, ZoomX = -1, ZoomY = 2, FadeIn = Seconds(-1) }).Clips[0];

        Assert.Equal((1.0, EditClip.MostZoom, 0.0, 1.0, TimeSpan.Zero), (clip.Volume, clip.Zoom, clip.ZoomX, clip.ZoomY, clip.FadeIn));
    }

    [Theory]
    [InlineData(2, 0.5, 0.5, 0.25, 0.25)]
    [InlineData(2, 0, 0, 0, 0)]
    [InlineData(2, 1, 1, 0.5, 0.5)]
    [InlineData(4, 0.9, 0.1, 0.75, 0)]
    [InlineData(1, 0.2, 0.8, 0, 0)]
    public void What_a_zoom_shows_stays_inside_the_frame(double zoom, double x, double y, double left, double top)
    {
        var view = (Lake with { Zoom = zoom, ZoomX = x, ZoomY = y }).ZoomedView();

        Assert.Equal((left, top, 1 / zoom), view);
    }

    private static EditSound Song(double at = 2) => new(@"C:\Music\song.mp3", Seconds(30), Seconds(5), Seconds(15), Seconds(at));

    [Fact]
    public void A_sound_can_be_slid_along_but_not_before_the_start()
    {
        var project = Two().AddSound(Song());

        Assert.Equal(Seconds(6), project.MoveSound(0, Seconds(6)).Sounds[0].At);
        Assert.Equal(TimeSpan.Zero, project.MoveSound(0, Seconds(-3)).Sounds[0].At);
    }

    [Fact]
    public void Trimming_a_sounds_start_leaves_the_rest_where_it_was()
    {
        var project = Two().AddSound(Song());

        var later = project.TrimSoundStart(0, Seconds(8)).Sounds[0];
        var earlier = project.TrimSoundStart(0, Seconds(0)).Sounds[0];

        Assert.Equal((Seconds(8), Seconds(5)), (later.Start, later.At));
        Assert.Equal((Seconds(3), TimeSpan.Zero), (earlier.Start, earlier.At));
        Assert.Equal(Seconds(30), project.TrimSoundEnd(0, Seconds(99)).Sounds[0].End);
        Assert.Equal(Seconds(5) + EditProject.ShortestClip, project.TrimSoundEnd(0, TimeSpan.Zero).Sounds[0].End);
    }

    [Fact]
    public void A_sound_is_only_heard_until_the_edit_ends_with_its_volume_and_fades()
    {
        var project = Two().AddSound(Song(at: 10) with { Volume = 0.8, FadeIn = Seconds(2), FadeOut = Seconds(4) });

        Assert.Equal(Seconds(4), project.HeardLengthOf(0));
        Assert.Equal(TimeSpan.Zero, project.MoveSound(0, Seconds(20)).HeardLengthOf(0));
        Assert.Equal(0.4, project.SoundGain(0, Seconds(1)), 6);
        Assert.Equal(0.8, project.SoundGain(0, Seconds(4)), 6);
        Assert.Equal(0.2, project.SoundGain(0, Seconds(9)), 6);
    }

    [Fact]
    public void Sounds_and_words_can_be_changed_and_taken_out_without_touching_the_others()
    {
        var hello = new EditText("Hello", Seconds(1), Seconds(2));
        var bye = new EditText("Bye", Seconds(5), Seconds(2));
        var project = Two().AddSound(Song()).AddSound(Song(at: 8)).AddText(hello).AddText(bye);

        Assert.Equal([Seconds(8)], project.RemoveSound(0).Sounds.Select(sound => sound.At));
        Assert.Equal([bye], project.RemoveText(0).Texts);
        Assert.Equal("Hi", project.ChangeText(0, hello with { Text = "Hi" }).Texts[0].Text);
        Assert.Equal(bye, project.ChangeText(0, hello with { Text = "Hi" }).Texts[1]);
        Assert.Equal(2, project.Remove(0).Sounds.Count);
    }

    [Fact]
    public void Words_show_from_their_start_until_their_finish()
    {
        var project = Two().AddText(new EditText("Hello", Seconds(1), Seconds(2)));

        Assert.Empty(project.TextsAt(Seconds(0.5)));
        Assert.Single(project.TextsAt(Seconds(1)));
        Assert.Single(project.TextsAt(Seconds(2.9)));
        Assert.Empty(project.TextsAt(Seconds(3)));
    }

    [Fact]
    public void Trimming_words_keeps_them_showing_for_a_moment_at_least()
    {
        var project = Two().AddText(new EditText("Hello", Seconds(1), Seconds(2)));

        var later = project.TrimTextStart(0, Seconds(2)).Texts[0];
        Assert.Equal((Seconds(2), Seconds(1)), (later.At, later.Length));
        Assert.Equal(EditProject.ShortestClip, project.TrimTextStart(0, Seconds(9)).Texts[0].Length);
        Assert.Equal(Seconds(5), project.TrimTextEnd(0, Seconds(6)).Texts[0].Length);
        Assert.Equal(EditProject.ShortestClip, project.TrimTextEnd(0, TimeSpan.Zero).Texts[0].Length);
    }
}

public sealed class EditStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "edit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private string PathOf(string name) => Path.Combine(_folder, name);

    [Fact]
    public void Keeps_an_edit_for_next_time()
    {
        var project = EditProject.Empty
            .Insert(EditClip.Whole(@"C:\Videos\lake.mp4", TimeSpan.FromSeconds(10)))
            .SplitAt(TimeSpan.FromSeconds(2.5))!
            .Move(1, 0);

        new EditStore(PathOf("edit.json")).Save(project);
        var read = new EditStore(PathOf("edit.json")).Load();

        Assert.Equal(project.Clips, read.Clips);
    }

    [Fact]
    public void Nothing_saved_yet_is_an_empty_edit() => Assert.Empty(new EditStore(PathOf("edit.json")).Load().Clips);

    [Fact]
    public void A_damaged_file_is_an_empty_edit()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(PathOf("edit.json"), "{ not json");

        Assert.Empty(new EditStore(PathOf("edit.json")).Load().Clips);
    }

    [Fact]
    public void A_clip_that_makes_no_sense_is_left_out()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(PathOf("edit.json"), """
            { "clips": [
              { "sourcePath": "C:\\Videos\\good.mp4", "sourceLength": "00:00:10", "start": "00:00:01", "end": "00:00:02" },
              { "sourcePath": "C:\\Videos\\backwards.mp4", "sourceLength": "00:00:10", "start": "00:00:05", "end": "00:00:02" },
              { "sourcePath": "C:\\Videos\\past-the-end.mp4", "sourceLength": "00:00:10", "start": "00:00:05", "end": "00:00:12" },
              { "sourcePath": "", "sourceLength": "00:00:10", "start": "00:00:00", "end": "00:00:02" }
            ] }
            """);

        var read = new EditStore(PathOf("edit.json")).Load();

        Assert.Equal(["good.mp4"], read.Clips.Select(clip => clip.Name));
    }

    [Fact]
    public void Keeps_clips_settings_sounds_and_words()
    {
        var project = EditProject.Empty
            .Insert(EditClip.Whole(@"C:\Videos\lake.mp4", TimeSpan.FromSeconds(10)) with { Volume = 0.5, FadeIn = TimeSpan.FromSeconds(1), Zoom = 2, ZoomX = 0.3 })
            .Insert(EditClip.Whole(@"C:\Videos\ridge.mp4", TimeSpan.FromSeconds(4)) with { Crossfade = TimeSpan.FromSeconds(1) })
            .AddSound(new EditSound(@"C:\Music\song.mp3", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(1)) { Volume = 0.7, FadeOut = TimeSpan.FromSeconds(2) })
            .AddText(new EditText("Hello", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)) { Place = TextPlace.Top, Colour = TextColour.Yellow, Box = true });

        new EditStore(PathOf("edit.json")).Save(project);
        var read = new EditStore(PathOf("edit.json")).Load();

        Assert.Equal(project.Clips, read.Clips);
        Assert.Equal(project.Sounds, read.Sounds);
        Assert.Equal(project.Texts, read.Texts);
        Assert.Contains("\"place\": \"Top\"", File.ReadAllText(PathOf("edit.json")));
    }

    [Fact]
    public void An_edit_kept_before_sounds_and_words_existed_still_reads()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(PathOf("edit.json"), """
            { "clips": [ { "sourcePath": "C:\\Videos\\good.mp4", "sourceLength": "00:00:10", "start": "00:00:01", "end": "00:00:02" } ] }
            """);

        var read = new EditStore(PathOf("edit.json")).Load();

        var clip = Assert.Single(read.Clips);
        Assert.Equal((1.0, 1.0, 0.5), (clip.Volume, clip.Zoom, clip.ZoomX));
        Assert.Empty(read.Sounds);
        Assert.Empty(read.Texts);
    }

    [Fact]
    public void Settings_out_of_range_are_brought_in_and_sounds_or_words_that_make_no_sense_are_left_out()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(PathOf("edit.json"), """
            {
              "clips": [ { "sourcePath": "C:\\Videos\\good.mp4", "sourceLength": "00:00:10", "start": "00:00:01", "end": "00:00:02", "volume": 7, "zoom": 0.2 } ],
              "sounds": [
                { "sourcePath": "C:\\Music\\good.mp3", "sourceLength": "00:00:10", "start": "00:00:00", "end": "00:00:05", "at": "-00:00:03", "volume": -1 },
                { "sourcePath": "C:\\Music\\backwards.mp3", "sourceLength": "00:00:10", "start": "00:00:05", "end": "00:00:02", "at": "00:00:00" }
              ],
              "texts": [
                { "text": "Hello", "at": "00:00:01", "length": "00:00:02", "place": "Top" },
                { "text": "", "at": "00:00:01", "length": "00:00:02" },
                { "text": "Odd", "at": "00:00:01", "length": "00:00:02", "size": 9 }
              ]
            }
            """);

        var read = new EditStore(PathOf("edit.json")).Load();

        Assert.Equal((1.0, 1.0), (read.Clips[0].Volume, read.Clips[0].Zoom));
        var sound = Assert.Single(read.Sounds);
        Assert.Equal((TimeSpan.Zero, 0.0), (sound.At, sound.Volume));
        Assert.Equal(["Hello"], read.Texts.Select(text => text.Text));
    }

    [Fact]
    public void Without_a_file_it_keeps_the_edit_in_memory()
    {
        var store = new EditStore(null);
        var project = EditProject.Empty.Insert(EditClip.Whole("a.mp4", TimeSpan.FromSeconds(1)));

        store.Save(project);

        Assert.Same(project, store.Load());
        Assert.False(Directory.Exists(_folder));
    }
}

public sealed class EditPlanTests
{
    [Fact]
    public void Saves_an_edit_at_the_first_clips_size_and_frame_rate_for_as_long_as_the_edit()
    {
        var phone = new VideoMetadata(1920, 1080, 90, TimeSpan.FromSeconds(60), 30, "avc1");

        var plan = EncodePlan.ForEdit(phone, TimeSpan.FromSeconds(12));

        Assert.Equal(1080, plan.Width);
        Assert.Equal(1920, plan.Height);
        Assert.Equal(30, plan.FramesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(12), plan.Duration);
        Assert.False(plan.SourceIsSideways);
        Assert.Equal(18, plan.ConstantQuality);
        Assert.Equal((long)(1080 * 1920 * 30 * 0.16), plan.VideoBitrate);
    }

    [Fact]
    public void A_first_clip_that_could_not_be_read_makes_landscape_1080p()
    {
        var plan = EncodePlan.ForEdit(null, TimeSpan.FromSeconds(5));

        Assert.Equal(1920, plan.Width);
        Assert.Equal(1080, plan.Height);
        Assert.Equal(8_000_000, plan.VideoBitrate);
        Assert.Equal(TimeSpan.FromSeconds(5), plan.Duration);
    }
}
