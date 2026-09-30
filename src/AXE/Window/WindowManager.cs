using System.Runtime.InteropServices;
using System.Windows.Interop;
using AXE.Native;
using AXE.Services;
using WpfWindow = System.Windows.Window;

namespace AXE.Window;

/// <summary>
/// Native window plumbing for the main AXE window:
/// <list type="bullet">
/// <item>Opacity — a layered-window alpha (<c>SetLayeredWindowAttributes</c>). It applies to the
/// whole window including the WebView2 child surface, and is purely a local visual effect.</item>
/// <item>Maximize bounds — keeps a borderless maximized window inside the monitor work area.</item>
/// <item>Global show/hide hotkey (<c>RegisterHotKey</c>, no keyboard hooks).</item>
/// <item>Display/DPI change notifications so capture exclusion can be re-verified.</item>
/// </list>
/// Opacity, topmost and capture exclusion are deliberately separate: nothing in here touches
/// display affinity, and <see cref="CaptureExclusionService"/> never touches opacity.
/// </summary>
public sealed class WindowManager : IDisposable
{
    private const int HotkeyId = 0xA7E;

    private readonly WpfWindow _window;
    private HwndSource? _source;
    private double _opacity = 1.0;
    private bool _wantLayered;
    private bool _hotkeyRegistered;
    private NativeMethods.SubclassProc? _subclassProc; // keep delegate alive while subclassed

    public WindowManager(WpfWindow window)
    {
        _window = window;
    }

    public IntPtr Handle { get; private set; }

    /// <summary>When true, maximize covers the whole monitor (page fullscreen, e.g. video).</summary>
    public bool CoverWholeMonitor { get; set; }

    public double Opacity => _opacity;

    public event EventHandler? DisplayChanged;

    /// <summary>
    /// Raised synchronously after the extended window style changed. Windows resets display
    /// affinity when WS_EX_LAYERED is removed, so capture exclusion must be re-applied here.
    /// </summary>
    public event EventHandler? ExtendedStyleChanged;
    public event EventHandler? HotkeyPressed;

    /// <summary>Call from SourceInitialized (the HWND exists but the window is not yet shown).</summary>
    public void Attach()
    {
        Handle = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(Handle);
        _source?.AddHook(WndProc);

        // Native subclass runs *around* WPF's window procedure, so it can correct the style
        // after WPF has processed WM_STYLECHANGING (see KeepLayeredStyle).
        _subclassProc = SubclassWndProc;
        if (!NativeMethods.SetWindowSubclass(Handle, _subclassProc, UIntPtr.Zero, IntPtr.Zero))
        {
            Log.Warn("Window subclass failed; opacity may reset after window style changes.");
        }

        var corner = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    /// <summary>Sets the local visual opacity (0.10–1.00). Does not affect capture exclusion.</summary>
    public void SetOpacity(double opacity)
    {
        _opacity = Models.AppSettings.ClampOpacity(opacity);

        // Fully opaque: drop the layered style for best video/game rendering performance.
        _wantLayered = _opacity < 0.999;
        if (Handle == IntPtr.Zero)
        {
            return;
        }

        var exStyle = NativeMethods.GetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE).ToInt64();
        var desired = _wantLayered ? exStyle | NativeMethods.WS_EX_LAYERED : exStyle & ~NativeMethods.WS_EX_LAYERED;
        if (desired != exStyle)
        {
            NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE, new IntPtr(desired));
        }

