using System.IO;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Threading;

namespace AXE.CaptureProbe;

/// <summary>
/// Automated capture-exclusion test for AXE.
///
/// A solid green backdrop window is placed behind AXE, AXE is opened on a magenta test page,
/// and the screen is captured with GDI (BitBlt) and Windows.Graphics.Capture. Inside AXE's
/// window rectangle a protected AXE must be invisible, i.e. the capture must show the green
/// backdrop. Positive controls run AXE with exclusion disabled (--diag-allow-capture) to prove
/// the detector does see AXE when it is not protected.
///
/// Usage: AXE.CaptureProbe suite &lt;path-to-AXE.exe&gt; &lt;output-dir&gt;
///        AXE.CaptureProbe shot &lt;output.png&gt;           (full-screen GDI + WGC captures)
/// </summary>
internal static class Program
{
    private static readonly (byte R, byte G, byte B) Backdrop = (0, 160, 0);
    private const int Tolerance = 6;
    private const double PassThreshold = 0.97;   // protected: ≥97% of AXE's rect shows the backdrop
    private const double ControlThreshold = 0.10; // unprotected control: ≤10% shows the backdrop

    private static readonly string TestPage = "data:text/html," + Uri.EscapeDataString(
        "<html><body style=\"margin:0;background:#ff00ff;font:24px Segoe UI;color:white\">" +
        "<select id=s style=\"position:absolute;left:16px;top:16px;width:160px;height:32px;font-size:18px\">" +
        "<option>Alpha</option><option>Bravo</option><option>Charlie</option><option>Delta</option><option>Echo</option></select>" +
        "<input id=t style=\"position:absolute;left:16px;top:80px;width:300px;height:32px;font-size:18px\" placeholder=\"type here\">" +
        "<div style=\"position:absolute;left:16px;top:140px\">AXE CAPTURE TEST PAGE</div></body></html>");

    private static string _axeExe = "";
    private static string _outDir = "";
    private static readonly List<string> Report = new();
    private static int _failures;
    private static Dispatcher? _backdrop;

    [STAThread]
    private static int Main(string[] args)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2: physical pixels everywhere

        if (args.Length >= 1 && args[0] == "shot")
        {
            var output = args.Length > 1 ? args[1] : "shot.png";
            Capture.SavePng(Capture.Gdi(), Path.ChangeExtension(output, ".gdi.png"));
            Capture.SavePng(Capture.Wgc(), Path.ChangeExtension(output, ".wgc.png"));
            return 0;
        }

        if (args.Length >= 1 && args[0] == "inspect")
        {
            foreach (var axe in Process.GetProcessesByName("AXE"))
            {
                var h = FindMainWindow(axe.Id);
                var ex = GetWindowLongPtr(h, -20).ToInt64();
                GetLayeredWindowAttributes(h, out _, out var alpha, out var flags);
                GetWindowDisplayAffinity(h, out var aff);
                Console.WriteLine($"pid {axe.Id} hwnd 0x{h:X} exstyle=0x{ex:X} layered={(ex & 0x80000) != 0} topmost={(ex & 0x8) != 0} alpha={alpha} lwaFlags={flags} affinity=0x{aff:X2}");
            }

            return 0;
        }

