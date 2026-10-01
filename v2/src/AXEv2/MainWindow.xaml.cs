using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AxeV2.Access;
using AxeV2.Browser;
using AxeV2.Controls;
using AxeV2.Dialogs;
using AxeV2.Models;
using AxeV2.Services;
using AxeV2.Window;
using Microsoft.Web.WebView2.Core;

namespace AxeV2;

public partial class MainWindow
{
    private const string WebView2DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    private readonly SettingsService _settingsService;
    private readonly CommandLineOptions _options;
    private readonly CaptureExclusionService _capture;
    private readonly WindowManager _windowManager;
    private readonly WindowStateManager _stateManager;
    private readonly BrowserService _browser;
    private readonly AccessController _accessController;
    private readonly AccessView _accessView;
    private bool _browserStarted;
    private Action? _messagePrimary;
    private Action? _messageSecondary;
    private bool _closing;

    public MainWindow(SettingsService settingsService, CommandLineOptions options, CaptureExclusionService capture)
    {
        _settingsService = settingsService;
        _options = options;
        _capture = capture;
        InitializeComponent();

        VersionRun.Text = " v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "2");
        _windowManager = new WindowManager(this);
        _stateManager = new WindowStateManager(this, _windowManager, _capture);

        _browser = new BrowserService(
            BrowserHost,
            this,
            _capture,
            settingsService.Current.Browser);

        TabStrip.ItemsSource = _browser.Tabs;

        // Access is completely separate from browser initialization.
        // The controller owns authorization, polling, trusted time and expiry.
        var accessStore = new AccessStore(AppPaths.Root);
        var accessConfig = AccessConfig.LoadEmbedded();

        _accessController = new AccessController(accessConfig, accessStore);
        _accessView = new AccessView(_accessController);

        AccessHost.Content = _accessView;

        _accessController.PhaseChanged += AccessController_PhaseChanged;

        // ≈ ¼ of the available screen, floating near the upper-right.
        var bounds = WindowPlacement.Initial(SystemParameters.WorkArea);
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        MinWidth = WindowPlacement.MinWidth;
        MinHeight = WindowPlacement.MinHeight;

        var opacity = AppSettings.ClampOpacity(options.Opacity ?? settingsService.Current.DefaultOpacity);
        OpacitySlider.Value = WindowPlacement.ToPercent(opacity);
        UpdatePlaceholder();

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            _windowManager.SetOpacity(OpacitySlider.Value / 100.0);
        };
        PreviewKeyDown += OnPreviewKeyDown;

