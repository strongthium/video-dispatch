# Platform notes

What the code relies on from each platform's official docs, last read on 2026-10-31, and the TikTok and YouTube upload pages on 2026-11-01. These rules change, so recheck the source before relying on a detail.

## TikTok

### Inbox upload

- Init: `POST https://open.tiktokapis.com/v2/post/publish/inbox/video/init/`, scope `video.upload`, 6 requests per minute per token.
- Body: `source_info` with `source: FILE_UPLOAD`, `video_size`, `chunk_size` and `total_chunk_count`.
- Response: `data.publish_id`, `data.upload_url` and `error.code`, which is `ok` on success.
- The video init has no `post_info` field, so **inbox mode can't send a caption**. The creator finishes the post in TikTok.
- At most 5 pending shares in any 24 hours. Past that, init returns 403 `spam_risk_too_many_pending_share`.

### Chunks

- `PUT upload_url` with `Content-Type` (`video/mp4`, `video/quicktime` or `video/webm`), `Content-Length` and `Content-Range: bytes FIRST-LAST/TOTAL`.
- `total_chunk_count` is `video_size / chunk_size`, rounded down.
- Each chunk is 5-64 MB and the final chunk can reach 128 MB, with 1-1000 chunks sent in order.
- A file under 5 MB goes in one request with `chunk_size` set to the file size. A file over 64 MB must be split.
- `upload_url` is valid for one hour. An expired URL returns 403, an unknown one 404, and a range mismatch 416.
- Every chunk but the last returns 206, and only 201 means the whole file arrived. A 400 means malformed headers or a wrong `Content-Length`. A 5xx means retry the same chunk.
- The response carries `Content-Range: bytes 0-{UPLOADED_BYTES}/{TOTAL_BYTE_LENGTH}`. The docs don't say whether that number is a position or a count, so the app accepts either and starts over only if it's behind the chunk just sent.
- The docs give **no way to resume**, and don't say how to recover from a 416. So the app starts again under a new init, once per upload. Whether an abandoned init counts against the 5 pending shares isn't stated.
- The docs don't say whether MB is decimal or binary, though the media guide's own example (4 MB is 4,194,304 bytes) reads as binary. The 16 MiB chunks are inside the limits either way. The 4 GB limit is checked as binary, so TikTok's own answer settles a file between 4,000,000,000 bytes and 4 GiB.

### Video limits

MP4 (recommended), WebM or MOV. H.264 (recommended), H.265, VP8 or VP9. 23-60 FPS, 360-4096 pixels per side, up to 10 minutes and 4 GB.

### Status

- `POST /v2/post/publish/status/fetch/` with `publish_id`, 30 requests per minute per token.
- Statuses: `PROCESSING_UPLOAD`, `SEND_TO_USER_INBOX`, `PUBLISH_COMPLETE` (in inbox mode, the creator posted the draft) and `FAILED`.
- `internal` is the fail reason TikTok says to retry. `auth_removed`, `spam_risk`, `spam_risk_text` and `spam_risk_user_banned_from_posting` must not be retried.
- `spam_risk_too_many_posts` means the creator passed the API's posting limit in 24 hours, so the app waits. The format, duration, frame rate and picture size failures don't state whether to retry, and the app treats them as final, since the same file would fail the same way.
- Processing has no guaranteed time limit. The docs give averages of under 30 seconds for 512 MB, about a minute for 1 GB, and over two minutes for 4 GB.
- The field names on this page are `error.log_id`, while the upload page says `error.logid` in its table. The app doesn't read either.

### Sign-in

- Authorize at `https://www.tiktok.com/v2/auth/authorize/` with PKCE. The challenge is the **hex** SHA256 of the verifier, not base64url.
- The verifier is 43-128 unreserved characters, new for each request. TikTok's sample code uses `Math.random()` and passes the verifier as the challenge, so don't copy it.
- Redirect host is `localhost` or `127.0.0.1`, and a port is required. A wildcard port is allowed: `http://127.0.0.1:*/callback/`.
- Token endpoint: `POST https://open.tiktokapis.com/v2/oauth/token/`. Exchange and refresh both **require `client_secret`**.
- Access tokens last 24 hours and refresh tokens 365 days. A refresh can return a new refresh token, which replaces the old one.
- Revoke: `POST /v2/oauth/revoke/` with `client_key`, `client_secret` and the access token as `token`.
- On a failed sign-in the redirect carries `error` and `error_description`. The docs list `state` only for success, so an error without a state is accepted, and an error with a wrong state is ignored.
- Token errors use the fields `error`, `error_description` and `log_id`, and the docs don't state the HTTP status. `invalid_grant` means the code or refresh token is invalid, expired or revoked. `server_error` and `temporarily_unavailable` are retryable.
- The app requests `user.info.basic` (display name, from `GET /v2/user/info/?fields=open_id,display_name`) and `video.upload`.

