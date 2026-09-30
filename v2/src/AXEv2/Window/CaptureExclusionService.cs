using System.Runtime.InteropServices;
using System.Windows.Threading;
using AxeV2.Native;
using AxeV2.Services;

namespace AxeV2.Window;

/// <summary>What AXE can honestly say about its capture protection.</summary>
public enum CaptureProtectionState
{
    /// <summary>Not applied yet (no window handle).</summary>
    Unknown,

    /// <summary>WDA_EXCLUDEFROMCAPTURE is active: the window is removed from supported captures.</summary>
    Excluded,

    /// <summary>Only WDA_MONITOR could be applied: the window appears as a black area in supported captures.</summary>
    MonitorOnly,

    /// <summary>The API failed; the window is NOT protected.</summary>
    Failed,

    /// <summary>Protection deliberately disabled with the diagnostic switch.</summary>
    Disabled,
}

/// <summary>
/// Applies and continuously verifies Windows display affinity so AXE is excluded from
/// capture APIs that honour <c>SetWindowDisplayAffinity</c> (GDI/BitBlt, DXGI Desktop
/// Duplication, Windows.Graphics.Capture and the apps built on them).
/// <para>
/// Display affinity is independent from window opacity: it is a DWM composition flag,
/// while opacity is a layered-window alpha. Changing one never changes the other, and this
/// service re-verifies after every state change to be sure.
/// </para>
/// </summary>
public sealed class CaptureExclusionService : IDisposable
{
    private readonly bool _disabled;
    private readonly HashSet<IntPtr> _auxiliary = new();
    private readonly DispatcherTimer _verifyTimer;
    private IntPtr _mainHwnd;
    private IntPtr _winEventHook;
    private NativeMethods.WinEventDelegate? _winEventProc; // keep delegate alive for the hook

    public CaptureExclusionService(bool disabledForDiagnostics)
    {
        _disabled = disabledForDiagnostics;
        _verifyTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _verifyTimer.Tick += (_, _) => VerifyAll();
    }

    public CaptureProtectionState State { get; private set; } = CaptureProtectionState.Unknown;

    /// <summary>True only when the window is actually removed from supported captures.</summary>
    public bool IsExcluded => State == CaptureProtectionState.Excluded;

    public event EventHandler<CaptureProtectionState>? StateChanged;

    /// <summary>Attach to the main AXE window once its native handle exists.</summary>
    public void AttachMainWindow(IntPtr hwnd)
    {
        _mainHwnd = hwnd;
        EnsureMain("attach");
        if (!_disabled)
        {
            InstallProcessWindowHook();
            _verifyTimer.Start();
        }
    }

    /// <summary>Re-applies protection to the main window (window state / display changes).</summary>
    public void EnsureMain(string reason)
    {
        if (_mainHwnd == IntPtr.Zero)
        {
            return;
        }

        var newState = _disabled ? ApplyDisabled(_mainHwnd) : ApplyTo(_mainHwnd);
        SetState(newState, reason);
    }

    /// <summary>
    /// Protects an auxiliary top-level window of this process (dialog, context menu, popup).
    /// Must be called before the window is first shown whenever possible.
    /// </summary>
    public void ProtectAuxiliary(IntPtr hwnd) => ProtectAuxiliary(hwnd, logFailure: true);

    private void ProtectAuxiliary(IntPtr hwnd, bool logFailure)
    {
        if (_disabled || hwnd == IntPtr.Zero || hwnd == _mainHwnd)
        {
            return;
        }

        if (_auxiliary.Add(hwnd) || !HasExpectedAffinity(hwnd))
        {
            var result = ApplyTo(hwnd, logFailure);
            if (result != CaptureProtectionState.Excluded && logFailure)
            {
                Log.Warn($"Capture exclusion for auxiliary window failed ({result}).");
            }
        }
    }

    /// <summary>Reads the current affinity of a window. Exposed for diagnostics and tests.</summary>
    public static uint? ReadAffinity(IntPtr hwnd)
    {
        return NativeMethods.GetWindowDisplayAffinity(hwnd, out var affinity) ? affinity : null;
    }

    /// <summary>
    /// Applies WDA_EXCLUDEFROMCAPTURE, falling back to WDA_MONITOR on systems older than
    /// Windows 10 2004, and verifies the result by reading the affinity back.
    /// </summary>
    public static CaptureProtectionState ApplyTo(IntPtr hwnd) => ApplyTo(hwnd, logFailure: true);

