using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GhostyNav.Services;

namespace GhostyNav.Pages;

public partial class ClipboardPage : UserControl, IIslandPage
{
    private static readonly string[] FilterNames = ["All", "Text", "Images", "Links"];

    private readonly ClipboardService _clipboard;
    private readonly IIslandHost _host;
    private int _filter;
    private bool _visible;

    public Size PreferredSize => new(540, 262);

    public ClipboardPage(ClipboardService clipboard, IIslandHost host)
    {
        InitializeComponent();
        _clipboard = clipboard;
        _host = host;

        for (int i = 0; i < FilterNames.Length; i++)
        {
            int index = i;
            var chip = new RadioButton
            {
                Style = (Style)FindResource("Chip"),
                Content = FilterNames[i],
                GroupName = "ClipFilter",
                IsChecked = i == 0
            };
            chip.Checked += (_, _) => { _filter = index; Rebuild(); };
            Filters.Children.Add(chip);
        }

        SearchBox.TextChanged += (_, _) => Rebuild();
        PreviewKeyDown += OnKey;
        _clipboard.Changed += () => { if (_visible) Rebuild(); };
    }

    public void OnShow()
    {
        _visible = true;
        Rebuild();
        Dispatcher.InvokeAsync(() => Keyboard.Focus(SearchBox), System.Windows.Threading.DispatcherPriority.Input);
    }

    public void OnHide()
    {
        _visible = false;
        PreviewImageHost.Background = null; // let the full preview bitmap go
    }

    // ---------- List ----------

