using System.Security.Cryptography.X509Certificates;
using System.Windows;
using PageSmith.Core.Pdf;
using PageSmith.Core.Tools.Edit;
using PdfSharp.Drawing;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.Signatures;

namespace PageSmith.Core.Tools.Security;

/// <summary>A certificate-based (digital) signature. <see cref="Bounds"/> is in display space; empty means invisible.</summary>
public sealed record DigitalSignatureRequest
{
    public required X509Certificate2 Certificate { get; init; }
    public int PageIndex { get; init; }
    public Rect Bounds { get; init; } = Rect.Empty;
    public string? Reason { get; init; }
    public string? Location { get; init; }
    public string? ContactInfo { get; init; }

    /// <summary>Picture of a handwritten signature shown inside the signature box.</summary>
    public byte[]? AppearanceImage { get; init; }

    /// <summary>RFC 3161 time-stamping server (needs internet); null for no trusted timestamp.</summary>
    public Uri? TimestampServer { get; init; }
}

public sealed record SignOptions
{
    public required InputFile Input { get; init; }
    public required string OutputPath { get; init; }

    /// <summary>Visible signature parts: signature, initials, name, date, text, company stamp…</summary>
    public IReadOnlyList<PageElement> Elements { get; init; } = [];

    /// <summary>Adds a cryptographic signature after the visible elements.</summary>
    public DigitalSignatureRequest? Digital { get; init; }
}

/// <summary>Sign PDF: simple (visual) signatures and certificate-based digital signatures (iLovePDF Premium).</summary>
public static class SignTool
{
    public static Task<ToolResult> RunAsync(SignOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (options.Elements.Count == 0 && options.Digital == null) throw new UserFacingException("Place a signature on the document first.");
            var scope = new ProgressScope(progress);
            var result = new ToolResult { InputBytes = new FileInfo(options.Input.Path).Length };
            using var doc = PdfDocs.Open(options.Input);
            if (options.Elements.Count > 0) EditPdfTool.Apply(doc, options.Elements, scope.Slice(0, 0.5), ct);

            if (options.Digital is { } d)
            {
                if (!d.Certificate.HasPrivateKey) throw new UserFacingException("The selected certificate has no private key and cannot sign.");
                if (d.PageIndex < 0 || d.PageIndex >= doc.PageCount) throw new UserFacingException("The signature page does not exist.");
                scope.Report(0.6, "Applying digital signature");
                var page = doc.Pages[d.PageIndex];
                var geometry = PageGeometry.From(page);
                var signer = new PdfSharpDefaultSigner(d.Certificate, PdfMessageDigestType.SHA256, d.TimestampServer!);
                var visible = !d.Bounds.IsEmpty && d.Bounds.Width > 1;
                // PDFsharp expects the widget rectangle in PDF user space.
                var user = visible ? geometry.ToUser(d.Bounds) : new Rect(0, 0, 0, 0);
                var signOptions = new DigitalSignatureOptions
                {
                    PageIndex = d.PageIndex,
                    Reason = d.Reason ?? "",
                    Location = d.Location ?? "",
                    ContactInfo = d.ContactInfo ?? "",
                    AppName = "PageSmith",
                    Rectangle = new XRect(user.Left, user.Top, user.Width, user.Height),
                    AppearanceHandler = visible ? new SignatureAppearance(d, geometry.Rotation) : null!,
                };
                DigitalSignatureHandler.ForDocument(doc, signer, signOptions);
                result.Summary = $"Digitally signed by {d.Certificate.GetNameInfo(X509NameType.SimpleName, false)}.";
            }
            else
            {
                result.Summary = $"Signed with {options.Elements.Count} signature element{(options.Elements.Count == 1 ? "" : "s")}.";
            }

            scope.Report(0.9, "Saving");
            PdfDocs.Save(doc, options.OutputPath);
            result.AddOutput(options.OutputPath);
            if (doc.Tag as string == PdfDocs.DecryptedCopyTag)
                result.Warnings.Add("The original password protection was removed; use Protect PDF before signing if it must stay encrypted.");
            scope.Report(1);
            return result;
        }, ct);

    /// <summary>Draws the visible part of a digital signature: the handwriting image and signer details.</summary>
    private sealed class SignatureAppearance(DigitalSignatureRequest request, int pageRotation) : IAnnotationAppearanceHandler
    {
        public void DrawAppearance(XGraphics gfx, XRect rect)
        {
            var state = gfx.Save();
            var box = rect;
            if (pageRotation is 90 or 270)
            {
                // Keep the appearance upright on rotated pages.
                gfx.TranslateTransform(rect.Width / 2, rect.Height / 2);
                gfx.RotateTransform(pageRotation == 90 ? -90 : 90);
                box = new XRect(-rect.Height / 2, -rect.Width / 2, rect.Height, rect.Width);
            }
            else if (pageRotation == 180)
            {
                gfx.RotateAtTransform(180, new XPoint(rect.Width / 2, rect.Height / 2));
            }

            var name = request.Certificate.GetNameInfo(X509NameType.SimpleName, false);
            var lines = new List<string> { $"Digitally signed by {name}", $"Date: {DateTime.Now:yyyy-MM-dd HH:mm zzz}" };
            if (!string.IsNullOrWhiteSpace(request.Reason)) lines.Add($"Reason: {request.Reason}");
            if (!string.IsNullOrWhiteSpace(request.Location)) lines.Add($"Location: {request.Location}");

            var textBox = box;
            if (request.AppearanceImage is { Length: > 0 } img)
            {
                using var ms = new MemoryStream(img);
                using var image = XImage.FromStream(ms);
                var imageWidth = box.Width * 0.45;
                var scale = Math.Min(imageWidth / image.PixelWidth, box.Height / image.PixelHeight);
                var w = image.PixelWidth * scale;
                var h = image.PixelHeight * scale;
                gfx.DrawImage(image, box.X + (imageWidth - w) / 2, box.Y + (box.Height - h) / 2, w, h);
                textBox = new XRect(box.X + imageWidth + 4, box.Y, Math.Max(1, box.Width - imageWidth - 4), box.Height);
            }
            var size = Math.Clamp(textBox.Height / (lines.Count * 1.3), 4, 10);
            var font = new XFont("Arial", size, XFontStyleEx.Regular, new XPdfFontOptions(PdfSharp.Pdf.PdfFontEncoding.Unicode));
            var y = textBox.Y + (textBox.Height - lines.Count * size * 1.25) / 2;
            foreach (var line in lines)
            {
                gfx.DrawString(line, font, XBrushes.Black, new XRect(textBox.X, y, textBox.Width, size * 1.25), XStringFormats.TopLeft);
                y += size * 1.25;
            }
            gfx.Restore(state);
        }
    }
}
