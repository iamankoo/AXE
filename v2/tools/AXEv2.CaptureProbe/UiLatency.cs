using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace AxeV2.CaptureProbe;

/// <summary>
/// Click latency of AXE's own (WPF) controls: real SendInput click on a control found by
/// UI Automation id → first visual change inside that control's rectangle (GDI), measured
/// separately for mousedown (pressed/focus feedback) and mouseup (the click's action).
/// Needs a capturable window, so AXE runs with --diag-allow-capture.
///
/// Usage: AXEv2.CaptureProbe uilatency &lt;label&gt; &lt;exe&gt; &lt;id,id,...&gt; [args...]
/// </summary>
internal static class UiLatency
{
    private const int Iterations = 12;

    public static int Run(string label, string exe, string ids, string[] args)
    {
        var launchedAt = DateTime.Now.AddSeconds(-1);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        Process.Start(psi);
        var name = Path.GetFileNameWithoutExtension(exe);
        try
        {
            var hwnd = WaitForWindow(name);
            Thread.Sleep(5000);
            var root = AutomationElement.FromHandle(hwnd);
            foreach (var id in ids.Split(','))
            {
                var element = root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, id));
                if (element is null)
                {
                    Console.WriteLine($"{label,-22} {id,-16} not found");
                    continue;
                }

                var b = element.Current.BoundingRectangle;
                var rect = (X: (int)b.X, Y: (int)b.Y, W: Math.Max(4, (int)b.Width), H: Math.Max(4, (int)b.Height));
                var cx = rect.X + rect.W / 2;
                var cy = rect.Y + rect.H / 2;
                var down = new List<double>();
                var up = new List<double>();
                for (var i = 0; i < Iterations; i++)
                {
                    SetForegroundWindow(Environment.GetEnvironmentVariable("LATENCY_INACTIVE") == "1" ? GetShellWindow() : hwnd);
                    SetCursorPos(cx, cy);
                    Thread.Sleep(350); // let hover feedback settle so only the click is measured

                    var before = Grab(rect);
                    var sw = Stopwatch.StartNew();
                    Send(0x0002);
                    if (WaitForChange(rect, before, sw, 600) is { } d)
                    {
                        down.Add(d);
                    }

                    Thread.Sleep(80);
                    before = Grab(rect);
                    sw.Restart();
                    Send(0x0004);
                    if (WaitForChange(rect, before, sw, 1500) is { } u)
                    {
                        up.Add(u);
                    }

                    Thread.Sleep(400);
                    Key(0x1B); // Esc: close a dialog/menu or leave the address bar before the next round
                    Thread.Sleep(300);
                }

                Console.WriteLine($"{label,-22} {id,-16} mousedown→visual {Stats(down)} | mouseup→visual {Stats(up)}");
            }

            return 0;
        }
        finally
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
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
            return "n= 0 (no visual change)";
        }

        values.Sort();
        double P(double q) => values[Math.Min(values.Count - 1, (int)(q * values.Count))];
        return $"n={values.Count,2} med {P(0.5),6:F1} p90 {P(0.9),6:F1} max {values[^1],6:F1} ms";
    }

    private static double? WaitForChange((int X, int Y, int W, int H) r, byte[] before, Stopwatch sw, int timeoutMs)
    {
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var now = Grab(r);
            var changed = 0;
            for (var i = 0; i < now.Length; i += 4)
            {
                if (Math.Abs(now[i] - before[i]) + Math.Abs(now[i + 1] - before[i + 1]) + Math.Abs(now[i + 2] - before[i + 2]) > 24
                    && ++changed >= 4)
                {
                    return sw.Elapsed.TotalMilliseconds;
                }
            }
        }

        return null;
    }

    private static byte[] Grab((int X, int Y, int W, int H) r)
    {
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, r.W, r.H);
        var old = SelectObject(mem, bmp);
        BitBlt(mem, 0, 0, r.W, r.H, screen, r.X, r.Y, 0x00CC0020 | 0x40000000);
        SelectObject(mem, old);
        var info = new BITMAPINFOHEADER { biSize = 40, biWidth = r.W, biHeight = -r.H, biPlanes = 1, biBitCount = 32 };
        var pixels = new byte[r.W * r.H * 4];
        GetDIBits(mem, bmp, 0, (uint)r.H, pixels, ref info, 0);
        DeleteObject(bmp);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
        return pixels;
    }

    private static void Send(uint flags)
    {
        var input = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flags } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void Key(ushort vk)
    {
        var inputs = new[]
        {
            new KINPUT { type = 1, ki = new KEYBDINPUT { wVk = vk } },
            new KINPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = 2 } },
        };
        SendInput(2, inputs, Marshal.SizeOf<KINPUT>());
    }

    private static IntPtr WaitForWindow(string processName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
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
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; public long pad; }
    [StructLayout(LayoutKind.Sequential)] private struct KINPUT { public uint type; public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, KINPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
}
