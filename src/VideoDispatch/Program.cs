using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using VideoDispatch.Core;

namespace VideoDispatch.App;

/// <summary>
/// The command line: <c>video-dispatch [video]</c> opens New post on that video. <c>--sample</c> runs on made-up data,
/// with no browser, no network and nothing saved. <c>--theme light|dark</c> sets the look rather than following
/// Windows. <c>--page new|queue|presets|accounts|about</c> opens on that page, for the README's pictures.
/// <c>--wait-for pid</c> is how an update starts the new program.
/// </summary>
internal static class Program
{
    private const int ShowRestore = 9;

    public static bool Sample { get; private set; }

    public static string? StartVideo { get; private set; }

    public static string? StartPage { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        string? theme = null;
        int? waitFor = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--sample":
                    Sample = true;
                    break;
                case "--theme" when i + 1 < args.Length:
                    theme = args[++i];
                    break;
                case "--page" when i + 1 < args.Length:
                    StartPage = args[++i];
                    break;
                case "--wait-for" when i + 1 < args.Length && int.TryParse(args[i + 1], out var pid):
                    waitFor = pid;
                    i++;
                    break;
                default:
                    StartVideo = args[i];
                    break;
            }
        }

        // Updated: the copy that started this one is quitting, and its file can go once it has. This comes first,
        // because that copy holds the one-instance lock until it does.
        Updater.Tidy(waitFor);

        // Two copies would each run the same queue, and each refresh the same sign-ins, and a refresh can end the token the other holds.
        using var instance = new Mutex(true, @"Local\strongthium-video-dispatch", out var isFirst);
        if (!isFirst && !Sample)
        {
            ShowRunningCopy();
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        app.ThemeMode = theme switch
        {
            "light" => ThemeMode.Light,
            "dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        return app.Run();
    }

    private static void ShowRunningCopy()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var other in Process.GetProcessesByName(current.ProcessName))
        {
            using (other)
            {
                if (other.Id != current.Id && other.MainWindowHandle != IntPtr.Zero)
                {
                    if (IsIconic(other.MainWindowHandle))
                    {
                        ShowWindow(other.MainWindowHandle, ShowRestore);
                    }

                    SetForegroundWindow(other.MainWindowHandle);
                    return;
                }
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