    private static CaptureProtectionState ApplyTo(IntPtr hwnd, bool logFailure)
    {
        if (NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE)
            && ReadAffinity(hwnd) == NativeMethods.WDA_EXCLUDEFROMCAPTURE)
        {
            return CaptureProtectionState.Excluded;
        }

        var error = Marshal.GetLastWin32Error();
        if (logFailure)
        {
            Log.Warn($"SetWindowDisplayAffinity(EXCLUDEFROMCAPTURE) failed, Win32 error {error}. Trying WDA_MONITOR.");
        }

        if (NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_MONITOR)
            && ReadAffinity(hwnd) == NativeMethods.WDA_MONITOR)
        {
            return CaptureProtectionState.MonitorOnly;
        }

        if (logFailure)
        {
            Log.Error($"SetWindowDisplayAffinity(MONITOR) failed, Win32 error {Marshal.GetLastWin32Error()}.");
        }

        return CaptureProtectionState.Failed;
    }

    private static CaptureProtectionState ApplyDisabled(IntPtr hwnd)
    {
        NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_NONE);
        return CaptureProtectionState.Disabled;
    }

    private static bool HasExpectedAffinity(IntPtr hwnd) =>
        ReadAffinity(hwnd) is NativeMethods.WDA_EXCLUDEFROMCAPTURE;

    private void VerifyAll()
    {
        if (_mainHwnd != IntPtr.Zero && NativeMethods.IsWindow(_mainHwnd))
        {
            var current = ReadAffinity(_mainHwnd);
            var expected = State switch
            {
                CaptureProtectionState.Excluded => NativeMethods.WDA_EXCLUDEFROMCAPTURE,
                CaptureProtectionState.MonitorOnly => NativeMethods.WDA_MONITOR,
                _ => (uint?)null,
            };

            // Re-apply if the affinity drifted or a previous attempt failed.
            if (expected is null || current != expected)
            {
                EnsureMain("verify");
            }
        }

        _auxiliary.RemoveWhere(h => !NativeMethods.IsWindow(h));
    }

    private void SetState(CaptureProtectionState state, string reason)
    {
        if (state == State)
        {
            return;
        }

        var previous = State;
        State = state;
        switch (state)
        {
            case CaptureProtectionState.Excluded:
                Log.Info($"Capture exclusion applied ({reason}).");
                break;
            case CaptureProtectionState.MonitorOnly:
                Log.Warn($"Capture exclusion unavailable; content protection (black) applied instead ({reason}).");
                break;
            case CaptureProtectionState.Failed:
                Log.Error($"Capture exclusion FAILED ({reason}). AXE is not protected.");
                break;
            case CaptureProtectionState.Disabled:
                Log.Warn("Capture exclusion disabled by diagnostic switch.");
                break;
        }

        if (previous != state)
        {
            StateChanged?.Invoke(this, state);
        }
    }

    /// <summary>
    /// Safety net: any new top-level window created by this process (WPF popups, menus,
    /// dialogs, IME windows) is protected as soon as Windows reports it. Windows that AXE
    /// creates itself are additionally protected before they are shown.
    /// </summary>
    private void InstallProcessWindowHook()
    {
        if (_winEventHook != IntPtr.Zero)
        {
            return;
        }

        _winEventProc = OnWinEvent;
        _winEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_CREATE,
            NativeMethods.EVENT_OBJECT_SHOW,
            IntPtr.Zero,
            _winEventProc,
            (uint)Environment.ProcessId,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);

        if (_winEventHook == IntPtr.Zero)
        {
            Log.Warn("Could not install window-creation hook for auxiliary windows.");
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != NativeMethods.OBJID_WINDOW || hwnd == IntPtr.Zero)
        {
            return;
        }

        if (NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) != hwnd)
        {
            return; // child window: covered by its top-level window's affinity
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == (uint)Environment.ProcessId)
        {
            // Some internal windows (e.g. hidden IME/owner windows) reject display affinity;
            // they are never visible, so failures here are not logged as errors.
            ProtectAuxiliary(hwnd, logFailure: false);
        }
    }

    public void Dispose()
    {
        _verifyTimer.Stop();
        if (_winEventHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
    }
}
