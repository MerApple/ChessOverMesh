using System.Security.Cryptography;
using System.Text;

namespace ChessOverMesh.Mesh;

/// <summary>
/// AES-256-CBC text encryption with a passphrase. The 256-bit key is derived from the
/// passphrase with SHA-256, a fresh random IV is used per message, and the output is
/// base64( IV(16) + ciphertext ). Both ends must use the same passphrase.
/// </summary>
public static class AesText
{
    private static byte[] DeriveKey(string passphrase) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(passphrase));

    public static string Encrypt(string plainText, string passphrase)
    {
        using var aes = Aes.Create();
        aes.Key = DeriveKey(passphrase);
        aes.GenerateIV();
        byte[] iv = aes.IV;

        using var encryptor = aes.CreateEncryptor();
        byte[] plain = Encoding.UTF8.GetBytes(plainText);
        byte[] cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);

        byte[] combined = new byte[iv.Length + cipher.Length];
        Buffer.BlockCopy(iv, 0, combined, 0, iv.Length);
        Buffer.BlockCopy(cipher, 0, combined, iv.Length, cipher.Length);
        return Convert.ToBase64String(combined);
    }

    /// <summary>Base64-decodes then decrypts. Returns false if the input isn't valid for this key.</summary>
    public static bool TryDecrypt(string base64, string passphrase, out string plainText)
    {
        plainText = string.Empty;
        try
        {
            byte[] data = Convert.FromBase64String(base64);
            if (data.Length <= 16 || (data.Length - 16) % 16 != 0) return false;  // need IV + whole blocks

            using var aes = Aes.Create();
            aes.Key = DeriveKey(passphrase);
            byte[] iv = new byte[16];
            Buffer.BlockCopy(data, 0, iv, 0, 16);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            byte[] plain = decryptor.TransformFinalBlock(data, 16, data.Length - 16);
            plainText = Encoding.UTF8.GetString(plain);
            return true;
        }
        catch
        {
            return false;   // not base64, wrong key, or corrupt — treat as not-for-us
        }
    }
}
