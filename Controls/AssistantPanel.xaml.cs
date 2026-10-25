using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using GhostyNav.Services.Assistant;

namespace GhostyNav.Controls;

/// <summary>The transcript/result card under the notch. Sets the ghost's mood to match.</summary>
public partial class AssistantPanel : UserControl
{
    private bool _shown;

    /// <summary>The ghost in the notch, whose mood follows what the card shows.</summary>
    public Mascot? Ghost { get; set; }

    /// <summary>The close button was clicked.</summary>
    public event Action? CloseRequested;

    public AssistantPanel()
    {
        InitializeComponent();
        Card.MouseEnter += (_, _) => CloseButton.Visibility = Visibility.Visible;
        Card.MouseLeave += (_, _) => CloseButton.Visibility = Visibility.Hidden;
    }

    public bool IsPointerOver => Card.IsMouseOver;

    public void Show()
    {
        Reset();
        Visibility = Visibility.Visible;
        _shown = true;
        Ghost?.SetMood(MascotMood.Listening);

        // The card unfolds under the notch once it has started dropping down.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var delay = TimeSpan.FromMilliseconds(140);
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { BeginTime = delay });
        var unfold = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(360)) { BeginTime = delay, EasingFunction = ease };
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, unfold);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, unfold);
        CardShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(360)) { BeginTime = delay, EasingFunction = ease });
    }

    public void Hide(Action? done = null)
    {
        if (!_shown)
        {
            done?.Invoke();
            return;
        }
        _shown = false;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) =>
        {
            if (_shown) return; // shown again meanwhile
            Visibility = Visibility.Collapsed;
            done?.Invoke();
        };
        Card.BeginAnimation(OpacityProperty, fade);
        CardShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    private void Reset()
    {
        FinalRun.Text = InterimRun.Text = "";
        Placeholder.Visibility = Visibility.Visible;
        CircledRow.Visibility = Visibility.Collapsed;
        ResultArea.Visibility = Visibility.Collapsed;
        UpdateLayoutForTranscript();
        Results.Children.Clear();
        AnswerScroll.Visibility = CopyButton.Visibility = Visibility.Collapsed;
        StopDots();
    }

    public void SetTranscript(string final, string interim)
    {
        FinalRun.Text = final;
        InterimRun.Text = final.Length > 0 && interim.Length > 0 ? " " + interim : interim;
        bool listening = ResultArea.Visibility != Visibility.Visible;
        Placeholder.Visibility = !HasTranscript && listening ? Visibility.Visible : Visibility.Collapsed;
        UpdateLayoutForTranscript();
        TranscriptScroll.ScrollToEnd();
    }

    private bool HasTranscript => FinalRun.Text.Length + InterimRun.Text.Length > 0;

    // With nothing said, the result stands alone: no empty line above it, no divider.
    private void UpdateLayoutForTranscript()
    {
        TranscriptScroll.Visibility = HasTranscript ? Visibility.Visible : Visibility.Collapsed;
        bool above = HasTranscript || Placeholder.Visibility == Visibility.Visible || CircledRow.Visibility == Visibility.Visible;
        Divider.Visibility = above ? Visibility.Visible : Visibility.Collapsed;
        ResultArea.Margin = new Thickness(0, above ? 10 : 0, 0, 0);
    }

    public void SetCircled(int count)
    {
        CircledRow.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CircledText.Text = count == 1 ? "1 area circled" : $"{count} areas circled";
        UpdateLayoutForTranscript();
    }

    public void ShowThinking(string text = "Thinking")
    {
        Ghost?.SetMood(MascotMood.Thinking);
        Placeholder.Visibility = Visibility.Collapsed;
        UpdateLayoutForTranscript();
        ResultArea.Visibility = Visibility.Visible;
        Thinking.Visibility = Visibility.Visible;
        ThinkingText.Text = text;
        Results.Children.Clear();
        AnswerScroll.Visibility = CopyButton.Visibility = Visibility.Collapsed;
        StartDots();
    }

    public void ShowResults(IReadOnlyList<ActionResult> results)
    {
        PrepareResult(results.All(r => r.Ok) ? MascotMood.Happy : MascotMood.Sad);
        foreach (var result in results) Results.Children.Add(ResultRow(result));
    }

    public void ShowAnswer(string text)
    {
        PrepareResult(MascotMood.Happy);
        Answer.Text = text;
        AnswerScroll.Visibility = Visibility.Visible;
        AnswerScroll.ScrollToHome();
        CopyIcon.Text = "\uE8C8";
        CopyText.Text = "Copy";
        CopyButton.Visibility = Visibility.Visible;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Answer.Text);
            CopyIcon.Text = "\uE73E";
            CopyText.Text = "Copied";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            CopyText.Text = "Clipboard busy, try again";
        }
    }

    /// <summary>
    /// Asks the user to allow an action, with Go ahead and Cancel buttons. False when cancelled, when
    /// nobody answers within <paramref name="timeout"/>, or when the assistant is dismissed.
    /// </summary>
    public async Task<bool> ConfirmAsync(string question, string hint, TimeSpan timeout, CancellationToken ct)
    {
        PrepareResult(MascotMood.Thinking);
        Results.Children.Add(ResultRow(new ActionResult("\uE897", question)));
        Results.Children.Add(Hint(hint));

        var answer = new TaskCompletionSource<bool>();
        var lavender = (Brush)FindResource("Lavender");
        var yes = new Button
        {
            Style = (Style)FindResource("PrimaryButton"), Content = "Go ahead", Height = 26, Padding = new Thickness(14, 0, 14, 0),
            FontSize = 11.5, Background = lavender, Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x14, 0x3A))
        };
        var no = new Button
        {
            Style = (Style)FindResource("FlatButton"), Content = "Cancel", Height = 26, Padding = new Thickness(12, 0, 12, 0),
            FontSize = 11.5, Margin = new Thickness(6, 0, 0, 0)
        };
        yes.Click += (_, _) => answer.TrySetResult(true);
        no.Click += (_, _) => answer.TrySetResult(false);
        Results.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(32, 8, 0, 2), Children = { yes, no }
        });

        using var cancel = ct.Register(() => answer.TrySetResult(false));
        bool allowed = await Task.WhenAny(answer.Task, Task.Delay(timeout, CancellationToken.None)) == answer.Task && answer.Task.Result;
        yes.IsEnabled = no.IsEnabled = false;
        return allowed;
    }

    public void ShowError(string text, string? hint = null)
    {
        PrepareResult(MascotMood.Sad);
        Results.Children.Add(ResultRow(new ActionResult("\uE783", text, Ok: false)));
        if (hint != null) Results.Children.Add(Hint(hint));
    }

    /// <summary>Areas were circled but nothing was said: nothing is sent until the user says what to do.</summary>
    public void ShowWaiting(int areas, string chord)
    {
        PrepareResult(MascotMood.Listening);
        Results.Children.Add(ResultRow(new ActionResult("\uE8B9", areas == 1
            ? "What should I do with it?"
            : "What should I do with them?")));
        Results.Children.Add(Hint($"Hold {chord} and tell me. Nothing is sent until you do."));
    }

    private TextBlock Hint(string text) => new()
    {
        Text = text, FontSize = 11, Foreground = (Brush)FindResource("SubTextBrush"),
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(32, 2, 0, 0)
    };

    private void PrepareResult(MascotMood mood)
    {
        Ghost?.SetMood(mood);
        StopDots();
        Placeholder.Visibility = Visibility.Collapsed;
        UpdateLayoutForTranscript();
        Thinking.Visibility = Visibility.Collapsed;
        ResultArea.Visibility = Visibility.Visible;
        Results.Children.Clear();
        AnswerScroll.Visibility = CopyButton.Visibility = Visibility.Collapsed;
    }

    private UIElement ResultRow(ActionResult result)
    {
        var lavender = (Brush)FindResource("Lavender");
        var icon = new Grid { Width = 24, Height = 24, Margin = new Thickness(0, 0, 10, 0) };
        icon.Children.Add(new Ellipse { Fill = result.Ok ? lavender : new SolidColorBrush(Color.FromRgb(0x3A, 0x26, 0x2E)) });
        icon.Children.Add(new TextBlock
        {
            Text = result.Glyph, Style = (Style)FindResource("Icon"), FontSize = 11,
            Foreground = result.Ok ? new SolidColorBrush(Color.FromRgb(0x1E, 0x14, 0x3A)) : new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x9A))
        });
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(new TextBlock
        {
            Text = result.Text, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xEA, 0xFF))
        });
        return row;
    }

    private void StartDots()
    {
        var dots = new[] { Dot1, Dot2, Dot3 };
        for (int i = 0; i < dots.Length; i++)
        {
            var pulse = new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(420))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(i * 140)
            };
            Timeline.SetDesiredFrameRate(pulse, 30);
            dots[i].BeginAnimation(OpacityProperty, pulse);
        }
    }

    private void StopDots()
    {
        foreach (var dot in new[] { Dot1, Dot2, Dot3 }) dot.BeginAnimation(OpacityProperty, null);
    }

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