        if (_wantLayered)
        {
            var alpha = (byte)Math.Round(_opacity * 255);
            if (!NativeMethods.SetLayeredWindowAttributes(Handle, 0, alpha, NativeMethods.LWA_ALPHA))
            {
                Log.Warn($"SetLayeredWindowAttributes failed, Win32 error {Marshal.GetLastWin32Error()}.");
            }
        }
    }

    /// <summary>Applies the same local opacity to an auxiliary AXE window (dialogs), with a readability floor.</summary>
    public static void ApplyOpacityTo(IntPtr hwnd, double opacity)
    {
        var value = Math.Max(0.6, Models.AppSettings.ClampOpacity(opacity));
        if (value >= 0.999)
        {
            return;
        }

        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle | NativeMethods.WS_EX_LAYERED));
        NativeMethods.SetLayeredWindowAttributes(hwnd, 0, (byte)Math.Round(value * 255), NativeMethods.LWA_ALPHA);
    }

    public void RegisterHotkey(string? shortcut)
    {
        if (Handle == IntPtr.Zero || !HotkeyParser.TryParse(shortcut, out var hotkey))
        {
            if (!string.IsNullOrWhiteSpace(shortcut))
            {
                Log.Warn("Toggle hotkey setting is invalid; hotkey disabled.");
            }

            return;
        }

        _hotkeyRegistered = NativeMethods.RegisterHotKey(Handle, HotkeyId, hotkey.Modifiers, hotkey.VirtualKey);
        if (_hotkeyRegistered)
        {
            Log.Info("Toggle hotkey registered.");
        }
        else
        {
            Log.Warn($"Toggle hotkey unavailable (in use by another app?), Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_GETMINMAXINFO:
                AdjustMaximizedBounds(hwnd, lParam);
                break;
            case NativeMethods.WM_DISPLAYCHANGE:
            case NativeMethods.WM_DPICHANGED:
            case NativeMethods.WM_SETTINGCHANGE:
                DisplayChanged?.Invoke(this, EventArgs.Empty);
                break;
            case NativeMethods.WM_HOTKEY when wParam.ToInt32() == HotkeyId:
                HotkeyPressed?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private IntPtr SubclassWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr refData)
    {
        if (msg == NativeMethods.WM_STYLECHANGING && wParam.ToInt64() == NativeMethods.GWL_EXSTYLE)
        {
            var result = NativeMethods.DefSubclassProc(hwnd, msg, wParam, lParam); // WPF runs first
            KeepLayeredStyle(lParam);
            return result;
        }

        if (msg == NativeMethods.WM_STYLECHANGED && wParam.ToInt64() == NativeMethods.GWL_EXSTYLE)
        {
            var result = NativeMethods.DefSubclassProc(hwnd, msg, wParam, lParam);
            ExtendedStyleChanged?.Invoke(this, EventArgs.Empty);
            return result;
        }

        if (msg == NativeMethods.WM_NCDESTROY && _subclassProc is not null)
        {
            NativeMethods.RemoveWindowSubclass(hwnd, _subclassProc, UIntPtr.Zero);
        }

        return NativeMethods.DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// WPF forces WS_EX_LAYERED to match AllowsTransparency on every WM_STYLECHANGING, which
    /// would silently reset AXE to 100% opacity. Keep the layered bit in the state AXE wants.
    /// </summary>
    private void KeepLayeredStyle(IntPtr lParam)
    {
        var style = Marshal.PtrToStructure<NativeMethods.STYLESTRUCT>(lParam);
        var hasLayered = (style.styleNew & (uint)NativeMethods.WS_EX_LAYERED) != 0;
        if (hasLayered == _wantLayered)
        {
            return;
        }

        style.styleNew = _wantLayered
            ? style.styleNew | (uint)NativeMethods.WS_EX_LAYERED
            : style.styleNew & ~(uint)NativeMethods.WS_EX_LAYERED;
        Marshal.StructureToPtr(style, lParam, false);
    }

    /// <summary>A borderless window would otherwise maximize over the taskbar and past the screen edge.</summary>
    private void AdjustMaximizedBounds(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var area = CoverWholeMonitor ? info.rcMonitor : info.rcWork;
        var mmi = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
        mmi.ptMaxPosition.X = area.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = area.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = area.Right - area.Left;
        mmi.ptMaxSize.Y = area.Bottom - area.Top;
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    public void Dispose()
    {
        if (_hotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(Handle, HotkeyId);
            _hotkeyRegistered = false;
        }

        _source?.RemoveHook(WndProc);
    }
}
