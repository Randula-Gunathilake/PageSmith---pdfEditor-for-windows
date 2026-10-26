using System.IO;
using System.Security.Cryptography.Pkcs;
using System.Text.RegularExpressions;
using System.Windows;
using PageSmith.Core.Pdf;
using PageSmith.Core.Tools.Security;

namespace PageSmith.Tests;

public class SignTests
{
    /// <summary>Checks a PDF signature: the CMS signature must cover exactly the /ByteRange bytes.</summary>
    internal static SignedCms VerifySignature(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        var range = Regex.Match(text, @"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]");
        Assert.True(range.Success, "no /ByteRange");
        var r = range.Groups.Values.Skip(1).Select(g => int.Parse(g.Value)).ToArray();
        var signed = new byte[r[1] + r[3]];
        Buffer.BlockCopy(bytes, r[0], signed, 0, r[1]);
        Buffer.BlockCopy(bytes, r[2], signed, r[1], r[3]);
        Assert.Equal(bytes.Length, r[2] + r[3]);
        var hex = text.Substring(r[1] + 1, r[2] - r[1] - 2).TrimEnd('0');
        if (hex.Length % 2 == 1) hex += "0";
        var cms = new SignedCms(new ContentInfo(signed), detached: true);
        cms.Decode(Convert.FromHexString(hex));
        cms.CheckSignature(verifySignatureOnly: true);
        return cms;
    }

    [Fact]
    public async Task Digital_signature_is_valid_and_visible_where_placed()
    {
        using var files = new TestFiles();
        var src = files.CreatePdf("contract.pdf", 2);
        var cert = Certificates.CreateSelfSigned("Test Signer", "signer@example.com", "PageSmith Tests", 1, saveToWindowsStore: false);
        var output = files.PathFor("signed.pdf");
        var result = await SignTool.RunAsync(new SignOptions
        {
            Input = src,
            OutputPath = output,
            Elements = [new TextElement { PageIndex = 0, Bounds = new Rect(72, 600, 200, 30), Text = "Approved", Style = new TextStyle { Size = 16 } }],
            Digital = new DigitalSignatureRequest { Certificate = cert, PageIndex = 1, Bounds = new Rect(300, 650, 220, 60), Reason = "Approval", Location = "Colombo" },
        });
        Assert.Contains("Test Signer", result.Summary);

        var cms = VerifySignature(output);
        Assert.Equal(cert.Thumbprint, cms.SignerInfos[0].Certificate!.Thumbprint);
        Assert.Contains("Approved", OrganizeTests.PageTexts(output)[0]);

        // The visible signature widget lands in the requested box on page 2 (user space: y = 842 - 650 - 60).
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(output, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        var widget = doc.Pages[1].Elements.GetArray("/Annots")!.Elements.GetDictionary(0)!;
        var rect = widget.Elements.GetArray("/Rect")!;
        Assert.Equal(300, rect.Elements.GetReal(0), 1);
        Assert.Equal(132, rect.Elements.GetReal(1), 1);
        Assert.Equal(520, rect.Elements.GetReal(2), 1);
        Assert.Equal(192, rect.Elements.GetReal(3), 1);
        Assert.NotNull(widget.Elements.GetDictionary("/AP"));
        // Printable, so it also shows in viewers that only draw printable annotations (Windows' renderer) and on paper.
        Assert.Equal(4, widget.Elements.GetInteger("/F") & 4);

        // And its appearance is actually drawn inside the box (the signer details are dark text).
        using var renderer = await PageSmith.Core.Rendering.PdfRenderer.OpenAsync(output);
        var px = PageSmith.Core.Imaging.ImageIO.ToPixels(await renderer.RenderAsync(1, 72));
        var dark = 0;
        for (var y = 652; y < 708; y++)
        for (var x = 302; x < 518; x++)
        {
            var i = (y * px.Width + x) * 4;
            if (px.Pixels[i] + px.Pixels[i + 1] + px.Pixels[i + 2] < 300) dark++;
        }
        Assert.True(dark > 100, $"signature box looks empty ({dark} dark pixels)");
    }
}
