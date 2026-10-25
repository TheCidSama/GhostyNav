using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GhostyNav.Controls;

/// <summary>The slim volume / brightness bar that drops out of the notch, like the iPhone's.</summary>
public sealed class LevelPeek
{
    private readonly TextBlock _icon, _label;
    private readonly ScaleTransform _fill = new(0, 1);

    public FrameworkElement Root { get; }

    public LevelPeek(FrameworkElement resources)
    {
        _icon = new TextBlock
        {
            Style = (Style)resources.FindResource("Icon"), FontSize = 14, Width = 22,
            Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center
        };
        _label = new TextBlock
        {
            FontSize = 12, FontWeight = FontWeights.SemiBold, Width = 40, TextAlignment = TextAlignment.Right,
            Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
        };
        var bar = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(new Border { Background = (Brush)resources.FindResource("TrackBrush"), CornerRadius = new CornerRadius(3) });
        bar.Children.Add(new Border
        {
            Background = Brushes.White, CornerRadius = new CornerRadius(3),
            RenderTransformOrigin = new Point(0, 0.5), RenderTransform = _fill
        });

        var row = new DockPanel { Margin = new Thickness(22, 0, 22, 2), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_icon, Dock.Left);
        DockPanel.SetDock(_label, Dock.Right);
        row.Children.Add(_icon);
        row.Children.Add(_label);
        row.Children.Add(bar);
        Root = row;
    }

    public void Set(string glyph, double percent, string label)
    {
        _icon.Text = glyph;
        _label.Text = label;
        _fill.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(Math.Clamp(percent / 100, 0, 1), TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }
}
