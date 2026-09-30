using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AxeV2.Browser;

/// <summary>
/// One browser tab: its own WebView2 control (and Chromium renderer), sharing AXE's single
/// browser environment/profile with the other tabs, like tabs in Chrome share cookies.
/// Every tab is a child of the protected AXE window, so it is covered by capture exclusion.
/// </summary>
public sealed class BrowserTab : INotifyPropertyChanged
{
    public const string DefaultTitle = "New tab";

    private static int _nextId;
    private string _title = DefaultTitle;
    private ImageSource? _favicon;
    private bool _isLoading;
    private bool _isActive;

    internal BrowserTab(WebView2 view)
    {
        View = view;
        Id = Interlocked.Increment(ref _nextId);
    }

    public int Id { get; }

    internal WebView2 View { get; }

    internal CoreWebView2? Core => View.CoreWebView2;

    /// <summary>Recent render-process crashes, used to stop reloading a page that keeps crashing.</summary>
    internal Queue<DateTime> RenderCrashes { get; } = new();

    public string Title
    {
        get => _title;
        internal set => Set(ref _title, string.IsNullOrWhiteSpace(value) ? DefaultTitle : value.Trim());
    }

    public ImageSource? Favicon
    {
        get => _favicon;
        internal set => Set(ref _favicon, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        internal set => Set(ref _isLoading, value);
    }

    public bool IsActive
    {
        get => _isActive;
        internal set => Set(ref _isActive, value);
    }

    public string Url => Core?.Source ?? string.Empty;

    public bool CanGoBack => Core?.CanGoBack == true;

    public bool CanGoForward => Core?.CanGoForward == true;

    public bool IsFullscreen => Core?.ContainsFullScreenElement == true;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
