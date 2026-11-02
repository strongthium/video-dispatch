using VideoDispatch.Core.Editing;
using VideoDispatch.Core.Media;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Reencoding;
using VideoDispatch.Core.Uploads;

namespace VideoDispatch.Core.Tests.Reencoding;

public sealed class EncodeRenderStepTests : IDisposable
{
    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "render-" + Guid.NewGuid().ToString("N"));
    private readonly string _rendered;
    private readonly string _source;
    private readonly FakeEncoder _encoder = new();
    private readonly EncodeRenderStep _step;
    private VideoMetadata? _metadata = new(1080, 1920, 0, TimeSpan.FromSeconds(10), 30, "avc1");

    public EncodeRenderStepTests()
    {
        Directory.CreateDirectory(_folder);
        _rendered = Path.Combine(_folder, "rendered");
        _source = Path.Combine(_folder, "clip.mp4");
        File.WriteAllBytes(_source, new byte[1000]);
        _step = new EncodeRenderStep(() => _encoder, _rendered, _ => _metadata);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private UploadJob Job(EncodeSettings? encode, string? source = null, Guid? batch = null, Platform platform = Platform.YouTube)
    {
        var path = source ?? _source;
        return new UploadJob
        {
            Id = Guid.NewGuid(),
            BatchId = batch ?? Guid.NewGuid(),
            SourcePath = path,
            SourceLength = new FileInfo(path).Length,
            SourceLastWriteUtc = File.GetLastWriteTimeUtc(path),
            Details = platform == Platform.YouTube ? new YouTubePostDetails { Encode = encode } : new TikTokPostDetails { Encode = encode },
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
    }

    [Fact]
    public async Task Without_settings_the_file_goes_as_it_is_and_no_engine_runs()
    {
        var result = await _step.RenderAsync(Job(null));

        Assert.Null(result.Path);
        Assert.Null(result.Note);
        Assert.Equal(0, _encoder.Runs);
    }

    [Fact]
    public async Task Makes_a_new_file_and_leaves_the_source_alone()
    {
        var before = File.ReadAllBytes(_source);

        var result = await _step.RenderAsync(Job(new EncodeSettings(EncodeGoal.Balanced)));

        Assert.Equal(_rendered, Path.GetDirectoryName(result.Path));
        Assert.EndsWith(".mp4", result.Path);
        Assert.DoesNotContain(".part", result.Path);
        Assert.Equal(100, new FileInfo(result.Path!).Length);
        Assert.Equal(before, File.ReadAllBytes(_source));
        Assert.Equal(_source, _encoder.LastInput);
    }

    [Fact]
    public async Task Passes_the_plan_for_the_goal_to_the_engine()
    {
        await _step.RenderAsync(Job(new EncodeSettings(EncodeGoal.SmallFile)));

        Assert.Equal((720, 1280), (_encoder.LastPlan!.Width, _encoder.LastPlan.Height));
    }

    [Fact]
    public async Task Both_platforms_asking_for_the_same_thing_share_one_encode()
    {
        var batch = Guid.NewGuid();
        var encode = new EncodeSettings(EncodeGoal.Balanced);
        var release = new TaskCompletionSource();
        _encoder.Work = release.Task;

        var first = _step.RenderAsync(Job(encode, batch: batch));
        var second = _step.RenderAsync(Job(encode, batch: batch, platform: Platform.TikTok));
        release.SetResult();

        Assert.Equal((await first).Path, (await second).Path);
        Assert.Equal(1, _encoder.Runs);
    }

    [Fact]
    public async Task Different_choices_for_the_same_video_make_different_files()
    {
        var batch = Guid.NewGuid();

        var high = await _step.RenderAsync(Job(new EncodeSettings(EncodeGoal.HighQuality), batch: batch));
        var small = await _step.RenderAsync(Job(new EncodeSettings(EncodeGoal.SmallFile), batch: batch));

        Assert.NotEqual(high.Path, small.Path);
        Assert.Equal(2, _encoder.Runs);
    }

    [Fact]
    public async Task A_source_changed_since_queueing_fails_rather_than_encoding_something_else()
    {
        var job = Job(new EncodeSettings(EncodeGoal.Balanced));
        File.WriteAllBytes(_source, new byte[2000]);

        var problem = await Assert.ThrowsAsync<UploadException>(() => _step.RenderAsync(job));

        Assert.Contains("changed since", problem.Message);
        Assert.Equal(0, _encoder.Runs);
    }

    [Fact]
    public async Task An_MP4_that_would_not_get_smaller_goes_as_it_is_and_says_so()
    {
        _encoder.OutputSize = 5000;
        var batch = Guid.NewGuid();
        var encode = new EncodeSettings(EncodeGoal.HighQuality);

        var result = await _step.RenderAsync(Job(encode, batch: batch));

        Assert.Null(result.Path);
        Assert.Contains("wouldn't have made this smaller", result.Note);
        Assert.Empty(Directory.EnumerateFiles(_rendered));

        // The other platform needn't try it again to find that out.
        Assert.Contains("smaller", (await _step.RenderAsync(Job(encode, batch: batch, platform: Platform.TikTok))).Note);
        Assert.Equal(1, _encoder.Runs);
    }

    [Fact]
    public async Task Another_format_is_converted_even_if_it_comes_out_bigger()
    {
        var mov = Path.Combine(_folder, "clip.mov");
        File.WriteAllBytes(mov, new byte[1000]);
        _encoder.OutputSize = 5000;

        var result = await _step.RenderAsync(Job(new EncodeSettings(EncodeGoal.HighQuality), mov));

        Assert.NotNull(result.Path);
    }

    [Fact]
    public async Task An_MP4_that_already_fits_goes_as_it_is_without_running_anything()
    {
        var result = await _step.RenderAsync(Job(new EncodeSettings(EncodeGoal.FitSize, 1)));

        Assert.Null(result.Path);
        Assert.Contains("already fits", result.Note);
        Assert.Equal(0, _encoder.Runs);
    }

    [Fact]
    public async Task A_problem_with_the_settings_fails_for_good()
    {
        _metadata = null;

        var problem = await Assert.ThrowsAsync<UploadException>(() => _step.RenderAsync(Job(new EncodeSettings(EncodeGoal.FitSize, 1))));

        Assert.Equal(UploadFailureKind.Permanent, problem.Kind);
    }

    [Fact]
    public async Task An_engine_failure_fails_for_good_with_its_message()
    {
        _encoder.Fails = new EncodeException("It couldn't read that.");

        var problem = await Assert.ThrowsAsync<UploadException>(() => _step.RenderAsync(Job(new EncodeSettings(EncodeGoal.Balanced))));

        Assert.Equal(UploadFailureKind.Permanent, problem.Kind);
        Assert.Equal("It couldn't read that.", problem.Message);
    }

    [Fact]
    public async Task An_engine_that_makes_nothing_is_a_failure()
    {
        _encoder.OutputSize = 0;

        await Assert.ThrowsAsync<UploadException>(() => _step.RenderAsync(Job(new EncodeSettings(EncodeGoal.Balanced))));
    }

    [Fact]
    public async Task Stopping_leaves_no_half_made_file()
    {
        using var stop = new CancellationTokenSource();
        _encoder.OnRun = stop.Cancel;
        _encoder.WriteFirst = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _step.RenderAsync(Job(new EncodeSettings(EncodeGoal.Balanced)), null, stop.Token));

        Assert.Empty(Directory.EnumerateFiles(_rendered));
    }

    [Fact]
    public async Task A_finished_file_is_used_again_after_a_restart()
    {
        var job = Job(new EncodeSettings(EncodeGoal.Balanced));
        var first = await _step.RenderAsync(job);

        var again = new EncodeRenderStep(() => _encoder, _rendered, _ => _metadata);
        var second = await again.RenderAsync(job);

        Assert.Equal(first.Path, second.Path);
        Assert.Equal(1, _encoder.Runs);
    }

    [Fact]
    public async Task Release_deletes_only_what_it_made()
    {
        var made = await _step.RenderAsync(Job(new EncodeSettings(EncodeGoal.Balanced)));

        _step.Release(_source);
        Assert.True(File.Exists(_source));

        _step.Release(made.Path!);
        Assert.False(File.Exists(made.Path));
    }

    [Fact]
    public async Task Tidying_clears_what_nothing_needs()
    {
        var kept = await _step.RenderAsync(Job(new EncodeSettings(EncodeGoal.Balanced)));
        var left = await _step.RenderAsync(Job(new EncodeSettings(EncodeGoal.SmallFile)));

        _step.Tidy([kept.Path!]);

        Assert.True(File.Exists(kept.Path));
        Assert.False(File.Exists(left.Path));
        Assert.True(File.Exists(_source));
    }

    [Fact]
    public void Tidying_before_anything_was_made_is_fine() => _step.Tidy([]);

    private sealed class FakeEncoder : IVideoEncoder
    {
        public int Runs { get; private set; }

        public string? LastInput { get; private set; }

        public EncodePlan? LastPlan { get; private set; }

        public int OutputSize { get; set; } = 100;

        public Task Work { get; set; } = Task.CompletedTask;

        public bool WriteFirst { get; set; }

        public Action? OnRun { get; set; }

        public Exception? Fails { get; set; }

        public async Task EncodeAsync(string input, string output, EncodePlan plan, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            Runs++;
            LastInput = input;
            LastPlan = plan;

            if (WriteFirst)
            {
                File.WriteAllBytes(output, new byte[OutputSize]);
            }

            OnRun?.Invoke();
            await Work;
            cancellationToken.ThrowIfCancellationRequested();

            if (Fails is not null)
            {
                throw Fails;
            }

            File.WriteAllBytes(output, new byte[OutputSize]);
        }

        public Task RenderAsync(EditProject project, EditWork work, string output, EncodePlan plan, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The queue never saves edits.");
    }
}
