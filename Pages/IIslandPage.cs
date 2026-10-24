using System.Windows;

namespace GhostyNav.Pages;

/// <summary>
/// A tab of the expanded island. Pages are created on first visit and only run timers
/// while visible, so a collapsed island costs no CPU.
/// </summary>
public interface IIslandPage
{
    /// <summary>Content size below the tab bar; the island morphs to fit it.</summary>
    Size PreferredSize { get; }
    void OnShow();
    void OnHide();
}

/// <summary>A page whose size depends on its content; the island re-fits when it changes.</summary>
public interface IResizingPage
{
    event Action? PreferredSizeChanged;
}

/// <summary>What pages may ask of the island window.</summary>
public interface IIslandHost
{
    void Collapse();
    /// <summary>Collapses, gives focus back to the app that was active before the island, and sends Ctrl+V.</summary>
    void PasteIntoPreviousApp();
    /// <summary>Keeps the island open while a dialog owned by it is showing.</summary>
    IDisposable HoldOpen();
    /// <summary>Settings opens from Controls, like a page beneath it, and returns there.</summary>
    void OpenSettings();
    void CloseSettings();
}
