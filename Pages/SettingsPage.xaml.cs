using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GhostyNav.Controls;
using GhostyNav.Services;
using GhostyNav.Services.Assistant;

namespace GhostyNav.Pages;

public partial class SettingsPage : UserControl, IIslandPage, IResizingPage
{
    private static readonly Brush Good = Frozen(0x9B, 0xD4, 0x5A), Bad = Frozen(0xFF, 0x8A, 0x8A);
    private static readonly string[] SectionNames = ["Assistant", "Notch", "About"];
    private readonly AppSettings _settings;
    private readonly RoutineScheduler _routines;
    private readonly IIslandHost _host;
    private readonly Speaker _preview = new();
    private readonly StackPanel[] _sections;
    private int _check; // ignore results of checks that a newer action superseded
    private bool _updating;

    // Grows with the section shown and the routines list (which scrolls past three rows).
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
        _sections = [AssistantSection, NotchSection, AboutSection];
        _routines.Changed += () =>
        {
            RenderRoutines();
            PreferredSizeChanged?.Invoke(); // the host re-fits the island only if this tab is showing
        };

        for (int i = 0; i < SectionNames.Length; i++)
        {
            int index = i;
            var chip = Chip(SectionNames[i], "SettingsSection", i == 0);
            chip.Checked += (_, _) => ShowSection(index);
            Sections.Children.Add(chip);
        }

