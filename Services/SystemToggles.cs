using Microsoft.Win32;

namespace GhostyNav.Services;

/// <summary>Night Light and dark mode, both stored per user in the registry.</summary>
public static class SystemToggles
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    // Night Light has no public API. Its state is a CloudStore blob that Windows watches;
    // format reverse-engineered by github.com/nathanbabcock/nightlight-cli.
    private const string NightLightKey =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\" +
        @"default$windows.data.bluelightreduction.bluelightreductionstate\" +
        @"windows.data.bluelightreduction.bluelightreductionstate";

    public static bool IsDarkMode
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
    }

    public static Task SetDarkModeAsync(bool dark) => Task.Run(() =>
    {
        using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
        int light = dark ? 0 : 1;
        key.SetValue("AppsUseLightTheme", light, RegistryValueKind.DWord);
        key.SetValue("SystemUsesLightTheme", light, RegistryValueKind.DWord);
        // Explorer, the taskbar and running apps re-read the theme on this broadcast.
        Native.BroadcastSettingChange("ImmersiveColorSet");
    });

    public static bool? IsNightLightOn
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(NightLightKey);
            if (key?.GetValue("Data") is not byte[] data || data.Length < 19) return null; // never configured
            return data[18] == 0x15;
        }
    }

    public static bool SetNightLight(bool on)
    {
        using var key = Registry.CurrentUser.OpenSubKey(NightLightKey, writable: true);
        if (key?.GetValue("Data") is not byte[] data || data.Length < 41) return false;
        bool isOn = data[18] == 0x15;
        if (isOn == on) return true;

        byte[] next;
        if (isOn)
        {
            // Drop the two "enabled" marker bytes at 23..24.
            next = new byte[data.Length - 2];
            Array.Copy(data, 0, next, 0, 23);
            Array.Copy(data, 25, next, 23, data.Length - 25);
            next[18] = 0x13;
        }
        else
        {
            next = new byte[data.Length + 2];
            Array.Copy(data, 0, next, 0, 23);
            Array.Copy(data, 23, next, 25, data.Length - 23);
            next[18] = 0x15;
            next[23] = 0x10;
            next[24] = 0x00;
        }

        // Bump the embedded timestamp so Windows notices the change.
        for (int i = 10; i < 15; i++)
        {
            if (next[i] != 0xFF)
            {
                next[i]++;
                break;
            }
        }
        key.SetValue("Data", next, RegistryValueKind.Binary);
        return true;
    }
}
