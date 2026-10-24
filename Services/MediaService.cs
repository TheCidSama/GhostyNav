using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace GhostyNav.Services;

/// <summary>
/// "Now playing" from any app that talks to Windows media controls (Spotify, browsers,
/// Apple Music, VLC...). Event-driven: nothing is polled.
/// </summary>
public sealed class MediaService
{
    private readonly Dispatcher _ui;
    private SessionManager? _manager;
    private Session? _session;
    private readonly List<Session> _watched = [];
    private bool _refreshQueued;
    private string _artKey = "";

    private TimeSpan _position;
    private TimeSpan _timelineStart;
    private DateTimeOffset _positionStamp;
    private double _rate = 1;

    public event Action? Changed;
    /// <summary>A different song started (not just play/pause or a timeline update).</summary>
    public event Action? TrackChanged;

    public bool HasSession => _session != null;
    public bool IsPlaying { get; private set; }
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string AppName { get; private set; } = "";
    public BitmapSource? Art { get; private set; }
    public Color Accent { get; private set; } = Colors.White;
    /// <summary>The cover reduced to a tiny colour grid, for a soft halo when stretched (null without a cover).</summary>
    public BitmapSource? ArtGlow { get; private set; }
    public TimeSpan Duration { get; private set; }
    public bool CanSeek { get; private set; }

    /// <summary>Position extrapolated from the last timeline report, since players only report occasionally.</summary>
    public TimeSpan Position
    {
        get
        {
            var p = _position;
            if (IsPlaying) p += TimeSpan.FromTicks((long)((DateTimeOffset.Now - _positionStamp).Ticks * _rate));
            if (p < TimeSpan.Zero) p = TimeSpan.Zero;
            return Duration > TimeSpan.Zero && p > Duration ? Duration : p;
        }
    }

