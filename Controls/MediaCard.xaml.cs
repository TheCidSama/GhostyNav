using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GhostyNav.Services;

namespace GhostyNav.Controls;

/// <summary>Now-playing card with transport controls. Updates only while <see cref="SetActive"/> is on.</summary>
public partial class MediaCard : UserControl
{
    private readonly MediaService _media;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _active;
    private double? _seekFraction; // set while the user drags the seek bar

    public MediaCard(MediaService media, bool chromeless = false)
    {
        InitializeComponent();
        _media = media;
        if (chromeless)
        {
            // Inside the peek the island itself is the card.
            Root.Background = Brushes.Transparent;
            Root.Padding = new Thickness(16, 12, 16, 10);
            SourceRow.Visibility = Visibility.Collapsed;
        }
        _media.Changed += () => { if (_active) Render(); };
        _tick.Tick += (_, _) => RenderProgress();
        Seekbar.SizeChanged += (_, _) => RenderProgress();
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (active)
        {
            Render();
            _tick.Start();
        }
        else
        {
            _tick.Stop();
            Progress.SetMoving(false);
        }
    }

    private void Render()
    {
        bool has = _media.HasSession;
        Player.Visibility = has ? Visibility.Visible : Visibility.Hidden;
        Empty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        Progress.SetMoving(_active && has && _media.IsPlaying);
        if (!has) return;

        TitleText.Text = string.IsNullOrEmpty(_media.Title) ? "Unknown title" : _media.Title;
        ArtistText.Text = _media.Artist;
        Source.Text = _media.AppName;

        if (_media.Art != null)
        {
            Art.Background = new ImageBrush(_media.Art) { Stretch = Stretch.UniformToFill };
            ArtPlaceholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            Art.Background = (Brush)FindResource("TrackBrush");
            ArtPlaceholder.Visibility = Visibility.Visible;
        }

        PlayPause.Content = _media.IsPlaying ? "\uE769" : "\uE768";
        RenderProgress();
    }

    private void RenderProgress()
    {
        var duration = _media.Duration;
        bool known = duration > TimeSpan.Zero;
        double fraction = _seekFraction ?? (known ? Math.Clamp(_media.Position / duration, 0, 1) : 0);

        Elapsed.Text = known ? Format(duration * fraction) : "";
        Total.Text = known ? Format(duration) : "";
        bool canSeek = known && _media.CanSeek;
        Seekbar.Cursor = canSeek ? Cursors.Hand : null;

        double width = Seekbar.ActualWidth * fraction;
        Progress.Value = fraction;
        Knob.Margin = new Thickness(Math.Clamp(width - Knob.Width / 2, 0, Math.Max(0, Seekbar.ActualWidth - Knob.Width)), 0, 0, 0);
        Knob.Visibility = canSeek && (Seekbar.IsMouseOver || _seekFraction != null) ? Visibility.Visible : Visibility.Hidden;
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    // ---------- Seeking: the bar follows the pointer while dragging; the player seeks on release ----------

    private double PointerFraction(MouseEventArgs e) =>
        Seekbar.ActualWidth > 0 ? Math.Clamp(e.GetPosition(Seekbar).X / Seekbar.ActualWidth, 0, 1) : 0;

    private void OnSeekStart(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // never let a press on the bar open the island or start a swipe
        if (!_media.CanSeek || _media.Duration <= TimeSpan.Zero) return;
        _seekFraction = PointerFraction(e);
        Seekbar.CaptureMouse();
        RenderProgress();
    }

    private void OnSeekMove(object sender, MouseEventArgs e)
    {
        if (_seekFraction == null || !Seekbar.IsMouseCaptured) return;
        _seekFraction = PointerFraction(e);
        RenderProgress();
    }

    private async void OnSeekEnd(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_seekFraction == null) return;
        double fraction = PointerFraction(e);
        _seekFraction = null;
        Seekbar.ReleaseMouseCapture();
        await _media.SeekAsync(_media.Duration * fraction);
        RenderProgress();
    }

    private void OnSeekLost(object sender, MouseEventArgs e)
    {
        if (_seekFraction == null) return;
        _seekFraction = null; // capture taken away mid-drag: cancel without seeking
        RenderProgress();
    }

    private void OnSeekHover(object sender, MouseEventArgs e) => RenderProgress();

    private async void OnPlayPause(object sender, RoutedEventArgs e)
    {
        Fx.Ripple(PlayPause, Color.FromRgb(0xED, 0xE6, 0xD6));
        await _media.PlayPauseAsync();
    }
    private async void OnNext(object sender, RoutedEventArgs e) => await _media.NextAsync();
    private async void OnPrevious(object sender, RoutedEventArgs e) => await _media.PreviousAsync();
}
