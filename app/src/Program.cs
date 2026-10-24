using System.IO;
using System.Windows;
using System.Windows.Threading;
using Winawdl.Engine;
using Winawdl.Model;
using Winawdl.Radio;
using Winawdl.Shell;
using Winawdl.Theme;
using Winawdl.Views;

namespace Winawdl;

public static class Program
{
    private const string OnlyOne = @"Local\winawdl-app";
    private const string ShowSignal = @"Local\winawdl-show";
    /// <summary>What follows this on the command line goes to the engine, for a
    /// test without a radio: --engine --loopback --peer [::1]:8772</summary>
    private const string EngineFlag = "--engine";

    [STAThread]
    public static int Main(string[] args) => args switch
    {
        [RadioSupervisor.Flag, .. var rest] => RadioSupervisor.Run(rest),
        [Dev.Stage.Flag, .. var rest] => Dev.Stage.Run(rest),
        [Dev.Drive.Flag, .. var rest] => Dev.Drive.Run(rest),
        [Dev.Checks.Flag, .. var rest] => Dev.Checks.Run(rest),
        [WifiCard.GiveBackFlag] => WifiCard.GiveBackNow(new WindowsCard(), RadioSupervisor.LogFolder),
        _ => RunApp(args),
    };

    /// <summary>The styles every window and every render shares.</summary>
    public static ThemeService Dress(Application app)
    {
        app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri("/winawdl;component/src/theme/controls.xaml", UriKind.Relative)));
        return new ThemeService(app.Resources, app.Dispatcher);
    }

    private static int RunApp(string[] args)
    {
        using var only = new Mutex(true, OnlyOne, out var first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignal);
        if (!first)
        {
            // Already running, perhaps with its window closed: bring that one up.
            show.Set();
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += OnCrash;
        var theme = Dress(app);
        theme.Watch();

        var engineArguments = args.SkipWhile(argument => argument != EngineFlag).Skip(1).ToList();
        var repo = Repo.Find();
        var settings = SettingsStore.Load();
        var engine = new EngineProcess(app.Dispatcher, repo, engineArguments);
        var radio = new RadioControl(app.Dispatcher, repo,
                                     repo is not null && !Repo.IsSourceTree(repo) ? new WindowsCard() : null);
        var model = new TableModel(engine, radio, new Desk(app.Dispatcher), settings);
        var window = new MainWindow(model, theme, settings);
        using var tray = new Tray(window, () =>
        {
            window.Quitting = true;
            window.Close();
            app.Shutdown();
        }, model);
        window.Closed += (_, _) => app.Shutdown();
        window.WentToTray += () =>
        {
            if (settings.TrayNoticeShown) return;
            settings.TrayNoticeShown = true;
            settings.Save();
            tray.Say("winawdl is still receiving. Open it or quit it from this icon.");
        };
        var requests = new RequestWindow(model, () => window.IsActive);
        window.Activated += (_, _) => requests.Reconsider();
        window.Deactivated += (_, _) => requests.Reconsider();
        model.Attention += headline =>
        {
            if (!window.IsActive && !requests.Announce()) tray.Say(headline);
        };
        model.Arrived += arrival =>
        {
            if (!window.IsActive && !requests.Announce(arrival))
                tray.Say(arrival.Headline, () => arrival.Press?.Execute(null));
        };

        var waiting = new Thread(() =>
        {
            while (show.WaitOne()) app.Dispatcher.BeginInvoke(window.Surface);
        })
        {
            IsBackground = true, Name = "show",
        };
        waiting.Start();

        model.StartTheEngine();
        window.Show();
        app.Run();
        engine.Stop();
        radio.Stop();
        return 0;
    }

    private static void OnCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var report = Path.Combine(Repo.DataFolder, "crash.log");
        File.AppendAllText(report, $"{DateTime.Now:u}\n{e.Exception}\n\n");
        MessageBox.Show($"winawdl has to close because of an error. What happened is written in\n{report}",
                        "winawdl", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
