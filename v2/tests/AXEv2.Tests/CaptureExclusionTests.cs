using System.Runtime.InteropServices;
using System.Windows.Interop;
using AxeV2.Native;
using AxeV2.Window;

namespace AxeV2.Tests;

/// <summary>
/// Exercises the real Windows API on a real top-level window: exclusion must be applied,
/// verified by read-back, and must survive opacity (layered alpha) changes in both directions.
/// Pixel-level verification against actual screen captures is done by tools/AxeV2.CaptureProbe.
/// </summary>
public class CaptureExclusionTests
{
    [Fact]
    public void Exclusion_is_applied_and_independent_of_opacity() => RunSta(() =>
    {
        // Plain Win32 popup (never shown) so the test isolates the Windows API behaviour.
        var hwnd = CreateWindowEx(0, "Static", "AXE test window", 0x80000000, 0, 0, 200, 150,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, hwnd);
        using var cleanup = new WindowCleanup(hwnd);

        Assert.Equal(CaptureProtectionState.Excluded, CaptureExclusionService.ApplyTo(hwnd));
        Assert.Equal(NativeMethods.WDA_EXCLUDEFROMCAPTURE, CaptureExclusionService.ReadAffinity(hwnd));

        foreach (var alpha in new byte[] { 26, 64, 128, 191, 255 }) // 10%, 25%, 50%, 75%, 100%
        {
            var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex | NativeMethods.WS_EX_LAYERED));
            Assert.True(NativeMethods.SetLayeredWindowAttributes(hwnd, 0, alpha, NativeMethods.LWA_ALPHA));
            Assert.Equal(NativeMethods.WDA_EXCLUDEFROMCAPTURE, CaptureExclusionService.ReadAffinity(hwnd));
        }

        // Windows resets display affinity when WS_EX_LAYERED is removed (AXE does this at 100%
        // opacity). This documents that platform behaviour; AXE re-applies exclusion on
        // WM_STYLECHANGED, and re-applying must restore it.
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style & ~NativeMethods.WS_EX_LAYERED));
        Assert.Equal(NativeMethods.WDA_NONE, CaptureExclusionService.ReadAffinity(hwnd));
        Assert.Equal(CaptureProtectionState.Excluded, CaptureExclusionService.ApplyTo(hwnd));
        Assert.Equal(NativeMethods.WDA_EXCLUDEFROMCAPTURE, CaptureExclusionService.ReadAffinity(hwnd));
    });

    [Fact]
    public void Invalid_window_reports_failure_not_success()
    {
        Assert.Equal(CaptureProtectionState.Failed, CaptureExclusionService.ApplyTo(new IntPtr(0x1234)));
    }

    [Fact]
    public void Diagnostic_mode_reports_disabled_and_clears_affinity() => RunSta(() =>
    {
        using var source = new HwndSource(new HwndSourceParameters("AXE diag") { WindowStyle = unchecked((int)0x80000000) });
        NativeMethods.SetWindowDisplayAffinity(source.Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);

        using var service = new CaptureExclusionService(disabledForDiagnostics: true);
        service.AttachMainWindow(source.Handle);

        Assert.Equal(CaptureProtectionState.Disabled, service.State);
        Assert.False(service.IsExcluded);
        Assert.Equal(NativeMethods.WDA_NONE, CaptureExclusionService.ReadAffinity(source.Handle));
    });

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    private sealed class WindowCleanup(IntPtr hwnd) : IDisposable
    {
        public void Dispose() => DestroyWindow(hwnd);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfoThrow(failure);
        }
    }

    private static void ExceptionDispatchInfoThrow(Exception ex) =>
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
}
