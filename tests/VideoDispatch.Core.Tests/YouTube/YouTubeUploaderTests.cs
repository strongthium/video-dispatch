using System.Net;
using System.Text.Json;
using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Tests.TikTok;
using VideoDispatch.Core.Uploads;
using VideoDispatch.Core.YouTube;

namespace VideoDispatch.Core.Tests.YouTube;

public sealed class YouTubeUploaderTests : IDisposable
{
    private const int Chunk = 256 * 1024;
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    // Test files stay beside the test program, in the ignored bin folder.
    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "uploads-" + Guid.NewGuid().ToString("N"));
    private readonly FakeYouTube _server = new();
    private readonly FakeTokens _tokens = new(Platform.YouTube);
    private readonly MemorySessions _sessions = new();
    private readonly JumpingTime _time = new(Start);
    private readonly YouTubeUploader _uploader;

    public YouTubeUploaderTests()
    {
        Directory.CreateDirectory(_folder);
        _uploader = new YouTubeUploader(new HttpClient(_server) { Timeout = Timeout.InfiniteTimeSpan }, _tokens, _sessions, _time)
        {
            ChunkSize = Chunk,
        };
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static YouTubePostDetails Details() => new()
    {
        Title = "Title",
        Description = "Text",
        Privacy = YouTubePrivacy.Private,
        MadeForKids = false,
    };

    private string WriteFile(long size, string name = "clip.mp4")
    {
        var path = Path.Combine(_folder, name);
        var bytes = new byte[size];
        for (long i = 0; i < size; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static UploadJob JobFor(string path, YouTubePostDetails? details = null) => new()
    {
        Id = Guid.NewGuid(),
        BatchId = Guid.NewGuid(),
        SourcePath = path,
        SourceLength = new FileInfo(path).Length,
        SourceLastWriteUtc = File.GetLastWriteTimeUtc(path),
        Details = details ?? Details(),
        CreatedAt = Start,
    };

    private UploadJob SmallJob(YouTubePostDetails? details = null) => JobFor(WriteFile(1000), details);

    private async Task<UploadException> UploadFailsAsync(UploadJob job) =>
        await Assert.ThrowsAsync<UploadException>(() => _uploader.UploadAsync(job));

    private async Task<UploadException> ResultFailsAsync() =>
        await Assert.ThrowsAsync<UploadException>(() => _uploader.WaitForResultAsync("video-1"));

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new ByteArrayContent([]) };

    [Fact]
    public async Task Sends_every_chunk_in_order_and_the_file_arrives_whole()
    {
        var size = 2L * Chunk + 123;
        var path = WriteFile(size);
        var progress = new RecordingProgress();

        var receipt = await _uploader.UploadAsync(JobFor(path), progress);

        Assert.Equal("video-1", receipt.RemoteId);
        Assert.Null(receipt.Warning);
        Assert.Equal(
            [
                $"bytes 0-{Chunk - 1}/{size}",
                $"bytes {Chunk}-{2 * Chunk - 1}/{size}",
                $"bytes {2 * Chunk}-{size - 1}/{size}",
            ],
            _server.Puts.Select(put => put.ContentRange));
        Assert.Equal(File.ReadAllBytes(path), _server.ReceivedIn(_server.Puts[0].Session));

        Assert.All(_server.Puts, put =>
        {
            Assert.Equal("video/mp4", put.ContentType);
            Assert.Equal("Bearer token-1", put.Authorization);
            Assert.Equal(put.BodyLength, put.ContentLength);
        });

        Assert.Equal(size, progress.Values.Last());
    }

    [Fact]
    public async Task Every_chunk_but_the_last_is_a_multiple_of_256_KiB()
    {
        await _uploader.UploadAsync(JobFor(WriteFile(3L * Chunk + 1)));

        Assert.All(_server.Puts.SkipLast(1), put => Assert.Equal(0, put.BodyLength % Chunk));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk + 1)]
    public void Refuses_a_chunk_size_YouTube_would_refuse(int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new YouTubeUploader(
            new HttpClient(_server) { Timeout = Timeout.InfiniteTimeSpan },
            _tokens,
            _sessions,
            _time)
        {
            ChunkSize = size,
        });
    }

    [Fact]
    public void Refuses_a_client_with_a_timeout()
    {
        Assert.Throws<ArgumentException>(() => new YouTubeUploader(new HttpClient(_server), _tokens, _sessions, _time));
    }

    [Fact]
    public async Task Refuses_a_job_for_another_platform()
    {
        var path = WriteFile(10);
        var job = JobFor(path) with { Details = new TikTokPostDetails() };

        await Assert.ThrowsAsync<ArgumentException>(() => _uploader.UploadAsync(job));
    }

    [Fact]
    public async Task Starts_the_session_with_everything_YouTube_needs()
    {
        var size = 1000;
        var publishAt = new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.FromHours(-5));
        var details = Details() with
        {
            Title = "Café",
            Tags = ["one", "two words"],
            CategoryId = "27",
            MadeForKids = true,
            PublishAt = publishAt,
        };

        await _uploader.UploadAsync(JobFor(WriteFile(size), details));

        var start = _server.Starts.Single();
        Assert.Equal("Bearer token-1", start.Authorization);
        Assert.Equal("1000", start.UploadLength);
        Assert.Equal("video/mp4", start.UploadType);
        Assert.Contains("part=snippet%2Cstatus", start.Url);

        var body = JsonText.Parse(start.Body);
        var snippet = body.GetProperty("snippet");
        var status = body.GetProperty("status");
        Assert.Equal("Café", snippet.GetProperty("title").GetString());
        Assert.Equal("Text", snippet.GetProperty("description").GetString());
        Assert.Equal(["one", "two words"], snippet.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()));
        Assert.Equal("27", snippet.GetProperty("categoryId").GetString());
        Assert.Equal("private", status.GetProperty("privacyStatus").GetString());
        Assert.Equal("2026-11-02T14:30:00Z", status.GetProperty("publishAt").GetString());
        Assert.True(status.GetProperty("selfDeclaredMadeForKids").GetBoolean());
    }

    [Fact]
    public async Task Leaves_out_empty_tags_and_an_unset_time_and_fills_in_a_category()
    {
        await _uploader.UploadAsync(SmallJob());

        var body = JsonText.Parse(_server.Starts.Single().Body);
        Assert.False(body.GetProperty("snippet").TryGetProperty("tags", out _));
        Assert.False(body.GetProperty("status").TryGetProperty("publishAt", out _));

        // YouTube's errors page says a video needs both a title and a category.
        Assert.Equal(YouTubeUploader.DefaultCategoryId, body.GetProperty("snippet").GetProperty("categoryId").GetString());
    }

    [Theory]
    [InlineData(YouTubePrivacy.Private, "private")]
    [InlineData(YouTubePrivacy.Unlisted, "unlisted")]
    [InlineData(YouTubePrivacy.Public, "public")]
    public async Task Sends_privacy_in_lowercase(YouTubePrivacy privacy, string expected)
    {
        _server.PrivacyToReport = expected;

        await _uploader.UploadAsync(SmallJob(Details() with { Privacy = privacy }));

        Assert.Equal(expected, JsonText.Parse(_server.Starts.Single().Body).GetProperty("status").GetProperty("privacyStatus").GetString());
    }

    [Fact]
    public async Task Never_starts_without_a_privacy_choice_because_YouTube_would_make_it_public()
    {
        var failure = await UploadFailsAsync(SmallJob(Details() with { Privacy = null }));

        Assert.Equal(UploadFailureKind.Permanent, failure.Kind);
        Assert.Empty(_server.Starts);
    }

    [Fact]
    public async Task Never_starts_when_the_scheduled_time_has_passed_because_YouTube_would_publish_at_once()
    {
        var failure = await UploadFailsAsync(SmallJob(Details() with { PublishAt = Start.AddMinutes(-1) }));

        Assert.Equal(UploadFailureKind.Permanent, failure.Kind);
        Assert.Empty(_server.Starts);
    }

    [Fact]
    public async Task Saves_the_session_before_sending_and_forgets_it_when_done()
    {
        var job = SmallJob();
        string? savedAtFirstChunk = null;
        _server.OnPut = (_, _) =>
        {
            savedAtFirstChunk = _sessions.Get(job.Id);
            return null;
        };

        await _uploader.UploadAsync(job);

        Assert.StartsWith(FakeYouTube.SessionPrefix, savedAtFirstChunk);
        Assert.Empty(_sessions.Saved);
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        var failure = await UploadFailsAsync(JobFor(WriteFile(0)));

        Assert.Equal(UploadFailureKind.Permanent, failure.Kind);
        Assert.Empty(_server.Starts);
    }

    [Fact]
    public async Task A_file_changed_since_queueing_is_refused()
    {
        var job = SmallJob();
        File.WriteAllBytes(job.SourcePath, new byte[999]);

        var failure = await UploadFailsAsync(job);

        Assert.Equal(UploadFailureKind.Permanent, failure.Kind);
        Assert.Empty(_server.Starts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData((2 * Chunk) - 1)]
    [InlineData(2 * Chunk)]
    [InlineData((2 * Chunk) + 99)]
    public async Task Resumes_from_exactly_where_YouTube_says_at_every_boundary(int alreadyThere)
    {
        var size = (2L * Chunk) + 100;
        var path = WriteFile(size);
        var bytes = File.ReadAllBytes(path);
        var job = JobFor(path);
        var session = _server.Seed(bytes[..alreadyThere]);
        _sessions.Save(job.Id, session);

        var receipt = await _uploader.UploadAsync(job);

        Assert.Empty(_server.Starts);
        Assert.Equal(bytes, _server.ReceivedIn(session));
        Assert.Equal("video-1", receipt.RemoteId);

        Assert.True(_server.Puts[0].IsStatusCheck);
        Assert.Equal($"bytes */{size}", _server.Puts[0].ContentRange);
        Assert.Equal(0, _server.Puts[0].ContentLength);
        Assert.StartsWith($"bytes {alreadyThere}-", _server.Puts[1].ContentRange);
    }

    [Fact]
    public async Task A_session_that_already_finished_makes_no_second_copy()
    {
        var path = WriteFile(1000);
        var job = JobFor(path);
        var session = _server.Seed(File.ReadAllBytes(path));
        _sessions.Save(job.Id, session);
        var progress = new RecordingProgress();

        var receipt = await _uploader.UploadAsync(job, progress);

        Assert.Equal("video-1", receipt.RemoteId);
        Assert.Empty(_server.Starts);
        Assert.Single(_server.Puts);
        Assert.True(_server.Puts[0].IsStatusCheck);
        Assert.Equal(1000, progress.Values.Last());
    }

    [Fact]
    public async Task A_lost_answer_to_the_last_chunk_finds_the_video_instead_of_uploading_again()
    {
        _server.AfterTaking = (number, _) =>
        {
            if (number == 0)
            {
                throw new HttpRequestException("Connection dropped.");
            }
        };

        var receipt = await _uploader.UploadAsync(SmallJob());

        Assert.Equal("video-1", receipt.RemoteId);
        Assert.Single(_server.Starts);
        Assert.Equal(2, _server.Puts.Count);
        Assert.True(_server.Puts[1].IsStatusCheck);
    }

    [Fact]
    public async Task Carries_on_from_the_Range_header_when_YouTube_took_only_part_of_a_chunk()
    {
        var size = 2L * Chunk;
        var path = WriteFile(size);
        _server.KeepOnly = (number, _) => number == 0 ? 1000 : null;

        await _uploader.UploadAsync(JobFor(path));

        Assert.StartsWith("bytes 1000-", _server.Puts[1].ContentRange);
        Assert.Equal(File.ReadAllBytes(path), _server.ReceivedIn(_server.Puts[0].Session));
        Assert.Empty(_time.Delays);
    }

    [Fact]
    public async Task No_Range_header_means_nothing_arrived()
    {
        var path = WriteFile(2L * Chunk);
        _server.KeepOnly = (number, _) => number == 0 ? 0 : null;

        await _uploader.UploadAsync(JobFor(path));

        Assert.Equal($"bytes */{2 * Chunk}", _server.Puts[1].ContentRange);
        Assert.StartsWith("bytes 0-", _server.Puts[2].ContentRange);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Server_trouble_asks_where_things_stand_and_carries_on_with_backoff(HttpStatusCode status)
    {
        var path = WriteFile(2L * Chunk);
        var failures = 2;
        _server.OnPut = (_, _) => failures-- > 0 ? Status(status) : null;

        await _uploader.UploadAsync(JobFor(path));

        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], _time.Delays);
        Assert.True(_server.Puts[1].IsStatusCheck);
        Assert.Equal(File.ReadAllBytes(path), _server.ReceivedIn(_server.Puts[0].Session));
    }

    [Fact]
    public async Task Waits_as_long_as_Retry_After_says()
    {
        var failed = false;
        _server.OnPut = (_, _) =>
        {
            if (failed)
            {
                return null;
            }

            failed = true;
            var response = Status(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        };

        await _uploader.UploadAsync(SmallJob());

        Assert.Equal([TimeSpan.FromSeconds(30)], _time.Delays);
    }

    [Fact]
    public async Task Waits_when_a_308_says_to()
    {
        var path = WriteFile(2L * Chunk);
        _server.OnPut = (number, _) =>
        {
            if (number != 0)
            {
                return null;
            }

            var response = Status(HttpStatusCode.PermanentRedirect);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        };

        await _uploader.UploadAsync(JobFor(path));

        Assert.Contains(TimeSpan.FromSeconds(7), _time.Delays);
    }

    [Fact]
    public async Task A_long_Retry_After_goes_back_to_the_caller_and_keeps_the_session()
    {
        var job = SmallJob();
        _server.OnPut = (_, _) =>
        {
            var response = Status(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return response;
        };

        var failure = await UploadFailsAsync(job);

        Assert.Equal(UploadFailureKind.Retryable, failure.Kind);
        Assert.Equal(Start.AddHours(1), failure.RetryAt);
        Assert.True(_sessions.Saved.ContainsKey(job.Id));
    }

    [Fact]
    public async Task Gives_up_after_repeated_failures_and_a_later_try_resumes_the_same_session()
    {
        var path = WriteFile(2L * Chunk);
        var job = JobFor(path);
        _server.OnPut = (_, _) => Status(HttpStatusCode.ServiceUnavailable);

        var failure = await UploadFailsAsync(job);

        Assert.Equal(UploadFailureKind.Retryable, failure.Kind);
        Assert.Equal([2, 4, 8, 16, 32], _time.Delays.Select(delay => delay.TotalSeconds));
        Assert.Single(_server.Starts);

        _server.OnPut = null;
        await _uploader.UploadAsync(job);

        Assert.Single(_server.Starts);
        Assert.Equal(File.ReadAllBytes(path), _server.ReceivedIn(_server.Puts[0].Session));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task An_expired_session_starts_a_new_one_from_the_beginning(HttpStatusCode status)
    {
        var path = WriteFile(2L * Chunk);
        var job = JobFor(path);
        _server.OnPut = (number, _) => number == 1 ? Status(status) : null;

        await _uploader.UploadAsync(job);

        Assert.Equal(2, _server.Starts.Count);
        Assert.Equal(File.ReadAllBytes(path), _server.ReceivedIn(FakeYouTube.SessionPrefix + 2));
        Assert.Empty(_sessions.Saved);
    }

    [Fact]
    public async Task Sessions_that_keep_expiring_go_back_to_the_caller()
    {
        _server.OnPut = (_, _) => Status(HttpStatusCode.NotFound);

        var failure = await UploadFailsAsync(SmallJob());

        Assert.Equal(UploadFailureKind.Retryable, failure.Kind);
        Assert.Equal(2, _server.Starts.Count);
    }

    [Fact]
    public async Task A_Range_past_the_end_of_the_file_starts_a_new_session()
    {
        _server.OnPut = (number, _) =>
        {
            if (number != 0)
            {
                return null;
            }

            var response = Status(HttpStatusCode.PermanentRedirect);
            response.Headers.TryAddWithoutValidation("Range", "bytes=0-5000");
            return response;
        };

        await _uploader.UploadAsync(SmallJob());

        Assert.Equal(2, _server.Starts.Count);
    }

    [Fact]
    public async Task An_unreadable_Range_asks_again()
    {
        _server.OnPut = (number, _) =>
        {
            if (number != 0)
            {
                return null;
            }

            var response = Status(HttpStatusCode.PermanentRedirect);
            response.Headers.TryAddWithoutValidation("Range", "nonsense");
            return response;
        };

        await _uploader.UploadAsync(SmallJob());

        Assert.Single(_server.Starts);
        Assert.True(_server.Puts[1].IsStatusCheck);
    }

    [Theory]
    [InlineData("quotaExceeded")]
    [InlineData("dailyLimitExceeded")]
    public async Task Running_out_of_quota_waits_until_midnight_Pacific(string reason)
    {
        _server.OnStart = _ => FakeYouTube.Error(HttpStatusCode.Forbidden, reason);

        var failure = await UploadFailsAsync(SmallJob());

        Assert.Equal(UploadFailureKind.PlatformCap, failure.Kind);

        // 12:00 UTC on 1 November 2026 is 04:00 in Los Angeles, after the change to standard time.
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.FromHours(-8)), failure.RetryAt);
        Assert.Single(_server.Starts);
    }

    [Fact]
    public async Task The_channels_upload_limit_is_a_wait()
    {
        _server.OnStart = _ => FakeYouTube.Error(HttpStatusCode.BadRequest, "uploadLimitExceeded");

        var failure = await UploadFailsAsync(SmallJob());

        Assert.Equal(UploadFailureKind.PlatformCap, failure.Kind);
        Assert.Equal(Start.AddHours(24), failure.RetryAt);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "rateLimitExceeded")]
    [InlineData(HttpStatusCode.Forbidden, "userRateLimitExceeded")]
    [InlineData(HttpStatusCode.TooManyRequests, "rateLimitExceeded")]
    public async Task A_rate_limit_is_worth_trying_again_shortly(HttpStatusCode status, string reason)
    {
        _server.OnStart = _ => FakeYouTube.Error(status, reason);

        var failure = await UploadFailsAsync(SmallJob());

        Assert.Equal(UploadFailureKind.Retryable, failure.Kind);
        Assert.Equal(Start.AddMinutes(1), failure.RetryAt);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "backendError")]
    [InlineData(HttpStatusCode.InternalServerError, "internalError")]
    public async Task Server_trouble_starting_a_session_goes_back_to_the_caller_to_try_again(HttpStatusCode status, string reason)
    {
        _server.OnStart = _ => FakeYouTube.Error(status, reason);

        Assert.Equal(UploadFailureKind.Retryable, (await UploadFailsAsync(SmallJob())).Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "invalidTitle")]
    [InlineData(HttpStatusCode.BadRequest, "invalidCategoryId")]
    [InlineData(HttpStatusCode.Forbidden, "forbiddenPrivacySetting")]
    [InlineData(HttpStatusCode.Forbidden, "forbidden")]
    public async Task Other_refusals_are_final_and_say_why(HttpStatusCode status, string reason)
    {
        _server.OnStart = _ => FakeYouTube.Error(status, reason);

        var failure = await UploadFailsAsync(SmallJob());

        Assert.Equal(UploadFailureKind.Permanent, failure.Kind);
        Assert.Contains(reason, failure.Message);
        Assert.Contains("From the server", failure.Message);
        Assert.Single(_server.Starts);
    }

    [Fact]
    public async Task Missing_permission_asks_to_reconnect()
    {
        _server.OnStart = _ => FakeYouTube.Error(HttpStatusCode.Forbidden, "insufficientPermissions");

        Assert.Equal(UploadFailureKind.Reconnect, (await UploadFailsAsync(SmallJob())).Kind);
    }

    [Fact]
    public async Task A_refusal_during_a_chunk_is_final_and_forgets_the_session()
    {
        var job = SmallJob();
        _server.OnPut = (_, _) => FakeYouTube.Error(HttpStatusCode.BadRequest, "invalidVideoMetadata");

        var failure = await UploadFailsAsync(job);

        Assert.Equal(UploadFailureKind.Permanent, failure.Kind);
        Assert.Empty(_sessions.Saved);
    }

    [Fact]
    public async Task Running_out_of_quota_during_a_chunk_keeps_the_session()
    {
        var job = SmallJob();
        _server.OnPut = (_, _) => FakeYouTube.Error(HttpStatusCode.Forbidden, "quotaExceeded");

        var failure = await UploadFailsAsync(job);

        Assert.Equal(UploadFailureKind.PlatformCap, failure.Kind);
        Assert.True(_sessions.Saved.ContainsKey(job.Id));
    }

    [Fact]
    public async Task A_refused_token_at_the_start_is_replaced_once()
    {
        _server.OnStart = number => number == 0 ? FakeYouTube.Error(HttpStatusCode.Unauthorized, "authError") : null;

        await _uploader.UploadAsync(SmallJob());

        Assert.Equal(["Bearer token-1", "Bearer token-2"], _server.Starts.Select(start => start.Authorization));
        Assert.Equal(["token-1"], _tokens.Rejected);
    }

    [Fact]
    public async Task A_token_refused_twice_at_the_start_asks_to_reconnect()
    {
        _server.OnStart = _ => FakeYouTube.Error(HttpStatusCode.Unauthorized, "authError");

        Assert.Equal(UploadFailureKind.Reconnect, (await UploadFailsAsync(SmallJob())).Kind);
        Assert.Equal(2, _server.Starts.Count);
    }

    [Fact]
    public async Task A_token_that_runs_out_during_a_chunk_is_replaced_and_the_upload_carries_on()
    {
        var path = WriteFile(2L * Chunk);
        _server.OnPut = (number, _) => number == 1 ? FakeYouTube.Error(HttpStatusCode.Unauthorized, "expired") : null;

        await _uploader.UploadAsync(JobFor(path));

        Assert.Equal(["token-1"], _tokens.Rejected);
        Assert.Single(_server.Starts);
        Assert.Equal("Bearer token-2", _server.Puts[^1].Authorization);
        Assert.Equal(File.ReadAllBytes(path), _server.ReceivedIn(_server.Puts[0].Session));
    }

    [Fact]
    public async Task A_token_refused_twice_during_a_chunk_asks_to_reconnect_and_keeps_the_session()
    {
        var job = SmallJob();
        _server.OnPut = (_, _) => FakeYouTube.Error(HttpStatusCode.Unauthorized, "expired");

        var failure = await UploadFailsAsync(job);

        Assert.Equal(UploadFailureKind.Reconnect, failure.Kind);
        Assert.True(_sessions.Saved.ContainsKey(job.Id));
    }

    [Fact]
    public async Task A_sign_in_that_has_ended_asks_to_reconnect()
    {
        _tokens.Throw = new ReconnectRequiredException(Platform.YouTube, "Connect again.");

        var failure = await UploadFailsAsync(SmallJob());

        Assert.Equal(UploadFailureKind.Reconnect, failure.Kind);
        Assert.Equal("Connect again.", failure.Message);
    }

    [Fact]
    public async Task Warns_when_YouTube_kept_a_video_at_a_different_privacy()
    {
        _server.PrivacyToReport = "private";

        var receipt = await _uploader.UploadAsync(SmallJob(Details() with { Privacy = YouTubePrivacy.Public }));

        Assert.Contains("public", receipt.Warning);
        Assert.Contains("private", receipt.Warning);
    }

    [Fact]
    public async Task Warns_when_a_video_meant_to_be_private_came_back_public()
    {
        _server.PrivacyToReport = "public";

        var receipt = await _uploader.UploadAsync(SmallJob());

        Assert.NotNull(receipt.Warning);
    }

    [Fact]
    public async Task A_saved_address_that_isnt_YouTubes_never_gets_the_token()
    {
        var job = SmallJob();
        _sessions.Save(job.Id, "https://elsewhere.test/session");

        await _uploader.UploadAsync(job);

        Assert.All(_server.Puts, put => Assert.StartsWith("https://www.googleapis.com/", put.Session));
        Assert.Single(_server.Starts);
    }

    [Theory]
    [InlineData("https://elsewhere.test/session")]
    [InlineData("https://googleapis.com.elsewhere.test/session")]
    [InlineData("http://www.googleapis.com/session")]
    public async Task A_session_address_that_isnt_YouTubes_is_refused(string address)
    {
        _server.OnStart = _ =>
        {
            var response = Status(HttpStatusCode.OK);
            response.Headers.Location = new Uri(address);
            return response;
        };

        var failure = await UploadFailsAsync(SmallJob());

        Assert.Equal(UploadFailureKind.Retryable, failure.Kind);
        Assert.Empty(_server.Puts);
    }

    [Fact]
    public async Task A_session_start_with_no_address_is_a_failure_to_retry()
    {
        _server.OnStart = _ => Status(HttpStatusCode.OK);

        Assert.Equal(UploadFailureKind.Retryable, (await UploadFailsAsync(SmallJob())).Kind);
    }

    [Fact]
    public async Task A_leftover_session_cant_carry_on_past_its_scheduled_time()
    {
        var path = WriteFile(1000);
        var job = JobFor(path, Details() with { PublishAt = Start.AddMinutes(-1) });
        _sessions.Save(job.Id, _server.Seed(File.ReadAllBytes(path)[..100]));

        var failure = await UploadFailsAsync(job);

        Assert.Equal(UploadFailureKind.Permanent, failure.Kind);
        Assert.Empty(_sessions.Saved);
        Assert.Single(_server.Puts);
    }

    [Fact]
    public async Task A_leftover_session_that_had_finished_is_kept_even_past_its_scheduled_time()
    {
        var path = WriteFile(1000);
        var job = JobFor(path, Details() with { PublishAt = Start.AddMinutes(-1) });
        _sessions.Save(job.Id, _server.Seed(File.ReadAllBytes(path)));

        var receipt = await _uploader.UploadAsync(job);

        Assert.Equal("video-1", receipt.RemoteId);
    }

    [Fact]
    public async Task Cancelling_stops_and_keeps_the_session()
    {
        var job = SmallJob();
        using var cancel = new CancellationTokenSource();
        _server.OnPut = (_, _) =>
        {
            cancel.Cancel();
            cancel.Token.ThrowIfCancellationRequested();
            return null;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _uploader.UploadAsync(job, null, cancel.Token));

        Assert.True(_sessions.Saved.ContainsKey(job.Id));
        Assert.Empty(_time.Delays);
    }

    [Fact]
    public async Task Messages_never_contain_the_session_address_or_a_token()
    {
        var job = SmallJob();
        _server.OnPut = (_, _) => throw new HttpRequestException(FakeYouTube.SessionPrefix + "1");

        var failure = await UploadFailsAsync(job);

        Assert.DoesNotContain("upload_id", failure.Message);
        Assert.DoesNotContain("token-", failure.Message);
    }

    [Fact]
    public async Task Reports_processed_once_YouTube_says_so()
    {
        var answers = new Queue<HttpResponseMessage>([FakeYouTube.VideoStatus("uploaded"), FakeYouTube.VideoStatus("processed")]);
        _server.Other.Add(("https://www.googleapis.com/youtube/v3/videos", answers.Dequeue));

        Assert.Equal(JobState.Completed, await _uploader.WaitForResultAsync("video-1"));

        Assert.Equal("GET https://www.googleapis.com/youtube/v3/videos?part=status&id=video-1", _server.Requests[0]);
        Assert.Equal([TimeSpan.FromSeconds(15)], _time.Delays);
    }

    [Fact]
    public async Task A_video_not_listed_yet_is_still_processing()
    {
        var answers = new Queue<HttpResponseMessage>(
            [FakeYouTube.Json(HttpStatusCode.OK, new { items = Array.Empty<object>() }), FakeYouTube.VideoStatus("processed")]);
        _server.Other.Add(("https://www.googleapis.com/youtube/v3/videos", answers.Dequeue));

        Assert.Equal(JobState.Completed, await _uploader.WaitForResultAsync("video-1"));
    }

    [Theory]
    [InlineData("failed", "uploadAborted", null, UploadFailureKind.Retryable)]
    [InlineData("failed", "codec", null, UploadFailureKind.Permanent)]
    [InlineData("failed", "conversion", null, UploadFailureKind.Permanent)]
    [InlineData("failed", "emptyFile", null, UploadFailureKind.Permanent)]
    [InlineData("failed", "invalidFile", null, UploadFailureKind.Permanent)]
    [InlineData("failed", "tooSmall", null, UploadFailureKind.Permanent)]
    [InlineData("failed", null, null, UploadFailureKind.Permanent)]
    [InlineData("rejected", null, "duplicate", UploadFailureKind.Permanent)]
    [InlineData("rejected", null, "copyright", UploadFailureKind.Permanent)]
    [InlineData("rejected", null, "termsOfUse", UploadFailureKind.Permanent)]
    [InlineData("rejected", null, null, UploadFailureKind.Permanent)]
    [InlineData("deleted", null, null, UploadFailureKind.Permanent)]
    public async Task A_video_YouTube_wont_keep_reports_what_to_do_next(string status, string? failure, string? rejection, UploadFailureKind kind)
    {
        _server.Other.Add(("https://www.googleapis.com/youtube/v3/videos", () => FakeYouTube.VideoStatus(status, failure, rejection)));

        Assert.Equal(kind, (await ResultFailsAsync()).Kind);
    }

    [Fact]
    public async Task Server_trouble_while_checking_only_means_asking_again()
    {
        var answers = new Queue<HttpResponseMessage>(
            [Status(HttpStatusCode.ServiceUnavailable), FakeYouTube.VideoStatus("processed")]);
        _server.Other.Add(("https://www.googleapis.com/youtube/v3/videos", answers.Dequeue));

        Assert.Equal(JobState.Completed, await _uploader.WaitForResultAsync("video-1"));
    }

    [Fact]
    public async Task Gives_back_processing_after_a_long_wait()
    {
        _server.Other.Add(("https://www.googleapis.com/youtube/v3/videos", () => FakeYouTube.VideoStatus("uploaded")));

        Assert.Equal(JobState.Processing, await _uploader.WaitForResultAsync("video-1"));

        Assert.True(_time.GetUtcNow() - Start >= TimeSpan.FromMinutes(25));
        Assert.All(_time.Delays, delay => Assert.True(delay <= TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Running_out_of_quota_while_checking_doesnt_hold_up_the_queue()
    {
        _server.Other.Add(("https://www.googleapis.com/youtube/v3/videos", () => FakeYouTube.Error(HttpStatusCode.Forbidden, "quotaExceeded")));

        Assert.Equal(JobState.Processing, await _uploader.WaitForResultAsync("video-1"));

        Assert.Empty(_time.Delays);
    }

    [Fact]
    public async Task A_sign_in_that_ended_while_checking_asks_to_reconnect()
    {
        _server.Other.Add(("https://www.googleapis.com/youtube/v3/videos", () => FakeYouTube.Error(HttpStatusCode.Unauthorized, "authError")));

        Assert.Equal(UploadFailureKind.Reconnect, (await ResultFailsAsync()).Kind);
    }
}
