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
            using var signedBuffer = new MemoryStream();
            if (options.Elements.Count > 0) EditPdfTool.Apply(doc, options.Elements, scope.Slice(0, 0.5), ct);

            if (options.Digital is { } d)
            {
                if (!d.Certificate.HasPrivateKey) throw new UserFacingException("The selected certificate has no private key and cannot sign.");
                if (d.PageIndex < 0 || d.PageIndex >= doc.PageCount) throw new UserFacingException("The signature page does not exist.");
                scope.Report(0.6, "Applying digital signature");
                var page = doc.Pages[d.PageIndex];
                var geometry = PageGeometry.From(page);
                var signer = new PrintableWidgetSigner(new PdfSharpDefaultSigner(d.Certificate, PdfMessageDigestType.SHA256, d.TimestampServer!), signedBuffer);
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
            if (options.Digital != null)
            {
                // Signed documents are written to memory first so PrintableWidgetSigner can see the bytes.
                doc.Info.Creator = string.IsNullOrEmpty(doc.Info.Creator) ? "PageSmith" : doc.Info.Creator;
                doc.Save(signedBuffer, false);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
                File.WriteAllBytes(options.OutputPath, signedBuffer.ToArray());
            }
            else
            {
                PdfDocs.Save(doc, options.OutputPath);
            }
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
            // The appearance is a form XObject whose box starts at (0, 0); rect only gives its size.
            var box = new XRect(0, 0, rect.Width, rect.Height);
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
            var options = new XPdfFontOptions(PdfSharp.Pdf.PdfFontEncoding.Unicode);
            var size = Math.Clamp(textBox.Height / (lines.Count * 1.3), 4, 10);
            // Shrink further when the longest line would not fit the width.
            var widest = lines.Max(l => gfx.MeasureString(l, new XFont("Arial", size, XFontStyleEx.Regular, options)).Width);
            if (widest > textBox.Width - 2) size = Math.Max(3, size * (textBox.Width - 2) / widest);
            var font = new XFont("Arial", size, XFontStyleEx.Regular, options);
            var y = textBox.Y + (textBox.Height - lines.Count * size * 1.25) / 2;
            foreach (var line in lines)
            {
                gfx.DrawString(line, font, XBrushes.Black, new XRect(textBox.X, y, textBox.Width, size * 1.25), XStringFormats.TopLeft);
                y += size * 1.25;
            }
            gfx.Restore(state);
        }
    }

    /// <summary>
    /// PDFsharp writes the signature widget without annotation flags, so viewers that draw only printable
    /// annotations (including Windows' PDF engine) hide the visible signature, and it doesn't print. The widget
    /// is built inside PDFsharp's save, out of reach, so the flag is set in the written bytes instead: the
    /// widget's empty default-resources entry "/DR&lt;&lt;&gt;&gt;" (meaningless on a signature) is overwritten with an
    /// equally long "/F 4". This happens when PDFsharp asks for the signature, before the signed byte ranges
    /// are hashed, so the signature covers the change and offsets stay valid.
    /// </summary>
    private sealed class PrintableWidgetSigner(IDigitalSigner inner, MemoryStream output) : IDigitalSigner
    {
        private static readonly byte[] Widget = Encoding.ASCII.GetBytes("/FT/Sig");
        private static readonly byte[] EmptyResources = Encoding.ASCII.GetBytes("/DR<<>>");
        private static readonly byte[] PrintFlag = Encoding.ASCII.GetBytes("/F 4   ");

        public string CertificateName => inner.CertificateName;

        public Task<int> GetSignatureSizeAsync() => inner.GetSignatureSizeAsync();

        public Task<byte[]> GetSignatureAsync(Stream stream)
        {
            var bytes = output.GetBuffer().AsSpan(0, (int)output.Length);
            for (var at = bytes.IndexOf(Widget); at >= 0;)
            {
                // The keys of one dictionary are written together; look a short way ahead only.
                var window = bytes.Slice(at, Math.Min(400, bytes.Length - at));
                var dr = window.IndexOf(EmptyResources);
                if (dr >= 0 && window[..dr].IndexOf((byte)'>') < 0) PrintFlag.CopyTo(window[dr..]);
                var next = bytes[(at + 1)..].IndexOf(Widget);
                at = next < 0 ? -1 : at + 1 + next;
            }
            return inner.GetSignatureAsync(stream);
        }
    }
}