        if (args.Length >= 3 && args[0] == "installer-gui")
        {
            // Drives the real installer wizard like a user would, screenshotting every page.
            var outDir = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(outDir);
            var psi = new ProcessStartInfo(Path.GetFullPath(args[1])) { UseShellExecute = false };
            foreach (var extra in args.Skip(3))
            {
                psi.ArgumentList.Add(extra);
            }

            var setup = Process.Start(psi)!;
            var priority = new[] { "Finish", "Install", "Next", "Install for me only" };
            var step = 0;
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (DateTime.UtcNow < deadline && !setup.HasExited)
            {
                Thread.Sleep(1500);
                var window = AutomationElement.RootElement.FindFirst(TreeScope.Children, new OrCondition(
                    new PropertyCondition(AutomationElement.NameProperty, "Setup - AXE"),
                    new PropertyCondition(AutomationElement.NameProperty, "Select Setup Install Mode")));
                if (window is null)
                {
                    continue;
                }

                AutomationElement? target = null;
                foreach (var name in priority)
                {
                    target = window.FindFirst(TreeScope.Descendants, new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                        new PropertyCondition(AutomationElement.IsEnabledProperty, true),
                        new PropertyCondition(AutomationElement.NameProperty, name)));
                    if (target is not null) break;
                }

                if (target is null)
                {
                    continue; // e.g. installing: no enabled navigation button yet
                }

                var r = window.Current.BoundingRectangle;
                step++;
                var label = target.Current.Name;
                Capture.SavePng(Capture.Gdi(), Path.Combine(outDir, $"{step:00}-{label.Replace(' ', '-')}.png"),
                    new RectI((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height));
                Console.WriteLine($"page {step}: [{window.Current.Name}] clicking '{label}'");
                ((InvokePattern)target.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            }

            Console.WriteLine($"wizard closed after {step} click(s); setup exit code {(setup.HasExited ? setup.ExitCode : -1)}");
            return 0;
        }

        if (args.Length >= 3 && args[0] == "external")
        {
            // Real OS / third-party capture tools: Win+PrintScreen, ffmpeg (DXGI Desktop Duplication
            // and GDI), and browser screen sharing (getDisplayMedia in Chrome and Edge).
            _axeExe = Path.GetFullPath(args[1]);
            _outDir = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(_outDir);
            var shareBackdrop = _backdrop = StartBackdrop(leftInsetDip: 560);
            try
            {
                Line($"# AXE external capture tools - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                foreach (var allow in new[] { true, false })
                {
                    var tag = allow ? "control-unprotected" : "protected";
                    Line($"## {tag}");
                    var launchArgs = new List<string> { TestPage, "--opacity=100" };
                    if (allow) launchArgs.Add("--diag-allow-capture");
                    var (proc, hwnd) = Launch(launchArgs);
                    Thread.Sleep(4000);
                    GetRect(hwnd, out var rect);
                    var tools = new List<(string Name, Func<string?> Shot)>
                    {
                        ("Win+PrintScreen", () => WinPrintScreen(tag)),
                        ("ffmpeg ddagrab (DXGI Desktop Duplication)", () => Ffmpeg(tag, "dda", "-f lavfi -i ddagrab=output_idx=0:framerate=10,hwdownload,format=bgra")),
                        ("ffmpeg gdigrab", () => Ffmpeg(tag, "gdigrab", "-f gdigrab -framerate 10 -i desktop")),
                        ("Chrome getDisplayMedia (screen share)", () => BrowserShare(tag, "chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe")),
                        ("Edge getDisplayMedia (screen share)", () => BrowserShare(tag, "edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")),
                    };
                    foreach (var (name, shot) in tools)
                    {
                        string? file = null;
                        try { file = shot(); } catch (Exception ex) { Line($"- {name}: tool error {ex.GetType().Name}: {ex.Message}"); }
                        if (file is null) { Line($"- {name}: NO CAPTURE PRODUCED (not evaluated)"); _failures++; continue; }
                        var frame = LoadPng(file);
                        // Map AXE's screen rect into the captured image (tools may capture at another size).
                        var sx = frame.Width / (double)GetSystemMetrics(0);
                        var sy = frame.Height / (double)GetSystemMetrics(1);
                        var inner = new RectI((int)((rect.X + 3) * sx), (int)((rect.Y + 3) * sy), (int)((rect.W - 6) * sx), (int)((rect.H - 6) * sy));
                        var (green, magenta, total) = Measure(frame, inner);
                        var gf = total == 0 ? 0 : (double)green / total;
                        var mf = total == 0 ? 0 : (double)magenta / total;
                        var ok = allow ? gf <= ControlThreshold && mf >= 0.5 : gf >= PassThreshold;
                        Line($"- {name}: {frame.Width}x{frame.Height}, AXE area shows backdrop {gf:P1}, AXE test page {mf:P1} -> " +
                             $"{(ok ? "PASS" : "FAIL")} ({(allow ? "control must SEE AXE" : "AXE must be ABSENT")}) [{Path.GetFileName(file)}]");
                        if (!ok) _failures++;
                    }

                    CloseViaButton(proc, hwnd);
                }
            }
            finally
            {
                shareBackdrop.Invoke(() => Dispatcher.CurrentDispatcher.InvokeShutdown());
                KillAxe();
            }

            Line(_failures == 0 ? "RESULT: ALL CHECKS PASSED" : $"RESULT: {_failures} CHECK(S) FAILED");
            File.WriteAllLines(Path.Combine(_outDir, "report.md"), Report, Encoding.UTF8);
            return _failures == 0 ? 0 : 1;
        }

        if (args.Length >= 2 && args[0] == "extras")
        {
            // Back/forward/refresh, global hotkey, and taskbar-button checks.
            _axeExe = Path.GetFullPath(args[1]);
            var (proc, hwnd) = Launch(new List<string> { "https://example.com" });
            Thread.Sleep(5000);
            var root = AutomationElement.FromHandle(hwnd);
            string Address() => ((ValuePattern)Find(root, "AddressBox").GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
            var box = Find(root, "AddressBox");
            box.SetFocus();
            ((ValuePattern)box.GetCurrentPattern(ValuePattern.Pattern)).SetValue("https://example.org");
            Key(0x0D);
            Thread.Sleep(4000);
            Console.WriteLine($"after navigate: {Address()}");
            Invoke(root, "BackButton"); Thread.Sleep(3000);
            var back = Address();
            Invoke(root, "ForwardButton"); Thread.Sleep(3000);
            var fwd = Address();
            Invoke(root, "ReloadButton"); Thread.Sleep(3000);
            var reload = Address();
            Console.WriteLine($"back -> {back} | forward -> {fwd} | refresh -> {reload}");
            Console.WriteLine($"BACK {(back.Contains("example.com") ? "PASS" : "FAIL")}, FORWARD {(fwd.Contains("example.org") ? "PASS" : "FAIL")}, REFRESH {(reload.Contains("example.org") && !proc.HasExited ? "PASS" : "FAIL")}");

            // Taskbar: an owned window without WS_EX_APPWINDOW gets no taskbar button.
            var owner = GetWindow(hwnd, 4 /*GW_OWNER*/);
            var ex = GetWindowLongPtr(hwnd, -20).ToInt64();
            Console.WriteLine($"owner=0x{owner:X} appwindow={(ex & 0x40000) != 0} toolwindow={(ex & 0x80) != 0} -> NO TASKBAR BUTTON {(owner != IntPtr.Zero && (ex & 0x40000) == 0 ? "PASS" : "FAIL")}");

            // Global hotkey Ctrl+Alt+X hides, then shows again.
            void Hotkey()
            {
                var inputs = new[] { KeyInput(0x11, 0, 0), KeyInput(0x12, 0, 0), KeyInput(0x58, 0, 0), KeyInput(0x58, 0, 2), KeyInput(0x12, 0, 2), KeyInput(0x11, 0, 2) };
                SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            }

            SetForegroundWindow(hwnd); Thread.Sleep(500);
            Hotkey(); Thread.Sleep(1000);
            var hidden = !IsWindowVisible(hwnd);
            Hotkey(); Thread.Sleep(1000);
            var shown = IsWindowVisible(hwnd);
            Console.WriteLine($"HOTKEY hide {(hidden ? "PASS" : "FAIL")}, show {(shown ? "PASS" : "FAIL")}");
            GetWindowDisplayAffinity(hwnd, out var aff);
            Console.WriteLine($"affinity after hotkey restore = 0x{aff:X2}");
            Invoke(AutomationElement.FromHandle(hwnd), "CloseButton");
            proc.WaitForExit(10000);
            return 0;
        }

        if (args.Length >= 3 && args[0] == "keys")
        {
            // Keyboard, shortcuts and clipboard inside the page (capture exclusion OFF for the screenshot).
            _axeExe = Path.GetFullPath(args[1]);
            _outDir = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(_outDir);
            var page = "data:text/html," + Uri.EscapeDataString(
                "<body style='font:20px Segoe UI;padding:20px'>First: <input id=a autofocus><br><br>Second: <input id=b><br><br>" +
                "<div id=log>keys: 0</div><script>let n=0;addEventListener('keydown',()=>{document.getElementById('log').textContent='keys: '+(++n)})</script></body>");
            var (proc, hwnd) = Launch(new List<string> { page, "--opacity=100", "--diag-allow-capture" });
            Thread.Sleep(4000);
            GetRect(hwnd, out var rect);
            var scale = GetDpiForWindow(hwnd) / 96.0;
            Click(rect.X + (int)(200 * scale), rect.Y + (int)((85 + 34) * scale)); // first input
            Thread.Sleep(300);
            TypeText("hello AXE 123");
            Chord(0x11, 0x41); // Ctrl+A
            Chord(0x11, 0x43); // Ctrl+C
            Key(0x09);         // Tab
            Chord(0x11, 0x56); // Ctrl+V
            Thread.Sleep(800);
            Capture.SavePng(Capture.Gdi(), Path.Combine(_outDir, "keys.png"), rect);
            Invoke(AutomationElement.FromHandle(hwnd), "CloseButton");
            proc.WaitForExit(10000);
            return 0;
        }

        if (args.Length >= 3 && args[0] == "smoke")
        {
            // Browser smoke test with capture exclusion OFF so screenshots show AXE's content.
            _axeExe = Path.GetFullPath(args[1]);
            _outDir = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(_outDir);
            var (proc, hwnd) = Launch(new List<string> { "--opacity=100", "--diag-allow-capture" });
            Thread.Sleep(6000);
            GetRect(hwnd, out var rect);
            Capture.SavePng(Capture.Gdi(), Path.Combine(_outDir, "1-start-page.png"), rect);

            var root = AutomationElement.FromHandle(hwnd);
            void Go(string text)
            {
                var box = Find(root, "AddressBox");
                box.SetFocus();
                ((ValuePattern)box.GetCurrentPattern(ValuePattern.Pattern)).SetValue(text);
                Thread.Sleep(150);
                Key(0x0D);
            }

            Go("weather in Delhi");
            Thread.Sleep(5000);
            Capture.SavePng(Capture.Gdi(), Path.Combine(_outDir, "2-search.png"), rect);

            foreach (var (name, url, wait, clickX, clickY) in new[]
            {
                ("3-video", "https://www.w3schools.com/html/mov_bbb.mp4", 5000, 0.5, 0.55),     // click = play
                ("4-game", "https://play2048.co", 6000, 681 / 960.0, 127 / 540.0),            // close tutorial
            })
            {
                Go(url);
                Thread.Sleep(wait);
                Click(rect.X + (int)(rect.W * clickX), rect.Y + (int)(rect.H * clickY));
                Thread.Sleep(2000);
                Capture.SavePng(Capture.Gdi(), Path.Combine(_outDir, $"{name}-a.png"), rect);
                Thread.Sleep(1500);
                Capture.SavePng(Capture.Gdi(), Path.Combine(_outDir, $"{name}-b.png"), rect);
            }

            // Keyboard input into the game (arrow keys)
            foreach (var k in new ushort[] { 0x25, 0x26, 0x27, 0x28, 0x25, 0x26 })
            {
                Key(k);
                Thread.Sleep(250);
            }

            Thread.Sleep(800);
            Capture.SavePng(Capture.Gdi(), Path.Combine(_outDir, "4-game-after-keys.png"), rect);
            Invoke(root, "BackButton");
            Thread.Sleep(2500);
            Capture.SavePng(Capture.Gdi(), Path.Combine(_outDir, "5-after-back.png"), rect);
            Invoke(root, "CloseButton");
            Console.WriteLine(proc.WaitForExit(10000) ? "closed" : "still running");
            return 0;
        }

        if (args.Length < 3 || args[0] != "suite")
        {
            Console.WriteLine("Usage: AXE.CaptureProbe suite <AXE.exe> <output-dir> | shot <file.png>");
            return 2;
        }

        _axeExe = Path.GetFullPath(args[1]);
        _outDir = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(_outDir);

        if (Process.GetProcessesByName("AXE").Length > 0)
        {
            Console.WriteLine("AXE is already running; close it before running the suite.");
            return 2;
        }

        var backdrop = _backdrop = StartBackdrop();
        try
        {
            Line($"# AXE capture-exclusion suite — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Line($"Windows {Environment.OSVersion.Version}, AXE: {_axeExe}");
            Line("");

            foreach (var percent in new[] { 10, 25, 50, 75, 100 })
            {
                RunStatic($"excluded-{percent}", percent, allowCapture: false);
            }

            RunStatic("control-unprotected-25", 25, allowCapture: true);
            RunStatic("control-unprotected-100", 100, allowCapture: true);
            RunPopups("control-unprotected-popups", allowCapture: true);
            RunPopups("popups", allowCapture: false);
            RunInteractive();
        }
        catch (Exception ex)
        {
            Fail($"Suite aborted: {ex}");
        }
        finally
        {
            backdrop.Invoke(() => System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown());
            KillAxe();
        }

        Line("");
        Line(_failures == 0 ? "RESULT: ALL CHECKS PASSED" : $"RESULT: {_failures} CHECK(S) FAILED");
        File.WriteAllLines(Path.Combine(_outDir, "report.md"), Report, Encoding.UTF8);
        return _failures == 0 ? 0 : 1;
    }

    // ================================================================== scenarios

    private static void RunStatic(string name, int percent, bool allowCapture)
    {
        Line($"## {name}");
        var args = new List<string> { TestPage, $"--opacity={percent}" };
        if (allowCapture)
        {
            args.Add("--diag-allow-capture");
        }

        var (process, hwnd) = Launch(args);
        Thread.Sleep(3500); // page load + first composition
        Check(name, hwnd, expectExcluded: !allowCapture, percent == 100 && allowCapture);
        CloseViaButton(process, hwnd);
        Line("");
    }

    /// <summary>Opens the page's &lt;select&gt; dropdown and the context menu at 100% opacity.</summary>
    private static void RunPopups(string name, bool allowCapture)
    {
        Line($"## {name}");
        var args = new List<string> { TestPage, "--opacity=100" };
        if (allowCapture)
        {
            args.Add("--diag-allow-capture");
        }

        var (process, hwnd) = Launch(args);
        Thread.Sleep(3500);
        GetRect(hwnd, out var r);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        int pageX = r.X + (int)(6 * scale), pageY = r.Y + (int)((1 + 40 + 44) * scale);

        Click(pageX + (int)(96 * scale), pageY + (int)(32 * scale));
        Thread.Sleep(1000);
        ReportTopLevelWindows(process.Id, "select dropdown open");
        Check($"{name}-select-dropdown", hwnd, expectExcluded: !allowCapture, extraRegion: true);
        Key(0x1B);
        Thread.Sleep(400);

        RightClick(pageX + (int)(420 * scale), pageY + (int)(260 * scale));
        Thread.Sleep(1000);
        ReportTopLevelWindows(process.Id, "context menu open");
        Check($"{name}-context-menu", hwnd, expectExcluded: !allowCapture, extraRegion: true);
        Key(0x1B);
        Thread.Sleep(400);

        CloseViaButton(process, hwnd);
        Line("");
    }

    private static void RunInteractive()
    {
        Line("## runtime (opacity changes, move, resize, maximize, minimize/restore, popups, typing)");
        var (process, hwnd) = Launch(new List<string> { TestPage, "--opacity=25" });
        Thread.Sleep(3500);
        var root = AutomationElement.FromHandle(hwnd);

        foreach (var value in new[] { 100, 10, 100, 60, 100, 25 })
        {
            var slider = Find(root, "OpacitySlider");
            ((RangeValuePattern)slider.GetCurrentPattern(RangePattern)).SetValue(value);
            Thread.Sleep(60); // capture right away: no window for a stale/unprotected frame
            Check($"slider-{value}", hwnd, expectExcluded: true);
        }

        GetRect(hwnd, out var r);
        SetWindowPos(hwnd, IntPtr.Zero, r.X - 350, r.Y + 120, 0, 0, 0x0001 | 0x0004 | 0x0010); // move
        Thread.Sleep(700);
        Check("moved", hwnd, expectExcluded: true);

        GetRect(hwnd, out r);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, r.W - 200, r.H + 100, 0x0002 | 0x0004 | 0x0010); // resize
        Thread.Sleep(700);
        Check("resized", hwnd, expectExcluded: true);

        Invoke(root, "MaximizeButton");
        Thread.Sleep(1200);
        Check("maximized", hwnd, expectExcluded: true);
        Invoke(root, "MaximizeButton");
        Thread.Sleep(1200);
        Check("restored-from-maximize", hwnd, expectExcluded: true);

        Invoke(root, "MinimizeButton");
        Thread.Sleep(800);
        Expect("minimize hides window", !IsWindowVisible(hwnd) && !process.HasExited,
            $"visible={IsWindowVisible(hwnd)}, running={!process.HasExited}");

        // A second launch (what Start/Search does) must restore the running instance.
        var second = Process.Start(_axeExe);
        Expect("second launch exits (single instance)", second.WaitForExit(10000), "second process still running");
        Thread.Sleep(1200);
        Expect("second launch restores window", IsWindowVisible(hwnd), "window still hidden");
        Check("restored-from-minimize", hwnd, expectExcluded: true);

        // Topmost after another app takes focus
        // Another application window takes focus (placed away from AXE so it doesn't cover it).
        System.Windows.Window? other = null;
        _backdrop!.Invoke(() =>
        {
            other = new System.Windows.Window { Title = "Other app", Left = 0, Top = 0, Width = 320, Height = 200, Topmost = true };
            other.Show();
            other.Activate();
        });
        Thread.Sleep(1200);
        var isTopmost = (GetWindowLongPtr(hwnd, -20).ToInt64() & 0x8) != 0;
        var otherFocused = GetForegroundWindow() != hwnd;
        Expect("stays topmost while another window is active (WS_EX_TOPMOST)", isTopmost,
            $"topmost={isTopmost}, otherWindowForeground={otherFocused}");
        Line($"  - note: another window foreground during check = {otherFocused}");
        Check("other-window-focused", hwnd, expectExcluded: true);
        _backdrop.Invoke(() => other!.Close());
        Thread.Sleep(500);

        GetRect(hwnd, out r);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        int pageX = r.X + (int)(6 * scale), pageY = r.Y + (int)((1 + 40 + 44) * scale);

        // Typed text into a page field
        Click(pageX + (int)(160 * scale), pageY + (int)(96 * scale));
        Thread.Sleep(300);
        TypeText("SECRET-TYPED-TEXT-123");
        Thread.Sleep(600);
        Check("typed-text", hwnd, expectExcluded: true);

        // Browser context menu (rendered by AXE)
        RightClick(pageX + (int)(420 * scale), pageY + (int)(260 * scale));
        Thread.Sleep(900);
        ReportTopLevelWindows(process.Id, "context menu open");
        Check("context-menu-open", hwnd, expectExcluded: true, extraRegion: true);
        Key(0x1B);
        Thread.Sleep(400);

        // <select> dropdown (rendered by Chromium in its own popup window)
        Click(pageX + (int)(96 * scale), pageY + (int)(32 * scale));
        Thread.Sleep(900);
        ReportTopLevelWindows(process.Id, "select dropdown open");
        Check("select-dropdown-open", hwnd, expectExcluded: true, extraRegion: true, informational: true);
        Key(0x1B);
        Thread.Sleep(400);

        CloseViaButton(process, hwnd);
        Line("");
    }

    // ================================================================== external tools

    private static string? WinPrintScreen(string tag)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        Directory.CreateDirectory(folder);
        var before = Directory.GetFiles(folder).ToHashSet();
        var inputs = new[] { KeyInput(0x5B, 0, 0), KeyInput(0x2C, 0, 0), KeyInput(0x2C, 0, 2), KeyInput(0x5B, 0, 2) }; // Win+PrtScn
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(500);
            var created = Directory.GetFiles(folder).FirstOrDefault(f => !before.Contains(f));
            if (created is not null)
            {
                Thread.Sleep(500);
                var dest = Path.Combine(_outDir, $"{tag}.win-printscreen.png");
                File.Move(created, dest, true); // keep the user's Screenshots folder clean
                return dest;
            }
        }

        return null;
    }

