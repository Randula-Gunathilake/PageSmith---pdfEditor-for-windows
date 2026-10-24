using System.Security.Cryptography;

namespace PageSmith.Core.Common;

/// <summary>
/// Encrypts secrets (API keys, workflow passwords) for the current Windows user with DPAPI,
/// so they are unreadable to other accounts and on other PCs.
/// </summary>
public static class SecretStore
{
    private static readonly byte[] Entropy = "PageSmith.Secrets.v1"u8.ToArray();

    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return System.Convert.ToBase64String(data);
    }

    public static string Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try
        {
            var data = ProtectedData.Unprotect(System.Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "";
        }
    }
}
