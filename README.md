# video-dispatch

A Windows desktop app for publishing video to TikTok and YouTube, including Shorts. It runs with no server: each user registers their own TikTok and Google apps, and tokens stay in Windows Credential Manager.

**Status: early development.** Only the core library exists so far: job and preset models, template rendering, an MP4/MOV metadata reader, account sign-in and secure token storage. There is no app to run yet, and it can't upload anything.

## Secure storage

Sign-ins and app keys are kept only in Windows Credential Manager, and never in a plain file. Access tokens stay in memory.

## Build and test

Requires Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build
```

```bash
dotnet test
```

## Project layout

- `src/VideoDispatch.Core`: the publishing library, with no UI code.
- `tests/VideoDispatch.Core.Tests`: tests for the library.
- `docs/platform-notes.md`: what the code relies on from each platform's docs.

## License

[MIT](LICENSE)
