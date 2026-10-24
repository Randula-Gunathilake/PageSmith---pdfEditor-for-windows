using System.Diagnostics;

namespace PageSmith.Core.Office;

/// <summary>Converts documents with a locally installed LibreOffice in headless mode.</summary>
public static class LibreOffice
{
    public static bool IsInstalled => OfficeDetection.LibreOfficePath != null;

    public static async Task ToPdfAsync(string input, string output, CancellationToken ct = default)
    {
        var soffice = OfficeDetection.LibreOfficePath ?? throw new UserFacingException("LibreOffice is not installed.");
        var work = Path.Combine(Path.GetTempPath(), $"pagesmith_lo_{Guid.NewGuid():N}");
        var profile = Path.Combine(work, "profile");
        Directory.CreateDirectory(profile);
        try
        {
            var psi = new ProcessStartInfo(soffice)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // A private profile lets conversions run while the user has LibreOffice open.
            psi.ArgumentList.Add($"-env:UserInstallation={new Uri(profile).AbsoluteUri}");
            psi.ArgumentList.Add("--headless");
            psi.ArgumentList.Add("--norestore");
            psi.ArgumentList.Add("--convert-to");
            psi.ArgumentList.Add("pdf");
            psi.ArgumentList.Add("--outdir");
            psi.ArgumentList.Add(work);
            psi.ArgumentList.Add(input);

            using var process = Process.Start(psi) ?? throw new UserFacingException("LibreOffice could not be started.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { /* already gone */ }
                throw;
            }

            var produced = Path.Combine(work, Path.GetFileNameWithoutExtension(input) + ".pdf");
            if (!File.Exists(produced))
            {
                var err = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                throw new UserFacingException($"LibreOffice could not convert \"{Path.GetFileName(input)}\". {err.Trim()}");
            }
            File.Move(produced, output, overwrite: true);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* temp */ }
        }
    }
}