    private static string? Ffmpeg(string tag, string name, string input)
    {
        var dest = Path.Combine(_outDir, $"{tag}.ffmpeg-{name}.png");
        var psi = new ProcessStartInfo("ffmpeg", $"-hide_banner -loglevel error -y {input} -frames:v 1 \"{dest}\"")
        {
            UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        if (!File.Exists(dest)) { Line($"  - ffmpeg {name} error: {err.Trim()}"); return null; }
        return dest;
    }

    private static string? BrowserShare(string tag, string browser, string exe)
    {
        if (!File.Exists(exe)) { Line($"  - {browser} not installed"); return null; }
        var fileName = $"axe-share-{browser}-{tag}.png";
        var page = Path.Combine(_outDir, $"share-{browser}.html");
        File.WriteAllText(page,
            "<!doctype html><html><body style='margin:0'><button id=b style='width:100vw;height:100vh;font-size:28px'>Share screen</button><script>" +
            "b.onclick=async()=>{try{const s=await navigator.mediaDevices.getDisplayMedia({video:{displaySurface:'monitor'},audio:false});" +
            "const v=document.createElement('video');v.muted=true;v.srcObject=s;await v.play();await new Promise(r=>setTimeout(r,2500));" +
            "const c=document.createElement('canvas');c.width=v.videoWidth;c.height=v.videoHeight;c.getContext('2d').drawImage(v,0,0);" +
            "s.getTracks().forEach(t=>t.stop());const a=document.createElement('a');a.href=c.toDataURL('image/png');a.download='" + fileName + "';a.click();" +
            "b.textContent='done '+c.width+'x'+c.height;}catch(e){b.textContent='error: '+e;document.title='error: '+e}};</script></body></html>");
        var profile = Path.Combine(_outDir, $"profile-{browser}-{tag}"); // fresh profile per run: no "restore pages" bubble
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var target = Path.Combine(downloads, fileName);
        if (File.Exists(target)) File.Delete(target);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { $"--user-data-dir={profile}", "--no-first-run", "--no-default-browser-check", "--disable-sync",
                     "--auto-select-desktop-capture-source=Entire screen", "--new-window",
                     "--window-position=0,0", "--window-size=540,420", new Uri(page).AbsoluteUri })
        {
            psi.ArgumentList.Add(a);
        }

        var proc = Process.Start(psi)!;
        proc.ErrorDataReceived += (_, _) => { }; // discard browser console noise
        proc.OutputDataReceived += (_, _) => { };
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();
        Thread.Sleep(6000);
        Click(270, 260); // the full-page "Share screen" button (user gesture)
        var deadline = DateTime.UtcNow.AddSeconds(25);
        string? result = null;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(500);
            if (File.Exists(target) && new FileInfo(target).Length > 0)
            {
                Thread.Sleep(700);
                result = Path.Combine(_outDir, $"{tag}.{browser}-getdisplaymedia.png");
                File.Move(target, result, true);
                break;
            }
        }

