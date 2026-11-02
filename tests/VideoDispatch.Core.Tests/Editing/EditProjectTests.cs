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
    public void Lists_each_video_once_however_many_clips_come_from_it()
    {
        var project = Two().SplitAt(Seconds(5))!.Insert(Lake with { SourcePath = @"c:\videos\LAKE.mp4" });

        Assert.Equal([@"C:\Videos\lake.mp4", @"C:\Videos\ridge.mp4"], project.Sources);
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