### Content Sharing Guidelines

- Apps must help authentic creators post original content, for a wide audience. A utility tool for accounts you or your team manage is not acceptable.
- `client_secret` must not be embedded in open-source projects, and apps must not add watermarks or promotional branding.
- The guidelines don't address apps that each user registers themselves, and an app must be approved for `video.upload`. Approval for an individual registration is not assured.
- Unaudited clients are limited to 5 users per 24 hours and `SELF_ONLY` posts. These rules are written for Direct Post, and the page doesn't mention inbox upload.

### Direct Post rules (not built yet)

Fetch creator info, show the nickname, check `max_video_post_duration_sec`, offer privacy from `privacy_level_options` with no default, leave Comment, Duet and Stitch off, show the music declaration, leave the commercial content toggle off, show a preview, keep preset text editable, and send nothing without explicit consent.

## YouTube

### Upload

- `videos.insert` has its own quota bucket of 100 calls per day. Other methods share 10,000 units per day, with `playlistItems.insert` at 50 and `playlists.list`, `videoCategories.list` and `channels.list` at 1 each. Quotas reset at midnight Pacific.
- Maximum file size is 256 GB. Media types are `video/*` and `application/octet-stream`.
- An omitted `privacyStatus` defaults to `public`, so it is always sent.
- The docs don't say whether starting a session or finishing one is what counts against the 100 calls. The app keeps its session so it doesn't start another needlessly.
- The `videos.insert` page doesn't list `snippet.categoryId` as required, but the errors page says `invalidVideoMetadata` occurs when the title and category aren't both set. So the app sends category 22 (People & Blogs) when none is chosen.

### Errors

Reasons are at `error.errors[].reason`.

- `403 quotaExceeded` and `403 dailyLimitExceeded`: wait until midnight Pacific.
- `400 uploadLimitExceeded`: the channel's own daily limit, separate from API quota. The docs give no reset time, so the app waits 24 hours.
- `403 rateLimitExceeded`, `403 userRateLimitExceeded` and `429 rateLimitExceeded`: try again shortly.
- `403 insufficientPermissions`: connect the account again.
- `401 authError` and `401 expired`: replace the token once, then connect again.
- Other 4xx are final.

### Field limits

- `snippet.title`: 100 characters, no `<` or `>`.
- `snippet.description`: 5000 bytes, no `<` or `>`.
- `snippet.tags[]`: 500 characters in total. Commas count, and a tag with a space counts as if quoted.
- `status.publishAt`: ISO 8601, only with `private`. **A time in the past publishes the video right away**, so the validator rejects it.

### Unverified projects

The sources conflict. The `videos.insert` page says "Videos uploaded from unverified API projects are not restricted to private viewing mode." The revision history entry of 2020-07-28 says they are restricted, and no reversal turned up in the part of that page that was read. The app must compare the `status.privacyStatus` YouTube returns with the value requested, and warn when they differ.

### Resumable upload

- Start: `POST https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=...` with `X-Upload-Content-Length` and `X-Upload-Content-Type`. It answers 200, and the session URI comes back in `Location`.
- The docs' samples send `Authorization` on every chunk and status check, so the app does too, and only ever to an https address under `googleapis.com`.
- Chunks are multiples of 256 KB, all the same size except the last.
- A 308 is YouTube's "keep going", not a redirect, so the upload client must not follow redirects.
- Status check: empty `PUT` with `Content-Range: bytes */TOTAL`. A 308 with `Range: bytes=0-N` gives the bytes received, and no `Range` means none. Honor `Retry-After`. A finished upload gives the same answer it gave when it finished, so a lost final answer doesn't need a second upload.
- Overlapping or skipping bytes means none of that request is taken, so the next chunk always starts from what `Range` says.
- Only 201 with the video resource means done. The app also accepts 200, which other Google upload docs use.
- Retry with backoff after no response, 500, 502, 503 or 504. Other codes are permanent. A 404 means the session expired and the upload starts again. The docs give no session lifetime.

### Processing

- `videos.list` with `part=status`, 1 unit per call.
- `status.uploadStatus` is `uploaded`, `processed`, `failed`, `rejected` or `deleted`, with `failureReason` and `rejectionReason` giving more.
- `failureReason` `uploadAborted` is treated as worth uploading again. The others would fail the same way.

### Playlists and categories

- `playlistItems.insert` needs `youtube.force-ssl` and costs 50 units. A `videoNotFound` just after uploading is treated as "not visible yet". `videoAlreadyInPlaylist` counts as done, so the step is safe to repeat.
- `playlists.list` with `mine=true` takes up to 50 per page.
- `videoCategories.list` needs `regionCode`. Only categories with `snippet.assignable` can be given to a video.