        // Close only the browser instance started for this test (identified by its private profile).
        foreach (var p in Process.GetProcesses().Where(p => p.ProcessName is "chrome" or "msedge"))
        {
            try
            {
                if (CommandLineOf(p.Id)?.Contains(profile, StringComparison.OrdinalIgnoreCase) == true)
                {
                    p.Kill(true);
                }
            }
            catch { }
        }

        Thread.Sleep(1000);
        return result;
    }

    private static string? CommandLineOf(int pid)
    {
        using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId={pid}");
        return searcher.Get().Cast<ManagementObject>().Select(o => o["CommandLine"] as string).FirstOrDefault();
    }

    private static Frame LoadPng(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var bmp = new System.Windows.Media.Imaging.FormatConvertedBitmap(decoder.Frames[0], System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
        bmp.CopyPixels(pixels, bmp.PixelWidth * 4, 0);
        return new Frame(pixels, bmp.PixelWidth, bmp.PixelHeight, 0, 0, Path.GetFileName(path));
    }

    // ================================================================== checks

    private static void Check(string name, IntPtr hwnd, bool expectExcluded, bool expectMagenta = false,
        bool extraRegion = false, bool informational = false)
    {
        GetRect(hwnd, out var rect);
        GetWindowDisplayAffinity(hwnd, out var affinity);
        var inner = new RectI(rect.X + 3, rect.Y + 3, rect.W - 6, rect.H - 6);

        foreach (var frame in new[] { Capture.Gdi(), Capture.Wgc() })
        {
            var (green, magenta, total) = Measure(frame, inner);
            if (total == 0)
            {
                Line($"- {name} [{frame.Api}]: AXE rect not on captured surface, skipped");
                continue;
            }

            var greenFrac = (double)green / total;
            var magentaFrac = (double)magenta / total;
            if (extraRegion)
            {
                // Popups can extend beyond AXE's rect: measure a wider area around it too.
                var (g2, _, t2) = Measure(frame, rect.Inflate(260));
                var around = t2 == 0 ? 1.0 : (double)g2 / t2;
                Line($"  - {name} [{frame.Api}] AXE rect + 260px surroundings: backdrop visible {around:P2}");
                greenFrac = Math.Min(greenFrac, expectExcluded ? around / 1.0 : greenFrac);
            }
            var file = $"{name}.{frame.Api.ToLowerInvariant()}.png";
            Capture.SavePng(frame, Path.Combine(_outDir, file), extraRegion ? rect.Inflate(260) : rect.Inflate(40));

            bool ok;
            string expectation;
            if (expectExcluded)
            {
                ok = greenFrac >= PassThreshold;
                expectation = $"backdrop ≥ {PassThreshold:P0}";
            }
            else
            {
                ok = greenFrac <= ControlThreshold && (!expectMagenta || magentaFrac >= 0.5);
                expectation = $"backdrop ≤ {ControlThreshold:P0}" + (expectMagenta ? ", page magenta ≥ 50%" : "");
            }

            var verdict = ok ? "PASS" : informational ? "LEAK (known limitation)" : "FAIL";
            Line($"- {name} [{frame.Api}] affinity=0x{affinity:X2} rect={rect}: backdrop visible {greenFrac:P1}, " +
                 $"test page visible {magentaFrac:P1} (expect {expectation}) → {verdict}  ({file})");
            if (!ok && !informational)
            {
                _failures++;
            }
        }
    }

    private static (long Green, long Magenta, long Total) Measure(Frame frame, RectI r)
    {
        long green = 0, magenta = 0, total = 0;
        int x0 = Math.Max(r.X - frame.OriginX, 0), y0 = Math.Max(r.Y - frame.OriginY, 0);
        int x1 = Math.Min(r.X + r.W - frame.OriginX, frame.Width), y1 = Math.Min(r.Y + r.H - frame.OriginY, frame.Height);
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var i = (y * frame.Width + x) * 4;
                byte b = frame.Pixels[i], g = frame.Pixels[i + 1], rr = frame.Pixels[i + 2];
                total++;
                if (Math.Abs(rr - Backdrop.R) <= Tolerance && Math.Abs(g - Backdrop.G) <= Tolerance && Math.Abs(b - Backdrop.B) <= Tolerance)
                {
                    green++;
                }
                else if (rr > 200 && g < 60 && b > 200)
                {
                    magenta++;
                }
            }
        }

        return (green, magenta, total);
    }

    private static void ReportTopLevelWindows(int axePid, string when)
    {
        var webviewPids = Process.GetProcessesByName("msedgewebview2").Select(p => (uint)p.Id).ToHashSet();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h))
            {
                return true;
            }

            GetWindowThreadProcessId(h, out var pid);
            if (pid == axePid || webviewPids.Contains(pid))
            {
                GetRect(h, out var r);
                GetWindowDisplayAffinity(h, out var aff);
                var owner = pid == axePid ? "AXE" : "msedgewebview2";
                Line($"  - {when}: visible top-level window of {owner} (pid {pid}) rect={r} affinity=0x{aff:X2}");
            }

            return true;
        }, IntPtr.Zero);
    }

    private static void Expect(string name, bool ok, string detail)
    {
        Line($"- {name} → {(ok ? "PASS" : "FAIL: " + detail)}");
        if (!ok)
        {
            _failures++;
        }
    }

    private static void Fail(string message)
    {
        Line($"- FAIL: {message}");
        _failures++;
    }

    private static void Line(string text)
    {
        Console.WriteLine(text);
        Report.Add(text);
    }

    // ================================================================== AXE process control

    private static (Process Process, IntPtr Hwnd) Launch(List<string> args)
    {
        var psi = new ProcessStartInfo(_axeExe) { UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        var process = Process.Start(psi)!;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var hwnd = FindMainWindow(process.Id);
            if (hwnd != IntPtr.Zero)
            {
                return (process, hwnd);
            }

            Thread.Sleep(200);
        }

        throw new TimeoutException("AXE window did not appear");
    }

    private static IntPtr FindMainWindow(int pid)
    {
        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var p);
            if (p == pid && IsWindowVisible(h))
            {
                var title = new StringBuilder(256);
                GetWindowText(h, title, title.Capacity);
                if (title.ToString().StartsWith("AXE", StringComparison.Ordinal))
                {
                    found = h;
                    return false;
                }
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static void CloseViaButton(Process process, IntPtr hwnd)
    {
        var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AXE", "WebView2");
        Invoke(AutomationElement.FromHandle(hwnd), "CloseButton");
        var exited = process.WaitForExit(10000);
        Expect("close button exits AXE process", exited, "process still running");

        // Browser helper processes belonging to AXE's profile must go away too.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        int leftovers;
        do
        {
            leftovers = CountWebViewProcesses(userData);
            if (leftovers == 0)
            {
                break;
            }

            Thread.Sleep(500);
        }
        while (DateTime.UtcNow < deadline);

        Expect("no AXE WebView2 processes remain after close", leftovers == 0, $"{leftovers} msedgewebview2 process(es) remain");
        if (!exited)
        {
            process.Kill(true);
        }
    }

    private static int CountWebViewProcesses(string userData)
    {
        using var searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE Name='msedgewebview2.exe'");
        return searcher.Get().Cast<ManagementObject>()
            .Count(p => (p["CommandLine"] as string)?.Contains(userData, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static void KillAxe()
    {
        foreach (var p in Process.GetProcessesByName("AXE"))
        {
            try { p.Kill(true); } catch { }
        }
    }

    // ================================================================== UI automation & input

    private static readonly AutomationPattern RangePattern = RangeValuePattern.Pattern;

    private static AutomationElement Find(AutomationElement root, string automationId) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))
        ?? throw new InvalidOperationException($"UI element {automationId} not found");

    private static void Invoke(AutomationElement root, string automationId) =>
        ((InvokePattern)Find(root, automationId).GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    private static void Click(int x, int y) => Mouse(x, y, 0x0002, 0x0004);

    private static void RightClick(int x, int y) => Mouse(x, y, 0x0008, 0x0010);

    private static void Mouse(int x, int y, uint down, uint up)
    {
        SetCursorPos(x, y);
        Thread.Sleep(80);
        var inputs = new[] { MouseInput(down), MouseInput(up) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void TypeText(string text)
    {
        foreach (var ch in text)
        {
            var inputs = new[] { KeyInput(0, ch, 0x0004), KeyInput(0, ch, 0x0004 | 0x0002) };
            SendInput(2, inputs, Marshal.SizeOf<INPUT>());
            Thread.Sleep(15);
        }
    }

    private static void Key(ushort vk)
    {
        var inputs = new[] { KeyInput(vk, 0, 0), KeyInput(vk, 0, 0x0002) };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void Chord(ushort modifier, ushort vk)
    {
        var inputs = new[] { KeyInput(modifier, 0, 0), KeyInput(vk, 0, 0), KeyInput(vk, 0, 0x0002), KeyInput(modifier, 0, 0x0002) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        Thread.Sleep(150);
    }

    private static INPUT MouseInput(uint flags) => new() { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } } };

    private static INPUT KeyInput(ushort vk, ushort scan, uint flags) =>
        new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } };

    // ================================================================== backdrop

    private static Dispatcher StartBackdrop(double leftInsetDip = 0)
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var window = new System.Windows.Window
            {
                WindowStyle = System.Windows.WindowStyle.None,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(Backdrop.R, Backdrop.G, Backdrop.B)),
                Title = "AXE capture test backdrop",
                WindowState = leftInsetDip > 0 ? System.Windows.WindowState.Normal : System.Windows.WindowState.Maximized,
                Left = leftInsetDip,
                Top = 0,
                Width = System.Windows.SystemParameters.PrimaryScreenWidth - leftInsetDip,
                Height = System.Windows.SystemParameters.PrimaryScreenHeight,
                // Topmost so it reliably covers everything else even when the probe can't take
                // the foreground; AXE (also topmost, launched later) still sits above it.
                Topmost = true,
            };
            window.Show();
            window.Activate();
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        ready.Wait();
        Thread.Sleep(800);
        return dispatcher!;
    }

    // ================================================================== interop

    private static void GetRect(IntPtr hwnd, out RectI rect)
    {
        if (DwmGetWindowAttribute(hwnd, 9 /*EXTENDED_FRAME_BOUNDS*/, out RECT r, Marshal.SizeOf<RECT>()) != 0)
        {
            GetWindowRect(hwnd, out r);
        }

        rect = new RectI(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr h, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
