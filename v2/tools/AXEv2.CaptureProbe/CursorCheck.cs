using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AxeV2.CaptureProbe;

/// <summary>
/// Non-interactive check of AXE's cursor lock (no mouse or keyboard input is sent): a local
/// test page full of elements that normally get special cursors — links, buttons, inputs,
/// site CSS with high-specificity !important rules, inline !important, a web component's
/// shadow DOM, a late-added element and an iframe — reports the computed cursor of each back
/// to this probe. Every value must be "default".
///
/// Usage: AXEv2.CaptureProbe cursorcheck &lt;exe&gt; [args...]   ({url} = test page)
/// </summary>
internal static class CursorCheck
{
    private static readonly BlockingCollection<string> Reports = new();

    private const string Page = """
        <!doctype html><html><head><title>cursor</title><style>
        @layer site { a { cursor: pointer !important } }
        #hi.x.y.z { cursor: crosshair !important }
        textarea { cursor: grab }
        </style></head><body>
        <a id="link" href="#">link</a>
        <button id="button" style="cursor:pointer">button</button>
        <input id="input"><textarea id="textarea"></textarea>
        <p id="text">plain text</p>
        <div id="hi" class="x y z">high specificity</div>
        <div id="inline" style="cursor: wait !important">inline important</div>
        <div id="custom" style="cursor: url(data:image/png;base64,iVBORw0KGgo=) 0 0, move">custom</div>
        <select id="select"><option>a</option></select>
        <x-comp id="comp"></x-comp>
        <iframe id="frame" src="/f" style="width:100px;height:50px"></iframe>
        <script>
        customElements.define('x-comp', class extends HTMLElement {
          constructor() { super(); const r = this.attachShadow({mode:'open'});
            r.innerHTML = '<style>span{cursor:pointer!important}</style><span id="s">shadow</span>'; }
        });
        const late = document.createElement('div'); late.id = 'late'; late.style.cursor = 'ns-resize';
        setTimeout(() => document.body.appendChild(late), 100);
        const dyn = document.getElementById('text');
        setTimeout(() => dyn.style.setProperty('cursor', 'help', 'important'), 150);
        setTimeout(() => {
          const out = {};
          for (const id of ['link','button','input','textarea','text','hi','inline','custom','select','late'])
            out[id] = getComputedStyle(document.getElementById(id)).cursor;
          out.shadow = getComputedStyle(document.getElementById('comp').shadowRoot.getElementById('s')).cursor;
          out.html = getComputedStyle(document.documentElement).cursor;
          fetch('/report?main=' + encodeURIComponent(JSON.stringify(out)));
        }, 800);
        </script></body></html>
        """;

    private const string Frame = """
        <!doctype html><html><body><a id="a" href="#" style="cursor:pointer">frame link</a><script>
        setTimeout(() => fetch('/report?frame=' + encodeURIComponent(getComputedStyle(document.getElementById('a')).cursor)), 600);
        </script></body></html>
        """;

    public static int Run(string exe, string[] args)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(() => Serve(listener));

        var launchedAt = DateTime.Now.AddSeconds(-1);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a.Replace("{url}", $"http://127.0.0.1:{port}/c"));
        }

        Process.Start(psi);
        var name = Path.GetFileNameWithoutExtension(exe);
        var failures = 0;
        try
        {
            string? main = null, frame = null;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && (main is null || frame is null))
            {
                if (!Reports.TryTake(out var r, 500))
                {
                    continue;
                }

                var q = r[(r.IndexOf('?') + 1)..].Split('=', 2);
                var value = Uri.UnescapeDataString(q[1]);
                if (q[0] == "main") main = value;
                else if (q[0] == "frame") frame = value;
            }

            if (main is null)
            {
                Console.WriteLine("FAIL no report from the page");
                return 1;
            }

            var values = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(main)!;
            values["iframe"] = frame ?? "(no report)";
            foreach (var (element, cursor) in values)
            {
                var ok = cursor == "default";
                failures += ok ? 0 : 1;
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {element,-10} cursor: {cursor}");
            }

            Console.WriteLine(failures == 0 ? "All elements show the standard arrow." : $"{failures} element(s) not locked.");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            listener.Stop();
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
                    var body = path.StartsWith("/report", StringComparison.Ordinal) ? "ok"
                        : path.StartsWith("/f", StringComparison.Ordinal) ? Frame : Page;
                    if (path.StartsWith("/report", StringComparison.Ordinal))
                    {
                        Reports.Add(path);
                    }

                    var bytes = Encoding.UTF8.GetBytes(body);
                    var header = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                    await stream.WriteAsync(bytes);
                }
            });
        }
    }
}