### Shorts

There is no API setting. YouTube's help says square or vertical videos up to three minutes, uploaded on or after 2024-10-15, are Shorts on standard channels. Official Artist Channels use 2025-12-08 for videos over one minute. Wider videos are long-form.

### Sign-in

- Desktop app client type, loopback redirect `http://127.0.0.1:port`, always PKCE. The S256 challenge is **base64url**, unlike TikTok.
- `client_secret` is listed as optional. Custom URI schemes and copy/paste flows are not supported.
- An external app in "Testing" status gets refresh tokens that **expire in 7 days**. Tokens also stop working after six months unused, on revocation, or past 100 per client per account.
- Endpoints: `https://accounts.google.com/o/oauth2/v2/auth`, `https://oauth2.googleapis.com/token` and `https://oauth2.googleapis.com/revoke`. Revoking removes every scope granted to the project.
- The installed-apps guide doesn't say a loopback redirect may have a path, so the app uses `http://127.0.0.1:port` with none.
- The same guide says `state` comes back in the URL fragment, which a local server can't see. The app reads it from the query string, as standard OAuth does. Confirm this at the first real sign-in.

### Scopes

From the [API discovery document](https://www.googleapis.com/discovery/v1/apis/youtube/v3/rest), revision 20261029:

| Method | Scopes that allow it |
|---|---|
| `videos.insert` | `youtube`, `youtube.force-ssl`, `youtube.upload`, `youtubepartner` |
| `playlistItems.insert` | `youtube`, `youtube.force-ssl`, `youtubepartner` |
| `playlists.list` | `youtube`, `youtube.force-ssl`, `youtube.readonly`, `youtubepartner` |
| `channels.list` | `youtube`, `youtube.force-ssl`, `youtube.readonly`, `youtubepartner`, `youtubepartner-channel-audit` |
| `videoCategories.list` | `youtube`, `youtube.force-ssl`, `youtube.readonly`, `youtubepartner` |

The app always requests `youtube.upload` and `youtube.readonly`. Adding to a playlist needs `youtube.force-ssl`, which also allows deleting videos, comments and captions, so it is requested only when the user asks for playlist support. Google doesn't support incremental authorization for installed apps, so turning this on means connecting again.

## Sources

- [TikTok inbox upload](https://developers.tiktok.com/doc/content-posting-api-reference-upload-video)
- [TikTok media transfer](https://developers.tiktok.com/doc/content-posting-api-media-transfer-guide)
- [TikTok post status](https://developers.tiktok.com/doc/content-posting-api-reference-get-video-status)
- [TikTok upload getting started](https://developers.tiktok.com/doc/content-posting-api-get-started-upload-content)
- [TikTok token management](https://developers.tiktok.com/doc/oauth-user-access-token-management)
- [TikTok Login Kit for Desktop](https://developers.tiktok.com/doc/login-kit-desktop)
- [TikTok OAuth error handling](https://developers.tiktok.com/doc/oauth-error-handling)
- [TikTok user info](https://developers.tiktok.com/doc/tiktok-api-v2-get-user-info)
- [TikTok Content Sharing Guidelines](https://developers.tiktok.com/doc/content-sharing-guidelines)
- [Google OAuth for installed apps](https://developers.google.com/youtube/v3/guides/auth/installed-apps)
- [YouTube videos.insert](https://developers.google.com/youtube/v3/docs/videos/insert)
- [YouTube videos resource](https://developers.google.com/youtube/v3/docs/videos)
- [YouTube resumable uploads](https://developers.google.com/youtube/v3/guides/using_resumable_upload_protocol)
- [YouTube errors](https://developers.google.com/youtube/v3/docs/errors)
- [YouTube global errors](https://developers.google.com/youtube/v3/docs/core_errors)
- [YouTube playlistItems.insert](https://developers.google.com/youtube/v3/docs/playlistItems/insert)
- [YouTube playlists.list](https://developers.google.com/youtube/v3/docs/playlists/list)
- [YouTube videoCategories.list](https://developers.google.com/youtube/v3/docs/videoCategories/list)
- [YouTube videos.list](https://developers.google.com/youtube/v3/docs/videos/list)
- [YouTube quota costs](https://developers.google.com/youtube/v3/determine_quota_cost)
- [YouTube API revision history](https://developers.google.com/youtube/v3/revision_history)
- [YouTube Shorts help](https://support.google.com/youtube/answer/15424877)
- [Google OAuth for native apps](https://developers.google.com/identity/protocols/oauth2/native-app)
- [Google OAuth overview](https://developers.google.com/identity/protocols/oauth2)
