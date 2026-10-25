using System.IO;

namespace GhostyNav.Services;

/// <summary>
/// Files parked on the notch, to drag out again somewhere else. Only their paths are kept (in
/// settings), never copies, so the shelf costs no disk space; files moved or deleted since drop off.
/// </summary>
public sealed class ShelfService(AppSettings settings)
{
    public const int MaxItems = 24;

    public event Action? Changed;

    public IReadOnlyList<string> Items
    {
        get
        {
            if (settings.Shelf.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p)) > 0) settings.Save();
            return settings.Shelf;
        }
    }

    public int Count => Items.Count;

    /// <returns>How many were new.</returns>
    public int Add(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            settings.Shelf.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            settings.Shelf.Add(path);
            added++;
        }
        while (settings.Shelf.Count > MaxItems) settings.Shelf.RemoveAt(0);
        if (added == 0) return 0;
        settings.Save();
        Changed?.Invoke();
        return added;
    }

    public void Remove(string path)
    {
        if (settings.Shelf.Remove(path)) Commit();
    }

    public void Clear()
    {
        settings.Shelf.Clear();
        Commit();
    }

    private void Commit()
    {
        settings.Save();
        Changed?.Invoke();
    }
}
