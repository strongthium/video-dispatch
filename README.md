<p align="center">
  <a href="https://strongthium.com" title="Strongthium Labs"><img src="https://raw.githubusercontent.com/strongthium/.github/main/profile/banner-2200x440.png" alt="Strongthium Labs: Take back control of your internet."></a>
</p>

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

## More from Strongthium

Strongthium Labs also makes software for running your own servers and talking to your customers, at [strongthium.com](https://strongthium.com):

<table>
  <tr>
    <td width="64" align="center" valign="top">
      <a href="https://strongthium.com/linux-server-firewall"><picture><source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/strongthium/.github/main/profile/icons/dark/guard.svg"><img src="https://raw.githubusercontent.com/strongthium/.github/main/profile/icons/light/guard.svg" width="48" height="48" alt="Strongthium Guard icon"></picture></a>
    </td>
    <td valign="top">
      <a href="https://strongthium.com/linux-server-firewall"><b>Strongthium Guard</b></a><br>
      <sub><b>Linux server firewall.</b> Locks down your Linux servers, reads their logs and shuts out whoever attacks them, within seconds.</sub>
    </td>
    <td width="64" align="center" valign="top">
      <a href="https://strongthium.com/self-hosted-mail-server"><picture><source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/strongthium/.github/main/profile/icons/dark/mail.svg"><img src="https://raw.githubusercontent.com/strongthium/.github/main/profile/icons/light/mail.svg" width="48" height="48" alt="Strongthium Mail icon"></picture></a>
    </td>
    <td valign="top">
      <a href="https://strongthium.com/self-hosted-mail-server"><b>Strongthium Mail</b></a><br>
      <sub><b>Self-hosted mail server for Linux and Windows.</b> Receives, checks and sends your mail, and tells you exactly why it refused what it refused.</sub>
    </td>
  </tr>
  <tr>
    <td width="64" align="center" valign="top">
      <a href="https://strongthium.com/self-hosted-live-chat"><picture><source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/strongthium/.github/main/profile/icons/dark/chat.svg"><img src="https://raw.githubusercontent.com/strongthium/.github/main/profile/icons/light/chat.svg" width="48" height="48" alt="Strongthium Chat icon"></picture></a>
    </td>
    <td valign="top">
      <a href="https://strongthium.com/self-hosted-live-chat"><b>Strongthium Chat</b></a><br>
      <sub><b>Self-hosted live chat for Linux and Windows.</b> A chat on your websites and text messages in one inbox, answered from Windows or a browser.</sub>
    </td>
    <td width="64" align="center" valign="top">
      <a href="https://strongthium.com/windows-ssh-client"><picture><source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/strongthium/.github/main/profile/icons/dark/remote.svg"><img src="https://raw.githubusercontent.com/strongthium/.github/main/profile/icons/light/remote.svg" width="48" height="48" alt="Strongthium Remote icon"></picture></a>
    </td>
    <td valign="top">
      <a href="https://strongthium.com/windows-ssh-client"><b>Strongthium Remote</b></a><br>
      <sub><b>Windows SSH client.</b> SSH terminal, file manager, editor, server task manager, database client and remote desktop in tabs, in one program.</sub>
    </td>
  </tr>
</table>

## License

[MIT](LICENSE)
