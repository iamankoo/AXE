using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AxeV2.Dialogs;
using AxeV2.Window;
using WpfWindow = System.Windows.Window;

namespace AxeV2.Updates;

/// <summary>
/// The update prompt: current and new version, release notes, "Update now" / "Later" (or only "Update now" and "Exit AXE" when
/// the update is enforced), a progress bar while downloading, and a clear error with a retry. AXE-styled and protected from
/// screen capture before it is shown, like every other AXE window.
/// </summary>
internal sealed class UpdateDialog : WpfWindow
{
    private readonly UpdateInfo _update;
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly Button _primary;
    private readonly Button _secondary;
    private CancellationTokenSource? _cts;

    /// <summary>Raised when the user asks to install; the owner runs the download and calls Succeeded/Failed.</summary>
    public event Func<IProgress<double>, CancellationToken, Task<bool>>? InstallRequested;

    public bool Installing { get; private set; }

    public UpdateDialog(WpfWindow? owner, UpdateInfo update)
    {
        _update = update;
        Owner = owner is { IsVisible: true } ? owner : null;
        WindowStartupLocation = Owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = (Brush)Application.Current.FindResource("SurfaceBrush");
        Title = "AXE update";
        MaxWidth = 460;
        AutomationProperties.SetAutomationId(this, "UpdateDialog");

        var muted = (Brush)Application.Current.FindResource("TextMutedBrush");
        var font = (FontFamily)Application.Current.FindResource("UiFont");
        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 18), MinWidth = 340 };

        panel.Children.Add(new TextBlock
        {
            Text = update.Enforced ? "AXE update required" : "AXE update available",
            FontFamily = font,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(Line($"Current version: {update.Current.ToString(3)}", muted, font, "UpdateCurrentVersion"));
        panel.Children.Add(Line($"New version: {update.Version.ToString(3)}  ({update.ReleaseDate})", muted, font, "UpdateNewVersion"));
        if (update.Enforced)
        {
            panel.Children.Add(Line("This update is required to keep using AXE.", (Brush)Application.Current.FindResource("TextBrush"), font, "UpdateRequiredNote"));
        }

        panel.Children.Add(new TextBlock
        {
            Text = update.ReleaseNotes,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = font,
            FontSize = 12,
            Foreground = muted,
            MaxHeight = 150,
            Margin = new Thickness(0, 10, 0, 0),
        });
        AutomationProperties.SetAutomationId((TextBlock)panel.Children[^1], "UpdateReleaseNotes");

        _status = new TextBlock { FontFamily = font, FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(_status, "UpdateStatus");
        panel.Children.Add(_status);
        _progress = new ProgressBar { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(_progress, "UpdateProgress");
        panel.Children.Add(_progress);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        _secondary = new Button { Content = update.Enforced ? "Exit AXE" : "Later", Style = (Style)Application.Current.FindResource("DialogButton"), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        AutomationProperties.SetAutomationId(_secondary, "UpdateLaterButton");
        _secondary.Click += (_, _) => OnSecondary();
        _primary = new Button { Content = "Update now", Style = (Style)Application.Current.FindResource("PrimaryDialogButton"), IsDefault = true };
        AutomationProperties.SetAutomationId(_primary, "UpdateNowButton");
        _primary.Click += async (_, _) => await StartAsync();
        buttons.Children.Add(_secondary);
        buttons.Children.Add(_primary);
        panel.Children.Add(buttons);

        Content = new Border { BorderBrush = (Brush)Application.Current.FindResource("WindowBorderBrush"), BorderThickness = new Thickness(1), Child = panel };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
        Loaded += (_, _) => _primary.Focus();
        Closing += (_, e) =>
        {
            if (Installing)
            {
                e.Cancel = true; // the user must cancel first
            }
        };
    }

    private static TextBlock Line(string text, Brush brush, FontFamily font, string id)
    {
        var block = new TextBlock { Text = text, FontFamily = font, FontSize = 12.5, Foreground = brush };
        AutomationProperties.SetAutomationId(block, id);
        return block;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        AxeDialog.Capture?.ProtectAuxiliary(hwnd); // before the dialog is first shown
        WindowManager.ApplyOpacityTo(hwnd, AxeDialog.CurrentOpacity?.Invoke() ?? 1.0);
    }

    private void OnSecondary()
    {
        if (Installing)
        {
            _cts?.Cancel(); // while downloading the secondary button is "Cancel"
            return;
        }

        DialogResult = false;
    }

    private async Task StartAsync()
    {
        if (Installing || InstallRequested is null)
        {
            return;
        }

        Installing = true;
        _cts = new CancellationTokenSource();
        _primary.IsEnabled = false;
        _secondary.Content = "Cancel";
        _status.Text = "Downloading and verifying the update...";
        _status.Visibility = Visibility.Visible;
        _progress.Value = 0;
        _progress.Visibility = Visibility.Visible;

        bool done;
        try
        {
            var progress = new Progress<double>(value =>
            {
                _progress.Value = value;
                _status.Text = $"Downloading and verifying the update... {value:P0}";
            });
            done = await InstallRequested.Invoke(progress, _cts.Token);
        }
        finally
        {
            Installing = false;
        }

        if (done)
        {
            _status.Text = "Update verified. AXE will close, install it and reopen.";
            DialogResult = true;
        }
    }

    /// <summary>The download/verification failed or was cancelled: show why and allow a retry.</summary>
    public void ShowFailure(string message)
    {
        Installing = false;
        _status.Text = message;
        _status.Visibility = Visibility.Visible;
        _progress.Visibility = Visibility.Collapsed;
        _primary.IsEnabled = true;
        _primary.Content = "Try again";
        _secondary.Content = _update.Enforced ? "Exit AXE" : "Later";
    }
}
