using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AXE.Browser;
using AXE.Models;
using AXE.Services;
using AXE.Window;
using WpfWindow = System.Windows.Window;

namespace AXE.Dialogs;

/// <summary>Small settings dialog: start page, search engine, default opacity, data tools.</summary>
public sealed class SettingsWindow : WpfWindow
{
    private readonly AppSettings _original;
    private readonly BrowserService _browser;
    private readonly TextBox _startPage;
    private readonly TextBox _customTemplate;
    private readonly List<(RadioButton Radio, string Template)> _engines = new();
    private readonly RadioButton _customRadio;
    private readonly Slider _opacity;
    private readonly TextBlock _error;

    public SettingsWindow(WpfWindow owner, AppSettings current, CaptureProtectionState captureState, BrowserService browser)
    {
        _original = current;
        _browser = browser;
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = 440;
        Title = "AXE Settings";
        Background = Res<Brush>("SurfaceBrush");
        FontFamily = Res<FontFamily>("UiFont");

        var root = new StackPanel { Margin = new Thickness(22, 16, 22, 18) };

        // Brand block
        var brand = new TextBlock { FontSize = 20, FontWeight = FontWeights.Black, FontFamily = new FontFamily("Segoe UI"), Foreground = Res<Brush>("TextBrush") };
        brand.Inlines.Add(new System.Windows.Documents.Run("AX"));
        brand.Inlines.Add(new System.Windows.Documents.Run("E") { Foreground = Res<Brush>("AccentBrush") });
        root.Children.Add(brand);
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        root.Children.Add(Muted($"Private Floating Browser · v{version}"));
        root.Children.Add(Muted("Engineered by Aniket Raj", 0));

        root.Children.Add(Label("Start page"));
        _startPage = new TextBox { Text = current.Browser.StartPage, Style = Res<Style>("FieldTextBox") };
        root.Children.Add(_startPage);

        root.Children.Add(Label("Search engine"));
        var enginePanel = new WrapPanel();
        foreach (var (name, template) in BrowserSettings.KnownSearchEngines)
        {
            var radio = new RadioButton { Content = name, GroupName = "engine", Style = Res<Style>("DialogRadio") };
            radio.IsChecked = string.Equals(template, current.Browser.SearchUrlTemplate, StringComparison.OrdinalIgnoreCase);
            _engines.Add((radio, template));
            enginePanel.Children.Add(radio);
        }

        _customRadio = new RadioButton { Content = "Custom", GroupName = "engine", Style = Res<Style>("DialogRadio") };
        _customRadio.IsChecked = !_engines.Any(e => e.Radio.IsChecked == true);
        enginePanel.Children.Add(_customRadio);
        root.Children.Add(enginePanel);

        _customTemplate = new TextBox
        {
            Text = _customRadio.IsChecked == true ? current.Browser.SearchUrlTemplate : "https://example.com/search?q={query}",
            Style = Res<Style>("FieldTextBox"),
            Margin = new Thickness(0, 8, 0, 0),
        };
        _customTemplate.IsEnabled = _customRadio.IsChecked == true;
        _customRadio.Checked += (_, _) => _customTemplate.IsEnabled = true;
        _customRadio.Unchecked += (_, _) => _customTemplate.IsEnabled = false;
        root.Children.Add(_customTemplate);
        root.Children.Add(Muted("Custom search URL must contain {query}.", 4));

        root.Children.Add(Label("Opacity when AXE starts"));
        var opacityRow = new DockPanel();
        var opacityValue = new TextBlock { Width = 40, Foreground = Res<Brush>("TextBrush"), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        DockPanel.SetDock(opacityValue, Dock.Right);
        _opacity = new Slider
        {
            Minimum = 10, Maximum = 100, SmallChange = 1, LargeChange = 10, Height = 20,
            Value = WindowPlacement.ToPercent(current.DefaultOpacity), Style = Res<Style>("AccentSlider"),
        };
        _opacity.ValueChanged += (_, e) => opacityValue.Text = $"{Math.Round(e.NewValue)}%";
        opacityValue.Text = $"{Math.Round(_opacity.Value)}%";
        opacityRow.Children.Add(opacityValue);
        opacityRow.Children.Add(_opacity);
        root.Children.Add(opacityRow);

        root.Children.Add(Label("Privacy & diagnostics"));
        root.Children.Add(Muted(DescribeCapture(captureState), 0));
        root.Children.Add(Muted($"Show / hide shortcut: {(string.IsNullOrWhiteSpace(current.ToggleHotkey) ? "off" : current.ToggleHotkey)}", 2));
        var tools = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var clear = new Button { Content = "Clear browsing data", Style = Res<Style>("DialogButton"), Margin = new Thickness(0, 0, 8, 0) };
        clear.Click += async (_, _) => await ClearBrowsingDataAsync(clear);
        var logs = new Button { Content = "Open log folder", Style = Res<Style>("DialogButton") };
        logs.Click += (_, _) => OpenFolder(AppPaths.Logs);
        tools.Children.Add(clear);
        tools.Children.Add(logs);
        root.Children.Add(tools);

        _error = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        root.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Cancel", Style = Res<Style>("DialogButton"), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "Save", Style = Res<Style>("PrimaryDialogButton"), IsDefault = true };
        save.Click += (_, _) => TrySave();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);

        Content = new Border { BorderBrush = Res<Brush>("WindowBorderBrush"), BorderThickness = new Thickness(1), Child = root };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox)
            {
                try { DragMove(); } catch (InvalidOperationException) { }
            }
        };
    }

    public AppSettings Result { get; private set; } = new();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        AxeDialog.Capture?.ProtectAuxiliary(hwnd);
        WindowManager.ApplyOpacityTo(hwnd, AxeDialog.CurrentOpacity?.Invoke() ?? 1.0);
    }

    private void TrySave()
    {
        var startPage = _startPage.Text.Trim();
        if (!AddressResolver.IsValidStartPage(startPage))
        {
            ShowError("Start page must be a full address, for example https://www.google.com");
            return;
        }

        var template = _customRadio.IsChecked == true
            ? _customTemplate.Text.Trim()
            : _engines.First(e => e.Radio.IsChecked == true).Template;
        if (!AddressResolver.IsValidSearchTemplate(template))
        {
            ShowError("Custom search URL must be an http(s) address containing {query}.");
            return;
        }

        Result = new AppSettings
        {
            DefaultOpacity = Math.Round(_opacity.Value) / 100.0,
            ToggleHotkey = _original.ToggleHotkey,
            Browser = new BrowserSettings
            {
                StartPage = startPage,
                SearchUrlTemplate = template,
                UserDataFolder = _original.Browser.UserDataFolder,
                EnableDevTools = _original.Browser.EnableDevTools,
                EnableAutofill = _original.Browser.EnableAutofill,
            },
        };
        DialogResult = true;
    }

    private async Task ClearBrowsingDataAsync(Button button)
    {
        if (_browser.Profile is null)
        {
            return;
        }

        if (!AxeDialog.Confirm(this, "Clear browsing data?", "Cookies, site data, cache and history stored by AXE will be deleted. You'll be signed out of websites.", "Clear", "Cancel"))
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            await _browser.Profile.ClearBrowsingDataAsync();
            Log.Info("Browsing data cleared.");
            button.Content = "Cleared";
        }
        catch (Exception ex)
        {
            Log.Error("Clearing browsing data failed.", ex);
            button.IsEnabled = true;
            ShowError("Browsing data couldn't be cleared.");
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open folder ({Log.Describe(ex)}).");
        }
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
    }

    private static string DescribeCapture(CaptureProtectionState state) => state switch
    {
        CaptureProtectionState.Excluded => "Screen-capture exclusion: active (supported capture apps only).",
        CaptureProtectionState.MonitorOnly => "Screen-capture exclusion: unavailable — AXE shows as a black area in captures.",
        CaptureProtectionState.Disabled => "Screen-capture exclusion: disabled by a diagnostic switch.",
        _ => "Screen-capture exclusion: NOT active — AXE may appear in captures.",
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12.5,
        FontWeight = FontWeights.SemiBold,
        Foreground = Res<Brush>("TextBrush"),
        Margin = new Thickness(0, 16, 0, 6),
    };

    private static TextBlock Muted(string text, double top = 2) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Res<Brush>("TextMutedBrush"),
        Margin = new Thickness(0, top, 0, 0),
    };

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);
}
