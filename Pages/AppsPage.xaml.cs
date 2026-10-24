using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GhostyNav.Services;

namespace GhostyNav.Pages;

/// <summary>A row of the apps list: either a section header or a launchable entry.</summary>
public sealed class AppRow : INotifyPropertyChanged
{
    private ImageSource? _icon;

    /// <summary>The app's own icon, once loaded; until then the row shows its first letter.</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasIcon)));
        }
    }

    public bool HasIcon => _icon != null;
    public event PropertyChangedEventHandler? PropertyChanged;

    public string? Header { get; init; }
    public AppEntry? App { get; init; }
    public string Title { get; init; } = "";
    public string Letter { get; init; } = "";
    public string Category { get; init; } = "";
    public bool Starred { get; init; }
    public bool IsWeb { get; init; }
    public bool IsHeader => Header != null;
    public bool IsItem => Header == null;
    public string StarGlyph => Starred ? "\uE735" : "\uE734";
}

public partial class AppsPage : UserControl, IIslandPage
{
    private readonly AppSettings _settings;
    private readonly IIslandHost _host;
    private bool _loading;

    public Size PreferredSize => new(440, 360);

    public AppsPage(AppSettings settings, IIslandHost host)
    {
        InitializeComponent();
        _settings = settings;
        _host = host;
        SearchBox.TextChanged += (_, _) => Rebuild();
        PreviewKeyDown += OnKey;
    }

    public async void OnShow()
    {
        SearchBox.Clear();
        Rebuild();
        await Dispatcher.InvokeAsync(() => Keyboard.Focus(SearchBox), System.Windows.Threading.DispatcherPriority.Input);

        // Picks up newly installed apps; returns the cached list when it's fresh.
        var load = AppIndex.GetAsync();
        if (load.IsCompleted && AppIndex.Apps.Count > 0) return;
        _loading = true;
        if (AppIndex.Apps.Count == 0) ShowStatus("Loading apps…");
        await load;
        _loading = false;
        Rebuild();
    }

    public void OnHide() { }

    private void ShowStatus(string? text)
    {
        Status.Text = text ?? "";
        Status.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Rebuild()
    {
        string q = SearchBox.Text.Trim();
        var apps = AppIndex.Apps;
        var starred = _settings.StarredApps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new List<AppRow>();

        if (q.Length == 0)
        {
            var byId = apps.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
            var star = _settings.StarredApps.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            var recent = _settings.RecentApps.Where(id => byId.ContainsKey(id) && !starred.Contains(id))
                                             .Select(id => byId[id]).Take(4).ToList();
            AddSection(rows, "STARRED", star, starred);
            AddSection(rows, "RECENT", recent, starred);
            AddSection(rows, "APPLICATIONS", apps, starred);
        }
        else
        {
            var matches = apps.Select(a => (App: a, Score: AppIndex.Score(a.Name, q) + (starred.Contains(a.Id) ? 0.3 : 0)))
                               .Where(m => m.Score > 0.3)
                               .OrderByDescending(m => m.Score).ThenBy(m => m.App.Name)
                               .Select(m => m.App).Take(40).ToList();
            AddSection(rows, "RESULTS", matches, starred);
            rows.Add(new AppRow { Title = $"Search the web for “{q}”", Letter = "\uE774", Category = "Web", IsWeb = true });
        }

        LoadIcons(rows);
        List.ItemsSource = rows;
        List.SelectedItem = rows.FirstOrDefault(r => r.IsItem);
        if (List.SelectedItem != null) List.ScrollIntoView(List.SelectedItem);
        ShowStatus(apps.Count == 0 && !_loading && q.Length == 0 ? "No apps found" : _loading && apps.Count == 0 ? "Loading apps…" : null);
    }

    /// <summary>Icons already loaded show at once; the rest arrive one by one in list order.</summary>
    private void LoadIcons(List<AppRow> rows)
    {
        int pixels = (int)Math.Round(24 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        foreach (var row in rows)
        {
            if (row.App is not { } app) continue;
            var load = IconLoader.ForAppAsync(app.Id, pixels);
            if (IconLoader.Cached(load) is { } icon) row.Icon = icon;
            else load.ContinueWith(t => row.Icon = t.Result, ui);
        }
    }

    private static void AddSection(List<AppRow> rows, string header, IEnumerable<AppEntry> apps, HashSet<string> starred)
    {
        bool first = true;
        foreach (var app in apps)
        {
            if (first)
            {
                rows.Add(new AppRow { Header = header });
                first = false;
            }
            rows.Add(new AppRow
            {
                App = app,
                Title = app.Name,
                Letter = app.Name[..1].ToUpperInvariant(),
                Category = app.IsGame ? "Game" : "Application",
                Starred = starred.Contains(app.Id)
            });
        }
    }

    // ---------- Actions ----------

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: Move(1); break;
            case Key.Up: Move(-1); break;
            case Key.Enter: Open(List.SelectedItem as AppRow); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Move(int direction)
    {
        if (List.ItemsSource is not List<AppRow> rows) return;
        int i = List.SelectedIndex;
        do i += direction; while (i >= 0 && i < rows.Count && rows[i].IsHeader);
        if (i < 0 || i >= rows.Count) return;
        List.SelectedIndex = i;
        List.ScrollIntoView(rows[i]);
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        // Single click opens, like a launcher; the star button handles its own clicks.
        if (e.OriginalSource is DependencyObject d && MainWindow.FindAncestor<Button>(d) != null) return;
        if (MainWindow.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is AppRow { IsItem: true } row)
            Open(row);
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void Open(AppRow? row)
    {
        if (row == null || row.IsHeader) return;
        if (row.IsWeb)
        {
            string url = "https://www.google.com/search?q=" + Uri.EscapeDataString(SearchBox.Text.Trim());
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Debug.WriteLine($"Web search failed: {ex.Message}"); }
            _host.Collapse();
            return;
        }
        if (row.App is not { } app) return;

        AppIndex.Launch(app);
        _settings.RecentApps.RemoveAll(id => id.Equals(app.Id, StringComparison.OrdinalIgnoreCase));
        _settings.RecentApps.Insert(0, app.Id);
        if (_settings.RecentApps.Count > 8) _settings.RecentApps.RemoveRange(8, _settings.RecentApps.Count - 8);
        _settings.Save();
        _host.Collapse();
    }

    private void OnStar(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not AppRow { App: { } app }) return;
        if (_settings.StarredApps.RemoveAll(id => id.Equals(app.Id, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            _settings.StarredApps.Add(app.Id);
            Controls.Fx.Sparkle((FrameworkElement)sender, Controls.Fx.Gold);
        }
        _settings.Save();
        Rebuild();
        e.Handled = true;
    }
}
