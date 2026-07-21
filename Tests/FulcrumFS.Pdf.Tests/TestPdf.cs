using System.Globalization;
using System.Text;

namespace FulcrumFS.Pdf;

/// <summary>
/// Generates minimal valid PDF documents for testing. Pages are rendered with a gray rectangle inset 10 points from each page edge.
/// </summary>
internal static class TestPdf
{
    public static byte[] Create(int widthPoints, int heightPoints, int pageCount = 1)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();

        sb.Append("%PDF-1.4\n");

        AppendObject(sb, offsets, "<< /Type /Catalog /Pages 2 0 R >>");

        string kids = string.Join(' ', Enumerable.Range(0, pageCount).Select(i => $"{3 + (i * 2)} 0 R"));
        AppendObject(sb, offsets, $"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");

        for (int i = 0; i < pageCount; i++)
        {
            AppendObject(sb, offsets, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {widthPoints} {heightPoints}] /Contents {4 + (i * 2)} 0 R >>");

            string content = $"0.5 0.5 0.5 rg\n10 10 {Math.Max(widthPoints - 20, 1)} {Math.Max(heightPoints - 20, 1)} re f\n";
            AppendObject(sb, offsets, $"<< /Length {content.Length} >>\nstream\n{content}endstream");
        }

        int xrefOffset = sb.Length;

        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count + 1}\n");
        sb.Append("0000000000 65535 f \n");

        foreach (int offset in offsets)
            sb.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");

        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static void AppendObject(StringBuilder sb, List<int> offsets, string body)
    {
        offsets.Add(sb.Length);
        sb.Append(CultureInfo.InvariantCulture, $"{offsets.Count} 0 obj\n{body}\nendobj\n");
    }
}
