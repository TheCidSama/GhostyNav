using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace GhostyNav.Services;

public sealed record MirroredNotification(string App, string Title, string Body);

/// <summary>
/// Shows new Windows notifications in the notch. Windows' change event only reaches packaged apps,
/// so the notification list is read every few seconds instead: a cheap local call, and only the
/// IDs are compared until something new arrives. Nothing leaves the PC.
/// </summary>
public sealed class NotificationMirror
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly HashSet<uint> _seen = [];
    private UserNotificationListener? _listener;
    private bool _primed, _busy;

    public event Action<MirroredNotification>? Arrived;

    public NotificationMirror() => _poll.Tick += async (_, _) => await PollAsync();

    /// <returns>False when Windows doesn't allow it (Settings › Privacy › Notifications).</returns>
    public async Task<bool> StartAsync()
    {
        try
        {
            _listener ??= UserNotificationListener.Current;
            if (await _listener.RequestAccessAsync() != UserNotificationListenerAccessStatus.Allowed) return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Notifications unavailable: {ex.Message}");
            return false;
        }
        _primed = false;
        await PollAsync(); // remembers what's already there, so only new ones show
        _poll.Start();
        return true;
    }

    public void Stop()
    {
        _poll.Stop();
        _seen.Clear();
    }

    private async Task PollAsync()
    {
        if (_listener == null || _busy) return;
        _busy = true;
        try
        {
            var current = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var fresh = new List<UserNotification>();
            var ids = new HashSet<uint>();
            foreach (var n in current)
            {
                ids.Add(n.Id);
                if (!_seen.Contains(n.Id)) fresh.Add(n);
            }
            _seen.IntersectWith(ids);
            _seen.UnionWith(ids);
            if (!_primed)
            {
                _primed = true;
                return;
            }
            // The newest only: a burst (an app catching up) shouldn't queue a parade of peeks.
            if (fresh.OrderByDescending(n => n.CreationTime).FirstOrDefault() is { } newest && Describe(newest) is { } shown)
                Arrived?.Invoke(shown);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Reading notifications failed: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private static MirroredNotification? Describe(UserNotification n)
    {
        var texts = n.Notification.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements()
            .Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];
        string app;
        try { app = n.AppInfo.DisplayInfo.DisplayName; }
        catch (Exception) { app = "Notification"; }
        if (texts.Count == 0) return null;
        return new MirroredNotification(app, texts[0], string.Join(" · ", texts.Skip(1)));
    }
}
