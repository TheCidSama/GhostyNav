using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GhostyNav.Services.Assistant;

public static class ScreenCapture
{
    private const int MaxSide = 1280; // plenty for Gemini to read text; keeps uploads small

    /// <summary>JPEG of a screen rectangle in physical pixels, or null if it can't be captured.</summary>
    public static byte[]? CaptureJpeg(Int32Rect area)
    {
        if (area.Width <= 0 || area.Height <= 0) return null;
        nint screen = GetDC(0), memory = CreateCompatibleDC(screen);
        nint bitmap = CreateCompatibleBitmap(screen, area.Width, area.Height);
        nint old = SelectObject(memory, bitmap);
        try
        {
            const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
            if (!BitBlt(memory, 0, 0, area.Width, area.Height, screen, area.X, area.Y, SRCCOPY | CAPTUREBLT)) return null;
            SelectObject(memory, old); // a bitmap can't be read while selected into a DC
            BitmapSource image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            double scale = Math.Min(1, (double)MaxSide / Math.Max(area.Width, area.Height));
            if (scale < 1) image = new TransformedBitmap(image, new ScaleTransform(scale, scale));
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        finally
        {
            SelectObject(memory, old);
            DeleteDC(memory);
            DeleteObject(bitmap);
            ReleaseDC(0, screen);
        }
    }

    /// <summary>
    /// Keeps a window out of screenshots and screen recordings (including our own captures),
    /// so the trail and the assistant's UI never end up in what's sent to Gemini.
    /// </summary>
    public static bool ExcludeFromCapture(Window window, bool exclude)
    {
        // For screen-recording a demo of the assistant: keep it visible (circled crops may then show the trail).
        if (Environment.GetEnvironmentVariable("GHOSTYNAV_SHOW_IN_CAPTURE") == "1") return false;
        const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
        var hwnd = new WindowInteropHelper(window).Handle;
        return hwnd != 0 && SetWindowDisplayAffinity(hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
