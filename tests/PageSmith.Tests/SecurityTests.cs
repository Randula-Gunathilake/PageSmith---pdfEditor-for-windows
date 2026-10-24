using System.IO;
using PageSmith.Core.Common;
using PageSmith.Core.Tools.Security;
using PdfSharp.Pdf.IO;

namespace PageSmith.Tests;

public class SecurityTests
{
    [Fact]
    public async Task Protect_then_unlock_round_trip()
    {
        using var files = new TestFiles();
        var src = files.CreatePdf("secret.pdf", 2, body: i => $"classified {i}");
        var protectedResult = await ProtectTool.RunAsync(new ProtectOptions { Files = [src], OutputDirectory = files.Dir, OpenPassword = "Open-Sesame1", AllowPrinting = false });
        var locked = protectedResult.OutputFiles[0];

        Assert.True(PdfDocs.RequiresPassword(locked));
        Assert.Throws<PasswordRequiredException>(() => PdfDocs.Open(new InputFile(locked, "wrong")));
        using (var opened = PdfDocs.Open(new InputFile(locked, "Open-Sesame1"), PdfDocumentOpenMode.Import))
            Assert.Equal(2, opened.PageCount);

        var unlocked = await UnlockTool.RunAsync(new UnlockOptions { Files = [new InputFile(locked, "Open-Sesame1")], OutputDirectory = files.Dir });
        Assert.False(PdfDocs.RequiresPassword(unlocked.OutputFiles[0]));
        Assert.Contains("classified 2", OrganizeTests.PageTexts(unlocked.OutputFiles[0])[1]);
    }

    [Fact]
    public async Task Restriction_only_files_unlock_without_password()
    {
        using var files = new TestFiles();
        var src = files.CreatePdf("restricted.pdf", 3);
        var r = await ProtectTool.RunAsync(new ProtectOptions { Files = [src], OutputDirectory = files.Dir, AllowCopying = false, AllowPrinting = false, Strength = EncryptionStrength.Aes128 });
        Assert.False(PdfDocs.RequiresPassword(r.OutputFiles[0]));

        var unlocked = await UnlockTool.RunAsync(new UnlockOptions { Files = [r.OutputFiles[0]], OutputDirectory = files.Dir });
        using var doc = PdfReader.Open(unlocked.OutputFiles[0], PdfDocumentOpenMode.Modify);
        Assert.False(doc.SecuritySettings.IsEncrypted);
        Assert.Equal(3, doc.PageCount);
    }

    [Fact]
    public async Task Tools_work_on_restricted_files_with_forms_and_bookmarks()
    {
        using var files = new TestFiles();
        var a = files.CreatePdf("a.pdf", 1);
        var b = files.CreatePdf("b.pdf", 1);
        var merged = files.PathFor("m.pdf");
        await PageSmith.Core.Tools.Organize.MergeTool.RunAsync(new PageSmith.Core.Tools.Organize.MergeOptions { Items = [new(a), new(b)], OutputPath = merged });
        var form = files.PathFor("form.pdf");
        await PageSmith.Core.Tools.Edit.FormsTool.CreateFieldsAsync(new PageSmith.Core.Tools.Edit.CreateFieldsOptions
        {
            Input = merged,
            OutputPath = form,
            Fields = [new(PageSmith.Core.Tools.Edit.FormFieldKind.Text, "Name", 1, new System.Windows.Rect(100, 100, 200, 20))],
        });
        var restricted = await ProtectTool.RunAsync(new ProtectOptions { Files = [form], OutputDirectory = files.Dir, AllowModification = false });

        using var doc = PdfDocs.Open(restricted.OutputFiles[0]);
        Assert.Equal(PdfDocs.DecryptedCopyTag, doc.Tag);
        Assert.Equal(2, doc.Outlines.Count);
        var filled = files.PathFor("filled.pdf");
        await PageSmith.Core.Tools.Edit.FormsTool.FillAsync(new PageSmith.Core.Tools.Edit.FillFormOptions
        {
            Input = restricted.OutputFiles[0],
            OutputPath = filled,
            Values = new Dictionary<string, string> { ["Name"] = "Linus" },
        });
        var field = Assert.Single(PageSmith.Core.Tools.Edit.FormsTool.ReadFields(filled));
        Assert.Equal("Linus", field.Value);
        Assert.Equal(1, field.PageIndex);
    }

    [Fact]
    public void Password_score_rewards_variety() =>
        Assert.True(ProtectTool.PasswordScore("abc") < ProtectTool.PasswordScore("Abcdef12!xyzQW"));
}
