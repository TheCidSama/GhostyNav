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
    private readonly double _glowStrength;
    private ImageSource? _glowFor; // the cover the glow was made from
    private readonly Border _glow;
    private readonly bool _sharedGlow;
    private double _glowOpacity;

    /// <param name="glowHost">
    /// Where to paint the cover's glow instead of this card's own rectangle: the notch, so the colour
    /// fills its whole shape, corners and shoulders included. Shown only while the card is active.
    /// </param>
    public MediaCard(MediaService media, bool chromeless = false, Border? glowHost = null)
    {
        InitializeComponent();
        _media = media;
        _glow = glowHost ?? Glow;
        _sharedGlow = glowHost != null;
        if (chromeless)
        {
            // Inside the peek the island itself is the card.
            Root.Background = Brushes.Transparent;
            Body.Margin = new Thickness(16, 12, 16, 10);
            SourceRow.Visibility = Visibility.Collapsed;
        }
        // On the black island the halo can be stronger than on a card.
        _glowStrength = chromeless ? 0.8 : 0.6;
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
            RenderGlow(); // a glow on the notch goes with the card
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
        RenderGlow();

        PlayPause.Content = _media.IsPlaying ? "\uE769" : "\uE768";
        RenderProgress();
    }

    /// <summary>The cover's colours, stretched smoothly into a soft halo; it fades across when the song changes.</summary>
    private void RenderGlow()
    {
        bool newCover = !ReferenceEquals(_glowFor, _media.ArtGlow);
        if (newCover)
        {
            _glowFor = _media.ArtGlow;
            if (_media.ArtGlow is { } glow)
            {
                var brush = new ImageBrush(glow) { Stretch = Stretch.Fill };
                RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality); // a smooth blend, not blocks
                brush.Freeze();
                _glow.Background = brush;
            }
        }

        double target = _glowFor != null && (_active || !_sharedGlow) ? _glowStrength : 0;
        if (!newCover && target == _glowOpacity) return;
        // A new cover fades in from nothing; otherwise the glow fades from where it is.
        var fade = target > 0 && newCover
            ? new System.Windows.Media.Animation.DoubleAnimation(0, target, TimeSpan.FromMilliseconds(450))
            : new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(target > 0 ? 300 : 160));
        _glow.BeginAnimation(OpacityProperty, fade);
        _glowOpacity = target;
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
