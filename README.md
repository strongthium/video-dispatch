# video-dispatch

A desktop app for publishing video to TikTok and YouTube, including Shorts. It runs on Windows, macOS and Linux with no server: each user registers their own TikTok and Google apps, and tokens stay in the operating system's secure store.

**Status: early development.** Only the core library exists so far: job and preset models, template rendering, and an MP4/MOV metadata reader. It can't connect to an account or upload anything yet.

## Build and test

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

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