    public MediaService(Dispatcher ui) => _ui = ui;

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await SessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Media sessions unavailable: {ex.Message}");
            return;
        }
        _manager.SessionsChanged += (_, _) => QueueRefresh();
        _manager.CurrentSessionChanged += (_, _) => QueueRefresh();
        QueueRefresh();
    }

    public async Task PlayPauseAsync()
    {
        if (!await Run(s => s.TryTogglePlayPauseAsync().AsTask())) return;
        // Show the new state right away; the player's own event confirms it a moment later.
        _position = Position;
        _positionStamp = DateTimeOffset.Now;
        IsPlaying = !IsPlaying;
        Changed?.Invoke();
    }

    public Task NextAsync() => Run(s => s.TrySkipNextAsync().AsTask());
    public Task PreviousAsync() => Run(s => s.TrySkipPreviousAsync().AsTask());

    public async Task SeekAsync(TimeSpan to)
    {
        // Players expect a position on their own timeline, which may not start at zero.
        if (!await Run(s => s.TryChangePlaybackPositionAsync((_timelineStart + to).Ticks).AsTask())) return;
        _position = to;
        _positionStamp = DateTimeOffset.Now;
        Changed?.Invoke();
    }

    /// <returns>Whether the player accepted the command.</returns>
    private async Task<bool> Run(Func<Session, Task<bool>> action)
    {
        if (_session == null) return false;
        try
        {
            return await action(_session);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Media command failed: {ex.Message}");
            return false;
        }
    }

    // WinRT events arrive on worker threads and in bursts; coalesce them into one UI-thread refresh.
    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        _ui.InvokeAsync(async () =>
        {
            await Task.Delay(80);
            _refreshQueued = false;
            await RefreshAsync();
        });
    }

    private void WatchSessions(IReadOnlyList<Session> sessions)
    {
        foreach (var s in _watched)
        {
            s.MediaPropertiesChanged -= OnSessionEvent;
            s.PlaybackInfoChanged -= OnSessionEvent;
            s.TimelinePropertiesChanged -= OnSessionEvent;
        }
        _watched.Clear();
        foreach (var s in sessions)
        {
            s.MediaPropertiesChanged += OnSessionEvent;
            s.PlaybackInfoChanged += OnSessionEvent;
            s.TimelinePropertiesChanged += OnSessionEvent;
            _watched.Add(s);
        }
    }

    private void OnSessionEvent(Session sender, object args) => QueueRefresh();

    private async Task RefreshAsync()
    {
        if (_manager == null) return;
        bool trackChanged = false;
        try
        {
            var sessions = _manager.GetSessions();
            if (sessions.Count != _watched.Count || sessions.Any(s => !_watched.Contains(s)))
                WatchSessions(sessions);

            // Prefer whatever is actually playing; fall back to Windows' notion of "current".
            _session = sessions.FirstOrDefault(s => s.GetPlaybackInfo()?.PlaybackStatus == PlaybackStatus.Playing)
                       ?? _manager.GetCurrentSession()
                       ?? sessions.FirstOrDefault();

            if (_session == null)
            {
                IsPlaying = false;
                Title = Artist = AppName = "";
                Art = null;
                ArtGlow = null;
                _artKey = "";
                Changed?.Invoke();
                return;
            }

            var playback = _session.GetPlaybackInfo();
            IsPlaying = playback?.PlaybackStatus == PlaybackStatus.Playing;
            _rate = playback?.PlaybackRate is double r && r > 0 ? r : 1;
            CanSeek = playback?.Controls.IsPlaybackPositionEnabled == true;

            var timeline = _session.GetTimelineProperties();
            _timelineStart = timeline.StartTime;
            Duration = timeline.EndTime - timeline.StartTime;
            _position = timeline.Position - timeline.StartTime;
            _positionStamp = timeline.LastUpdatedTime;
            // Some players never set LastUpdatedTime; don't extrapolate from year 1601.
            if (_positionStamp.Year < 2000) _positionStamp = DateTimeOffset.Now;

            AppName = PrettyAppName(_session.SourceAppUserModelId);

            var props = await _session.TryGetMediaPropertiesAsync();
            Title = props?.Title ?? "";
            Artist = props?.Artist ?? "";

            string key = $"{AppName}|{Title}|{Artist}";
            if (key != _artKey)
            {
                bool hadTrack = _artKey.Length > 0;
                _artKey = key;
                Art = props?.Thumbnail != null ? await LoadArtAsync(props.Thumbnail) : null;
                Accent = Art != null ? DominantColor(Art) : Colors.White;
                ArtGlow = Art != null ? GlowFrom(Art) : null;
                if (hadTrack && Title.Length > 0) trackChanged = true;
            }
        }
        catch (Exception ex)
        {
            // Sessions can vanish mid-query when the player closes.
            System.Diagnostics.Debug.WriteLine($"Media refresh failed: {ex.Message}");
        }
        Changed?.Invoke();
        if (trackChanged) TrackChanged?.Invoke();
    }

    private static async Task<BitmapSource?> LoadArtAsync(Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        try
        {
            using var winStream = await reference.OpenReadAsync();
            using var source = winStream.AsStreamForRead();
            var buffer = new MemoryStream();
            await source.CopyToAsync(buffer);
            buffer.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 160; // decode small: full-size covers can be several MB in memory
            bmp.StreamSource = buffer;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null; // Art is optional; a broken thumbnail just shows the placeholder.
        }
    }

    /// <summary>
    /// A 6×5 version of the cover with livelier colours, kept within a band of brightness so the
    /// halo is never black or glaring. Stretched with smooth scaling it becomes a soft gradient.
    /// </summary>
    private static BitmapSource GlowFrom(BitmapSource art)
    {
        var small = new FormatConvertedBitmap(new TransformedBitmap(art,
            new ScaleTransform(6.0 / art.PixelWidth, 5.0 / art.PixelHeight)), PixelFormats.Bgra32, null, 0);
        int w = small.PixelWidth, h = small.PixelHeight;
        var px = new byte[w * h * 4];
        small.CopyPixels(px, w * 4, 0);
        for (int i = 0; i < px.Length; i += 4)
        {
            double b = px[i], g = px[i + 1], r = px[i + 2];
            double grey = (r + g + b) / 3;
            // Away from grey: a little more saturated than the cover itself.
            r = grey + (r - grey) * 1.35; g = grey + (g - grey) * 1.35; b = grey + (b - grey) * 1.35;
            double max = Math.Max(1, Math.Max(r, Math.Max(g, b)));
            double scale = Math.Clamp(max, 90, 200) / max;
            px[i] = (byte)Math.Clamp(b * scale, 0, 255);
            px[i + 1] = (byte)Math.Clamp(g * scale, 0, 255);
            px[i + 2] = (byte)Math.Clamp(r * scale, 0, 255);
            px[i + 3] = 255;
        }
        var glow = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        glow.Freeze();
        return glow;
    }

    /// <summary>Average colour weighted towards saturated pixels, so dark/grey borders don't win.</summary>
    private static Color DominantColor(BitmapSource art)
    {
        var small = new FormatConvertedBitmap(new TransformedBitmap(art,
            new ScaleTransform(24.0 / art.PixelWidth, 24.0 / art.PixelHeight)), PixelFormats.Bgra32, null, 0);
        int w = small.PixelWidth, h = small.PixelHeight;
        var px = new byte[w * h * 4];
        small.CopyPixels(px, w * 4, 0);

        double r = 0, g = 0, b = 0, total = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            double bb = px[i], gg = px[i + 1], rr = px[i + 2];
            double max = Math.Max(rr, Math.Max(gg, bb)), min = Math.Min(rr, Math.Min(gg, bb));
            double weight = (max - min) / 255.0 * (max / 255.0) + 0.02;
            r += rr * weight; g += gg * weight; b += bb * weight; total += weight;
        }
        r /= total; g /= total; b /= total;

        // Lift dark results so the colour stays readable on the black island.
        double m = Math.Max(r, Math.Max(g, b));
        if (m < 170 && m > 0)
        {
            double k = 170 / m;
            r *= k; g *= k; b *= k;
        }
        return Color.FromRgb((byte)Math.Min(255, r), (byte)Math.Min(255, g), (byte)Math.Min(255, b));
    }

    private static string PrettyAppName(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "";
        if (aumid.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (aumid.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (aumid.Contains("msedge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (aumid.Contains("firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (aumid.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase)) return "Apple Music";

        string name = aumid;
        int bang = name.LastIndexOf('!');
        if (bang >= 0) name = name[(bang + 1)..];
        name = Path.GetFileNameWithoutExtension(name);
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : aumid;
    }
}
