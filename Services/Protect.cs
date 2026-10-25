using System.Security.Cryptography;
using System.Text;

namespace GhostyNav.Services;

/// <summary>
/// Encrypts small secrets at rest with Windows DPAPI, tied to the current Windows account: other
/// accounts, or the files copied elsewhere, can't read them. Each purpose gets its own entropy so
/// one encrypted value can't be swapped in for another.
/// </summary>
public static class Protect
{
    public static byte[] Encrypt(string plain, string purpose) => EncryptBytes(Encoding.UTF8.GetBytes(plain), purpose);

    public static byte[] EncryptBytes(byte[] plain, string purpose) =>
        ProtectedData.Protect(plain, Entropy(purpose), DataProtectionScope.CurrentUser);

    /// <returns>null when the data is damaged or was encrypted by another account or PC.</returns>
    public static byte[]? DecryptBytes(byte[] blob, string purpose)
    {
        try
        {
            return ProtectedData.Unprotect(blob, Entropy(purpose), DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <returns>null when the data is missing, damaged, or was encrypted by another account or PC.</returns>
    public static string? Decrypt(byte[]? blob, string purpose)
    {
        if (blob is not { Length: > 0 }) return null;
        foreach (var entropy in new[] { Entropy(purpose), LegacyEntropy(purpose) })
        {
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, entropy, DataProtectionScope.CurrentUser));
            }
            catch (CryptographicException) { }
        }
        return null;
    }

    /// <summary>Encrypted and base64-encoded, for storing in JSON.</summary>
    public static string? EncryptToText(string? plain, string purpose) =>
        plain == null ? null : Convert.ToBase64String(Encrypt(plain, purpose));

    public static string? DecryptText(string? text, string purpose)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            return Decrypt(Convert.FromBase64String(text), purpose);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static byte[] Entropy(string purpose) => Encoding.UTF8.GetBytes("GhostyNav/" + purpose);

    // Values saved before the app was renamed GhostyNav; still readable, re-encrypted when next saved.
    private static byte[] LegacyEntropy(string purpose) => Encoding.UTF8.GetBytes("DynamicIsland/" + purpose);
}
