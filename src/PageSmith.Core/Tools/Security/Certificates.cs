using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PageSmith.Core.Tools.Security;

/// <summary>Finds, loads and creates certificates for digital signatures.</summary>
public static class Certificates
{
    /// <summary>Certificates in the user's Windows certificate store that can sign (private key, currently valid).</summary>
    public static IReadOnlyList<X509Certificate2> SigningCertificates()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var now = DateTime.Now;
        return store.Certificates
            .Where(c => c.HasPrivateKey && c.NotBefore <= now && c.NotAfter >= now && CanSign(c))
            .OrderByDescending(c => c.NotAfter)
            .ToList();
    }

    private static bool CanSign(X509Certificate2 c)
    {
        var usage = c.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        return usage == null || usage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature) || usage.KeyUsages.HasFlag(X509KeyUsageFlags.NonRepudiation);
    }

    public static X509Certificate2 LoadPfx(string path, string? password)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException ex)
        {
            throw new UserFacingException($"The certificate file could not be opened: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Creates a self-signed document-signing certificate (RSA 3072, SHA-256). Viewers show such signatures
    /// as valid but from an unverified signer until the certificate is trusted.
    /// </summary>
    public static X509Certificate2 CreateSelfSigned(string name, string? email, string? organization, int years = 3, bool saveToWindowsStore = true)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new UserFacingException("Enter the name to put on the certificate.");
        var subject = new StringBuilder($"CN={Escape(name)}");
        if (!string.IsNullOrWhiteSpace(organization)) subject.Append($", O={Escape(organization)}");
        if (!string.IsNullOrWhiteSpace(email)) subject.Append($", E={Escape(email)}");

        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(subject.ToString(), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.4.1.311.10.3.12", "Document Signing"), new Oid("1.3.6.1.5.5.7.3.4", "Secure Email")], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var created = request.CreateSelfSigned(DateTimeOffset.Now.AddMinutes(-5), DateTimeOffset.Now.AddYears(Math.Clamp(years, 1, 20)));

        // Re-import so the private key is persisted (store) or usable for signing (ephemeral).
        var pfx = created.Export(X509ContentType.Pfx);
        var flags = saveToWindowsStore ? X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet;
        var cert = X509CertificateLoader.LoadPkcs12(pfx, null, flags | X509KeyStorageFlags.Exportable);
        cert.FriendlyName = $"{name} (PageSmith)";
        if (saveToWindowsStore)
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Add(cert);
        }
        return cert;
    }

    /// <summary>Saves a certificate with its private key as a password-protected .pfx file.</summary>
    public static void ExportPfx(X509Certificate2 certificate, string path, string password) =>
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));

    public static string Describe(X509Certificate2 c) =>
        $"{c.GetNameInfo(X509NameType.SimpleName, false)} — issued by {c.GetNameInfo(X509NameType.SimpleName, true)}, valid until {c.NotAfter:d}";

    private static string Escape(string value) => value.Replace(",", "\\,").Replace("=", "\\=").Replace("+", "\\+").Trim();
}
