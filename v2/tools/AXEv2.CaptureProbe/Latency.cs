using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AxeV2.CaptureProbe;

/// <summary>
/// End-to-end click latency: real input (SendInput) → first changed pixel on screen.
///
/// The browser shows a page whose background flips colour on mousedown and again on click
/// (mouseup). The probe clicks the page centre and polls that screen pixel with GDI, so the
/// number includes everything the user experiences: input routing, the browser, rendering,
/// window composition (layered opacity) and DWM. The target must be capturable, so AXE runs
/// with its diagnostic --diag-allow-capture switch for this measurement only.
///
/// Usage: AXEv2.CaptureProbe latency &lt;label&gt; &lt;exe&gt; [args...]   ({page} is replaced by the test page URL)
/// </summary>
internal static class Latency
{
    public const string Page =
        "data:text/html,<html><body style='margin:0;background:%23000'><script>" +
        "var n=0;addEventListener('mousedown',function(){document.body.style.background='%23fff'});" +
        "addEventListener('click',function(){document.body.style.background=(++n%252)?'%2300f':'%23f00'});" +
        "</script></body></html>";

    private static readonly bool Inactive = Environment.GetEnvironmentVariable("LATENCY_INACTIVE") == "1";

    public static int Run(string label, string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a.Replace("{page}", Page));
        }

        var launchedAt = DateTime.Now.AddSeconds(-1);
        Process.Start(psi);
        var name = Path.GetFileNameWithoutExtension(exe);
        try
        {
            var hwnd = WaitForWindow(name, TimeSpan.FromSeconds(30));
            Thread.Sleep(4000); // let the page load and settle
            GetWindowRect(hwnd, out var r);
            // Click inside the page area: lower-middle of the window, below any browser chrome.
            var x = (r.Left + r.Right) / 2;
            var y = r.Top + (int)((r.Bottom - r.Top) * 0.7);
            SetForegroundWindow(hwnd);
            SetCursorPos(x, y);
            Thread.Sleep(500);

            var down = new List<double>();
            var up = new List<double>();
            for (var i = 0; i < 25; i++)
            {
                if (Inactive)
                {
                    // The real overlay scenario: the user is working in another app and clicks AXE.
                    SetForegroundWindow(GetShellWindow());
                    Thread.Sleep(250);
                }

                var before = Pixel(x, y);
                var sw = Stopwatch.StartNew();
                Send(0x0002); // left down
                var dt = WaitForChange(x, y, before, sw);
                if (dt is { } d1)
                {
                    down.Add(d1);
                }

                Thread.Sleep(60);
                before = Pixel(x, y);
                sw.Restart();
                Send(0x0004); // left up → click
                var ut = WaitForChange(x, y, before, sw);
                if (ut is { } d2)
                {
                    up.Add(d2);
                }

                Thread.Sleep(250);
            }

            Console.WriteLine($"{label,-34} mousedown→pixel {Stats(down)} | click(mouseup)→pixel {Stats(up)}");
            return 0;
        }
        finally
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                // Only processes this probe started; never the user's own browser windows.
                try
                {
                    if (p.StartTime >= launchedAt)
                    {
                        p.Kill(true);
                    }
                }
                catch
                {
                    // already gone
                }
            }
        }
    }

    private static string Stats(List<double> values)
    {
        if (values.Count == 0)
        {
            return "no response";
        }

        values.Sort();
        double P(double q) => values[Math.Min(values.Count - 1, (int)(q * values.Count))];
        return $"n={values.Count,2} median {P(0.5),6:F1} ms  p90 {P(0.9),6:F1} ms  max {values[^1],6:F1} ms";
    }

    private static double? WaitForChange(int x, int y, uint before, Stopwatch sw)
    {
        while (sw.ElapsedMilliseconds < 3000)
        {
            var now = Pixel(x, y);
            if (Differs(before, now))
            {
                return sw.Elapsed.TotalMilliseconds;
            }
        }

        return null;
    }

    private static bool Differs(uint a, uint b)
    {
        int D(int shift) => Math.Abs((int)((a >> shift) & 0xFF) - (int)((b >> shift) & 0xFF));
        return D(0) > 20 || D(8) > 20 || D(16) > 20;
    }

    private static IntPtr _screen, _mem, _bmp;

    private static uint Pixel(int x, int y)
    {
        if (_screen == IntPtr.Zero)
        {
            _screen = GetDC(IntPtr.Zero);
            _mem = CreateCompatibleDC(_screen);
            _bmp = CreateCompatibleBitmap(_screen, 1, 1);
            SelectObject(_mem, _bmp);
        }

        BitBlt(_mem, 0, 0, 1, 1, _screen, x, y, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT
        return GetPixel(_mem, 0, 0);
    }

    private static void Send(uint flags)
    {
        var input = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flags } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static IntPtr WaitForWindow(string processName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var pids = Process.GetProcessesByName(processName).Select(p => (uint)p.Id).ToHashSet();
            IntPtr best = IntPtr.Zero;
            var bestArea = 0;
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var pid);
                if (pids.Contains(pid) && IsWindowVisible(h) && GetWindowTextLength(h) > 0)
                {
                    GetWindowRect(h, out var rr);
                    var area = (rr.Right - rr.Left) * (rr.Bottom - rr.Top);
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = h;
                    }
                }

                return true;
            }, IntPtr.Zero);
            if (best != IntPtr.Zero && bestArea > 200 * 200)
            {
                return best;
            }

            Thread.Sleep(300);
        }

        throw new TimeoutException($"No window for {processName}");
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public MOUSEINPUT mi; }
    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
}
