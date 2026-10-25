using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GhostyNav.Services;

namespace GhostyNav.Controls;

/// <summary>
/// The shelf inside the notch: files dropped on it, as tiles that drag out again (into a folder,
/// an email, a chat). Double-click opens one.
/// </summary>
public sealed class ShelfView : Border
{
    public const double ShelfHeight = 92;
    private const int IconPixels = 32;

    private readonly ShelfService _shelf;
    private readonly StackPanel _tiles = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _title = new() { FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _clear;
    private readonly Border _dropZone;
    private Point _pressedAt;
    private string? _pressed;

    /// <summary>A tile is being dragged out: the island must ignore it coming back over the notch.</summary>
    public bool DraggingOut { get; private set; }

    public ShelfView(ShelfService shelf)
    {
        _shelf = shelf;
        Height = ShelfHeight;
        Padding = new Thickness(18, 8, 14, 10);

        var sub = (Brush)FindResource("SubTextBrush");
        _clear = new Button
        {
            Style = (Style)FindResource("FlatButton"), Content = "Clear", Height = 20, Padding = new Thickness(8, 0, 8, 0),
            FontSize = 10.5, Background = Brushes.Transparent, Foreground = sub
        };
        _clear.Click += (_, e) =>
        {
            _shelf.Clear();
            e.Handled = true;
        };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(_clear, Dock.Right);
        header.Children.Add(_clear);
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = "\uE7B8", Style = (Style)FindResource("Icon"), FontSize = 11, Foreground = sub, Margin = new Thickness(0, 1, 7, 0) },
                _title
            }
        });

        _dropZone = new Border
        {
            CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1.5), Visibility = Visibility.Collapsed,
            BorderBrush = (Brush)FindResource("AccentBrush"), Background = (Brush)FindResource("AccentDimBrush"),
            Child = new TextBlock
            {
                Text = "Drop to keep it here", FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false, Content = _tiles
        };
        // The mouse wheel scrolls sideways through a long shelf.
        scroll.PreviewMouseWheel += (_, e) =>
        {
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta / 2.0);
            e.Handled = true;
        };
        var body = new Grid();
        body.Children.Add(scroll);
        body.Children.Add(_dropZone);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(body);
        Child = root;

        _shelf.Changed += Render;
        Render();
    }

    /// <summary>Highlights the shelf as a drop target while files are dragged over the notch.</summary>
    public void SetDropping(bool dropping)
    {
        _dropZone.Visibility = dropping ? Visibility.Visible : Visibility.Collapsed;
        Render();
    }

    public void Render()
    {
        var items = _shelf.Items;
        _title.Text = items.Count switch { 0 => "Shelf", 1 => "Shelf · 1 file", var n => $"Shelf · {n} files" };
        _clear.Visibility = items.Count > 0 && _dropZone.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        _tiles.Children.Clear();
        foreach (var path in items.Reverse()) _tiles.Children.Add(Tile(path));
    }

    private FrameworkElement Tile(string path)
    {
        string name = Path.GetFileName(path.TrimEnd('\\'));
        var image = new Image { Width = 32, Height = 32, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        _ = LoadIconAsync(image, path);

        var remove = new Button
        {
            Style = (Style)FindResource("IconButton"), Content = "\uE711", FontSize = 7, Width = 16, Height = 16,
            Background = (Brush)FindResource("TrackBrush"), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -2, 0), Visibility = Visibility.Hidden,
            ToolTip = "Take off the shelf"
        };
        remove.Click += (_, e) =>
        {
            _shelf.Remove(path);
            e.Handled = true;
        };

        var tile = new Grid { Width = 62, Margin = new Thickness(0, 0, 4, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = path };
        tile.Children.Add(new StackPanel
        {
            Children =
            {
                image,
                new TextBlock
                {
                    Text = name, FontSize = 9.5, Margin = new Thickness(2, 4, 2, 0), TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis, Foreground = (Brush)FindResource("TextBrush")
                }
            }
        });
        tile.Children.Add(remove);
        tile.MouseEnter += (_, _) => remove.Visibility = Visibility.Visible;
        tile.MouseLeave += (_, _) => remove.Visibility = Visibility.Hidden;

        tile.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && MainWindow.FindAncestor<Button>(d) != null) return;
            if (e.ClickCount == 2)
            {
                Open(path);
                e.Handled = true;
                return;
            }
            _pressed = path;
            _pressedAt = e.GetPosition(this);
        };
        tile.PreviewMouseMove += (_, e) =>
        {
            if (_pressed != path || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _pressedAt.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - _pressedAt.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _pressed = null;
            DragOut(tile, path);
        };
        // Releasing on a tile mustn't open the whole island.
        tile.MouseLeftButtonUp += (_, e) =>
        {
            _pressed = null;
            e.Handled = true;
        };
        return tile;
    }

    private void DragOut(DependencyObject source, string path)
    {
        var data = new DataObject(DataFormats.FileDrop, new[] { path });
        DraggingOut = true;
        try
        {
            // Moved somewhere else (Shift-drag into a folder): it's no longer where the shelf points.
            if (DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link)
                == DragDropEffects.Move)
                _shelf.Remove(path);
        }
        finally
        {
            DraggingOut = false;
        }
        Render(); // a moved file drops off the shelf
    }

    private static async Task LoadIconAsync(Image image, string path)
    {
        int pixels = (int)Math.Round(IconPixels * VisualTreeHelper.GetDpi(image).DpiScaleX);
        image.Source = await IconLoader.ForFileAsync(path, pixels);
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"Couldn't open {path}: {ex.Message}");
        }
    }
}
