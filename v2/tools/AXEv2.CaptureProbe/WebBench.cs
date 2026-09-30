using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace AxeV2.CaptureProbe;

/// <summary>
/// In-page responsiveness benchmark that works while capture exclusion is ON (pixels of an
/// excluded window cannot be screen-captured, so the page measures itself and reports to a
/// local HTTP server run by this probe; nothing leaves the machine).
///
/// Each iteration the probe really clicks a full-page link (SendInput) and the pages report:
/// <list type="bullet">
/// <item>input→frame: OS input timestamp of mousedown → the frame after the handler ran.</item>
/// <item>click→navigation start: link click → the next document's navigation start.</item>
/// <item>click→first paint: link click → first contentful paint of the next page.</item>
/// </list>
/// Usage: AXEv2.CaptureProbe webbench &lt;label&gt; &lt;exe&gt; [args...]   ({url} = start page)
/// </summary>
internal static class WebBench
{
    private const int Iterations = 20;
    private static readonly BlockingCollection<string> Reports = new();

    private const string PageA = """
        <!doctype html><html><head><title>bench a</title><style>
        html,body{margin:0;height:100%;background:#000}
        a{display:block;height:100vh;background:#000;color:#fff;font:20px sans-serif;text-decoration:none}
        </style></head><body><a id="go" href="/b">click</a><script>
        let inputToFrame = -1;
        addEventListener('mousedown', e => {
          sessionStorage.t0 = performance.timeOrigin + performance.now();
          document.body.style.background = '#fff';
          const ts = e.timeStamp;
          requestAnimationFrame(() => requestAnimationFrame(() => {
            inputToFrame = performance.now() - ts;
            fetch('/report?k=input&v=' + inputToFrame.toFixed(1), {keepalive:true});
          }));
        }, true);
        addEventListener('click', () => { sessionStorage.t1 = performance.timeOrigin + performance.now(); }, true);
        fetch('/report?k=ready&v=0');
        </script></body></html>
        """;

    private const string PageB = """
        <!doctype html><html><head><title>bench b</title></head>
        <body style="margin:0;background:#00f;color:#fff;font:40px sans-serif">loaded<script>
        const t1 = +sessionStorage.t1;
        const nav = performance.timeOrigin - t1;
        new PerformanceObserver(list => {
          for (const e of list.getEntries()) {
            if (e.name === 'first-contentful-paint') {
              const fcp = performance.timeOrigin + e.startTime - t1;
              fetch('/report?k=nav&v=' + nav.toFixed(1) + '&fcp=' + fcp.toFixed(1)).then(() => location.replace('/a'));
            }
          }
        }).observe({type: 'paint', buffered: true});
        </script></body></html>
        """;

    public static int Run(string label, string exe, string[] args)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(() => Serve(listener));
        var url = $"http://127.0.0.1:{port}/a";

        var launchedAt = DateTime.Now.AddSeconds(-1);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a.Replace("{url}", url));
        }

        Process.Start(psi);
        var name = Path.GetFileNameWithoutExtension(exe);
        var input = new List<double>();
        var nav = new List<double>();
        var fcp = new List<double>();
        try
        {
            if (!WaitFor("ready", TimeSpan.FromSeconds(30)))
            {
                Console.WriteLine($"{label}: page never loaded");
                return 1;
            }

            var hwnd = WaitForWindow(name);
            Thread.Sleep(1500);
            GetWindowRect(hwnd, out var r);
            var x = (r.Left + r.Right) / 2;
            var y = r.Top + (int)((r.Bottom - r.Top) * 0.7);
            SetCursorPos(x, y);

            for (var i = 0; i < Iterations; i++)
            {
                if (Environment.GetEnvironmentVariable("LATENCY_INACTIVE") == "1")
                {
                    SetForegroundWindow(GetShellWindow());
                    Thread.Sleep(250);
                }
                else
                {
                    SetForegroundWindow(hwnd);
                    Thread.Sleep(100);
                }

                Drain();
                Send(0x0002);
                Thread.Sleep(40);
                Send(0x0004);

                var deadline = DateTime.UtcNow.AddSeconds(10);
                bool gotNav = false, gotReady = false;
                while (DateTime.UtcNow < deadline && !(gotNav && gotReady))
                {
                    if (!Reports.TryTake(out var report, 500))
                    {
                        continue;
                    }

                    var q = ParseQuery(report);
                    switch (q.GetValueOrDefault("k"))
                    {
                        case "input":
                            input.Add(double.Parse(q["v"], CultureInfo.InvariantCulture));
                            break;
                        case "nav":
                            nav.Add(double.Parse(q["v"], CultureInfo.InvariantCulture));
                            fcp.Add(double.Parse(q["fcp"], CultureInfo.InvariantCulture));
                            gotNav = true;
                            break;
                        case "ready":
                            gotReady = gotNav;
                            break;
                    }
                }

                Thread.Sleep(300);
            }

            Console.WriteLine($"{label,-40} input→frame {Stats(input)} | click→nav start {Stats(nav)} | click→first paint {Stats(fcp)}");
            return 0;
        }
        finally
        {
            listener.Stop();
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

    private static void Drain()
    {
        while (Reports.TryTake(out _))
        {
        }
    }

    private static bool WaitFor(string kind, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Reports.TryTake(out var report, 500) && ParseQuery(report).GetValueOrDefault("k") == kind)
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, string> ParseQuery(string pathAndQuery)
    {
        var result = new Dictionary<string, string>();
        var q = pathAndQuery.IndexOf('?');
        if (q < 0)
        {
            return result;
        }

        foreach (var pair in pathAndQuery[(q + 1)..].Split('&'))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2)
            {
                result[kv[0]] = Uri.UnescapeDataString(kv[1]);
            }
        }

        return result;
    }

    private static string Stats(List<double> values)
    {
        if (values.Count == 0)
        {
            return "n=0";
        }

        values.Sort();
        double P(double q) => values[Math.Min(values.Count - 1, (int)(q * values.Count))];
        return $"n={values.Count,2} med {P(0.5),6:F1} p90 {P(0.9),6:F1} ms";
    }

    private static async Task Serve(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    var requestLine = await reader.ReadLineAsync() ?? string.Empty;
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
                    {
                    }

                    var path = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";
                    string body;
                    var type = "text/html; charset=utf-8";
                    if (path.StartsWith("/report", StringComparison.Ordinal))
                    {
                        Reports.Add(path);
                        body = "ok";
                        type = "text/plain";
                    }
                    else
                    {
                        body = path.StartsWith("/b", StringComparison.Ordinal) ? PageB : PageA;
                    }

                    var bytes = Encoding.UTF8.GetBytes(body);
                    var header = $"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                    await stream.WriteAsync(bytes);
                }
            });
        }
    }

    private static void Send(uint flags)
    {
        var input = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flags } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
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
    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
}
