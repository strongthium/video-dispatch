using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VideoDispatch.App;

public sealed partial class App : Application
{
    private Services? _services;

    public static ImageSource Icon { get; } = BitmapFrame.Create(new Uri("pack://application:,,,/video-dispatch;component/Assets/video-dispatch.png"));

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // There's no window yet, and with none open the app would count as finished.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _services = Program.Sample ? await SampleServices.CreateAsync() : Services.CreateReal();

        // Jobs are read off the UI thread, and the queue starts moving them.
        var queue = _services.Queue;
        var damaged = await Task.Run(queue.Start);

        var window = new MainWindow(_services, damaged);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Jobs stay as they are on disk, for the next start to pick up. The app won't wait long for slow network calls to stop.
        if (_services is { } services)
        {
            Task.Run(async () => await services.Queue.DisposeAsync()).Wait(TimeSpan.FromSeconds(5));
        }

        base.OnExit(e);
    }
}
