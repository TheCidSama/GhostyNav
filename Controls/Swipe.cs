using System.Windows;

namespace GhostyNav.Controls;

/// <summary>
/// Marks elements that handle horizontal drags themselves (seek bars), so dragging them
/// doesn't also swipe to the next tab.
/// </summary>
public static class Swipe
{
    public static readonly DependencyProperty IgnoreProperty = DependencyProperty.RegisterAttached(
        "Ignore", typeof(bool), typeof(Swipe), new FrameworkPropertyMetadata(false));

    public static bool GetIgnore(DependencyObject d) => (bool)d.GetValue(IgnoreProperty);
    public static void SetIgnore(DependencyObject d, bool value) => d.SetValue(IgnoreProperty, value);
}