        BuildAssistant();
        BuildNotch();
        BuildAbout();
    }

    public void OnShow()
    {
        ShowSaved();
        HaloSwitch.IsChecked = _settings.AssistantHalo;
        AutoUpdateSwitch.IsChecked = _settings.CheckForUpdates;
        RenderRoutines(); // "next" times move on while the tab is closed
        RenderScreens();  // a screen may have been plugged in since
        RenderUpdate();
    }

    public void OnHide()
    {
        KeyBox.Clear();
        _check++;
        _preview.Stop();
    }

    private void ShowSection(int index)
    {
        for (int i = 0; i < _sections.Length; i++) _sections[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        PreferredSizeChanged?.Invoke();
    }

    /// <summary>Saves, and lets the notch catch up with what changed.</summary>
    private void Changed()
    {
        _settings.Save();
        _host.ApplySettings();
    }

    // ---------- Assistant ----------

    private void BuildAssistant()
    {
        foreach (var voice in Enum.GetValues<AssistantVoice>())
        {
            var chip = Chip(voice.ToString(), "AssistantVoice", voice == _settings.AssistantVoice);
            chip.Tag = voice;
            chip.Checked += OnVoiceChosen;
            VoiceChoices.Children.Add(chip);
        }
        UpdateVoiceHint();

        foreach (var chord in Enum.GetValues<Chord>())
        {
            var chip = Chip(KeyChord.NameOf(chord), "AssistantChord", chord == _settings.AssistantChord);
            chip.Checked += (_, _) =>
            {
                if (chord == _settings.AssistantChord) return;
                _settings.AssistantChord = chord;
                UpdateChordText();
                Changed();
            };
            ChordChoices.Children.Add(chip);
        }
        UpdateChordText();
    }

    private void UpdateChordText()
    {
        string keys = KeyChord.NameOf(_settings.AssistantChord);
        KeyIntro.Text = $"Powers the voice assistant (hold {keys}). Kept only on this PC.";
        NoRoutines.Text = $"None yet. Hold {keys} and say “remind me to stretch every day at 3” or “open Spotify every weekday at 9”.";
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

    private void OnGetKey(object sender, RoutedEventArgs e) => Open("https://aistudio.google.com/apikey");

    // ---------- Notch ----------

    private void BuildNotch()
    {
        foreach (var size in Enum.GetValues<NotchSize>())
        {
            var chip = Chip(size.ToString(), "NotchSize", size == _settings.NotchSize);
            chip.Checked += (_, _) =>
            {
                _settings.NotchSize = size;
                Changed();
            };
            SizeChoices.Children.Add(chip);
        }

        AccentName.Text = Theme.Find(_settings.Accent).Name;
        foreach (var accent in Theme.Accents)
        {
            var swatch = new RadioButton
            {
                GroupName = "Accent", IsChecked = accent.Name == Theme.Find(_settings.Accent).Name, ToolTip = accent.Name,
                Cursor = Cursors.Hand, Focusable = false, Margin = new Thickness(3, 0, 0, 0),
                Template = SwatchTemplate(accent.Main)
            };
            swatch.Checked += (_, _) =>
            {
                _settings.Accent = accent.Name;
                AccentName.Text = accent.Name;
                Theme.Apply(accent.Name);
                _settings.Save();
            };
            AccentChoices.Children.Add(swatch);
        }

        AddSwitch("\uE995", "Volume and brightness", "A slim bar drops down when they change",
            () => _settings.LevelPopups, v => _settings.LevelPopups = v);
        AddSwitch("\uEA8F", "Notifications", "Show new Windows notifications in the notch",
            () => _settings.MirrorNotifications, v => _settings.MirrorNotifications = v);
        AddSwitch("\uE720", "Microphone and camera light", "A dot while another app is using them",
            () => _settings.PrivacyDots, v => _settings.PrivacyDots = v);
        AddSwitch("\uE7F6", "Charging and earbuds", "Plugged in, low battery, headphones connected",
            () => _settings.DevicePeeks, v => _settings.DevicePeeks = v);

        foreach (var tab in _host.OptionalTabs)
        {
            var chip = new ToggleButton
            {
                Style = (Style)FindResource("ToggleChip"), Content = tab, IsChecked = !_settings.HiddenTabs.Contains(tab),
                Margin = new Thickness(0, 0, 2, 4)
            };
            chip.Click += (_, _) =>
            {
                _settings.HiddenTabs.Remove(tab);
                if (chip.IsChecked != true) _settings.HiddenTabs.Add(tab);
                Changed();
            };
            TabChoices.Children.Add(chip);
        }
    }

    private void AddSwitch(string glyph, string title, string hint, Func<bool> get, Action<bool> set)
    {
        if (MomentSwitches.Children.Count > 0) MomentSwitches.Children.Add(new Rectangle { Style = (Style)FindResource("Divider") });
        var toggle = new ToggleButton { Style = (Style)FindResource("Switch"), IsChecked = get() };
        toggle.Click += (_, _) =>
        {
            set(toggle.IsChecked == true);
            Changed();
        };
        var row = new DockPanel();
        DockPanel.SetDock(toggle, Dock.Right);
        row.Children.Add(toggle);
        row.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = glyph, Style = (Style)FindResource("Icon"), FontSize = 12, Width = 24, Margin = new Thickness(0, 0, 9, 0),
                    Foreground = (Brush)FindResource("SubTextBrush")
                },
                new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = title, Style = (Style)FindResource("RowTitle") },
                        new TextBlock { Text = hint, Style = (Style)FindResource("RowHint") }
                    }
                }
            }
        });
        MomentSwitches.Children.Add(row);
    }

    private void RenderScreens()
    {
        var screens = Native.Screens();
        ScreenRow.Visibility = screens.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ScreenChoices.Children.Clear();
        if (screens.Count < 2) return;
        string current = screens.Any(s => s.Device == _settings.Screen) ? _settings.Screen! : screens[0].Device;
        for (int i = 0; i < screens.Count; i++)
        {
            var screen = screens[i];
            var chip = Chip(screen.Primary ? "Main" : $"Screen {i + 1}", "NotchScreen", screen.Device == current);
            chip.Checked += (_, _) =>
            {
                _settings.Screen = screen.Primary ? null : screen.Device;
                Changed();
            };
            ScreenChoices.Children.Add(chip);
        }
    }

    private static ControlTemplate SwatchTemplate(Color color)
    {
        var ring = new FrameworkElementFactory(typeof(Ellipse), "Ring");
        ring.SetValue(Shape.StrokeProperty, Brushes.White);
        ring.SetValue(Shape.StrokeThicknessProperty, 0.0);
        ring.SetValue(WidthProperty, 20.0);
        ring.SetValue(HeightProperty, 20.0);
        var dot = new FrameworkElementFactory(typeof(Ellipse));
        dot.SetValue(Shape.FillProperty, new SolidColorBrush(color));
        dot.SetValue(WidthProperty, 14.0);
        dot.SetValue(HeightProperty, 14.0);
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
        grid.AppendChild(ring);
        grid.AppendChild(dot);
        var template = new ControlTemplate(typeof(RadioButton)) { VisualTree = grid };
        template.Triggers.Add(new Trigger
        {
            Property = ToggleButton.IsCheckedProperty, Value = true,
            Setters = { new Setter(Shape.StrokeThicknessProperty, 1.5, "Ring") }
        });
        return template;
    }

    // ---------- About ----------

    private void BuildAbout()
    {
        GhostIcon.Source = GhostSprite.Frame(0, GhostEyes.Happy, 0, false);
        VersionText.Text = $"GhostyNav {Updater.CurrentText}";
        RenderUpdate();
    }

    private void RenderUpdate()
    {
        if (_updating) return;
        var update = _host.AvailableUpdate;
        UpdateButton.Visibility = update != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.Content = update != null ? $"Update to {update.Version}" : "";
        if (update != null) UpdateStatus.Text = "A new version is ready";
        else if (UpdateStatus.Text.Length == 0) UpdateStatus.Text = "Click Check now to look for updates";
    }

    private void OnAutoUpdateChanged(object sender, RoutedEventArgs e)
    {
        _settings.CheckForUpdates = AutoUpdateSwitch.IsChecked == true;
        _settings.Save();
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        UpdateStatus.Text = "Checking…";
        var update = await _host.CheckForUpdateAsync();
        CheckButton.IsEnabled = true;
        UpdateStatus.Text = update == null ? "You have the latest version" : "";
        RenderUpdate();
    }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (_host.AvailableUpdate is not { } update) return;
        _updating = true;
        UpdateButton.IsEnabled = CheckButton.IsEnabled = false;
        using var hold = _host.HoldOpen();
        var progress = new Progress<double>(p => UpdateStatus.Text = $"Downloading… {p:P0}");
        string? problem = await Updater.InstallAsync(update, progress);
        if (problem == null)
        {
            UpdateStatus.Text = "Installing, see you in a moment";
            await Task.Delay(400);
            Application.Current.Shutdown(); // Setup starts the new version once this one has gone
            return;
        }
        _updating = false;
        UpdateStatus.Text = problem;
        UpdateButton.IsEnabled = CheckButton.IsEnabled = true;
    }

    private void OnRepo(object sender, RoutedEventArgs e) => Open("https://github.com/TheCidSama/GhostyNav");

    // ---------- Shared ----------

    private RadioButton Chip(string text, string group, bool isChecked) => new()
    {
        Style = (Style)FindResource("Chip"), Content = text, GroupName = group, IsChecked = isChecked
    };

    private void OnBack(object sender, RoutedEventArgs e) => _host.CloseSettings();

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
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
