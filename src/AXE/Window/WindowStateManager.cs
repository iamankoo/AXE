using System.Windows;
using System.Windows.Media;
using AXE.Native;
using AXE.Services;
using WpfWindow = System.Windows.Window;

namespace AXE.Window;

/// <summary>
/// Minimize / maximize / restore / page-fullscreen behaviour for a window without a taskbar button.
/// <para>
/// Because AXE has no taskbar button, "minimize" hides the window instead of leaving a
/// title-bar stub on the desktop. AXE keeps running (the browser session is untouched) and
/// comes back when the user launches AXE again from Start/Search or presses the hotkey.
/// </para>
/// </summary>
public sealed class WindowStateManager
{
    private readonly WpfWindow _window;
    private readonly WindowManager _windowManager;
    private readonly CaptureExclusionService _capture;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;
    private bool _changingState;

    public WindowStateManager(WpfWindow window, WindowManager windowManager, CaptureExclusionService capture)
    {
        _window = window;
        _windowManager = windowManager;
        _capture = capture;
        _window.StateChanged += OnStateChanged;
    }

    public bool IsHidden { get; private set; }

    public bool IsPageFullscreen { get; private set; }

    public event EventHandler? VisualStateChanged;

    public void Minimize()
    {
        if (IsHidden)
        {
            return;
        }

        IsHidden = true;
        _window.Hide();
        Log.Info("Window minimized (hidden).");
    }

    public void Restore()
    {
        if (IsHidden)
        {
            IsHidden = false;
            _window.Show();
            Log.Info("Window restored.");
        }

        // Stacking order is independent of capture exclusion and opacity; re-assert all three
        // explicitly so none is left in a stale state after the window was hidden.
        _window.Topmost = true;
        _capture.EnsureMain("restore");
        _windowManager.SetOpacity(_windowManager.Opacity);
        _window.Activate();
        NativeMethods.SetForegroundWindow(_windowManager.Handle);
        VisualStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleVisibility()
    {
        if (IsHidden || !_window.IsActive)
        {
            Restore();
        }
        else
        {
            Minimize();
        }
    }

    public void ToggleMaximize()
    {
        if (IsPageFullscreen)
        {
            return;
        }

        _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>Restores a maximized window under the cursor so it can be dragged, like a normal title bar.</summary>
    public void PrepareDragFromMaximized(Point cursorInWindow)
    {
        if (_window.WindowState != WindowState.Maximized || IsPageFullscreen)
        {
            return;
        }

        var ratioX = _window.ActualWidth > 0 ? cursorInWindow.X / _window.ActualWidth : 0.5;
        var screenPoint = _window.PointToScreen(cursorInWindow);
        var source = PresentationSource.FromVisual(_window);
        var dip = source?.CompositionTarget?.TransformFromDevice.Transform(screenPoint) ?? screenPoint;
        var restoreWidth = _window.RestoreBounds.Width > 0 ? _window.RestoreBounds.Width : _window.Width;

        _window.WindowState = WindowState.Normal;
        _window.Left = dip.X - restoreWidth * ratioX;
        _window.Top = dip.Y - cursorInWindow.Y;
    }

    /// <summary>Used when a web page requests fullscreen (e.g. a video player).</summary>
    public void SetPageFullscreen(bool fullscreen)
    {
        if (fullscreen == IsPageFullscreen)
        {
            return;
        }

        IsPageFullscreen = fullscreen;
        if (fullscreen)
        {
            _stateBeforeFullscreen = _window.WindowState;
            _windowManager.CoverWholeMonitor = true;
            if (_window.WindowState == WindowState.Maximized)
            {
                _window.WindowState = WindowState.Normal; // force WM_GETMINMAXINFO to re-run
            }

            _window.WindowState = WindowState.Maximized;
        }
        else
        {
            _windowManager.CoverWholeMonitor = false;
            _window.WindowState = WindowState.Normal;
            if (_stateBeforeFullscreen == WindowState.Maximized)
            {
                _window.WindowState = WindowState.Maximized;
            }
        }

        VisualStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_changingState)
        {
            return;
        }

        // Windows can still minimize us (e.g. Win+M / "show desktop"). Without a taskbar
        // button that would leave an iconic stub, so convert it into AXE's hide behaviour.
        if (_window.WindowState == WindowState.Minimized)
        {
            _changingState = true;
            _window.WindowState = WindowState.Normal;
            _changingState = false;
            Minimize();
            return;
        }

        Log.Info($"Window state changed: {_window.WindowState}.");
        _capture.EnsureMain("state change");
        VisualStateChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static Matrix DeviceToDip(Visual visual) =>
        PresentationSource.FromVisual(visual)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
}
