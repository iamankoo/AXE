using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AXE.Window;
using WpfWindow = System.Windows.Window;

namespace AXE.Dialogs;

/// <summary>
/// AXE-styled modal dialog. Used instead of browser/OS dialogs so that every prompt AXE shows
/// is its own top-level window, excluded from capture before it is ever displayed.
/// </summary>
public sealed class AxeDialog : WpfWindow
{
    private readonly TextBox? _input;

    private AxeDialog(WpfWindow? owner, string title, string message, string primary, string? secondary,
        string? defaultText, bool withInput)
    {
        Owner = owner is { IsVisible: true } ? owner : null;
        WindowStartupLocation = Owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = (Brush)Application.Current.FindResource("SurfaceBrush");
        Title = "AXE";
        MaxWidth = 460;

        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 18), MinWidth = 320 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
            FontSize = 13,
            Foreground = (Brush)Application.Current.FindResource("TextMutedBrush"),
            MaxHeight = 260,
        });

        if (withInput)
        {
            _input = new TextBox
            {
                Text = defaultText ?? string.Empty,
                Style = (Style)Application.Current.FindResource("FieldTextBox"),
                Margin = new Thickness(0, 12, 0, 0),
            };
            panel.Children.Add(_input);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        if (secondary is not null)
        {
            var cancel = new Button
            {
                Content = secondary,
                Style = (Style)Application.Current.FindResource("DialogButton"),
                IsCancel = true,
                Margin = new Thickness(0, 0, 8, 0),
            };
            cancel.Click += (_, _) => DialogResult = false;
            buttons.Children.Add(cancel);
        }

        var ok = new Button
        {
            Content = primary,
            Style = (Style)Application.Current.FindResource("PrimaryDialogButton"),
            IsDefault = true,
            IsCancel = secondary is null,
        };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        Content = new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("WindowBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = panel,
        };

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
        Loaded += (_, _) =>
        {
            if (_input is not null)
            {
                _input.Focus();
                _input.SelectAll();
            }
            else
            {
                ok.Focus();
            }
        };
    }

    /// <summary>Set by App at startup; lets dialogs protect themselves and match AXE's opacity.</summary>
    internal static CaptureExclusionService? Capture { get; set; }

    internal static Func<double>? CurrentOpacity { get; set; }

    public string InputText => _input?.Text ?? string.Empty;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        Capture?.ProtectAuxiliary(hwnd); // before the dialog is first shown
        WindowManager.ApplyOpacityTo(hwnd, CurrentOpacity?.Invoke() ?? 1.0);
    }

    public static void ShowMessage(WpfWindow? owner, string title, string message, string button = "OK") =>
        new AxeDialog(owner, title, message, button, null, null, false).ShowDialog();

    public static bool Confirm(WpfWindow? owner, string title, string message, string yes = "OK", string no = "Cancel") =>
        new AxeDialog(owner, title, message, yes, no, null, false).ShowDialog() == true;

    public static string? Prompt(WpfWindow? owner, string title, string message, string? defaultText)
    {
        var dialog = new AxeDialog(owner, title, message, "OK", "Cancel", defaultText, true);
        return dialog.ShowDialog() == true ? dialog.InputText : null;
    }
}
