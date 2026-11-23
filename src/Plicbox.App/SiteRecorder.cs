using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Plicbox.App;

/// <summary>
/// Dev aid (--record-site): films the main window for the website as a video. A background thread keeps grabbing
/// the main window and, while one is open, a second window (PrintWindow, so WebView2 content is included and nothing
/// on the desktop gets in the way). Another thread builds frames at a steady 30 per second: the main window, the
/// second window floating over it with a soft shadow, and a drawn mouse pointer that the script moves and clicks.
/// Frames go straight into ffmpeg as raw pixels.
/// </summary>
internal sealed partial class SiteRecorder : IAsyncDisposable
{
    private const int Fps = 30;
    private readonly IntPtr _main;
    private readonly int _width, _height;
    private readonly double _scale;
    private readonly Process _ffmpeg;
    private readonly Stream _pipe;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Thread _grabber, _writer;
    private volatile bool _stop;
    private readonly object _gate = new();

    // Latest grabs (BGRA, top-down).
    private byte[]? _mainPixels;
    private byte[]? _popupPixels;
    private int _popupW, _popupH;
    private IntPtr _popup;

    // Where the popup window floats, in frame pixels, and when it opened or started closing (clock seconds).
    private int _popupX, _popupY;
    private double _popupOpened = double.MaxValue, _popupClosing = double.MaxValue;
    private byte[]? _shadow; // alpha only, popup size plus Pad on every side
    private const int Pad = 60;

    // The pointer: a tween between two points, its visibility, and recent clicks.
    private Point _from, _to;
    private double _moveStart, _moveTime = 1;
    private double _shownAt = double.MaxValue, _hiddenAt = double.MaxValue;
    private readonly List<(Point At, double Time)> _clicks = [];
    private readonly byte[] _arrow;
    private readonly int _arrowW, _arrowH;

    /// <summary>
    /// Starts filming. Never wait for the grabber on the UI thread: PrintWindow asks the window's own thread to draw,
    /// so blocking that thread (Sleep, Join) deadlocks.
    /// </summary>
    public static async Task<SiteRecorder> StartAsync(Window main, string ffmpeg, string output)
    {
        var recorder = new SiteRecorder(main, ffmpeg, output);
        while (Volatile.Read(ref recorder._mainPixels) is null) await Task.Delay(10);
        recorder._writer.Start();
        return recorder;
    }

