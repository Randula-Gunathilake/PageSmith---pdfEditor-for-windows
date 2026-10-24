using System.Windows.Media;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;

namespace PageSmith.Core.Pdf;

/// <summary>
/// Finds how large each image XObject is drawn (in points) by walking page content streams and
/// tracking the current transformation matrix through q/Q/cm and nested form XObjects.
/// </summary>
public static class ImagePlacement
{
    private const int MaxDepth = 12;

    /// <summary>Largest drawn width and height in points for every image dictionary used on the pages.</summary>
    public static Dictionary<PdfDictionary, (double Width, double Height)> Measure(PdfDocument doc, CancellationToken ct = default)
    {
        var sizes = new Dictionary<PdfDictionary, (double, double)>(ReferenceEqualityComparer.Instance);
        foreach (var page in doc.Pages)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var content = ContentReader.ReadContent(page);
                Walk(content, page.Elements.GetDictionary("/Resources"), Matrix.Identity, sizes, 0);
            }
            catch
            {
                // Unparseable content: images on this page fall back to size caps.
            }
        }
        return sizes;
    }

    private static void Walk(CSequence ops, PdfDictionary? resources, Matrix start, Dictionary<PdfDictionary, (double, double)> sizes, int depth)
    {
        var ctm = start;
        var stack = new Stack<Matrix>();
        foreach (var item in ops)
        {
            if (item is not COperator op) continue;
            switch (op.OpCode.OpCodeName)
            {
                case OpCodeName.q:
                    stack.Push(ctm);
                    break;
                case OpCodeName.Q:
                    if (stack.Count > 0) ctm = stack.Pop();
                    break;
                case OpCodeName.cm when op.Operands.Count == 6:
                    var m = new Matrix(Num(op.Operands[0]), Num(op.Operands[1]), Num(op.Operands[2]), Num(op.Operands[3]), Num(op.Operands[4]), Num(op.Operands[5]));
                    ctm = Matrix.Multiply(m, ctm);
                    break;
                case OpCodeName.Do when op.Operands.Count == 1 && op.Operands[0] is CName name:
                    var xobject = resources?.Elements.GetDictionary("/XObject")?.Elements.GetDictionary(name.Name);
                    if (xobject == null) break;
                    var subtype = xobject.Elements.GetName("/Subtype");
                    if (subtype == "/Image")
                    {
                        var w = Math.Sqrt(ctm.M11 * ctm.M11 + ctm.M12 * ctm.M12);
                        var h = Math.Sqrt(ctm.M21 * ctm.M21 + ctm.M22 * ctm.M22);
                        sizes[xobject] = sizes.TryGetValue(xobject, out var prev)
                            ? (Math.Max(prev.Item1, w), Math.Max(prev.Item2, h))
                            : (w, h);
                    }
                    else if (subtype == "/Form" && depth < MaxDepth && xobject.Stream != null)
                    {
                        var formMatrix = Matrix.Identity;
                        if (xobject.Elements.GetArray("/Matrix") is { Elements.Count: 6 } arr)
                            formMatrix = new Matrix(arr.Elements.GetReal(0), arr.Elements.GetReal(1), arr.Elements.GetReal(2), arr.Elements.GetReal(3), arr.Elements.GetReal(4), arr.Elements.GetReal(5));
                        try
                        {
                            var inner = ContentReader.ReadContent(xobject.Stream.UnfilteredValue);
                            var formResources = xobject.Elements.GetDictionary("/Resources") ?? resources;
                            Walk(inner, formResources, Matrix.Multiply(formMatrix, ctm), sizes, depth + 1);
                        }
                        catch
                        {
                            // Skip broken forms.
                        }
                    }
                    break;
            }
        }
    }

    private static double Num(CObject o) => o switch
    {
        CInteger i => i.Value,
        CReal r => r.Value,
        _ => 0,
    };
}
