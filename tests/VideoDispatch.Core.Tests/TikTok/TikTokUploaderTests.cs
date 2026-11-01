using System.Net;
using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.TikTok;
using VideoDispatch.Core.Uploads;

namespace VideoDispatch.Core.Tests.TikTok;

public sealed class TikTokUploaderTests : IDisposable
{
    private const long MiB = 1024 * 1024;
    private const long Chunk = TikTokChunkPlan.StandardChunkSize;
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    // Test files stay beside the test program, in the ignored bin folder.
    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "uploads-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTikTok _server = new();
    private readonly FakeTokens _tokens = new();
    private readonly JumpingTime _time = new(Start);
    private readonly TikTokUploader _uploader;

    public TikTokUploaderTests()
    {
        Directory.CreateDirectory(_folder);
        _uploader = new TikTokUploader(new HttpClient(_server) { Timeout = Timeout.InfiniteTimeSpan }, _tokens, _time);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

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

    private static UploadJob JobFor(string path, string caption = "") => new()
    {
        Id = Guid.NewGuid(),
        BatchId = Guid.NewGuid(),
        SourcePath = path,
        SourceLength = new FileInfo(path).Length,
        SourceLastWriteUtc = File.GetLastWriteTimeUtc(path),
        Details = new TikTokPostDetails { Caption = caption },
        CreatedAt = Start,
    };

    private UploadJob SmallJob() => JobFor(WriteFile(1000));

    private async Task<UploadException> UploadFailsAsync(UploadJob job) =>
        await Assert.ThrowsAsync<UploadException>(() => _uploader.UploadAsync(job));

    private async Task<UploadException> ResultFailsAsync() =>
        await Assert.ThrowsAsync<UploadException>(() => _uploader.WaitForResultAsync("publish-1"));

    [Fact]
    public async Task Sends_every_chunk_in_order_and_the_file_arrives_whole()
    {
        var size = 3 * Chunk + 123;
        var path = WriteFile(size);
        var progress = new RecordingProgress();

        var receipt = await _uploader.UploadAsync(JobFor(path), progress);

        Assert.Equal("publish-1", receipt.RemoteId);
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
            Assert.Equal(put.BodyLength, put.ContentLength);
        });
        Assert.Equal(Chunk + 123, _server.Puts[^1].BodyLength);

