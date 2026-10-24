using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GhostyNav.Services;

/// <summary>
/// App and program icons, from the Windows shell, loaded one at a time on a background STA thread
/// (the shell expects one) and cached. The thread sleeps while there's nothing to load.
/// </summary>
public static class IconLoader
{
    private static readonly BlockingCollection<Action> Work = [];
    private static readonly Dictionary<string, Task<ImageSource?>> Cache = []; // touched on the UI thread only
    private static Thread? _thread;

    /// <param name="pixels">Size in device pixels.</param>
    public static Task<ImageSource?> ForAppAsync(string appId, int pixels) =>
        Get($"app|{appId}|{pixels}", () => Load(@"shell:AppsFolder\" + appId, pixels));

    /// <summary>The icon of a running process's program; <paramref name="key"/> groups processes that share one.</summary>
    public static Task<ImageSource?> ForProcessAsync(int pid, string key, int pixels) =>
        Get($"proc|{key}|{pixels}", () => ProcessPath(pid) is { } path ? Load(path, pixels) : null);

    /// <summary>The icon if it's already loaded, without waiting.</summary>
    public static ImageSource? Cached(Task<ImageSource?> task) => task.IsCompletedSuccessfully ? task.Result : null;

    private static Task<ImageSource?> Get(string key, Func<ImageSource?> load)
    {
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Cache[key] = tcs.Task;
        EnsureThread();
        Work.Add(() =>
        {
            try { tcs.SetResult(load()); }
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
            {
                tcs.SetResult(null); // no icon: the caller shows a letter instead
            }
        });
        return tcs.Task;
    }

    private static void EnsureThread()
    {
        if (_thread != null) return;
        _thread = new Thread(() => { foreach (var job in Work.GetConsumingEnumerable()) job(); })
        {
            IsBackground = true,
            Name = "IconLoader"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private static ImageSource? Load(string parsingName, int pixels)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(parsingName, 0, ref iid, out var factory) != 0 || factory == null) return null;
        try
        {
            const int SIIGBF_ICONONLY = 0x4;
            if (factory.GetImage(new SIZE { cx = pixels, cy = pixels }, SIIGBF_ICONONLY, out nint bitmap) != 0) return null;
            try { return ToBitmapSource(bitmap); }
            finally { DeleteObject(bitmap); }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>
    /// Copies the shell's 32-bit bitmap out top-down. Interop's own HBITMAP conversion would drop
    /// the alpha channel and leave icons on black squares.
    /// </summary>
    private static BitmapSource? ToBitmapSource(nint bitmap)
    {
        var info = new BITMAP();
        if (GetObject(bitmap, Marshal.SizeOf<BITMAP>(), ref info) == 0 || info.bmWidth <= 0 || info.bmHeight <= 0) return null;
        int w = info.bmWidth, h = info.bmHeight;
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32
        };
        var pixels = new byte[w * h * 4];
        nint screen = GetDC(0);
        try
        {
            if (GetDIBits(screen, bitmap, 0, (uint)h, pixels, ref header, 0) == 0) return null;
        }
        finally
        {
            ReleaseDC(0, screen);
        }

        // Old 24-bit icons arrive with no alpha at all: make them opaque rather than invisible.
        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
        source.Freeze();
        return source;
    }

    private static string? ProcessPath(int pid)
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        nint process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return null; // protected or gone
        try
        {
            var path = new StringBuilder(1024);
            int size = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out nint bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public nint bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    [DllImport("gdi32.dll")] private static extern int GetObject(nint obj, int size, ref BITMAP bitmap);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint process, int flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
