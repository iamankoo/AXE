using System.Windows;
using System.Windows.Threading;
using AxeV2.Dialogs;
using AxeV2.Services;
using AxeV2.Window;

namespace AxeV2;

public partial class App
{
    private SingleInstanceService? _singleInstance;
    private CaptureExclusionService? _capture;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Initialize(AppPaths.Logs);

        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.IsPrimary)
        {
            // AXE is already running (possibly minimized): bring it back instead of starting a
            // second browser on the same profile.
            _singleInstance.SignalPrimary();
            _singleInstance.Dispose();
            Shutdown();
            return;
        }

        Log.Info($"AXE v2 {typeof(App).Assembly.GetName().Version?.ToString(3)} started on Windows {Environment.OSVersion.Version}.");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception.", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };

        var options = CommandLineOptions.Parse(e.Args);
        var settings = new SettingsService(AppPaths.SettingsFile);
        settings.Load();

        // Locked UI requirement: the standard arrow cursor over all of AXE's own UI (text
        // fields, buttons, tabs, menus, dialogs). Applies to this process's WPF windows only;
        // web content is handled by CursorLock and window edges by WindowManager.
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Arrow;

        _capture = new CaptureExclusionService(options.DiagnosticAllowCapture);
        var window = new MainWindow(settings, options, _capture);
        AxeDialog.Capture = _capture;
        AxeDialog.CurrentOpacity = () => window.CurrentOpacity;
        MainWindow = window;

        _singleInstance.ListenForActivation(() =>
            Dispatcher.BeginInvoke(() => window.RestoreFromExternalRequest()));

        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception.", e.Exception);
        e.Handled = true; // keep the browser session alive; the error is logged locally
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        if (_capture is not null) // only the primary instance ever created a window
        {
            _capture.Dispose();
            Log.Info("AXE v2 closed.");
        }

        base.OnExit(e);
    }
}