        Assert.Equal(size, progress.Values.Last());
        var values = progress.Values.ToArray();
        Assert.True(values.Zip(values.Skip(1)).All(pair => pair.First <= pair.Second));
    }

    [Fact]
    public async Task Asks_for_the_plan_it_then_sends()
    {
        var size = 2 * Chunk + 5;
        await _uploader.UploadAsync(JobFor(WriteFile(size)));

        var source = JsonText.Parse(_server.InitBodies.Single()).GetProperty("source_info");
        Assert.Equal("FILE_UPLOAD", source.GetProperty("source").GetString());
        Assert.Equal(size, source.GetProperty("video_size").GetInt64());
        Assert.Equal(Chunk, source.GetProperty("chunk_size").GetInt64());
        Assert.Equal(2, source.GetProperty("total_chunk_count").GetInt32());
        Assert.Equal(2, _server.Puts.Count);
        Assert.Equal("Bearer token-1", _server.InitAuthorizations.Single());
    }

    [Fact]
    public async Task A_small_file_goes_in_one_request_sized_to_the_file()
    {
        var path = WriteFile(1000);

        await _uploader.UploadAsync(JobFor(path));

        var source = JsonText.Parse(_server.InitBodies.Single()).GetProperty("source_info");
        Assert.Equal(1000, source.GetProperty("chunk_size").GetInt64());
        Assert.Equal(1, source.GetProperty("total_chunk_count").GetInt32());
        Assert.Equal("bytes 0-999/1000", Assert.Single(_server.Puts).ContentRange);
    }

    [Theory]
    [InlineData("clip.mov", "video/quicktime")]
    [InlineData("clip.webm", "video/webm")]
    public async Task Names_the_files_media_type(string name, string expected)
    {
        await _uploader.UploadAsync(JobFor(WriteFile(10, name)));

        Assert.Equal(expected, Assert.Single(_server.Puts).ContentType);
    }

    [Fact]
    public async Task Never_sends_the_caption()
    {
        const string caption = "my-private-caption-text";

        await _uploader.UploadAsync(JobFor(WriteFile(10), caption));

        var body = _server.InitBodies.Single();
        Assert.DoesNotContain(caption, body);
        Assert.DoesNotContain("post_info", body);
        Assert.DoesNotContain("title", body);
    }

    [Fact]
    public async Task A_file_with_a_new_size_fails_before_anything_is_sent()
    {
        var path = WriteFile(1000);
        var job = JobFor(path);
        var written = File.GetLastWriteTimeUtc(path);
        File.AppendAllText(path, "more");
        File.SetLastWriteTimeUtc(path, written);

        var failure = await UploadFailsAsync(job);

        Assert.Equal(UploadFailureKind.Permanent, failure.Kind);
        Assert.Contains("changed", failure.Message);
        Assert.Empty(_server.InitBodies);
    }

    [Fact]
    public async Task A_file_with_a_new_write_time_fails_before_anything_is_sent()
    {
        var path = WriteFile(1000);
        var job = JobFor(path);
        File.SetLastWriteTimeUtc(path, job.SourceLastWriteUtc.UtcDateTime.AddSeconds(1));

        var failure = await UploadFailsAsync(job);

        Assert.Equal(UploadFailureKind.Permanent, failure.Kind);
        Assert.Empty(_server.InitBodies);
    }

    [Fact]
    public async Task A_missing_file_fails_for_good()
    {
        var path = WriteFile(1000);
        var job = JobFor(path);
        File.Delete(path);

        Assert.Equal(UploadFailureKind.Permanent, (await UploadFailsAsync(job)).Kind);
        Assert.Empty(_server.InitBodies);
    }

    [Fact]
    public async Task A_file_being_written_can_be_tried_again_later()
    {
        var path = WriteFile(1000);
        var job = JobFor(path);
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        Assert.Equal(UploadFailureKind.Retryable, (await UploadFailsAsync(job)).Kind);
        Assert.Empty(_server.InitBodies);
    }

    [Fact]
    public async Task Nothing_can_change_the_file_during_the_upload()
    {
        var path = WriteFile(1000);
        _server.OnPut = (_, _) =>
        {
            Assert.Throws<IOException>(() => File.AppendAllText(path, "more"));
            return null;
        };

        await _uploader.UploadAsync(JobFor(path));

        Assert.Single(_server.Puts);
    }

    [Fact]
    public async Task A_rendered_file_is_not_compared_with_the_source()
    {
        var source = WriteFile(1000);
        var rendered = WriteFile(500, "rendered.mp4");

        await _uploader.UploadAsync(JobFor(source) with { RenderedPath = rendered });

        Assert.Equal("bytes 0-499/500", Assert.Single(_server.Puts).ContentRange);
    }

    [Fact]
    public async Task A_kind_of_file_TikTok_does_not_take_fails_for_good()
    {
        Assert.Equal(UploadFailureKind.Permanent, (await UploadFailsAsync(JobFor(WriteFile(10, "clip.avi")))).Kind);
    }

    [Fact]
    public async Task An_empty_file_fails_for_good()
    {
        Assert.Equal(UploadFailureKind.Permanent, (await UploadFailsAsync(JobFor(WriteFile(0)))).Kind);
        Assert.Empty(_server.InitBodies);
    }

    [Fact]
    public async Task A_job_for_another_platform_is_refused()
    {
        var job = SmallJob() with { Details = new YouTubePostDetails() };

        await Assert.ThrowsAsync<ArgumentException>(() => _uploader.UploadAsync(job));
    }

    [Fact]
    public void An_HttpClient_with_a_timeout_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new TikTokUploader(new HttpClient(_server), _tokens, _time));
    }

    [Fact]
    public async Task Too_many_pending_shares_means_waiting_not_retrying()
    {
        _server.OnInit = _ => FakeTikTok.Error(HttpStatusCode.Forbidden, "spam_risk_too_many_pending_share");

        var failure = await UploadFailsAsync(SmallJob());

        Assert.Equal(UploadFailureKind.PlatformCap, failure.Kind);
        Assert.Equal(Start.AddHours(24), failure.RetryAt);
        Assert.Single(_server.InitBodies);
        Assert.Empty(_server.Puts);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "spam_risk_user_banned_from_posting")]
    [InlineData(HttpStatusCode.BadRequest, "invalid_param")]
    [InlineData(HttpStatusCode.BadRequest, "something_new")]
    public async Task Refusals_that_will_not_change_fail_for_good(HttpStatusCode status, string code)
    {
        _server.OnInit = _ => FakeTikTok.Error(status, code);

        Assert.Equal(UploadFailureKind.Permanent, (await UploadFailsAsync(SmallJob())).Kind);
        Assert.Single(_server.InitBodies);
    }

    [Fact]
    public async Task A_rate_limit_can_be_tried_again_when_TikTok_says()
    {
        _server.OnInit = _ =>
        {
            var response = FakeTikTok.Error(HttpStatusCode.TooManyRequests, "rate_limit_exceeded");
            response.Headers.Add("Retry-After", "90");
            return response;
        };

        var failure = await UploadFailsAsync(SmallJob());

        Assert.Equal(UploadFailureKind.Retryable, failure.Kind);
        Assert.Equal(Start.AddSeconds(90), failure.RetryAt);
    }

    [Fact]
    public async Task A_server_error_on_init_can_be_tried_again()
    {
        _server.OnInit = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);

        Assert.Equal(UploadFailureKind.Retryable, (await UploadFailsAsync(SmallJob())).Kind);
    }

    [Theory]
    [InlineData("""{"data":{},"error":{"code":"ok"}}""")]
    [InlineData("""{"data":{"publish_id":"p","upload_url":"http://upload.test/plain"},"error":{"code":"ok"}}""")]
    [InlineData("not json")]
    public async Task An_unusable_init_reply_sends_no_video(string reply)
    {
        _server.OnInit = _ => FakeTikTok.Json(HttpStatusCode.OK, reply);

        Assert.Equal(UploadFailureKind.Retryable, (await UploadFailsAsync(SmallJob())).Kind);
        Assert.Empty(_server.Puts);
    }

    [Fact]
    public async Task A_refused_token_is_replaced_once_and_the_call_retried()
    {
        _server.OnInit = attempt => attempt == 0 ? FakeTikTok.Error(HttpStatusCode.Unauthorized, "access_token_invalid") : null;

        await _uploader.UploadAsync(SmallJob());

        Assert.Equal(["token-1"], _tokens.Rejected);
        Assert.Equal(["Bearer token-1", "Bearer token-2"], _server.InitAuthorizations);
        Assert.Single(_server.Puts);
    }

    [Fact]
    public async Task A_second_refused_token_asks_for_a_reconnect()
    {
        _server.OnInit = _ => FakeTikTok.Error(HttpStatusCode.Unauthorized, "access_token_invalid");

        var failure = await UploadFailsAsync(SmallJob());

        Assert.Equal(UploadFailureKind.Reconnect, failure.Kind);
        Assert.Equal(2, _server.InitBodies.Count);
        Assert.Single(_tokens.Rejected);
    }

    [Fact]
    public async Task A_missing_permission_asks_for_a_reconnect_at_once()
    {
        _server.OnInit = _ => FakeTikTok.Error(HttpStatusCode.Unauthorized, "scope_not_authorized");

        Assert.Equal(UploadFailureKind.Reconnect, (await UploadFailsAsync(SmallJob())).Kind);
        Assert.Single(_server.InitBodies);
        Assert.Empty(_tokens.Rejected);
    }

    [Fact]
    public async Task An_expired_sign_in_asks_for_a_reconnect()
    {
        _tokens.Throw = new ReconnectRequiredException(Platform.TikTok, "Connect again.");

        Assert.Equal(UploadFailureKind.Reconnect, (await UploadFailsAsync(SmallJob())).Kind);
        Assert.Empty(_server.InitBodies);
    }

    [Fact]
    public async Task Missing_keys_ask_for_a_reconnect()
    {
        _tokens.Throw = new AccountSetupException(Platform.TikTok, "Enter your keys.");

        Assert.Equal(UploadFailureKind.Reconnect, (await UploadFailsAsync(SmallJob())).Kind);
    }

    [Fact]
    public async Task A_token_refresh_that_fails_for_now_can_be_tried_again()
    {
        _tokens.Throw = new TransientAccountException(Platform.TikTok, "Couldn't reach TikTok.");

        Assert.Equal(UploadFailureKind.Retryable, (await UploadFailsAsync(SmallJob())).Kind);
    }

    [Fact]
    public async Task At_most_six_inits_start_in_a_minute()
    {
        var job = SmallJob();

        for (var i = 0; i < 6; i++)
        {
            await _uploader.UploadAsync(job);
        }

        Assert.Equal(Start, _time.GetUtcNow());

        await _uploader.UploadAsync(job);

        Assert.Equal(Start.AddMinutes(1), _time.GetUtcNow());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.RequestedRangeNotSatisfiable)]
    public async Task A_dead_upload_starts_over_from_the_first_byte_under_a_new_init(HttpStatusCode status)
    {
        var size = 2 * Chunk;
        var path = WriteFile(size);
        var progress = new RecordingProgress();
        _server.OnPut = (number, _) => number == 1 ? new HttpResponseMessage(status) : null;

        var receipt = await _uploader.UploadAsync(JobFor(path), progress);

        Assert.Equal("publish-2", receipt.RemoteId);
        Assert.Equal(2, _server.InitBodies.Count);
        Assert.Equal(4, _server.Puts.Count);
        Assert.Equal(_server.Puts[0].Session, _server.Puts[1].Session);
        Assert.NotEqual(_server.Puts[1].Session, _server.Puts[2].Session);
        Assert.Equal($"bytes 0-{Chunk - 1}/{size}", _server.Puts[2].ContentRange);
        Assert.Equal(File.ReadAllBytes(path), _server.ReceivedIn(_server.Puts[2].Session));
        Assert.Contains(0, progress.Values.Skip(1));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.RequestedRangeNotSatisfiable)]
    public async Task A_second_dead_upload_stops_instead_of_looping(HttpStatusCode status)
    {
        _server.OnPut = (_, _) => new HttpResponseMessage(status);

        var failure = await UploadFailsAsync(SmallJob());

        Assert.Equal(UploadFailureKind.Retryable, failure.Kind);
        Assert.Equal(2, _server.InitBodies.Count);
        Assert.Equal(2, _server.Puts.Count);
    }

    [Fact]
    public async Task A_range_mismatch_is_never_followed_by_the_next_chunk_on_the_same_session()
    {
        _server.OnPut = (number, _) => number == 0 ? new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) : null;

        await _uploader.UploadAsync(JobFor(WriteFile(2 * Chunk)));

        Assert.NotEqual(_server.Puts[0].Session, _server.Puts[1].Session);
        Assert.StartsWith("bytes 0-", _server.Puts[1].ContentRange);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_server_error_on_a_chunk_retries_that_same_chunk(HttpStatusCode status)
    {
        var size = 2 * Chunk;
        var path = WriteFile(size);
        _server.OnPut = (number, _) => number == 1 ? new HttpResponseMessage(status) : null;

        await _uploader.UploadAsync(JobFor(path));

        Assert.Equal(3, _server.Puts.Count);
        Assert.Equal(_server.Puts[1].ContentRange, _server.Puts[2].ContentRange);
        Assert.Single(_server.InitBodies);
        Assert.Equal(File.ReadAllBytes(path), _server.ReceivedIn(_server.Puts[0].Session));
        Assert.Equal([TimeSpan.FromSeconds(2)], _time.Delays);
    }

    [Fact]
    public async Task A_lost_connection_retries_the_same_chunk()
    {
        _server.OnPut = (number, _) => number == 0 ? throw new HttpRequestException("Connection reset") : null;

        await _uploader.UploadAsync(SmallJob());

        Assert.Equal(2, _server.Puts.Count);
        Assert.Single(_server.InitBodies);
    }

    [Fact]
    public async Task Chunk_retries_back_off_and_then_hand_the_job_back()
    {
        _server.OnPut = (_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        var failure = await UploadFailsAsync(SmallJob());

        Assert.Equal(UploadFailureKind.Retryable, failure.Kind);
        Assert.Equal(4, _server.Puts.Count);
        Assert.Single(_server.InitBodies);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)], _time.Delays);
    }

    [Fact]
    public async Task A_short_Retry_After_on_a_chunk_is_honored()
    {
        _server.OnPut = (number, _) =>
        {
            if (number > 0)
            {
                return null;
            }

            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.Add("Retry-After", "17");
            return response;
        };

        await _uploader.UploadAsync(SmallJob());

        Assert.Equal([TimeSpan.FromSeconds(17)], _time.Delays);
    }

    [Fact]
    public async Task A_long_Retry_After_on_a_chunk_hands_the_job_back_with_the_time()
    {
        _server.OnPut = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.Add("Retry-After", "600");
            return response;
        };

        var failure = await UploadFailsAsync(SmallJob());

        Assert.Equal(UploadFailureKind.Retryable, failure.Kind);
        Assert.Equal(Start.AddMinutes(10), failure.RetryAt);
        Assert.Single(_server.Puts);
        Assert.Empty(_time.Delays);
    }

    [Fact]
    public async Task A_stalled_chunk_counts_as_no_response()
    {
        var uploader = new TikTokUploader(
            new HttpClient(new StallingOnce(_server)) { Timeout = Timeout.InfiniteTimeSpan },
            _tokens,
            _time)
        {
            StallTimeout = TimeSpan.FromMilliseconds(50),
        };

        await uploader.UploadAsync(SmallJob());

        Assert.Single(_server.Puts);
        Assert.Equal([TimeSpan.FromSeconds(2)], _time.Delays);
    }

    [Fact]
    public async Task A_malformed_chunk_request_fails_for_good()
    {
        _server.OnPut = (_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest);

        Assert.Equal(UploadFailureKind.Permanent, (await UploadFailsAsync(SmallJob())).Kind);
        Assert.Single(_server.Puts);
    }

    [Fact]
    public async Task A_final_chunk_that_is_not_confirmed_is_not_called_finished()
    {
        _server.OnPut = (_, _) => new HttpResponseMessage(HttpStatusCode.PartialContent);

        Assert.Equal(UploadFailureKind.Retryable, (await UploadFailsAsync(SmallJob())).Kind);
    }

    [Theory]
    [InlineData(0)]   // the last number is the position of the last byte
    [InlineData(1)]   // the last number is how many bytes arrived
    public async Task Either_reading_of_TikToks_progress_header_is_accepted(long offset)
    {
        _server.Progress = (received, total) => $"bytes 0-{received - 1 + offset}/{total}";

        await _uploader.UploadAsync(JobFor(WriteFile(2 * Chunk)));

        Assert.Single(_server.InitBodies);
        Assert.Equal(2, _server.Puts.Count);
    }

    [Fact]
    public async Task A_missing_progress_header_is_fine()
    {
        _server.Progress = (_, _) => null;

        await _uploader.UploadAsync(JobFor(WriteFile(2 * Chunk)));

        Assert.Single(_server.InitBodies);
    }

    [Fact]
    public async Task A_garbled_progress_header_is_ignored()
    {
        _server.Progress = (_, _) => "who knows";

        await _uploader.UploadAsync(JobFor(WriteFile(2 * Chunk)));

        Assert.Single(_server.InitBodies);
    }

    [Fact]
    public async Task Progress_that_falls_behind_what_was_sent_starts_over()
    {
        var calls = 0;
        _server.Progress = (received, total) => calls++ == 0 ? $"bytes 0-100/{total}" : $"bytes 0-{received - 1}/{total}";

        var receipt = await _uploader.UploadAsync(JobFor(WriteFile(2 * Chunk)));

        Assert.Equal("publish-2", receipt.RemoteId);
        Assert.Equal(2, _server.InitBodies.Count);
    }

    [Fact]
    public async Task Canceling_stops_the_upload_with_no_retry()
    {
        using var cancel = new CancellationTokenSource();
        _server.OnPut = (_, _) =>
        {
            cancel.Cancel();
            throw new OperationCanceledException(cancel.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _uploader.UploadAsync(JobFor(WriteFile(2 * Chunk)), null, cancel.Token));

        Assert.Single(_server.Puts);
        Assert.Empty(_time.Delays);
    }

    [Fact]
    public async Task Failure_messages_never_contain_the_upload_address_or_token()
    {
        _server.OnPut = (_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden);

        var failure = await UploadFailsAsync(SmallJob());

        Assert.DoesNotContain("very-secret-url", failure.ToString());
        Assert.DoesNotContain("upload.test", failure.ToString());
        Assert.DoesNotContain("token-", failure.ToString());
    }

    [Theory]
    [InlineData("SEND_TO_USER_INBOX", JobState.AwaitingCreator)]
    [InlineData("PUBLISH_COMPLETE", JobState.Completed)]
    public async Task Maps_finished_statuses(string status, JobState expected)
    {
        _server.Statuses.Enqueue(() => FakeTikTok.Status(status));

        Assert.Equal(expected, await _uploader.WaitForResultAsync("publish-1"));
        Assert.Equal("publish-1", JsonText.Parse(_server.StatusBodies.Single()).GetProperty("publish_id").GetString());
        Assert.Empty(_time.Delays);
    }

    [Fact]
    public async Task Polls_with_growing_delays_until_the_status_moves()
    {
        for (var i = 0; i < 5; i++)
        {
            _server.Statuses.Enqueue(() => FakeTikTok.Status("PROCESSING_UPLOAD"));
        }

        _server.Statuses.Enqueue(() => FakeTikTok.Status("SEND_TO_USER_INBOX"));

        Assert.Equal(JobState.AwaitingCreator, await _uploader.WaitForResultAsync("publish-1"));

        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60)],
            _time.Delays);
    }

    [Fact]
    public async Task A_status_nobody_has_heard_of_keeps_polling()
    {
        _server.Statuses.Enqueue(() => FakeTikTok.Status("SOMETHING_NEW"));
        _server.Statuses.Enqueue(() => FakeTikTok.Status("PUBLISH_COMPLETE"));

        Assert.Equal(JobState.Completed, await _uploader.WaitForResultAsync("publish-1"));
    }

    [Fact]
    public async Task Stops_after_a_long_wait_so_the_caller_can_ask_later()
    {
        _server.Statuses.Enqueue(() => FakeTikTok.Status("PROCESSING_UPLOAD"));

        Assert.Equal(JobState.Processing, await _uploader.WaitForResultAsync("publish-1"));

        Assert.InRange(_time.GetUtcNow() - Start, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(32));
    }

    [Fact]
    public async Task Never_polls_more_than_thirty_times_in_a_minute()
    {
        for (var i = 0; i < 31; i++)
        {
            await _uploader.WaitForResultAsync("publish-1");
        }

        Assert.Equal(31, _server.StatusBodies.Count);
        Assert.Equal(Start.AddMinutes(1), _time.GetUtcNow());
    }

    [Fact]
    public async Task A_server_error_while_polling_only_means_asking_again()
    {
        _server.Statuses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        _server.Statuses.Enqueue(() => FakeTikTok.Status("SEND_TO_USER_INBOX"));

        Assert.Equal(JobState.AwaitingCreator, await _uploader.WaitForResultAsync("publish-1"));
    }

    [Fact]
    public async Task A_lost_connection_while_polling_only_means_asking_again()
    {
        _server.Statuses.Enqueue(() => throw new HttpRequestException("Connection reset"));
        _server.Statuses.Enqueue(() => FakeTikTok.Status("SEND_TO_USER_INBOX"));

        Assert.Equal(JobState.AwaitingCreator, await _uploader.WaitForResultAsync("publish-1"));
    }

    [Fact]
    public async Task A_rate_limit_while_polling_waits_as_long_as_TikTok_says()
    {
        _server.Statuses.Enqueue(() =>
        {
            var response = FakeTikTok.Error(HttpStatusCode.TooManyRequests, "rate_limit_exceeded");
            response.Headers.Add("Retry-After", "45");
            return response;
        });
        _server.Statuses.Enqueue(() => FakeTikTok.Status("SEND_TO_USER_INBOX"));

        await _uploader.WaitForResultAsync("publish-1");

        Assert.Equal([TimeSpan.FromSeconds(45)], _time.Delays);
    }

    [Fact]
    public async Task A_very_long_rate_limit_while_polling_goes_back_to_the_caller()
    {
        _server.Statuses.Enqueue(() =>
        {
            var response = FakeTikTok.Error(HttpStatusCode.TooManyRequests, "rate_limit_exceeded");
            response.Headers.Add("Retry-After", "86400");
            return response;
        });

        Assert.Equal(JobState.Processing, await _uploader.WaitForResultAsync("publish-1"));

        Assert.Empty(_time.Delays);
    }

    [Fact]
    public async Task A_refused_token_while_polling_is_replaced_once()
    {
        _server.Statuses.Enqueue(() => FakeTikTok.Error(HttpStatusCode.Unauthorized, "access_token_invalid"));
        _server.Statuses.Enqueue(() => FakeTikTok.Status("SEND_TO_USER_INBOX"));

        await _uploader.WaitForResultAsync("publish-1");

        Assert.Equal(["token-1"], _tokens.Rejected);
        Assert.Equal(["Bearer token-1", "Bearer token-2"], _server.StatusAuthorizations);
    }

    [Fact]
    public async Task A_second_refused_token_while_polling_asks_for_a_reconnect()
    {
        _server.Statuses.Enqueue(() => FakeTikTok.Error(HttpStatusCode.Unauthorized, "access_token_invalid"));

        Assert.Equal(UploadFailureKind.Reconnect, (await ResultFailsAsync()).Kind);
    }

    [Fact]
    public async Task An_unknown_publish_id_fails_for_good()
    {
        _server.Statuses.Enqueue(() => FakeTikTok.Error(HttpStatusCode.BadRequest, "invalid_publish_id"));

        Assert.Equal(UploadFailureKind.Permanent, (await ResultFailsAsync()).Kind);
    }

    [Fact]
    public async Task TikToks_internal_failure_can_be_tried_again()
    {
        _server.Statuses.Enqueue(() => FakeTikTok.Status("FAILED", "internal"));

        Assert.Equal(UploadFailureKind.Retryable, (await ResultFailsAsync()).Kind);
        Assert.Single(_server.StatusBodies);
    }

    [Theory]
    [InlineData("auth_removed")]
    [InlineData("spam_risk")]
    [InlineData("spam_risk_text")]
    [InlineData("spam_risk_user_banned_from_posting")]
    [InlineData("file_format_check_failed")]
    [InlineData("duration_check_failed")]
    [InlineData("frame_rate_check_failed")]
    [InlineData("picture_size_check_failed")]
    [InlineData("a_reason_nobody_has_heard_of")]
    public async Task Failures_TikTok_says_not_to_retry_fail_for_good(string reason)
    {
        _server.Statuses.Enqueue(() => FakeTikTok.Status("FAILED", reason));

        Assert.Equal(UploadFailureKind.Permanent, (await ResultFailsAsync()).Kind);
        Assert.Single(_server.StatusBodies);
    }

    [Fact]
    public async Task A_failure_with_no_reason_fails_for_good()
    {
        _server.Statuses.Enqueue(() => FakeTikTok.Status("FAILED"));

        Assert.Equal(UploadFailureKind.Permanent, (await ResultFailsAsync()).Kind);
    }

    [Fact]
    public async Task Too_many_posts_means_waiting()
    {
        _server.Statuses.Enqueue(() => FakeTikTok.Status("FAILED", "spam_risk_too_many_posts"));

        var failure = await ResultFailsAsync();

        Assert.Equal(UploadFailureKind.PlatformCap, failure.Kind);
        Assert.Equal(Start.AddHours(24), failure.RetryAt);
    }

    /// <summary>Hangs on the first chunk until the request is canceled, then behaves.</summary>
    private sealed class StallingOnce(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private bool _stalled;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put && !_stalled)
            {
                _stalled = true;
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
