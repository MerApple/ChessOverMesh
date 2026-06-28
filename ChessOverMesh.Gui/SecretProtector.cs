using System.Security.Cryptography;
using System.Text;

namespace ChessOverMesh.Gui;

/// <summary>
/// Protects small secrets (the cached channel keys) at rest with Windows DPAPI, scoped to the
/// current user — so they're unreadable by other users or if the cache file is copied elsewhere.
/// Protected values are tagged with a "dpapi:" prefix; untagged values are treated as legacy
/// plaintext (so older caches keep working and migrate on next edit).
/// </summary>
internal static class SecretProtector
{
    private const string Prefix = "dpapi:";

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain;
        try
        {
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(data);
        }
        catch
        {
            return plain;   // DPAPI unavailable — fall back to storing as-is
        }
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;   // legacy plaintext
        try
        {
            byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(Prefix.Length)),
                                                  null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            return string.Empty;   // can't decrypt (different user/machine) — treat as no key
        }
    }
}