        _capture.StateChanged += (_, _) => UpdateCaptureWarning();
        _stateManager.VisualStateChanged += (_, _) => UpdateWindowChrome();
        _browser.NavigationStateChanged += (_, _) => UpdateNavigationState();
        _browser.FaviconChanged += (_, icon) => UpdateFavicon(icon);
        _browser.FullscreenChanged += (_, fullscreen) => _stateManager.SetPageFullscreen(fullscreen);
        _browser.DownloadsChanged += (_, _) => DownloadsButton.Visibility = Visibility.Visible;
        _browser.ActiveTabChanged += (_, _) => FindTabPanel()?.InvalidateArrange();
        _browser.EngineFailed += (_, message) =>
        {
            var reopen = _browser.OpenTabUrls();
            ShowMessage(
                "Browser stopped", $"{message} Restart the browser to continue.",
                "Restart browser", async () => await StartBrowserAsync(reopen), null, null);
        };
    }

    public double CurrentOpacity => _windowManager.Opacity;

    /// <summary>Brings AXE back (second launch from Start/Search, or the hotkey).</summary>
    public void RestoreFromExternalRequest() => _stateManager.Restore();

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowManager.Attach();
        _windowManager.ExtendedStyleChanged += (_, _) => _capture.EnsureMain("style change");

        // Three independent concerns, applied independently:
        _capture.AttachMainWindow(_windowManager.Handle);       // 1. capture exclusion
        _windowManager.SetOpacity(OpacitySlider.Value / 100.0);  // 2. local opacity
        Topmost = true;                                          // 3. stacking order

        _windowManager.DisplayChanged += (_, _) => _capture.EnsureMain("display change");
        _windowManager.HotkeyPressed += (_, _) => _stateManager.ToggleVisibility();
        _windowManager.RegisterHotkey(_settingsService.Current.ToggleHotkey);
        UpdateCaptureWarning();
        Log.Info("Main window created.");
    }

    // ------------------------------------------------------------------ access lifecycle

    private void AccessController_PhaseChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(
                () => AccessController_PhaseChanged(null, EventArgs.Empty));
            return;
        }

        if (_accessController.Phase.AllowsBrowsing())
        {
            ShowAuthorizedBrowser();
        }
        else
        {
            HideAuthorizedBrowser();
        }
    }

    private void ShowAuthorizedBrowser()
    {
        AccessHost.Visibility = Visibility.Collapsed;
        BrowserHost.Visibility = Visibility.Visible;

        if (_browserStarted)
        {
            _browser.SetBrowserVisible(true);
            _browser.FocusPage();
            return;
        }

        _browserStarted = true;
        _ = StartBrowserAfterAuthorizationAsync();
    }

    private void HideAuthorizedBrowser()
    {
        _browser.SetBrowserVisible(false);

        BrowserHost.Visibility = Visibility.Collapsed;
        AccessHost.Visibility = Visibility.Visible;
    }

    private async Task StartBrowserAfterAuthorizationAsync()
    {
        try
        {
            HideMessage();

            var result = await _browser.InitializeAsync(_options.InitialAddress);

            // Authorization could theoretically change while WebView2
            // is initializing, so check again before exposing the browser.
            if (_accessController.Phase != AccessPhase.Active)
            {
                HideAuthorizedBrowser();
                return;
            }

            switch (result)
            {
                case BrowserInitResult.Ready:
                    _browser.SetBrowserVisible(true);
                    _browser.FocusPage();
                    break;

                case BrowserInitResult.RuntimeMissing:
                    ShowMessage(
                        "WebView2 Runtime required",
                        "AXE displays websites with Microsoft Edge WebView2, which isn't installed on this PC. " +
                        "Install the free runtime from Microsoft, then select Retry.",
                        "Retry",
                        async () =>
                        {
                            if (_accessController.Phase == AccessPhase.Active)
                            {
                                await StartBrowserAfterAuthorizationAsync();
                            }
                        },
                        "Get WebView2",
                        () => OpenExternal(WebView2DownloadUrl));
                    break;

                default:
                    ShowMessage(
                        "The browser couldn't start",
                        "AXE couldn't start its browser engine. Close other AXE windows and try again.",
                        "Retry",
                        async () =>
                        {
                            if (_accessController.Phase == AccessPhase.Active)
                            {
                                await StartBrowserAfterAuthorizationAsync();
                            }
                        },
                        "Close AXE",
                        Close);
                    break;
            }
        }
        catch (Exception ex)
        {
            _browserStarted = false;

            Log.Error("Browser initialization failed after authorization.", ex);

            HideAuthorizedBrowser();

            if (_accessController.Phase == AccessPhase.Active)
            {
                ShowMessage(
                    "The browser couldn't start",
                    "AXE couldn't initialize its browser engine.",
                    "Retry",
                    async () =>
                    {
                        if (_accessController.Phase == AccessPhase.Active)
                        {
                            await StartBrowserAfterAuthorizationAsync();
                        }
                    },
                    "Close AXE",
                    Close);
            }
        }
    }

    // ------------------------------------------------------------------ browser lifecycle

    private async Task StartBrowserAsync(IReadOnlyList<string>? reopenTabs = null)
    {
        if (_accessController.Phase != AccessPhase.Active)
        {
            HideAuthorizedBrowser();
            return;
        }

        HideMessage();

        var result = await _browser.InitializeAsync(
            _options.InitialAddress,
            reopenTabs);
        switch (result)
        {
            case BrowserInitResult.Ready:
                _browser.FocusPage();
                break;
            case BrowserInitResult.RuntimeMissing:
                ShowMessage(
                    "WebView2 Runtime required",
                    "AXE displays websites with Microsoft Edge WebView2, which isn't installed on this PC. " +
                    "Install the free runtime from Microsoft, then select Retry.",
                    "Retry", async () => await StartBrowserAsync(),
                    "Get WebView2", () => OpenExternal(WebView2DownloadUrl));
                break;
            default:
                ShowMessage(
                    "The browser couldn't start",
                    "AXE couldn't start its browser engine. Close other AXE windows and try again. " +
                    "If this keeps happening, repairing the WebView2 Runtime usually fixes it.",
                    "Retry", async () => await StartBrowserAsync(),
                    "Close AXE", Close);
                break;
        }
    }

    private void ShowMessage(string title, string body, string primary, Action primaryAction, string? secondary, Action? secondaryAction)
    {
        MessageTitle.Text = title;
        MessageBody.Text = body;
        MessagePrimaryButton.Content = primary;
        _messagePrimary = primaryAction;
        MessageSecondaryButton.Content = secondary;
        MessageSecondaryButton.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        _messageSecondary = secondaryAction;
        _browser.SetBrowserVisible(false); // the page HWND would otherwise cover the panel
        MessagePanel.Visibility = Visibility.Visible;
        SetLoading(false);
    }

    private void HideMessage()
    {
        MessagePanel.Visibility = Visibility.Collapsed;
        _browser.SetBrowserVisible(true);
    }

    private void MessagePrimaryButton_Click(object sender, RoutedEventArgs e) => _messagePrimary?.Invoke();

    private void MessageSecondaryButton_Click(object sender, RoutedEventArgs e) => _messageSecondary?.Invoke();

    private static void OpenExternal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open external link ({Log.Describe(ex)}).");
        }
    }

    // ------------------------------------------------------------------ navigation UI

    private void UpdateNavigationState()
    {
        BackButton.IsEnabled = _browser.CanGoBack;
        ForwardButton.IsEnabled = _browser.CanGoForward;
        ReloadButton.Content = _browser.IsLoading ? "" : "";
        AutomationPropertiesHelper.SetName(ReloadButton, _browser.IsLoading ? "Stop" : "Refresh");
        SetLoading(_browser.IsLoading);

        if (!AddressBox.IsKeyboardFocused)
        {
            AddressBox.Text = DisplayUrl(_browser.CurrentUrl);
        }
    }

    internal static string DisplayUrl(string url) => MainWindowUrl.Display(url);

    // ------------------------------------------------------------------ tabs

    private async void NewTabButton_Click(object sender, RoutedEventArgs e) => await OpenNewTabAsync();

    /// <summary>
    /// Ctrl+T / "+": a start-page tab with the address bar ready for typing, as in Chrome.
    /// The tab is selected and the address bar focused before Chromium finishes creating the
    /// tab (NewTabAsync activates synchronously), so the click responds immediately.
    /// </summary>
    private async Task OpenNewTabAsync()
    {
        var opening = _browser.NewTabAsync();
        FocusAddressBar();
        await opening;
    }

    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab)
        {
            _browser.Activate(tab);
            _browser.FocusPage();
            e.Handled = true; // a click on a tab is not a window drag
        }
    }

    private void Tab_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && (sender as FrameworkElement)?.DataContext is BrowserTab tab)
        {
            _browser.CloseTab(tab);
            e.Handled = true;
        }
    }

    private void TabCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab)
        {
            _browser.CloseTab(tab);
        }

        e.Handled = true;
    }

    private TabStripPanel? FindTabPanel()
    {
        static T? Find<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                {
                    return match;
                }

                if (Find<T>(child) is { } nested)
                {
                    return nested;
                }
            }

            return null;
        }

        return Find<TabStripPanel>(TabStrip);
    }

    private void UpdateFavicon(ImageSource? icon)
    {
        FaviconImage.Source = icon;
        FaviconImage.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        SearchGlyph.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePlaceholder() =>
        AddressPlaceholder.Text = $"Search {_settingsService.Current.Browser.SearchEngineName} or type a URL";

    private void SetLoading(bool loading)
    {
        if (loading)
        {
            if (LoadingTrack.Visibility == Visibility.Visible)
            {
                return;
            }

            LoadingTrack.Visibility = Visibility.Visible;
            var sweep = new DoubleAnimation(-LoadingBar.Width, Math.Max(ActualWidth, 400), TimeSpan.FromSeconds(1.1))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            LoadingOffset.BeginAnimation(TranslateTransform.XProperty, sweep);
        }
        else
        {
            LoadingOffset.BeginAnimation(TranslateTransform.XProperty, null);
            LoadingTrack.Visibility = Visibility.Hidden;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => _browser.GoBack();

    private void ForwardButton_Click(object sender, RoutedEventArgs e) => _browser.GoForward();

    private void ReloadButton_Click(object sender, RoutedEventArgs e) => _browser.ReloadOrStop();

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (_browser.Navigate(AddressBox.Text))
            {
                _browser.FocusPage();
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            AddressBox.Text = DisplayUrl(_browser.CurrentUrl);
            _browser.FocusPage();
            e.Handled = true;
        }
    }

    private void AddressBox_TextChanged(object sender, TextChangedEventArgs e) =>
        AddressPlaceholder.Visibility = AddressBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void AddressBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        AddressPill.BorderBrush = (Brush)FindResource("AccentBrush");
        AddressBox.SelectAll();
    }

    private void AddressBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        AddressPill.BorderBrush = (Brush)FindResource("FieldBorderBrush");
        AddressBox.Text = DisplayUrl(_browser.CurrentUrl);
    }

    private void AddressBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // First click selects the whole address, like other browsers.
        if (!AddressBox.IsKeyboardFocusWithin)
        {
            AddressBox.Focus();
            e.Handled = true;
        }
    }

    private void AddressPill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        AddressBox.Focus();
        e.Handled = true;
    }

    private void FocusAddressBar()
    {
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        if ((mods == ModifierKeys.Control && key == Key.L) || (mods == ModifierKeys.Alt && key == Key.D) || key == Key.F6)
        {
            FocusAddressBar();
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && key == Key.T)
        {
            _ = OpenNewTabAsync();
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && key is Key.W or Key.F4)
        {
            _browser.CloseActiveTab();
            e.Handled = true;
        }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.T)
        {
            _browser.ReopenClosedTab();
            e.Handled = true;
        }
        else if ((mods == ModifierKeys.Control && key is Key.Tab or Key.PageDown)
                 || (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.Tab)
                 || (mods == ModifierKeys.Control && key == Key.PageUp))
        {
            var back = key == Key.PageUp || (mods & ModifierKeys.Shift) != 0;
            _browser.SelectRelative(back ? -1 : 1);
            _browser.FocusPage();
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && key is >= Key.D1 and <= Key.D9)
        {
            _browser.SelectByNumber(key - Key.D0);
            _browser.FocusPage();
            e.Handled = true;
        }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key is Key.Up or Key.Down)
        {
            OpacitySlider.Value = Math.Clamp(OpacitySlider.Value + (key == Key.Up ? 5 : -5), OpacitySlider.Minimum, OpacitySlider.Maximum);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ downloads & settings

    private void DownloadsButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = DownloadsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        _browser.ProtectWhenCreated(menu);

        if (_browser.Downloads.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No downloads yet", IsEnabled = false });
        }

        foreach (var entry in _browser.Downloads)
        {
            var status = entry.State switch
            {
                CoreWebView2DownloadState.Completed => "Done",
                CoreWebView2DownloadState.Interrupted => "Failed",
                _ => entry.Percent is { } p ? $"{p}%" : "Downloading",
            };
            var item = new MenuItem { Header = entry.FileName, InputGestureText = status };
            _browser.ProtectWhenCreated(item);
            var open = new MenuItem { Header = "Open", IsEnabled = entry.State == CoreWebView2DownloadState.Completed };
            open.Click += (_, _) => BrowserService.OpenDownload(entry);
            var folder = new MenuItem { Header = "Show in folder", IsEnabled = entry.State == CoreWebView2DownloadState.Completed };
            folder.Click += (_, _) => BrowserService.ShowDownloadInFolder(entry);
            var cancel = new MenuItem { Header = "Cancel", IsEnabled = entry.State == CoreWebView2DownloadState.InProgress };
            cancel.Click += (_, _) => BrowserService.CancelDownload(entry);
            item.Items.Add(open);
            item.Items.Add(folder);
            item.Items.Add(cancel);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(this, _settingsService.Current, _capture.State, _browser);
        if (dialog.ShowDialog() == true)
        {
            _settingsService.Save(dialog.Result);
            _browser.UpdateSettings(_settingsService.Current.Browser);
            UpdatePlaceholder();
            Log.Info("Settings saved.");
        }
    }

    // ------------------------------------------------------------------ opacity

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityText is null)
        {
            return; // during InitializeComponent
        }

        var percent = (int)Math.Round(e.NewValue);
        OpacityText.Text = $"{percent}%";

        // Local visual opacity only. Capture exclusion is a separate window attribute and is not touched.
        _windowManager?.SetOpacity(percent / 100.0);
    }

    // ------------------------------------------------------------------ capture status

    private void UpdateCaptureWarning()
    {
        // No indicator is shown while AXE is protected. The warning appears only if protection
        // could NOT be applied, so AXE never implies it is hidden when it is not.
        var unprotected = _capture.State is CaptureProtectionState.Failed or CaptureProtectionState.MonitorOnly;
        CaptureWarning.Visibility = unprotected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CaptureWarning_Click(object sender, MouseButtonEventArgs e)
    {
        var text = _capture.State == CaptureProtectionState.MonitorOnly
            ? "This version of Windows can't remove AXE from screen captures. AXE will appear as a black area instead. Windows 10 version 2004 or later is required for full exclusion."
            : "Windows didn't accept AXE's screen-capture exclusion request, so AXE may be visible in screen captures and screen sharing.";
        AxeDialog.ShowMessage(this, "Screen-capture exclusion unavailable", text);
    }

    // ------------------------------------------------------------------ window chrome

    private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _stateManager.ToggleMaximize();
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        _stateManager.PrepareDragFromMaximized(e.GetPosition(this));
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // mouse released before the drag started
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => _stateManager.Minimize();

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => _stateManager.ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateWindowChrome()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "" : "";
        AutomationPropertiesHelper.SetName(MaximizeButton, maximized ? "Restore" : "Maximize");

        var fullscreen = _stateManager.IsPageFullscreen;
        HeaderRow.Height = fullscreen ? new GridLength(0) : new GridLength(42);
        NavRow.Height = fullscreen ? new GridLength(0) : new GridLength(44);
        BrowserHost.Margin = fullscreen || maximized ? new Thickness(0) : new Thickness(5, 0, 5, 5);
        RootBorder.BorderThickness = fullscreen || maximized ? new Thickness(0) : new Thickness(1);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _closing)
        {
            return;
        }

        _closing = true;
        Log.Info("Closing AXE.");
        
        _accessView.Dispose();
        _accessController.Dispose();
        _browser.Dispose();
        _windowManager.Dispose();
        _capture.Dispose();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}

/// <summary>Small helper so code-behind reads cleanly.</summary>
internal static class AutomationPropertiesHelper
{
    public static void SetName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);
}