    private SiteRecorder(Window main, string ffmpeg, string output)
    {
        _main = new System.Windows.Interop.WindowInteropHelper(main).Handle;
        GetWindowRect(_main, out var r);
        (_width, _height) = (r.Right - r.Left, r.Bottom - r.Top);
        _scale = VisualTreeHelper.GetDpi(main).DpiScaleX;
        (_arrow, _arrowW, _arrowH) = RenderArrow(_scale);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(output))!);
        // Near-lossless intermediate; the website's small file is encoded from it afterwards.
        _ffmpeg = Process.Start(new ProcessStartInfo(ffmpeg,
            $"-y -loglevel error -f rawvideo -pix_fmt bgra -s {_width}x{_height} -r {Fps} -i - -c:v libx264 -preset ultrafast -crf 8 -pix_fmt yuv444p \"{output}\"")
        {
            RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
        })!;
        _pipe = _ffmpeg.StandardInput.BaseStream;

        _grabber = new Thread(GrabLoop) { IsBackground = true, Name = "site-recorder-grab" };
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "site-recorder-write" };
        _grabber.Start();
    }

    /// <summary>
    /// The video's own clock: how far it has been built. Frames are built behind real time (each takes a while to put
    /// together and encode) while the windows' pictures are always the latest, so the script times everything by this
    /// clock (<see cref="DelayAsync"/>) instead of the wall clock: what it does then lands at the right moment on video,
    /// however fast or slow the frames come.
    /// </summary>
    public double Now => Interlocked.Read(ref _builtMicros) / 1e6;

    private long _builtMicros;

    /// <summary>Waits until the video is this much further along.</summary>
    public async Task DelayAsync(double seconds)
    {
        var target = (long)((Now + seconds) * 1e6);
        while (!_stop && Interlocked.Read(ref _builtMicros) < target) await Task.Delay(4);
    }

    /// <summary>A WPF element's centre, in frame pixels: on the main window, or on the popup where it floats.</summary>
    public Point CentreOf(FrameworkElement element, bool onPopup = false)
    {
        var screen = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        if (!onPopup)
        {
            GetWindowRect(_main, out var r);
            return new Point(screen.X - r.Left, screen.Y - r.Top);
        }
        var frame = VisibleFrame(new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(element)!).Handle);
        lock (_gate) return new Point(screen.X - frame.Left + _popupX, screen.Y - frame.Top + _popupY);
    }

    /// <summary>The window as seen on screen: without the invisible resize borders a standard window has around it.</summary>
    private static Rect32 VisibleFrame(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<Rect32>()) == 0 ? frame
        : GetWindowRect(hwnd, out var r) ? r : default;

    public void ShowPointer(Point at)
    {
        lock (_gate) { _from = _to = at; _shownAt = Now; _hiddenAt = double.MaxValue; }
    }

    public void HidePointer() { lock (_gate) _hiddenAt = Now; }

    public async Task MoveAsync(Point to, double seconds)
    {
        lock (_gate) { _from = PointerAt(Now); _to = to; _moveStart = Now; _moveTime = seconds; }
        await DelayAsync(seconds);
    }

    /// <summary>Clicks where the pointer is; returns a moment later on video, when what the click does may show.</summary>
    public async Task ClickAsync()
    {
        lock (_gate) _clicks.Add((PointerAt(Now), Now));
        await DelayAsync(0.12);
    }

    /// <summary>Starts filming a second window, floating at (x, y) in the frame; it fades in.</summary>
    public void OpenPopup(Window window, int x, int y)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var r = VisibleFrame(hwnd);
        var shadow = RenderShadow(r.Right - r.Left, r.Bottom - r.Top);
        lock (_gate)
        {
            (_popupX, _popupY, _shadow) = (x, y, shadow);
            _popup = hwnd;
            _popupOpened = Now;
            _popupClosing = double.MaxValue;
        }
    }

    /// <summary>Fades the second window out; its last picture is kept while it does, so it can be closed meanwhile.</summary>
    public async Task ClosePopupAsync()
    {
        lock (_gate) { _popupClosing = Now; _popup = IntPtr.Zero; }
        await DelayAsync(0.26);
    }

    public async ValueTask DisposeAsync()
    {
        _stop = true;
        await Task.Run(() => { _writer.Join(); _grabber.Join(); }); // off the UI thread: see StartAsync
        _pipe.Dispose();
        await _ffmpeg.WaitForExitAsync();
    }

    // ───────── grabbing ─────────

    private void GrabLoop()
    {
        while (!_stop)
        {
            var main = Grab(_main, out _, out _);
            IntPtr popup;
            lock (_gate) popup = _popup;
            byte[]? second = null;
            int w = 0, h = 0;
            if (popup != IntPtr.Zero) second = Grab(popup, out w, out h, visibleOnly: true);
            lock (_gate)
            {
                if (main is not null && main.Length == _width * _height * 4) _mainPixels = main;
                if (second is not null && popup == _popup) (_popupPixels, _popupW, _popupH) = (second, w, h);
            }
            Thread.Sleep(5);
        }
    }

    private static byte[]? Grab(IntPtr hwnd, out int width, out int height, bool visibleOnly = false)
    {
        var whole = Grab(hwnd, out var fullW, out var fullH);
        if (!visibleOnly || whole is null) { (width, height) = (fullW, fullH); return whole; }
        GetWindowRect(hwnd, out var r);
        var f = VisibleFrame(hwnd);
        (width, height) = (f.Right - f.Left, f.Bottom - f.Top);
        int dx = f.Left - r.Left, dy = f.Top - r.Top;
        if (width <= 0 || height <= 0 || dx < 0 || dy < 0 || dx + width > fullW || dy + height > fullH) return null;
        var cropped = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(whole, ((y + dy) * fullW + dx) * 4, cropped, y * width * 4, width * 4);
        return cropped;
    }

    private static byte[]? Grab(IntPtr hwnd, out int width, out int height)
    {
        GetWindowRect(hwnd, out var r);
        (width, height) = (r.Right - r.Left, r.Bottom - r.Top);
        if (width <= 0 || height <= 0) return null;
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var old = SelectObject(memDc, bitmap);
        try
        {
            if (!PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT)) return null;
            SelectObject(memDc, old);
            var info = new BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            var pixels = new byte[width * height * 4];
            return GetDIBits(memDc, bitmap, 0, (uint)height, pixels, ref info, 0) == height ? pixels : null;
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // ───────── frames ─────────

    private void WriteLoop()
    {
        var frame = new byte[_width * _height * 4];
        for (var n = 0; !_stop; n++)
        {
            var due = n / (double)Fps;
            while (!_stop && _clock.Elapsed.TotalSeconds < due) Thread.Sleep(2); // never ahead of real time
            if (_stop) break;
            Compose(frame, due);
            _pipe.Write(frame);
            Interlocked.Exchange(ref _builtMicros, (long)(due * 1e6));
        }
    }

    private void Compose(byte[] frame, double t)
    {
        byte[] main; byte[]? popup; byte[]? shadow; int pw, ph, px, py; double opened, closing;
        lock (_gate)
        {
            (main, popup, shadow, pw, ph, px, py) = (_mainPixels!, _popupPixels, _shadow, _popupW, _popupH, _popupX, _popupY);
            (opened, closing) = (_popupOpened, _popupClosing);
        }
        Buffer.BlockCopy(main, 0, frame, 0, frame.Length);

        // The popup: fades in while rising a little, fades out in place.
        var alpha = Math.Clamp((t - opened) / 0.22, 0, 1) * (1 - Math.Clamp((t - closing) / 0.22, 0, 1));
        if (popup is not null && shadow is not null && alpha > 0 && t >= opened)
        {
            var rise = (int)Math.Round((1 - Ease(Math.Clamp((t - opened) / 0.22, 0, 1))) * 14 * _scale);
            DrawShadow(frame, shadow, pw + 2 * Pad, ph + 2 * Pad, px - Pad, py - Pad + rise, alpha);
            DrawPopup(frame, popup, pw, ph, px, py + rise, alpha, (int)(8 * _scale));
        }

        // Click rings, then the pointer on top.
        List<(Point At, double Time)> clicks;
        lock (_gate) clicks = [.. _clicks];
        foreach (var (at, time) in clicks)
            if (t >= time && t - time < 0.5) DrawRing(frame, at, (t - time) / 0.5);
        var visible = Math.Clamp((t - _shownAt) / 0.2, 0, 1) * (1 - Math.Clamp((t - _hiddenAt) / 0.2, 0, 1));
        if (visible > 0)
        {
            Point p;
            lock (_gate) p = PointerAt(t);
            DrawArrow(frame, (int)p.X, (int)p.Y, visible);
        }
    }

    private Point PointerAt(double t)
    {
        var k = Ease(Math.Clamp((t - _moveStart) / _moveTime, 0, 1));
        return new Point(_from.X + (_to.X - _from.X) * k, _from.Y + (_to.Y - _from.Y) * k);
    }

    private static double Ease(double k) => k < .5 ? 4 * k * k * k : 1 - Math.Pow(-2 * k + 2, 3) / 2;

    private void DrawPopup(byte[] frame, byte[] popup, int pw, int ph, int x0, int y0, double alpha, int radius)
    {
        var a = (int)(alpha * 256);
        for (var y = 0; y < ph; y++)
        {
            var fy = y0 + y;
            if (fy < 0 || fy >= _height) continue;
            for (var x = 0; x < pw; x++)
            {
                var fx = x0 + x;
                if (fx < 0 || fx >= _width) continue;
                var cover = CornerCover(x, y, pw, ph, radius);
                if (cover <= 0) continue;
                var k = (int)(a * cover);
                int s = (y * pw + x) * 4, d = (fy * _width + fx) * 4;
                for (var c = 0; c < 3; c++) frame[d + c] = (byte)((popup[s + c] * k + frame[d + c] * (256 - k)) >> 8);
            }
        }
    }

    /// <summary>How much of a pixel lies inside the window's rounded corner (Windows 11 windows are rounded).</summary>
    private static double CornerCover(int x, int y, int w, int h, int r)
    {
        var cx = x < r ? r : x >= w - r ? w - r - 1 : x;
        var cy = y < r ? r : y >= h - r ? h - r - 1 : y;
        if (cx == x || cy == y) return 1;
        var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
        return Math.Clamp(r - d + .5, 0, 1);
    }

    private void DrawShadow(byte[] frame, byte[] shadow, int sw, int sh, int x0, int y0, double alpha)
    {
        for (var y = 0; y < sh; y++)
        {
            var fy = y0 + y;
            if (fy < 0 || fy >= _height) continue;
            for (var x = 0; x < sw; x++)
            {
                var fx = x0 + x;
                if (fx < 0 || fx >= _width) continue;
                var k = (int)(shadow[y * sw + x] * alpha);
                if (k == 0) continue;
                var d = (fy * _width + fx) * 4;
                for (var c = 0; c < 3; c++) frame[d + c] = (byte)(frame[d + c] * (256 - k) >> 8);
            }
        }
    }

    private void DrawRing(byte[] frame, Point at, double k)
    {
        var radius = (6 + 22 * Ease(k)) * _scale;
        var width = 2.2 * _scale;
        var strength = (1 - k) * .55;
        var box = (int)(radius + width + 2);
        for (var y = -box; y <= box; y++)
        for (var x = -box; x <= box; x++)
        {
            int fx = (int)at.X + x, fy = (int)at.Y + y;
            if (fx < 0 || fy < 0 || fx >= _width || fy >= _height) continue;
            var cover = Math.Clamp(width / 2 - Math.Abs(Math.Sqrt(x * x + y * y) - radius) + .5, 0, 1) * strength;
            if (cover <= 0) continue;
            var d = (fy * _width + fx) * 4;
            // A warm accent ring (#ee7a0c), like the app's own buttons.
            frame[d] = (byte)(frame[d] + (0x0c - frame[d]) * cover);
            frame[d + 1] = (byte)(frame[d + 1] + (0x7a - frame[d + 1]) * cover);
            frame[d + 2] = (byte)(frame[d + 2] + (0xee - frame[d + 2]) * cover);
        }
    }

    private void DrawArrow(byte[] frame, int x0, int y0, double alpha)
    {
        for (var y = 0; y < _arrowH; y++)
        for (var x = 0; x < _arrowW; x++)
        {
            int fx = x0 + x - 2, fy = y0 + y - 2;
            if (fx < 0 || fy < 0 || fx >= _width || fy >= _height) continue;
            int s = (y * _arrowW + x) * 4, d = (fy * _width + fx) * 4;
            var k = _arrow[s + 3] * alpha / 255;
            if (k <= 0) continue;
            for (var c = 0; c < 3; c++) frame[d + c] = (byte)(_arrow[s + c] * alpha + frame[d + c] * (1 - k)); // premultiplied
        }
    }

    // ───────── sprites, drawn once with WPF ─────────

    private static (byte[] Pixels, int Width, int Height) RenderArrow(double scale)
    {
        var size = 1.25 * scale; // a little larger than the system pointer, so it reads in a scaled-down video
        var arrow = Geometry.Parse("M 2,2 L 2,19 L 6.2,15 L 9.2,21.6 L 11.8,20.4 L 8.9,14 L 14.6,14 Z");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size, size));
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null, Geometry.Parse("M 3,3.5 L 3,20.5 L 7.2,16.5 L 10.2,23.1 L 12.8,21.9 L 9.9,15.5 L 15.6,15.5 Z"));
            dc.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 1.1) { LineJoin = PenLineJoin.Round }, arrow);
        }
        int w = (int)Math.Ceiling(19 * size), h = (int)Math.Ceiling(26 * size);
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[w * h * 4];
        bitmap.CopyPixels(pixels, w * 4, 0);
        return (pixels, w, h);
    }

    private byte[] RenderShadow(int w, int h)
    {
        var host = new Rectangle
        {
            Width = w, Height = h, Fill = Brushes.Black, RadiusX = 8 * _scale, RadiusY = 8 * _scale,
            Effect = new DropShadowEffect { BlurRadius = 46 * _scale, ShadowDepth = 10 * _scale, Direction = 270, Opacity = .42, Color = Colors.Black },
        };
        var canvas = new System.Windows.Controls.Canvas { Width = w + 2 * Pad, Height = h + 2 * Pad };
        System.Windows.Controls.Canvas.SetLeft(host, Pad);
        System.Windows.Controls.Canvas.SetTop(host, Pad);
        canvas.Children.Add(host);
        canvas.Measure(new Size(canvas.Width, canvas.Height));
        canvas.Arrange(new Rect(0, 0, canvas.Width, canvas.Height));
        var bitmap = new RenderTargetBitmap(w + 2 * Pad, h + 2 * Pad, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var pixels = new byte[(w + 2 * Pad) * (h + 2 * Pad) * 4];
        bitmap.CopyPixels(pixels, (w + 2 * Pad) * 4, 0);
        var alpha = new byte[(w + 2 * Pad) * (h + 2 * Pad)];
        for (var i = 0; i < alpha.Length; i++) alpha[i] = pixels[i * 4 + 3];
        // Only the shadow around the window: under the window itself it would dim the window while it fades in.
        var r = (int)(8 * _scale);
        for (var y = Pad + r; y < Pad + h - r; y++)
            Array.Clear(alpha, y * (w + 2 * Pad) + Pad, w);
        for (var y = Pad; y < Pad + h; y++)
            Array.Clear(alpha, y * (w + 2 * Pad) + Pad + r, w - 2 * r);
        return alpha;
    }

    // ───────── Win32 ─────────

    private const uint PW_RENDERFULLCONTENT = 2;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    [LibraryImport("dwmapi.dll")] private static partial int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect32 value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32 { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out Rect32 rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [LibraryImport("user32.dll")] private static partial IntPtr GetDC(IntPtr hwnd);
    [LibraryImport("user32.dll")] private static partial int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [LibraryImport("gdi32.dll")] private static partial IntPtr CreateCompatibleDC(IntPtr hdc);
    [LibraryImport("gdi32.dll")] private static partial IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [LibraryImport("gdi32.dll")] private static partial IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr hdc);
    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(IntPtr hdc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);
}
