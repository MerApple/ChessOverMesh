using System.Text;

namespace ChessOverMesh.Maui;

/// <summary>
/// Android port of the desktop DPAPI-based secret protector. On Android the device-cache file already
/// lives in app-private storage, so we don't add a second encryption layer here; values are tagged so
/// the format matches the desktop cache. A Windows "dpapi:" value (e.g. from a copied cache) can't be
/// read here and is treated as "no key", exactly like the desktop fallback on a foreign machine.
/// </summary>
internal static class SecretProtector
{
    private const string Prefix = "b64:";
    private const string DpapiPrefix = "dpapi:";   // desktop-only; unreadable here

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain;
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal)) return string.Empty;   // Windows-protected — can't read
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;             // legacy plaintext
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(stored.Substring(Prefix.Length))); }
        catch { return string.Empty; }
    }
}
