using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PageSmith.Core.Tools.Security;

public sealed record UnlockOptions
{
    /// <summary>Files with the password that opens them (user or owner password; none for restriction-only files).</summary>
    public required IReadOnlyList<InputFile> Files { get; init; }
    public required string OutputDirectory { get; init; }
}

public enum EncryptionStrength
{
    Aes256,
    Aes128,
    Rc4With128Bits,
}

public sealed record ProtectOptions
{
    public required IReadOnlyList<InputFile> Files { get; init; }
    public required string OutputDirectory { get; init; }

    /// <summary>Password needed to open the file. Null or empty: anyone can open it (restrictions only).</summary>
    public string? OpenPassword { get; init; }

    /// <summary>Password that lifts the restrictions. Generated when restrictions are set without one.</summary>
    public string? PermissionsPassword { get; init; }

    public bool AllowPrinting { get; init; } = true;
    public bool AllowHighQualityPrinting { get; init; } = true;
    public bool AllowModification { get; init; } = true;
    public bool AllowCopying { get; init; } = true;
    public bool AllowComments { get; init; } = true;
    public bool AllowFormFilling { get; init; } = true;
    public bool AllowAssembly { get; init; } = true;

    public EncryptionStrength Strength { get; init; } = EncryptionStrength.Aes256;

    public bool HasRestrictions => !(AllowPrinting && AllowHighQualityPrinting && AllowModification && AllowCopying && AllowComments && AllowFormFilling && AllowAssembly);
}

/// <summary>Unlock PDF: removes passwords and permission restrictions.</summary>
public static class UnlockTool
{
    public static Task<ToolResult> RunAsync(UnlockOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var scope = new ProgressScope(progress);
            var result = new ToolResult();
            for (var i = 0; i < options.Files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = options.Files[i];
                scope.Report(i, options.Files.Count, $"Unlocking {file.Name}");
                result.InputBytes += new FileInfo(file.Path).Length;
                var output = OutputPaths.ForInput(file.Path, "unlocked", ".pdf", options.OutputDirectory);
                Unlock(file, output);
                result.AddOutput(output);
            }
            result.Summary = $"Removed the protection from {options.Files.Count} file{(options.Files.Count == 1 ? "" : "s")}.";
            scope.Report(1);
            return result;
        }, ct);

    public static void Unlock(InputFile file, string output)
    {
        // Opening with the user password (or none, for restriction-only files) yields a decrypted copy.
        using var doc = PdfDocs.Open(file);
        doc.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
        PdfDocs.Save(doc, output);
    }
}

/// <summary>Protect PDF: password protection and permission restrictions with AES-256 by default.</summary>
public static class ProtectTool
{
    public static Task<ToolResult> RunAsync(ProtectOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (string.IsNullOrEmpty(options.OpenPassword) && !options.HasRestrictions && string.IsNullOrEmpty(options.PermissionsPassword))
                throw new UserFacingException("Set an open password or at least one restriction.");
            var scope = new ProgressScope(progress);
            var result = new ToolResult();
            for (var i = 0; i < options.Files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = options.Files[i];
                scope.Report(i, options.Files.Count, $"Protecting {file.Name}");
                result.InputBytes += new FileInfo(file.Path).Length;
                using var doc = PdfDocs.Open(file);
                Apply(doc, options);
                var output = OutputPaths.ForInput(file.Path, "protected", ".pdf", options.OutputDirectory);
                PdfDocs.Save(doc, output);
                result.AddOutput(output);
            }
            var what = string.IsNullOrEmpty(options.OpenPassword) ? "Restricted" : "Password-protected";
            result.Summary = $"{what} {options.Files.Count} file{(options.Files.Count == 1 ? "" : "s")} with {Describe(options.Strength)} encryption.";
            if (options.HasRestrictions && string.IsNullOrEmpty(options.PermissionsPassword))
                result.Warnings.Add("No permissions password was set, so a random one was used; the restrictions cannot be lifted later without Unlock PDF.");
            scope.Report(1);
            return result;
        }, ct);

    public static void Apply(PdfDocument doc, ProtectOptions o)
    {
        var settings = doc.SecuritySettings;
        settings.UserPassword = o.OpenPassword ?? "";
        var owner = o.PermissionsPassword;
        if (string.IsNullOrEmpty(owner)) owner = o.HasRestrictions || string.IsNullOrEmpty(o.OpenPassword) ? Guid.NewGuid().ToString("N") : o.OpenPassword;
        settings.OwnerPassword = owner;
        settings.PermitPrint = o.AllowPrinting;
        settings.PermitFullQualityPrint = o.AllowPrinting && o.AllowHighQualityPrinting;
        settings.PermitModifyDocument = o.AllowModification;
        settings.PermitExtractContent = o.AllowCopying;
        settings.PermitAnnotations = o.AllowComments;
        settings.PermitFormsFill = o.AllowFormFilling;
        settings.PermitAssembleDocument = o.AllowAssembly;
        switch (o.Strength)
        {
            case EncryptionStrength.Aes128:
                doc.SecurityHandler.SetEncryptionToV4UsingAES(true);
                break;
            case EncryptionStrength.Rc4With128Bits:
                doc.SecurityHandler.SetEncryptionToV2With128Bits();
                break;
            default:
                doc.SecurityHandler.SetEncryptionToV5(true);
                break;
        }
    }

    public static string Describe(EncryptionStrength s) => s switch
    {
        EncryptionStrength.Aes128 => "AES 128-bit",
        EncryptionStrength.Rc4With128Bits => "RC4 128-bit",
        _ => "AES 256-bit",
    };

    /// <summary>Rough strength of a password for the strength meter: 0 (weak) to 4 (strong).</summary>
    public static int PasswordScore(string password)
    {
        if (string.IsNullOrEmpty(password)) return 0;
        var score = 0;
        if (password.Length >= 8) score++;
        if (password.Any(char.IsLower) && password.Any(char.IsUpper)) score++;
        if (password.Any(char.IsDigit)) score++;
        if (password.Any(c => !char.IsLetterOrDigit(c))) score++;
        if (password.Length >= 14 && score >= 3) score = 4;
        return Math.Min(score, 4);
    }
}
