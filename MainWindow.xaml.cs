using System.Diagnostics;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using GhostyNav.Controls;
using GhostyNav.Pages;
using GhostyNav.Services;
using GhostyNav.Services.Assistant;
using Microsoft.Win32;

namespace GhostyNav;

public partial class MainWindow : Window, IIslandHost, IAssistantView
{
    private const double TabBarHeight = 42, BottomPad = 10;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const int ControlsTab = 0, TodayTab = 1, ClipboardTab = 3, AppsTab = 4, SettingsTab = 6;
    private const int HotkeyApps = 1, HotkeyClipboard = 2;
    private static readonly Size MediaPeekSize = new(360, 100), NoticePeekSize = new(300, 58);

    private enum Mode { Compact, Peek, Expanded, Assistant }

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly MediaService _media;
    private readonly RadioService _radios;
    private readonly ClipboardService _clipboard;
    private readonly AudioService _audio = new();
    private readonly TimerService _timer = new();
    private readonly RoutineScheduler _routines = new();
    private readonly Queue<Func<bool>> _pendingNotices = new();
    private readonly DispatcherTimer _noticeRetry = new() { Interval = TimeSpan.FromSeconds(3) };

    private readonly (string Title, Func<IIslandPage> Create)[] _tabs;
    private readonly IIslandPage?[] _pages;
    private readonly RadioButton[] _tabButtons;
    private int _tab;

    // Island shape springs
    private const double CompactHeight = 24, CompactSlant = 8;
    private readonly Spring _width = new(190), _height = new(CompactHeight), _radius = new(9), _slant = new(CompactSlant);
    private TimeSpan _lastFrame;
    private bool _animating;

    private Mode _mode;
    private bool _hover, _hiddenForFullscreen, _timerShown;
    private int _holdOpen;
    private Size _expandedSize, _peekSize;
    private int _peekTab;
    private MediaCard? _peekCard;

