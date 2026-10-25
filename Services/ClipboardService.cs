using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GhostyNav.Services;

public enum ClipKind { Text, Link, Image, Files }

public sealed class ClipItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; }
    public string Text { get; set; } = "";
    /// <summary>File name (inside the clips folder) of a saved PNG, for images.</summary>
    public string? ImageFile { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime Copied { get; set; } = DateTime.Now;
    public bool Pinned { get; set; }
}

/// <summary>
/// Clipboard history. Windows notifies us of every change (WM_CLIPBOARDUPDATE); nothing is polled.
/// Everything is encrypted for the current Windows account (see <see cref="Protect"/>): the list in
/// clipboard.dat, images as encrypted PNGs in clips\.
/// </summary>
public sealed class ClipboardService
{
    private const int MaxItems = 60;
    private const string FileName = "clipboard.dat", Purpose = "clipboard", ImagePurpose = "clipboard-image";
    private const string PlainFileName = "clipboard.json"; // earlier versions, unencrypted
    private static readonly string ImageFolder = Path.Combine(Store.Folder, "clips");

    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public List<ClipItem> Items { get; }
    public event Action? Changed;

    public ClipboardService(Dispatcher ui)
    {
        _ui = ui;
        Items = Store.LoadProtected<List<ClipItem>>(FileName, Purpose) ?? [];
        EncryptPlainHistory();
        // Copy bursts (Office writes many formats one after another) collapse into one read.
        _debounce.Tick += (_, _) => { _debounce.Stop(); Capture(); };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Store.SaveProtected(FileName, Items, Purpose); };
    }

    /// <summary>Moves history an earlier version saved in plain files into encrypted ones.</summary>
    private void EncryptPlainHistory()
    {
        string plain = Path.Combine(Store.Folder, PlainFileName);
        if (File.Exists(plain))
        {
            if (Items.Count == 0) Items.AddRange(Store.Load<List<ClipItem>>(PlainFileName));
            Store.SaveProtected(FileName, Items, Purpose);
            if (File.Exists(Path.Combine(Store.Folder, FileName))) File.Delete(plain);
        }
        bool changed = false;
        foreach (var item in Items.Where(i => i.ImageFile?.EndsWith(".png", StringComparison.OrdinalIgnoreCase) == true))
        {
            string png = Path.Combine(ImageFolder, item.ImageFile!);
            try
            {
                if (File.Exists(png))
                {
                    File.WriteAllBytes(Path.Combine(ImageFolder, item.Id + ".bin"), Protect.EncryptBytes(File.ReadAllBytes(png), ImagePurpose));
                    File.Delete(png);
                }
                item.ImageFile = item.Id + ".bin";
                changed = true;
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Couldn't encrypt {png}: {ex.Message}");
            }
        }
        if (changed) Store.SaveProtected(FileName, Items, Purpose);

        // Unencrypted leftovers of items removed while their preview was open.
        if (!Directory.Exists(ImageFolder)) return;
        var stillPlain = Items.Select(i => i.ImageFile).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var png in Directory.GetFiles(ImageFolder, "*.png").Where(p => !stillPlain.Contains(Path.GetFileName(p))))
        {
            try { File.Delete(png); }
            catch (IOException) { }
        }
    }

    public void OnClipboardUpdate()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void Capture()
    {
        IDataObject? data = Retry(Clipboard.GetDataObject);
        if (data == null || ShouldIgnore(data)) return;

        try
        {
            if (data.GetDataPresent(DataFormats.UnicodeText))
            {
                string text = data.GetData(DataFormats.UnicodeText) as string ?? "";
                if (string.IsNullOrWhiteSpace(text)) return;
                bool isLink = Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
                              && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                              && !text.Trim().Contains(' ');
                AddOrBump(isLink ? ClipKind.Link : ClipKind.Text, isLink ? text.Trim() : text);
            }
            else if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
            {
                AddOrBump(ClipKind.Files, string.Join("\n", files));
            }
            else if (data.GetDataPresent(DataFormats.Bitmap) && Retry(Clipboard.GetImage) is BitmapSource image)
            {
                AddImage(image);
            }
        }
        catch (Exception ex)
        {
            // Another app may hold the clipboard or have put malformed data on it.
            System.Diagnostics.Debug.WriteLine($"Clipboard read failed: {ex.Message}");
        }
    }

    /// <summary>Respect apps (password managers) that ask clipboard history not to record a copy.</summary>
    private static bool ShouldIgnore(IDataObject data)
    {
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") ||
            data.GetDataPresent("Clipboard Viewer Ignore"))
            return true;
        foreach (string format in new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
        {
            if (data.GetDataPresent(format) && data.GetData(format) is MemoryStream ms && ms.Length >= 4)
            {
                var buffer = new byte[4];
                ms.Read(buffer, 0, 4);
                if (BitConverter.ToInt32(buffer) == 0) return true;
            }
        }
        return false;
    }

    private void AddOrBump(ClipKind kind, string text)
    {
        var existing = Items.FirstOrDefault(i => i.Kind == kind && i.Text == text);
        if (existing != null)
        {
            Items.Remove(existing);
            existing.Copied = DateTime.Now;
            Items.Insert(0, existing);
        }
        else
        {
            Items.Insert(0, new ClipItem { Kind = kind, Text = text });
        }
        Commit();
    }

    private void AddImage(BitmapSource image)
    {
        var item = new ClipItem { Kind = ClipKind.Image, Width = image.PixelWidth, Height = image.PixelHeight };
        item.ImageFile = item.Id + ".bin";
        Directory.CreateDirectory(ImageFolder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var png = new MemoryStream())
        {
            encoder.Save(png);
            File.WriteAllBytes(Path.Combine(ImageFolder, item.ImageFile), Protect.EncryptBytes(png.ToArray(), ImagePurpose));
        }
        Items.Insert(0, item);
        Commit();
    }

    private void Commit()
    {
        // Pinned items never expire; the oldest unpinned ones do.
        while (Items.Count(i => !i.Pinned) > MaxItems)
        {
            var oldest = Items.Last(i => !i.Pinned);
            Remove(oldest, notify: false);
        }
        _saveTimer.Stop();
        _saveTimer.Start();
        Changed?.Invoke();
    }

    public void TogglePin(ClipItem item)
    {
        item.Pinned = !item.Pinned;
        Commit();
    }

    public void Remove(ClipItem item, bool notify = true)
    {
        Items.Remove(item);
        if (item.ImageFile != null)
        {
            try { File.Delete(Path.Combine(ImageFolder, item.ImageFile)); }
            catch (IOException) { /* still in use by a preview; harmless leftover */ }
        }
        if (notify) Commit();
    }

    public void ClearUnpinned()
    {
        foreach (var item in Items.Where(i => !i.Pinned).ToList()) Remove(item, notify: false);
        Commit();
    }

    /// <summary>Puts an item back on the clipboard. Our own listener then bumps it to the top.</summary>
    public bool CopyToClipboard(ClipItem item)
    {
        try
        {
            switch (item.Kind)
            {
                case ClipKind.Image:
                    var image = LoadImage(item, 0);
                    return image != null && Retry(() => { Clipboard.SetImage(image); return true; });
                case ClipKind.Files:
                    var list = new System.Collections.Specialized.StringCollection();
                    list.AddRange(item.Text.Split('\n'));
                    return Retry(() => { Clipboard.SetFileDropList(list); return true; });
                default:
                    return Retry(() => { Clipboard.SetText(item.Text); return true; });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Clipboard write failed: {ex.Message}");
            return false;
        }
    }

    /// <param name="decodeWidth">0 for full size.</param>
    public static BitmapSource? LoadImage(ClipItem item, int decodeWidth)
    {
        if (item.ImageFile == null) return null;
        string path = Path.Combine(ImageFolder, item.ImageFile);
        if (!File.Exists(path)) return null;
        byte[]? png;
        try { png = Protect.DecryptBytes(File.ReadAllBytes(path), ImagePurpose); }
        catch (IOException) { return null; }
        if (png == null) return null;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
        bmp.StreamSource = new MemoryStream(png);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    // The clipboard is a shared lock; another process holding it makes calls throw briefly.
    private static T? Retry<T>(Func<T?> action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (COMException) when (attempt < 4) { Thread.Sleep(30); }
            catch (COMException) { return default; }
        }
    }
}