    private IEnumerable<ClipItem> Filtered()
    {
        string q = SearchBox.Text.Trim();
        return _clipboard.Items.Where(i =>
            (_filter switch
            {
                1 => i.Kind is ClipKind.Text or ClipKind.Files,
                2 => i.Kind == ClipKind.Image,
                3 => i.Kind == ClipKind.Link,
                _ => true
            }) &&
            (q.Length == 0 || i.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase)));
    }

    private void Rebuild()
    {
        var selected = (List.SelectedItem as ListBoxItem)?.Tag as ClipItem;
        var items = Filtered().ToList();
        SearchBox.Tag = $"Search {_clipboard.Items.Count} items...";

        List.Items.Clear();
        var today = DateTime.Today;
        AddGroup("PINNED", items.Where(i => i.Pinned));
        AddGroup("TODAY", items.Where(i => !i.Pinned && i.Copied.Date == today));
        AddGroup("YESTERDAY", items.Where(i => !i.Pinned && i.Copied.Date == today.AddDays(-1)));
        AddGroup("EARLIER", items.Where(i => !i.Pinned && i.Copied.Date < today.AddDays(-1)));

        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _clipboard.Items.Count == 0 ? "Copy something and it shows up here." : "No matches.";

        var keep = List.Items.OfType<ListBoxItem>().FirstOrDefault(r => r.Tag == selected && selected != null)
                   ?? List.Items.OfType<ListBoxItem>().FirstOrDefault(r => r.IsEnabled);
        List.SelectedItem = keep;
        if (keep == null) ShowPreview(null);
    }

    private void AddGroup(string title, IEnumerable<ClipItem> items)
    {
        bool first = true;
        foreach (var item in items)
        {
            if (first)
            {
                List.Items.Add(new ListBoxItem
                {
                    IsEnabled = false,
                    Content = new TextBlock { Text = title, Style = (Style)FindResource("Label"), FontSize = 9.5 }
                });
                first = false;
            }
            List.Items.Add(MakeRow(item));
        }
    }

    private ListBoxItem MakeRow(ClipItem item)
    {
        var sub = (Brush)FindResource("SubTextBrush");
        var icon = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x28)),
            Child = new TextBlock
            {
                Style = (Style)FindResource("Icon"), FontSize = 10,
                Text = item.Pinned ? "\uE718" : Glyph(item.Kind)
            }
        };
        var time = new TextBlock { Text = Ago(item.Copied), FontSize = 9.5, Foreground = sub, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        var text = new TextBlock
        {
            Text = Summary(item), FontSize = 11.5, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
        };
        var dock = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(time, Dock.Right);
        dock.Children.Add(icon);
        dock.Children.Add(time);
        dock.Children.Add(text);
        return new ListBoxItem { Content = dock, Tag = item };
    }

    private static string Glyph(ClipKind kind) => kind switch
    {
        ClipKind.Link => "\uE71B",
        ClipKind.Image => "\uE8B9",
        ClipKind.Files => "\uE8B7",
        _ => "\uE7C3"
    };

    private static string Summary(ClipItem item) => item.Kind switch
    {
        ClipKind.Image => $"Image • {item.Width}×{item.Height}",
        ClipKind.Files => string.Join(", ", item.Text.Split('\n').Select(Path.GetFileName)),
        _ => item.Text.Trim().ReplaceLineEndings(" ")
    };

    private static string Ago(DateTime when)
    {
        var d = DateTime.Now - when;
        return d.TotalMinutes < 1 ? "now"
            : d.TotalHours < 1 ? $"{(int)d.TotalMinutes}m"
            : d.TotalDays < 1 ? $"{(int)d.TotalHours}h"
            : $"{(int)d.TotalDays}d";
    }

    // ---------- Preview ----------

    private ClipItem? Selected => (List.SelectedItem as ListBoxItem)?.Tag as ClipItem;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowPreview(Selected);
        if (List.SelectedItem != null) List.ScrollIntoView(List.SelectedItem);
    }

    private void ShowPreview(ClipItem? item)
    {
        Meta.Visibility = item == null ? Visibility.Hidden : Visibility.Visible;
        PreviewImageHost.Visibility = Visibility.Collapsed;
        PreviewImageHost.Background = null;
        PreviewText.Visibility = Visibility.Visible;
        if (item == null)
        {
            PreviewText.Text = "";
            return;
        }

        var culture = CultureInfo.CurrentCulture;
        string day = item.Copied.Date == DateTime.Today ? "Today"
            : item.Copied.Date == DateTime.Today.AddDays(-1) ? "Yesterday"
            : item.Copied.ToString("MMM d", culture);
        MetaCopied.Text = $"{day}, {item.Copied.ToString("t", culture)}";

        switch (item.Kind)
        {
            case ClipKind.Image:
                MetaType.Text = "Image";
                MetaSize.Text = $"{item.Width} × {item.Height}";
                PreviewText.Visibility = Visibility.Collapsed;
                if (ClipboardService.LoadImage(item, 320) is { } image)
                {
                    // Fit inside the preview area, keeping the aspect ratio.
                    double scale = Math.Min(250.0 / image.PixelWidth, 120.0 / image.PixelHeight);
                    PreviewImageHost.Width = image.PixelWidth * scale;
                    PreviewImageHost.Height = image.PixelHeight * scale;
                    PreviewImageHost.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                    PreviewImageHost.Visibility = Visibility.Visible;
                }
                else
                {
                    PreviewText.Visibility = Visibility.Visible;
                    PreviewText.Text = "Image file is missing.";
                }
                break;
            case ClipKind.Files:
                var files = item.Text.Split('\n');
                MetaType.Text = "Files";
                MetaSize.Text = files.Length == 1 ? "1 file" : $"{files.Length} files";
                PreviewText.Text = string.Join("\n", files);
                break;
            default:
                string text = item.Text;
                int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                int lines = text.Split('\n').Length;
                MetaType.Text = item.Kind == ClipKind.Link ? "Link" : "Text";
                MetaSize.Text = $"{text.Length} characters • {words} words • {lines} line{(lines == 1 ? "" : "s")}";
                PreviewText.Text = text.Length > 1500 ? text[..1500] + "…" : text;
                break;
        }
    }

    // ---------- Actions ----------

    private void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Down: Move(1); break;
            case Key.Up: Move(-1); break;
            case Key.Enter: Paste(); break;
            case Key.Tab when !ctrl:
                ((RadioButton)Filters.Children[(_filter + 1) % FilterNames.Length]).IsChecked = true;
                break;
            case Key.C when ctrl && SearchBox.SelectionLength == 0:
                if (Selected is { } c) _clipboard.CopyToClipboard(c);
                break;
            case Key.P when ctrl:
                if (Selected is { } p) _clipboard.TogglePin(p);
                break;
            case Key.Delete when SearchBox.Text.Length == 0:
                Remove();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Move(int direction)
    {
        var rows = List.Items.OfType<ListBoxItem>().Where(r => r.IsEnabled).ToList();
        if (rows.Count == 0) return;
        int index = List.SelectedItem is ListBoxItem current ? rows.IndexOf(current) : -1;
        List.SelectedItem = rows[Math.Clamp(index + direction, 0, rows.Count - 1)];
    }

    private void Remove()
    {
        if (Selected is not { } item) return;
        int index = List.SelectedIndex;
        _clipboard.Remove(item);
        // Keep the cursor roughly where it was.
        var rows = List.Items.OfType<ListBoxItem>().ToList();
        List.SelectedItem = rows.Skip(Math.Max(0, index - 1)).FirstOrDefault(r => r.IsEnabled)
                            ?? rows.FirstOrDefault(r => r.IsEnabled);
    }

    private void Paste()
    {
        if (Selected is not { } item) return;
        if (_clipboard.CopyToClipboard(item)) _host.PasteIntoPreviousApp();
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && MainWindow.FindAncestor<ListBoxItem>(d) is { IsEnabled: true })
            Paste();
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var clear = new MenuItem { Header = "Clear history (keeps pinned)" };
        clear.Click += (_, _) => _clipboard.ClearUnpinned();
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Items = { clear } };
        menu.IsOpen = true;
    }
}