    private readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };
    private readonly DispatcherTimer _peekHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _peekHideTimer = new();
    private readonly DispatcherTimer _fullscreenTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    private nint _hwnd, _previousForeground;
    private Native.WinEventProc? _foregroundProc; // must outlive the native hook

    // Swipe state
    private bool _pressed, _dragging;
    private Point _dragStart;
    private double _lastDragX, _dragVelocity;
    private readonly Stopwatch _dragClock = new();
    private long _lastWheel;

    private readonly ScaleTransform[] _barScales = new ScaleTransform[4];
    private bool _barsRunning;

    private readonly Assistant _assistant;
    private readonly DispatcherTimer _assistantDismiss = new();
    private readonly ScaleTransform[] _levelScales = new ScaleTransform[5];

    public MainWindow()
    {
        InitializeComponent();

        _media = new MediaService(Dispatcher);
        _radios = new RadioService(Dispatcher);
        _clipboard = new ClipboardService(Dispatcher);

        // Pages are built on first visit, so unused tabs cost no memory.
        _tabs =
        [
            ("Controls", () => new ControlsPage(_audio, _radios, _media)),
            ("Today", () => new TodayPage(_timer, _settings, this)),
            ("Stats", () => new StatsPage(_settings, this)),
            ("Clipboard", () => new ClipboardPage(_clipboard, this)),
            ("Apps", () => new AppsPage(_settings, this)),
            ("Power", () => new PowerPage(this)),
            ("Settings", () => new SettingsPage(_settings, _routines)),
        ];
        _pages = new IIslandPage?[_tabs.Length];
        _tabButtons = BuildTabs();
        BuildBars();
        BuildLevelBars();
        Apply();

        Fx.Layer = FxLayer;
        _assistant = new Assistant(Dispatcher, this, _settings, _routines);
        _routines.Due += OnRoutineDue;
        _noticeRetry.Tick += (_, _) => TryPendingNotice();
        Loaded += (_, _) => _routines.Start();
        AssistantUI.Ghost = Ghost;
        AssistantUI.CloseRequested += () =>
        {
            _assistant.Stop();
            DismissAssistant();
        };
        _assistantDismiss.Tick += (_, _) =>
        {
            // Reading the answer: wait until the pointer leaves the card.
            if (AssistantUI.IsPointerOver)
            {
                _assistantDismiss.Interval = TimeSpan.FromSeconds(2);
                return;
            }
            DismissAssistant();
        };
        Closed += (_, _) => _assistant.Dispose();

        Island.PreviewMouseLeftButtonDown += OnIslandPress;
        Island.MouseLeftButtonUp += OnIslandRelease;
        Island.MouseEnter += OnIslandEnter;
        Island.MouseLeave += OnIslandLeave;
        Tabs.MouseWheel += (_, e) => { e.Handled = true; WheelTab(e.Delta < 0 ? 1 : -1); };
        PageHost.PreviewMouseLeftButtonDown += OnSwipeDown;
        PageHost.PreviewMouseMove += OnSwipeMove;
        PageHost.PreviewMouseLeftButtonUp += OnSwipeUp;
        PageHost.LostMouseCapture += (_, _) => { if (_dragging) CancelDrag(); };
        Deactivated += (_, _) => { if (_holdOpen == 0) Collapse(); };
        PreviewKeyDown += OnPreviewKey;
        KeyDown += OnKey;

        _leaveTimer.Tick += (_, _) =>
        {
            _leaveTimer.Stop();
            if (_mode == Mode.Expanded && !_hover && Mouse.Captured == null && _holdOpen == 0 && !IsTyping()) Collapse();
        };
        _peekHoverTimer.Tick += (_, _) =>
        {
            _peekHoverTimer.Stop();
            if (_mode == Mode.Compact && _hover && _media.HasSession) ShowMediaPeek(autoHideMs: 0);
        };
        _peekHideTimer.Tick += (_, _) =>
        {
            _peekHideTimer.Stop();
            if (_mode == Mode.Peek && !_hover) Collapse();
        };
        _fullscreenTimer.Tick += (_, _) => CheckFullscreen();

        _media.Changed += OnMediaChanged;
        _media.TrackChanged += () =>
        {
            if (_mode == Mode.Compact && !_hiddenForFullscreen) ShowMediaPeek(autoHideMs: 3500);
        };
        _timer.Changed += OnTimerChanged;
        _timer.Finished += () =>
        {
            SystemSounds.Asterisk.Play();
            ShowNotice("\uE916", "Timer done", $"{TimerService.Format(_timer.Duration)} timer finished", TodayTab);
        };

        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) =>
        {
            await _media.InitializeAsync();
            await _radios.InitializeAsync();
        };

        MoveStartupEntryFromOldName();
        StartupItem.IsChecked = Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("GhostyNav") != null;
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.InvokeAsync(PlaceWindow);
        PlaceWindow();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.MakeToolWindow(_hwnd); // keep it out of Alt+Tab
        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);

        Native.RegisterHotKey(_hwnd, HotkeyApps, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x20 /* Space */);
        Native.RegisterHotKey(_hwnd, HotkeyClipboard, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x56 /* V */);
        Native.AddClipboardFormatListener(_hwnd);

        // Remember which app was active before the island, to paste clipboard items back into it.
        _foregroundProc = (_, _, hwnd, _, _, _, _) => _previousForeground = hwnd;
        Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, 0, _foregroundProc,
            0, 0, 0x2 /* WINEVENT_SKIPOWNPROCESS */);

        _fullscreenTimer.Start();
    }

    private void PlaceWindow()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    // ---------- Shape animation ----------

    private void UpdateTargets()
    {
        double w, h, r, slant = 0;
        switch (_mode)
        {
            case Mode.Expanded:
                (w, h, r) = (_expandedSize.Width, _expandedSize.Height, 22);
                break;
            case Mode.Peek:
                (w, h, r) = (_peekSize.Width, _peekSize.Height, 20);
                break;
            case Mode.Assistant:
                // The notch drops down, keeping its sloped sides, with room for the ghost.
                (w, h, r, slant) = (236, 58, 18, CompactSlant);
                break;
            default:
                // A low notch with sloped sides; wider when it has a live activity to show.
                w = _media.HasSession ? 250 : _timerShown ? 220 : 190;
                h = CompactHeight;
                if (_hover) { w += 12; h += 3; }
                r = 9;
                slant = CompactSlant;
                break;
        }

        // Opening gets a little bounce; closing settles without overshoot.
        double damping = _mode == Mode.Compact ? 34 : 26;
        foreach (var s in new[] { _width, _height, _radius, _slant }) s.Damping = damping;

        _width.Target = w;
        _height.Target = h;
        _radius.Target = r;
        _slant.Target = slant;
        StartAnimating();
    }

    private void StartAnimating()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        // Rendering can fire more than once per frame with the same timestamp.
        if (now == _lastFrame) return;
        double dt = _lastFrame == TimeSpan.Zero ? 1.0 / 60 : Math.Min((now - _lastFrame).TotalSeconds, 0.05);
        _lastFrame = now;

        bool moving = _width.Step(dt) | _height.Step(dt) | _radius.Step(dt) | _slant.Step(dt);
        Apply();

        if (!moving)
        {
            CompositionTarget.Rendering -= OnFrame; // idle: zero frames rendered
            _animating = false;
        }
    }

    private void Apply()
    {
        double w = Math.Max(1, _width.Value), h = Math.Max(1, _height.Value);
        double k = Math.Clamp(_slant.Value, 0, w / 4);
        double r = Math.Clamp(_radius.Value, 0, Math.Min(w / 2, h - 1));
        // The bottom corners must not overlap across the (narrower) bottom edge.
        double len = Math.Sqrt(k * k + h * h);
        if (len > k) r = Math.Min(r, h * (w / 2 - k) / (len - k));
        double shoulder = Math.Clamp(r * 0.55, 5, 14);
        r = Math.Max(0, Math.Min(r, h - shoulder));

        var outline = NotchGeometry(w, h, r, shoulder, k, out double totalWidth);
        Island.Width = totalWidth;
        Island.Height = h;
        Shape.Data = outline;
        Clipper.Clip = outline;
    }

    /// <summary>
    /// Notch outline flush with y = 0: <paramref name="w"/> wide at the top, each side sloping
    /// inwards by <paramref name="k"/> towards the bottom ("\___/"), bottom corners rounded with
    /// <paramref name="r"/>. Concave shoulders of radius <paramref name="s"/> flare the sides into the
    /// screen edge; they sit outside the body, so the full shape is wider than <paramref name="w"/>.
    /// </summary>
    private static StreamGeometry NotchGeometry(double w, double h, double r, double s, double k, out double totalWidth)
    {
        // Right side runs from its top corner (rightTop, 0) to (rightTop - k, h); n is its outward normal.
        double len = Math.Sqrt(k * k + h * h);
        double nx = h / len, ny = k / len;
        double flare = s * (len - k) / h; // how far the shoulder reaches past the top corner
        double rightTop = flare + w;
        totalWidth = w + 2 * flare;

        // Shoulder: circle tangent to the screen edge and to the side, outside the body.
        var shoulderStart = new Point(rightTop + flare, 0);
        var shoulderEnd = new Point(shoulderStart.X - s * nx, s - s * ny);
        // Bottom corner: circle tangent to the side and to the bottom edge, inside the body.
        double cornerX = rightTop - (r * len + (h - r) * k) / h;
        var cornerStart = new Point(cornerX + r * nx, h - r + r * ny);
        var cornerEnd = new Point(cornerX, h);

        double total = totalWidth;
        Point Mirror(Point p) => new(total - p.X, p.Y);

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
            ctx.LineTo(shoulderStart, false, false);
            ctx.ArcTo(shoulderEnd, new Size(s, s), 0, false, SweepDirection.Counterclockwise, false, false);
            ctx.LineTo(cornerStart, false, false);
            ctx.ArcTo(cornerEnd, new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(Mirror(cornerEnd), false, false);
            ctx.ArcTo(Mirror(cornerStart), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(Mirror(shoulderEnd), false, false);
            ctx.ArcTo(new Point(0, 0), new Size(s, s), 0, false, SweepDirection.Counterclockwise, false, false);
        }
        g.Freeze();
        return g;
    }

    // ---------- Mouse on the island ----------

    private void OnIslandPress(object sender, MouseButtonEventArgs e)
    {
        if (_mode == Mode.Assistant)
        {
            // Once the assistant has answered, clicking the notch opens the island as usual.
            e.Handled = true;
            if (!_assistant.IsListening) Expand(_tab);
            return;
        }
        if (_mode != Mode.Compact) return;
        e.Handled = true;
        Expand(_tab);
    }

    private void OnIslandRelease(object sender, MouseButtonEventArgs e)
    {
        // In a peek, buttons and the seek bar handle their own clicks; anywhere else opens the island.
        if (_mode != Mode.Peek || e.Handled) return;
        e.Handled = true;
        Expand(_peekTab);
    }

    private void OnIslandEnter(object sender, MouseEventArgs e)
    {
        _hover = true;
        _leaveTimer.Stop();
        _peekHideTimer.Stop();
        if (_mode == Mode.Compact && _media.HasSession) _peekHoverTimer.Start();
        if (_mode == Mode.Compact) UpdateTargets();
    }

    private void OnIslandLeave(object sender, MouseEventArgs e)
    {
        _hover = false;
        _peekHoverTimer.Stop();
        switch (_mode)
        {
            case Mode.Expanded:
                _leaveTimer.Interval = TimeSpan.FromSeconds(1.2);
                _leaveTimer.Start();
                break;
            case Mode.Peek:
                _peekHideTimer.Interval = TimeSpan.FromMilliseconds(450);
                _peekHideTimer.Start();
                break;
            default:
                UpdateTargets();
                break;
        }
    }

    private static bool IsTyping() => Keyboard.FocusedElement is TextBox { Text.Length: > 0 };

    // ---------- Expand / peek / collapse ----------

    private void Expand(int tab)
    {
        if (_mode == Mode.Expanded)
        {
            SelectTab(tab);
            return;
        }
        if (_mode == Mode.Assistant)
        {
            _assistant.Stop();
            DismissAssistant();
        }
        var was = _mode;
        _mode = Mode.Expanded;
        _peekHoverTimer.Stop();
        _peekHideTimer.Stop();
        Activate(); // so clicking anywhere else raises Deactivated and collapses us

        if (was == Mode.Peek) HidePeekContent();
        else Fade(Compact, 0, 90);

        ShowPageNow(tab);
        Expanded.Visibility = Visibility.Visible;
        Expanded.IsHitTestVisible = true;
        Fade(Expanded, 1, 220, delay: 60);
        var grow = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ExpandedScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        ExpandedScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        SetBarsRunning(false);
        UpdateTargets();
    }

    public void Collapse()
    {
        // The assistant closes on its own schedule, not when the island loses focus.
        if (_mode is Mode.Compact or Mode.Assistant) return;
        var was = _mode;
        _mode = Mode.Compact;
        _leaveTimer.Stop();
        _peekHideTimer.Stop();
        if (_dragging) CancelDrag();

        if (was == Mode.Expanded)
        {
            _pages[_tab]?.OnHide();
            // While it fades out, the panel must not catch clicks meant for the notch or a peek.
            Expanded.IsHitTestVisible = false;
            Fade(Expanded, 0, 110, completed: () =>
            {
                if (_mode != Mode.Expanded) Expanded.Visibility = Visibility.Collapsed; // no layout/render cost while compact
            });
            var shrink = new DoubleAnimation(0.94, TimeSpan.FromMilliseconds(150));
            ExpandedScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            ExpandedScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        }
        else
        {
            HidePeekContent();
        }
        Fade(Compact, 1, 160, delay: 140);

        UpdateCompact();
        UpdateTargets();
    }

    private void ShowMediaPeek(int autoHideMs)
    {
        _peekCard ??= new MediaCard(_media, chromeless: true);
        ShowPeek(_peekCard, MediaPeekSize, ControlsTab, autoHideMs);
        _peekCard.SetActive(true);
    }

    /// <summary>
    /// A notice that must not be lost (a routine's reminder): shown as soon as the island is free,
    /// i.e. not open, not listening, not hidden behind a fullscreen app.
    /// </summary>
    private void ShowNoticeWhenFree(string glyph, string title, string text, int tab, Action? shown = null)
    {
        _pendingNotices.Enqueue(() =>
        {
            if (!ShowNotice(glyph, title, text, tab, width: 360, autoHideMs: 8000)) return false;
            shown?.Invoke();
            return true;
        });
        TryPendingNotice();
    }

    private void TryPendingNotice()
    {
        _noticeRetry.Stop();
        if (_pendingNotices.Count == 0) return;
        // One at a time: the next waits until this one has had its moment.
        if (_mode == Mode.Compact && !_hiddenForFullscreen && _pendingNotices.Peek()()) _pendingNotices.Dequeue();
        if (_pendingNotices.Count > 0) _noticeRetry.Start();
    }

    private void OnRoutineDue(Routine routine, bool late)
    {
        if (routine.Action == RoutineAction.Remind)
        {
            string text = late ? $"{routine.Value} (missed at {routine.NextRun:t})" : routine.Value;
            ShowNoticeWhenFree("\uE823", routine.Title, text, SettingsTab, shown: () =>
            {
                SystemSounds.Asterisk.Play();
                _ = _assistant.SayAsync(routine.Value);
            });
            return;
        }
        var result = AssistantActions.RunRoutine(routine, _routines);
        ShowNoticeWhenFree(result.Glyph, routine.Title, result.Text, SettingsTab);
    }

    private bool ShowNotice(string glyph, string title, string text, int tab, double width = 300, int autoHideMs = 5000)
    {
        var icon = new Grid { Width = 30, Height = 30, Margin = new Thickness(0, 0, 12, 0) };
        icon.Children.Add(new Ellipse { Fill = (Brush)FindResource("AccentBrush") });
        icon.Children.Add(new TextBlock
        {
            Text = glyph, Style = (Style)FindResource("Icon"), FontSize = 14, Foreground = (Brush)FindResource("OnAccentBrush")
        });
        var content = new DockPanel { Margin = new Thickness(18, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(icon);
        content.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock
                {
                    Text = text, FontSize = 11, Foreground = (Brush)FindResource("SubTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        });
        if (_mode is Mode.Expanded or Mode.Assistant || _hiddenForFullscreen) return false;
        _peekCard?.SetActive(false);
        return ShowPeek(content, new Size(width, NoticePeekSize.Height), tab, autoHideMs);
    }

    private bool ShowPeek(FrameworkElement content, Size size, int tab, int autoHideMs)
    {
        if (_mode is Mode.Expanded or Mode.Assistant || _hiddenForFullscreen) return false;
        _peekSize = size;
        _peekTab = tab;
        PeekHost.Content = content;
        Peek.Width = size.Width;
        Peek.Height = size.Height;

        if (_mode != Mode.Peek)
        {
            _mode = Mode.Peek;
            Peek.Visibility = Visibility.Visible;
            Peek.IsHitTestVisible = true;
            Fade(Peek, 1, 200, delay: 60);
            Fade(Compact, 0, 90);
            SetBarsRunning(false);
        }

        _peekHideTimer.Stop();
        if (autoHideMs > 0)
        {
            _peekHideTimer.Interval = TimeSpan.FromMilliseconds(autoHideMs);
            _peekHideTimer.Start();
        }
        UpdateTargets();
        return true;
    }

    private void HidePeekContent()
    {
        _peekCard?.SetActive(false);
        Peek.IsHitTestVisible = false;
        Fade(Peek, 0, 90, completed: () =>
        {
            if (_mode != Mode.Peek) Peek.Visibility = Visibility.Collapsed;
        });
    }

    private static void Fade(UIElement target, double to, int ms, int delay = 0, Action? completed = null)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        // Must be attached before BeginAnimation: the clock copies handlers when it's created,
        // so one added afterwards never runs.
        if (completed != null) anim.Completed += (_, _) => completed();
        target.BeginAnimation(OpacityProperty, anim);
    }

    private void CheckFullscreen()
    {
        if (_mode == Mode.Assistant) return; // asked for explicitly, so it shows even over fullscreen apps
        bool fullscreen = _mode != Mode.Expanded && Native.IsFullscreenAppActive(_hwnd);
        if (fullscreen == _hiddenForFullscreen) return;
        _hiddenForFullscreen = fullscreen;
        if (fullscreen) Collapse();
        Island.Visibility = fullscreen ? Visibility.Hidden : Visibility.Visible;
        UpdateCompact();
    }

    // ---------- Tabs ----------

    private RadioButton[] BuildTabs()
    {
        var buttons = new RadioButton[_tabs.Length];
        for (int i = 0; i < _tabs.Length; i++)
        {
            int index = i;
            var button = new RadioButton { Style = (Style)FindResource("TabButton"), Content = _tabs[i].Title, GroupName = "IslandTabs" };
            if (i == SettingsTab)
            {
                // A gear rather than a word keeps the tab bar narrow enough for the smaller panels.
                button.Content = new TextBlock { Text = "\uE713", Style = (Style)FindResource("Icon"), FontSize = 12.5, Margin = new Thickness(0, 1, 0, 1) };
                button.ToolTip = _tabs[i].Title;
            }
            button.Click += (_, _) => SelectTab(index);
            buttons[i] = button;
            Tabs.Children.Add(button);
        }
        return buttons;
    }

    private FrameworkElement EnsurePage(int index)
    {
        if (_pages[index] is FrameworkElement existing) return existing;
        var page = _tabs[index].Create();
        var element = (FrameworkElement)page;
        element.Width = page.PreferredSize.Width;
        element.Height = page.PreferredSize.Height;
        element.HorizontalAlignment = HorizontalAlignment.Center;
        element.VerticalAlignment = VerticalAlignment.Top;
        element.RenderTransform = new TranslateTransform();
        _pages[index] = page;
        PageHost.Children.Add(element);
        if (page is IResizingPage resizing)
        {
            resizing.PreferredSizeChanged += () =>
            {
                element.Height = page.PreferredSize.Height;
                if (_mode != Mode.Expanded || _tab != index) return;
                SizeIslandTo(index);
                UpdateTargets();
            };
        }
        return element;
    }

    private void SizeIslandTo(int index)
    {
        var size = _pages[index]!.PreferredSize;
        _expandedSize = new Size(size.Width, TabBarHeight + size.Height + BottomPad);
        Expanded.Width = _expandedSize.Width;
        Expanded.Height = _expandedSize.Height;
    }

    /// <summary>Shows a tab without a slide (used when the island opens).</summary>
    private void ShowPageNow(int index)
    {
        var current = EnsurePage(index);
        foreach (var page in _pages)
        {
            if (page is not FrameworkElement element) continue;
            var shift = (TranslateTransform)element.RenderTransform;
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.X = 0;
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 1;
            element.Visibility = element == current ? Visibility.Visible : Visibility.Collapsed;
        }
        _tab = index;
        _tabButtons[index].IsChecked = true;
        SizeIslandTo(index);
        _pages[index]!.OnShow();
    }

    private void SelectTab(int index)
    {
        index = Math.Clamp(index, 0, _tabs.Length - 1);
        if (_mode != Mode.Expanded) return;
        if (index == _tab)
        {
            SlideHome((FrameworkElement)_pages[_tab]!);
            return;
        }

        int dir = index > _tab ? 1 : -1;
        int oldIndex = _tab;
        var old = (FrameworkElement)_pages[oldIndex]!;
        _pages[oldIndex]!.OnHide();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var oldShift = (TranslateTransform)old.RenderTransform;
        oldShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-dir * 60, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
        fadeOut.Completed += (_, _) =>
        {
            if (_tab != oldIndex) old.Visibility = Visibility.Collapsed;
        };
        old.BeginAnimation(OpacityProperty, fadeOut);

        var current = EnsurePage(index);
        _tab = index;
        _tabButtons[index].IsChecked = true;
        SizeIslandTo(index);

        current.Visibility = Visibility.Visible;
        ((TranslateTransform)current.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(dir * 60, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        current.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { BeginTime = TimeSpan.FromMilliseconds(40) });

        _pages[index]!.OnShow();
        UpdateTargets();
    }

    private static void SlideHome(FrameworkElement page)
    {
        ((TranslateTransform)page.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void WheelTab(int direction)
    {
        // Touchpads send many small wheel events per gesture; one tab per gesture.
        long now = Environment.TickCount64;
        if (now - _lastWheel < 350) return;
        _lastWheel = now;
        SelectTab(_tab + direction);
    }

    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Escape)
        {
            if (_mode == Mode.Assistant)
            {
                _assistant.Stop();
                DismissAssistant();
            }
            else Collapse();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Tab)
        {
            SelectTab(_tab + (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1));
            e.Handled = true;
        }
        else if (ctrl && e.Key >= Key.D1 && e.Key < Key.D1 + _tabs.Length)
        {
            SelectTab(e.Key - Key.D1);
            e.Handled = true;
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        // Arrow keys switch tabs unless a text field wants them.
        if (e.Handled || Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.Left) SelectTab(_tab - 1);
        else if (e.Key == Key.Right) SelectTab(_tab + 1);
    }

    // ---------- Swiping between tabs ----------

    private TranslateTransform? CurrentShift =>
        _pages[_tab] is FrameworkElement page ? (TranslateTransform)page.RenderTransform : null;

    private void OnSwipeDown(object sender, MouseButtonEventArgs e)
    {
        // Sliders, text fields, scrollbars and seek bars own horizontal drags.
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<Slider>(source) != null || FindAncestor<TextBoxBase>(source) != null ||
            FindAncestor<ScrollBar>(source) != null)
            return;
        for (var node = source; node != null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (Swipe.GetIgnore(node)) return;
        _pressed = true;
        _dragging = false;
        _dragStart = e.GetPosition(PageHost);
    }

    private void OnSwipeMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed || CurrentShift is not { } shift) return;
        var p = e.GetPosition(PageHost);
        double dx = p.X - _dragStart.X;

        if (!_dragging)
        {
            if (Math.Abs(dx) < 10 || Math.Abs(dx) < Math.Abs(p.Y - _dragStart.Y) * 1.2) return;
            _dragging = true;
            _lastDragX = p.X;
            _dragVelocity = 0;
            _dragClock.Restart();
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            PageHost.CaptureMouse(); // steals capture from any pressed button, cancelling its click
        }

        double dt = Math.Max(_dragClock.Elapsed.TotalSeconds, 0.001);
        _dragClock.Restart();
        _dragVelocity = 0.7 * ((p.X - _lastDragX) / dt) + 0.3 * _dragVelocity;
        _lastDragX = p.X;

        // Rubber-band past the first and last tab.
        bool atEdge = _tab == 0 && dx > 0 || _tab == _tabs.Length - 1 && dx < 0;
        shift.X = atEdge ? dx / 3 : dx;
        e.Handled = true;
    }

    private void OnSwipeUp(object sender, MouseButtonEventArgs e)
    {
        _pressed = false;
        if (!_dragging || CurrentShift is not { } shift) return;
        _dragging = false;
        PageHost.ReleaseMouseCapture();
        e.Handled = true;

        double dx = shift.X;
        bool flick = Math.Abs(_dragVelocity) > 600 && Math.Abs(dx) > 15;
        if ((dx < -70 || flick && dx < 0) && _tab < _tabs.Length - 1) SelectTab(_tab + 1);
        else if ((dx > 70 || flick && dx > 0) && _tab > 0) SelectTab(_tab - 1);
        else SlideHome((FrameworkElement)_pages[_tab]!);
    }

    private void CancelDrag()
    {
        _dragging = _pressed = false;
        if (Mouse.Captured == PageHost) PageHost.ReleaseMouseCapture();
        if (_pages[_tab] is FrameworkElement page) SlideHome(page);
    }

    // ---------- Window messages ----------

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_MOUSEHWHEEL when _mode == Mode.Expanded:
                // WPF has no event for horizontal scrolling (touchpad two-finger swipe).
                short delta = (short)((long)wParam >> 16);
                if (Math.Abs(delta) >= 20) WheelTab(delta > 0 ? 1 : -1);
                handled = true;
                break;
            case Native.WM_HOTKEY:
                ToggleTab((int)wParam == HotkeyApps ? AppsTab : ClipboardTab);
                handled = true;
                break;
            case Native.WM_CLIPBOARDUPDATE:
                _clipboard.OnClipboardUpdate();
                break;
        }
        return 0;
    }

    private void ToggleTab(int tab)
    {
        if (_mode == Mode.Expanded && _tab == tab && IsActive)
        {
            Collapse();
            return;
        }
        Expand(tab);
        Activate(); // a hotkey press lets us take the foreground, so typing goes to the search box
    }

    // ---------- IIslandHost ----------

    public void PasteIntoPreviousApp()
    {
        nint target = _previousForeground;
        Collapse();
        if (target == 0 || !Native.IsWindow(target)) return;
        Native.SetForegroundWindow(target);
        _ = SendPasteSoon();

        static async Task SendPasteSoon()
        {
            await Task.Delay(90); // let the target window take focus before the keys arrive
            Native.SendCtrlV();
        }
    }

    public IDisposable HoldOpen()
    {
        _holdOpen++;
        return new Releaser(() =>
        {
            _holdOpen--;
            // Whatever the hold covered (a speed test, a dialog) is done; leave its result up
            // for a moment, then close unless the pointer is on the island.
            if (_holdOpen > 0 || _mode != Mode.Expanded || _hover) return;
            _leaveTimer.Interval = TimeSpan.FromSeconds(5);
            _leaveTimer.Start();
        });
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose()
        {
            _release?.Invoke();
            _release = null;
        }
    }

    // ---------- Voice assistant ----------

    public AssistantPanel Panel => AssistantUI;

    public void BeginAssistant()
    {
        _assistantDismiss.Stop();
        if (_mode != Mode.Assistant)
        {
            Collapse(); // from an open panel or a peek
            _mode = Mode.Assistant;
            _peekHoverTimer.Stop();
            Fade(Compact, 0, 90);
            AssistantBar.Visibility = Visibility.Visible;
            Fade(AssistantBar, 1, 180, delay: 90);
            SetBarsRunning(false);

            // The ghost slides down into view as the notch drops open (the notch's outline clips it).
            GhostDrop.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-44, 0, TimeSpan.FromMilliseconds(560))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 }
            });
            Ghost.Animate(true);
        }
        // Shown even over a fullscreen app: the user asked for it.
        _hiddenForFullscreen = false;
        Island.Visibility = Visibility.Visible;
        // Keeps the mascot and card out of the screenshots taken of circled areas.
        ScreenCapture.ExcludeFromCapture(this, true);

        var pulse = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(600))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(pulse, 30);
        AssistantDot.BeginAnimation(OpacityProperty, pulse);
        AssistantUI.Show();
        UpdateTargets();
    }

    public void SetAssistantStatus(string text) => AssistantStatus.Text = text;

    public void SetAssistantLevel(double level)
    {
        Ghost.SetLevel(level);
        double[] shape = [0.55, 0.85, 1, 0.75, 0.5]; // taller in the middle, like a voice meter
        for (int i = 0; i < _levelScales.Length; i++)
        {
            double target = 0.2 + 0.8 * Math.Clamp(level * shape[i] * (0.8 + 0.4 * Random.Shared.NextDouble()), 0, 1);
            _levelScales[i].BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(110)));
        }
    }

    public void EndAssistant(TimeSpan after)
    {
        AssistantDot.BeginAnimation(OpacityProperty, null);
        _assistantDismiss.Interval = after;
        _assistantDismiss.Start();
    }

    public void SpeakingFor(TimeSpan duration) => Ghost.Speak(duration);

    public void DismissAssistant()
    {
        if (_mode != Mode.Assistant) return;
        _assistantDismiss.Stop();
        _mode = Mode.Compact;
        AssistantDot.BeginAnimation(OpacityProperty, null);
        AssistantUI.Hide(() => ScreenCapture.ExcludeFromCapture(this, false));
        // The ghost ducks back up into the notch as it closes.
        GhostDrop.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-44, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseIn, Amplitude = 0.3 }
        });
        Fade(AssistantBar, 0, 140, delay: 120, completed: () =>
        {
            if (_mode == Mode.Assistant) return;
            AssistantBar.Visibility = Visibility.Collapsed;
            Ghost.Animate(false);
        });
        Fade(Compact, 1, 160, delay: 220);
        UpdateCompact();
        UpdateTargets();
    }

    private void BuildLevelBars()
    {
        var lavender = new SolidColorBrush(Color.FromRgb(0xC9, 0xB6, 0xFF));
        lavender.Freeze();
        for (int i = 0; i < _levelScales.Length; i++)
        {
            _levelScales[i] = new ScaleTransform(1, 0.2);
            LevelBars.Children.Add(new Rectangle
            {
                Width = 2.5,
                RadiusX = 1.25,
                RadiusY = 1.25,
                Margin = new Thickness(1.25, 0, 1.25, 0),
                Fill = lavender,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _levelScales[i]
            });
        }
    }

    // ---------- Compact live activity ----------

    private void BuildBars()
    {
        for (int i = 0; i < _barScales.Length; i++)
        {
            _barScales[i] = new ScaleTransform(1, 0.3);
            Bars.Children.Add(new Rectangle
            {
                Width = 3,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Margin = new Thickness(1.5, 0, 1.5, 0),
                Fill = Brushes.White,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _barScales[i]
            });
        }
    }

    private void SetBarsRunning(bool run)
    {
        if (run == _barsRunning) return;
        _barsRunning = run;
        double[] periods = [0.42, 0.55, 0.36, 0.48];
        for (int i = 0; i < _barScales.Length; i++)
        {
            if (!run)
            {
                _barScales[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _barScales[i].ScaleY = 0.3;
                continue;
            }
            var anim = new DoubleAnimation(0.25, 1, TimeSpan.FromSeconds(periods[i]))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(i * 0.11),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            // 30 fps is plenty for a 14px meter and halves the redraw cost.
            Timeline.SetDesiredFrameRate(anim, 30);
            _barScales[i].BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
    }

    private void OnMediaChanged()
    {
        UpdateCompact();
        if (_mode == Mode.Compact) UpdateTargets();
        else if (_mode == Mode.Peek && PeekHost.Content == _peekCard && !_media.HasSession) Collapse();
    }

    private void OnTimerChanged()
    {
        bool show = _timer.IsActive && !_media.HasSession;
        CompactTimerText.Text = TimerService.Format(_timer.Remaining);
        if (show == _timerShown) return;
        UpdateCompact();
        if (_mode == Mode.Compact) UpdateTargets();
    }

    private void UpdateCompact()
    {
        bool media = _media.HasSession;
        _timerShown = !media && _timer.IsActive;
        CompactArt.Visibility = Bars.Visibility = media ? Visibility.Visible : Visibility.Hidden;
        CompactTimerIcon.Visibility = CompactTimerText.Visibility = _timerShown ? Visibility.Visible : Visibility.Hidden;

        CompactArt.Background = _media.Art != null
            ? new ImageBrush(_media.Art) { Stretch = Stretch.UniformToFill }
            : (Brush)FindResource("TrackBrush");
        var accent = new SolidColorBrush(_media.Accent);
        accent.Freeze();
        foreach (Rectangle bar in Bars.Children) bar.Fill = accent;

        SetBarsRunning(media && _media.IsPlaying && _mode == Mode.Compact && !_hiddenForFullscreen);
    }

    // ---------- Context menu ----------

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (StartupItem.IsChecked)
            key.SetValue("GhostyNav", $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue("GhostyNav", throwOnMissingValue: false);
    }

    /// <summary>"Start with Windows" set up before the rename pointed at the old exe: point it here instead.</summary>
    private static void MoveStartupEntryFromOldName()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue("DynamicIsland") == null) return;
        key.DeleteValue("DynamicIsland", throwOnMissingValue: false);
        key.SetValue("GhostyNav", $"\"{Environment.ProcessPath}\"");
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    internal static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match) return match;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
