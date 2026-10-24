using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GhostyNav.Services;
using GhostyNav.Services.Assistant;

namespace GhostyNav.Pages;

public partial class SettingsPage : UserControl, IIslandPage, IResizingPage
{
    private static readonly Brush Good = Frozen(0x9B, 0xD4, 0x5A), Bad = Frozen(0xFF, 0x8A, 0x8A);
    private readonly AppSettings _settings;
    private readonly RoutineScheduler _routines;
    private readonly IIslandHost _host;
    private readonly Speaker _preview = new();
    private int _check; // ignore results of checks that a newer action superseded

    // Grows with the routines list (which scrolls past three rows).
    public Size PreferredSize
    {
        get
        {
            Root.Measure(new Size(430 - Root.Margin.Left - Root.Margin.Right, double.PositiveInfinity));
            return new Size(430, Math.Ceiling(Root.DesiredSize.Height));
        }
    }

    public event Action? PreferredSizeChanged;

    public SettingsPage(AppSettings settings, RoutineScheduler routines, IIslandHost host)
    {
        InitializeComponent();
        _settings = settings;
        _routines = routines;
        _host = host;
        _routines.Changed += () =>
        {
            RenderRoutines();
            PreferredSizeChanged?.Invoke(); // the host re-fits the island only if this tab is showing
        };
        RenderRoutines();
        foreach (var voice in Enum.GetValues<AssistantVoice>())
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("Chip"), Content = voice.ToString(), GroupName = "AssistantVoice",
                Tag = voice, IsChecked = voice == settings.AssistantVoice
            };
            chip.Checked += OnVoiceChosen;
            VoiceChoices.Children.Add(chip);
        }
        UpdateVoiceHint();
    }

    public void OnShow()
    {
        ShowSaved();
        HaloSwitch.IsChecked = _settings.AssistantHalo;
        RenderRoutines(); // "next" times move on while the tab is closed
    }

    public void OnHide()
    {
        KeyBox.Clear();
        _check++;
        _preview.Stop();
    }

    private void OnHaloChanged(object sender, RoutedEventArgs e)
    {
        _settings.AssistantHalo = HaloSwitch.IsChecked == true;
        _settings.Save();
    }

    private async void OnVoiceChosen(object sender, RoutedEventArgs e)
    {
        var voice = (AssistantVoice)((FrameworkElement)sender).Tag;
        if (voice == _settings.AssistantVoice) return;
        _settings.AssistantVoice = voice;
        _settings.Save();
        UpdateVoiceHint();
        // A short sample, so the choice can be heard right away.
        await _preview.SpeakAsync("Hi! This is how I'll sound.", voice);
    }

    private void RenderRoutines()
    {
        var routines = _routines.Routines.OrderBy(r => r.NextRun).ToList();
        RoutineCount.Text = routines.Count == 0 ? "" : $"{routines.Count} of {RoutineScheduler.MaxRoutines}";
        NoRoutines.Visibility = routines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RoutineScroll.Visibility = routines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var sub = (Brush)FindResource("SubTextBrush");
        RoutineList.Children.Clear();
        foreach (var routine in routines)
        {
            string glyph = routine.Action switch
            {
                RoutineAction.Remind => "\uE823",
                RoutineAction.OpenApp => "\uE8A7",
                RoutineAction.SearchYouTube => "\uE714",
                RoutineAction.SearchWeb => "\uE721",
                _ => "\uE774"
            };
            var remove = new Button
            {
                Style = (Style)FindResource("IconButton"), Content = "\uE74D", FontSize = 10, Width = 24, Height = 24,
                Foreground = sub, ToolTip = "Remove"
            };
            string id = routine.Id;
            remove.Click += (_, _) => _routines.Remove(id);

            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var icon = new TextBlock
            {
                Text = glyph, Style = (Style)FindResource("Icon"), FontSize = 11, Foreground = sub,
                Width = 24, Margin = new Thickness(0, 0, 9, 0)
            };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{routine.Title} · {routine.DescribeAction()}", FontSize = 11.5, FontWeight = FontWeights.SemiBold,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    new TextBlock
                    {
                        Text = routine.Repeat == RoutineRepeat.Once
                            ? routine.DescribeSchedule()
                            : $"{routine.DescribeSchedule()} · next {routine.NextRun.ToString(routine.NextRun.Date == DateTime.Today ? "t" : "ddd t")}",
                        FontSize = 10.5, Foreground = sub, TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            });
            RoutineList.Children.Add(row);
        }
    }

    private void UpdateVoiceHint() => VoiceHint.Text = _settings.AssistantVoice == AssistantVoice.Off
        ? "Replies are shown, not spoken"
        : "Reads the assistant's replies aloud";

    private void ShowSaved()
    {
        string? saved = GeminiKey.Saved, env = GeminiKey.FromEnvironment;
        TestButton.IsEnabled = saved != null || env != null;
        RemoveButton.IsEnabled = saved != null;
        if (saved != null) Status("\uE8D7", $"Saved: {GeminiKey.Mask(saved)}", null);
        else if (env != null) Status("\uE8D7", $"Using GEMINI_API_KEY: {GeminiKey.Mask(env)}", null);
        else Status("\uE946", "No key yet. The voice assistant needs one.", null);
    }

    private void Status(string glyph, string text, Brush? color)
    {
        StatusIcon.Text = glyph;
        StatusIcon.Foreground = StatusText.Foreground = color ?? (Brush)FindResource("SubTextBrush");
        StatusText.Text = text;
    }

    private void OnKeyChanged(object sender, RoutedEventArgs e)
    {
        bool typed = KeyBox.Password.Trim().Length > 0;
        KeyPlaceholder.Visibility = typed ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.IsEnabled = typed;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !SaveButton.IsEnabled) return;
        OnSave(sender, e);
        e.Handled = true;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        string key = KeyBox.Password.Trim();
        if (key.Length == 0) return;
        GeminiKey.Save(key);
        KeyBox.Clear();
        ShowSaved();
        await CheckAsync(key, $"Saved: {GeminiKey.Mask(key)}");
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        if (GeminiKey.Load() is { } key) await CheckAsync(key, GeminiKey.Mask(key));
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        _check++;
        GeminiKey.Remove();
        ShowSaved();
    }

    private async Task CheckAsync(string key, string label)
    {
        int check = ++_check;
        Status("\uE895", $"{label} · checking…", null);
        string? problem = await GeminiAgent.CheckKeyAsync(key);
        if (check != _check) return;
        if (problem == null) Status("\uE73E", $"{label} · works", Good);
        else Status("\uE783", problem, Bad);
    }

    private void OnBack(object sender, RoutedEventArgs e) => _host.CloseSettings();

    private void OnGetKey(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://aistudio.google.com/apikey") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"Couldn't open the browser: {ex.Message}");
        }
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
