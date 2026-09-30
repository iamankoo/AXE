using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace AxeV2.CaptureProbe;

/// <summary>A BGRA32 screen image in physical pixels with its origin in virtual-screen coordinates.</summary>
internal sealed record Frame(byte[] Pixels, int Width, int Height, int OriginX, int OriginY, string Api);

/// <summary>Two independent Windows capture paths that are expected to honour display affinity.</summary>
internal static class Capture
{
    // ---------------------------------------------------------------- GDI BitBlt (classic screenshot path)

    public static Frame Gdi()
    {
        int x = GetSystemMetrics(76), y = GetSystemMetrics(77), w = GetSystemMetrics(78), h = GetSystemMetrics(79);
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, w, h);
        var old = SelectObject(memDc, bitmap);
        try
        {
            if (!BitBlt(memDc, 0, 0, w, h, screenDc, x, y, 0x00CC0020 | 0x40000000)) // SRCCOPY | CAPTUREBLT
            {
                throw new InvalidOperationException("BitBlt failed");
            }

            SelectObject(memDc, old);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var converted = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var pixels = new byte[w * h * 4];
            converted.CopyPixels(pixels, w * 4, 0);
            return new Frame(pixels, w, h, x, y, "GDI");
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // ---------------------------------------------------------------- Windows.Graphics.Capture (modern path)

    public static Frame Wgc()
    {
        var monitor = MonitorFromPoint(new POINT(), 1); // primary
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);

        var item = CreateItemForMonitor(monitor);
        using var device = CreateDevice();
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        using var session = pool.CreateCaptureSession(item);
        try { session.IsCursorCaptureEnabled = false; } catch { /* older builds */ }

        var frames = new System.Collections.Concurrent.BlockingCollection<Direct3D11CaptureFrame>();
        pool.FrameArrived += (p, _) =>
        {
            var f = p.TryGetNextFrame();
            if (f is not null)
            {
                frames.Add(f);
            }
        };
        session.StartCapture();

        if (!frames.TryTake(out var frame, TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("No WGC frame arrived");
        }

        // Prefer a later frame so the capture reflects the current composition.
        while (frames.TryTake(out var newer, TimeSpan.FromMilliseconds(400)))
        {
            frame.Dispose();
            frame = newer;
        }

        using (frame)
        {
            var bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult();
            using (bitmap)
            {
                var buffer = new Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
                bitmap.CopyToBuffer(buffer);
                return new Frame(buffer.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight, info.rcMonitor.Left, info.rcMonitor.Top, "WGC");
            }
        }
    }

    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmon)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        var ptr = interop.CreateForMonitor(hmon, ref iid);
        var item = GraphicsCaptureItem.FromAbi(ptr);
        Marshal.Release(ptr);
        return item;
    }

    private static IDirect3DDevice CreateDevice()
    {
        var hr = D3D11CreateDevice(IntPtr.Zero, 1 /*HARDWARE*/, IntPtr.Zero, 0x20 /*BGRA*/, IntPtr.Zero, 0, 7, out var d3d, out _, out var ctx);
        if (hr < 0)
        {
            hr = D3D11CreateDevice(IntPtr.Zero, 5 /*WARP*/, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out d3d, out _, out ctx);
            Marshal.ThrowExceptionForHR(hr);
        }

        var dxgiIid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, ref dxgiIid, out var dxgi));
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
        var device = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        Marshal.Release(inspectable);
        Marshal.Release(dxgi);
        Marshal.Release(ctx);
        Marshal.Release(d3d);
        return device;
    }

    // ---------------------------------------------------------------- saving

    public static void SavePng(Frame frame, string path, RectI? crop = null)
    {
        var r = crop ?? new RectI(frame.OriginX, frame.OriginY, frame.Width, frame.Height);
        int x0 = Math.Clamp(r.X - frame.OriginX, 0, frame.Width), y0 = Math.Clamp(r.Y - frame.OriginY, 0, frame.Height);
        int x1 = Math.Clamp(r.X + r.W - frame.OriginX, 0, frame.Width), y1 = Math.Clamp(r.Y + r.H - frame.OriginY, 0, frame.Height);
        int w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var cropped = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
        {
            Buffer.BlockCopy(frame.Pixels, ((y0 + row) * frame.Width + x0) * 4, cropped, row * w * 4, w * 4);
        }

        for (var i = 3; i < cropped.Length; i += 4)
        {
            cropped[i] = 255;
        }

        var bmp = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, cropped, w * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // ---------------------------------------------------------------- interop

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}

internal readonly record struct RectI(int X, int Y, int W, int H)
{
    public RectI Inflate(int d) => new(X - d, Y - d, W + 2 * d, H + 2 * d);
    public override string ToString() => $"{X},{Y} {W}x{H}";
}
