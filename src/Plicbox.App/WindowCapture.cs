using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Plicbox.App;

/// <summary>Dev helper: saves a PNG of the window including WebView2 content (PrintWindow with PW_RENDERFULLCONTENT).</summary>
internal static partial class WindowCapture
{
    public static void SavePng(Window window, string path) => SavePng(new WindowInteropHelper(window).Handle, path);

    public static void SavePng(IntPtr hwnd, string path)
    {
        GetWindowRect(hwnd, out var r);
        int width = r.Right - r.Left, height = r.Bottom - r.Top;

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var old = SelectObject(memDc, bitmap);
        try
        {
            PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT);
            SelectObject(memDc, old);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgr24, null, 0)));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var file = File.Create(path);
            encoder.Save(file);
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>What's on screen around a window's bottom-right corner (popups included), plus where its edges are.</summary>
    public static string SaveCornerPng(IntPtr hwnd, string path, int size = 160)
    {
        GetWindowRect(hwnd, out var r);
        DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<Rect>());
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, size, size);
        var old = SelectObject(memDc, bitmap);
        try
        {
            BitBlt(memDc, 0, 0, size, size, screenDc, r.Right - size + 20, r.Bottom - size + 20, SRCCOPY);
            SelectObject(memDc, old);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgr24, null, 0)));
            using var file = File.Create(path);
            encoder.Save(file);
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
        return $"window {r.Left},{r.Top} → {r.Right},{r.Bottom}  frame {frame.Left},{frame.Top} → {frame.Right},{frame.Bottom}  (grab starts at {r.Right - size + 20},{r.Bottom - size + 20})";
    }

    private const uint PW_RENDERFULLCONTENT = 2, SRCCOPY = 0x00CC0020;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    [LibraryImport("dwmapi.dll")] private static partial int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out Rect rect);
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
}
