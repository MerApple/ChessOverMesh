using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Meshtastic.Proxy;

/// <summary>Loads a PFX certificate for the TLS server, or generates (and persists) a self-signed one on first
/// run. The proxy's clients accept any certificate (like the app already does for the device's self-signed
/// HTTPS), so a self-signed cert is fine — TLS here is for transport encryption, not identity.</summary>
internal static class SelfSignedCert
{
    public static X509Certificate2 GetOrCreate(string pfxPath, string password)
    {
        if (File.Exists(pfxPath))
            return new X509Certificate2(pfxPath, password, X509KeyStorageFlags.Exportable);

        var cert = Create();
        try { File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pfx, password)); }
        catch { /* non-fatal: run with the in-memory cert if we can't persist it */ }
        return cert;
    }

    public static X509Certificate2 Create()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Meshtastic.Proxy", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));   // server authentication
        var now = DateTimeOffset.UtcNow;
        using var ephemeral = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));
        // Round-trip through a PFX so the private key is usable by SslStream on Windows.
        return new X509Certificate2(ephemeral.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }
}
