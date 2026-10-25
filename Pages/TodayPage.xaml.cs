using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using GhostyNav.Controls;
using GhostyNav.Services;

namespace GhostyNav.Pages;

public partial class TodayPage : UserControl, IIslandPage
{
    private static readonly int[] PresetMinutes = [5, 10, 15, 20];
    private static readonly Brush Orange = Frozen(0xFF, 0xB1, 0x3D), Green = Frozen(0x9B, 0xD4, 0x5A);

    private readonly TimerService _timer;
    private readonly AppSettings _settings;
    private readonly WeatherService _weather;
    private readonly CalendarService _calendar;
    private readonly IIslandHost _host;
    private readonly DispatcherTimer _clock = new();
    private DateTime _weekBuiltFor;
    private bool _visible;

    public Size PreferredSize => new(540, 262);

    public TodayPage(TimerService timer, AppSettings settings, CalendarService calendar, IIslandHost host)
    {
        InitializeComponent();
        _host = host;
        _calendar = calendar;
        _calendar.Changed += () => { if (_visible) RenderNext(); };
        _timer = timer;
        _settings = settings;
        _weather = new WeatherService(settings);
        _clock.Tick += (_, _) => RenderClock();
        _timer.Changed += () => { if (_visible) RenderTimer(); };

        foreach (int minutes in PresetMinutes)
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("Chip"),
                Content = $"{minutes}m",
                GroupName = "TimerPresets",
                Padding = new Thickness(0, 3, 0, 3),
                FontSize = 9.5
            };
            chip.Checked += (_, _) =>
            {
                HoursBox.Text = "0";
                MinutesBox.Text = minutes.ToString("00");
                _timer.SetDuration(TimeSpan.FromMinutes(minutes));
            };
            Presets.Children.Add(chip);
        }
        // Focus: 25-minute rounds with breaks in between, until reset.
        var focus = new RadioButton
        {
            Style = (Style)FindResource("Chip"), Content = "Focus", GroupName = "TimerPresets",
            Padding = new Thickness(0, 3, 0, 3), FontSize = 9.5, ToolTip = "25 minutes of focus, then a 5-minute break"
        };
        focus.Checked += (_, _) =>
        {
            if (_timer.Phase == TimerPhase.Plain) _timer.SetFocus();
        };
        Presets.Children.Add(focus);

        // Digits only; apply the typed duration when the field loses focus or Enter is pressed.
        foreach (var box in new[] { HoursBox, MinutesBox })
        {
            box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
            box.LostKeyboardFocus += (_, _) => ApplyTypedDuration();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyTypedDuration(); };
            box.GotKeyboardFocus += (_, _) => box.SelectAll();
        }
    }

    public void OnShow()
    {
        _visible = true;
        RenderClock();
        RenderTimer();
        RenderTodos(); // the assistant may have changed them
        _ = _calendar.RefreshAsync();
        _ = RenderWeatherAsync();
    }

    public void OnHide()
    {
        _visible = false;
        _clock.Stop();
    }

    // ---------- Clock and week ----------

    private void RenderClock()
    {
        var now = DateTime.Now;
        var culture = CultureInfo.CurrentCulture;
        bool twelveHour = culture.DateTimeFormat.ShortTimePattern.Contains('h');
        HourText.Text = now.ToString(twelveHour ? "hh" : "HH", culture);
        MinuteText.Text = now.ToString("mm", culture);
        string ampm = twelveHour ? now.ToString("tt", culture) + " • " : "";
        ClockSub.Text = ampm + now.ToString("ddd d", culture);

        if (now.Date != _weekBuiltFor) BuildWeek(now.Date, culture);

        // Wake at the next minute boundary instead of ticking every second.
        _clock.Interval = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond - 20);
        _clock.Start();
    }

    private void BuildWeek(DateTime today, CultureInfo culture)
    {
        _weekBuiltFor = today;
        var firstDay = culture.DateTimeFormat.FirstDayOfWeek;
        var start = today.AddDays(-(((int)today.DayOfWeek - (int)firstDay + 7) % 7));
        WeekRange.Text = $"{start.ToString("MMM d", culture)} – {start.AddDays(6).ToString("MMM d", culture)}";

        var sub = (Brush)FindResource("SubTextBrush");
        var accent = (Brush)FindResource("AccentBrush");
        var onAccent = (Brush)FindResource("OnAccentBrush");
        var text = (Brush)FindResource("TextBrush");
        WeekDays.Children.Clear();
        for (int i = 0; i < 7; i++)
        {
            var day = start.AddDays(i);
            bool isToday = day == today;
            WeekDays.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(1, 0, 1, 0),
                Padding = new Thickness(0, 3, 0, 4),
                Background = isToday ? accent : null,
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = day.ToString("ddd", culture), FontSize = 9.5,
                            Foreground = isToday ? onAccent : sub, HorizontalAlignment = HorizontalAlignment.Center
                        },
                        new TextBlock
                        {
                            Text = day.Day.ToString(culture), FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                            Foreground = isToday ? onAccent : text, HorizontalAlignment = HorizontalAlignment.Center,
                            Margin = new Thickness(0, 1, 0, 0)
                        }
                    }
                }
            });
        }
    }

    // ---------- Weather ----------

    private async Task RenderWeatherAsync(bool locate = false)
    {
        var weather = await _weather.GetAsync(locate);
        if (weather != null)
        {
            WeatherIcon.Text = weather.Glyph;
            WeatherTemp.Text = $"{Math.Round(weather.Temperature)}{weather.Unit}";
            WeatherText.Text = weather.Description;
        }
        else
        {
            WeatherIcon.Text = "\uE753";
            WeatherTemp.Text = "—";
            WeatherText.Text = _weather.Error ?? "";
        }
    }

    private async void OnWeatherClick(object sender, MouseButtonEventArgs e)
    {
        if (_weather.Error == "Location is off")
        {
            Process.Start(new ProcessStartInfo("ms-settings:privacy-location") { UseShellExecute = true });
            return;
        }
        WeatherText.Text = "Locating…";
        // Windows may show a permission prompt; don't collapse while it has focus.
        using (_host.HoldOpen())
            await RenderWeatherAsync(locate: true);
    }

    // ---------- Timer ----------

    private void ApplyTypedDuration()
    {
        if (_timer.Phase != TimerPhase.Plain) return; // a focus session sets its own lengths
        int.TryParse(HoursBox.Text, out int h);
        int.TryParse(MinutesBox.Text, out int m);
        var duration = TimeSpan.FromHours(Math.Min(h, 23)) + TimeSpan.FromMinutes(Math.Min(m, 599));
        if (duration <= TimeSpan.Zero || duration == _timer.Duration) return;
        foreach (RadioButton chip in Presets.Children) chip.IsChecked = false;
        _timer.SetDuration(duration);
    }

    private void OnStartPause(object sender, RoutedEventArgs e)
    {
        if (_timer.IsRunning)
        {
            _timer.Pause();
            return;
        }
        ApplyTypedDuration();
        _timer.Start();
        Fx.Ripple(TimerRing, Fx.Orange);
    }

    private void OnReset(object sender, RoutedEventArgs e) => _timer.Reset();

    private void RenderTimer()
    {
        var remaining = _timer.Remaining;
        TimerText.Text = TimerService.Format(remaining);
        TimerRing.Value = _timer.Duration > TimeSpan.Zero ? remaining / _timer.Duration : 0;
        TimerRing.Stroke = _timer.Phase == TimerPhase.Break ? Green : Orange;
        PhaseText.Text = _timer.PhaseLabel;
        PhaseText.Visibility = _timer.Phase == TimerPhase.Plain ? Visibility.Collapsed : Visibility.Visible;
        StartButton.Content = _timer.IsRunning ? "Pause" : _timer.IsActive ? "Resume" : "Start";
        if (_timer.Phase == TimerPhase.Plain && Presets.Children[^1] is RadioButton { IsChecked: true } focus) focus.IsChecked = false;
    }

    private static Brush Frozen(byte r, byte g, byte b) => (Brush)new SolidColorBrush(Color.FromRgb(r, g, b)).GetAsFrozen();

    // ---------- To-dos ----------

    private void OnTodoKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddTodo();
        e.Handled = true;
    }

    private void OnAddTodo(object sender, RoutedEventArgs e) => AddTodo();

    private void AddTodo()
    {
        string text = TodoBox.Text.Trim();
        if (text.Length == 0) return;
        var added = new TodoItem { Text = text };
        _settings.Todos.Add(added);
        _settings.Save();
        TodoBox.Clear();
        _animate = added;
        RenderTodos();
    }

    private TodoItem? _animate; // the item whose new row should pop in

    /// <summary>Under the week: the next calendar event if there is one, else the next to-do.</summary>
    private void RenderNext()
    {
        if (_calendar.Next is { } e)
        {
            NextTitle.Text = e.Title;
            NextSub.Text = CalendarService.When(e) + (e.Location.Length > 0 ? $" · {e.Location}" : "");
            return;
        }
        var todos = _settings.Todos;
        var next = todos.FirstOrDefault(t => !t.Done);
        NextTitle.Text = next != null ? "Next" : "All caught up";
        NextSub.Text = next?.Text ?? (todos.Count == 0 ? "Add a to-do on the right" : "Every to-do is done");
    }

    private void RenderTodos()
    {
        var todos = _settings.Todos;
        int done = todos.Count(t => t.Done);
        TodoCount.Text = $"{done}/{todos.Count}";
        TodoRing.Value = todos.Count == 0 ? 0 : (double)done / todos.Count;

        RenderNext();

        var sub = (Brush)FindResource("SubTextBrush");
        var textBrush = (Brush)FindResource("TextBrush");
        var green = (Brush)FindResource("RamBrush");
        TodoList.Children.Clear();
        foreach (var todo in todos)
        {
            var item = todo;
            var check = new Grid { Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand };
            check.Children.Add(new Ellipse
            {
                Stroke = item.Done ? green : sub,
                StrokeThickness = 1.5,
                Fill = item.Done ? green : Brushes.Transparent
            });
            if (item.Done)
                check.Children.Add(new TextBlock
                {
                    Text = "\uE73E", Style = (Style)FindResource("Icon"), FontSize = 9, Foreground = Brushes.Black
                });
            check.MouseLeftButtonUp += (_, _) =>
            {
                item.Done = !item.Done;
                _settings.Save();
                // Done: green liquid splashes out of the circle (from where it is now, before the row is rebuilt).
                if (item.Done) Fx.Splash(check, Fx.Green);
                _animate = item;
                RenderTodos();
                if (item.Done) Fx.Ripple(TodoRing, Fx.Green);
            };
            if (_animate == item)
            {
                Fx.Pop(check, item.Done ? 0.5 : 0.8);
                _animate = null;
            }

            var remove = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Content = "\uE711", FontSize = 8, Width = 18, Height = 18,
                Foreground = sub, Visibility = Visibility.Hidden, ToolTip = "Remove"
            };
            remove.Click += (_, _) =>
            {
                _settings.Todos.Remove(item);
                _settings.Save();
                RenderTodos();
            };

            var text = new TextBlock
            {
                Text = item.Text, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = item.Done ? sub : textBrush,
                TextDecorations = item.Done ? TextDecorations.Strikethrough : null
            };

            var row = new DockPanel { Margin = new Thickness(2, 3, 0, 3), Background = Brushes.Transparent };
            DockPanel.SetDock(check, Dock.Left);
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(check);
            row.Children.Add(remove);
            row.Children.Add(text);
            row.MouseEnter += (_, _) => remove.Visibility = Visibility.Visible;
            row.MouseLeave += (_, _) => remove.Visibility = Visibility.Hidden;
            TodoList.Children.Add(row);
        }
    }
}
