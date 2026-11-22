using System.Text;

namespace Dafa.Contracts.Core.Services;

/// <summary>
/// Release 4: writes a small, valid PDF with plain text (Helvetica, A4), without any PDF library.
/// Used for the signature page of the Simulated e-signature provider. Text is reduced to ASCII.
/// </summary>
public static class SimplePdf
{
    private const int LinesPerPage = 48;
    private const int MaxLineLength = 92;

    /// <summary>Characters outside printable ASCII become a close ASCII character or '?'.</summary>
    public static string ToAscii(string? text)
    {
        var builder = new StringBuilder();
        foreach (var ch in text ?? "")
        {
            switch (ch)
            {
                case '–':
                case '—':
                    builder.Append('-');
                    break;
                case 'æ': builder.Append("ae"); break;
                case 'Æ': builder.Append("Ae"); break;
                case 'ø': builder.Append("oe"); break;
                case 'Ø': builder.Append("Oe"); break;
                case 'å': builder.Append("aa"); break;
                case 'Å': builder.Append("Aa"); break;
                case '\t': builder.Append(' '); break;
                default:
                    builder.Append(ch >= ' ' && ch <= '~' ? ch : '?');
                    break;
            }
        }
        return builder.ToString();
    }

    private static string Escape(string text) =>
        ToAscii(text).Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static List<string> Wrap(IEnumerable<string> lines)
    {
        var result = new List<string>();
        foreach (var raw in lines)
        {
            var line = ToAscii(raw);
            if (line.Length == 0)
            {
                result.Add("");
                continue;
            }
            while (line.Length > MaxLineLength)
            {
                var cut = line.LastIndexOf(' ', MaxLineLength);
                if (cut < 20)
                {
                    cut = MaxLineLength;
                }
                result.Add(line.Substring(0, cut));
                line = "    " + line.Substring(cut).TrimStart();
            }
            result.Add(line);
        }
        return result;
    }

    public static byte[] FromLines(string title, IEnumerable<string> lines)
    {
        var wrapped = Wrap(lines);
        var pages = new List<List<string>>();
        for (var i = 0; i < wrapped.Count; i += LinesPerPage)
        {
            pages.Add(wrapped.Skip(i).Take(LinesPerPage).ToList());
        }
        if (pages.Count == 0)
        {
            pages.Add(new List<string>());
        }

        // Objects: 1 catalog, 2 pages, 3 Helvetica, 4 Helvetica-Bold, then a page and a content stream per page.
        var objects = new List<string>();
        var kids = new StringBuilder();
        for (var i = 0; i < pages.Count; i++)
        {
            kids.Append(5 + i * 2).Append(" 0 R ");
        }
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add("<< /Type /Pages /Kids [ " + kids + "] /Count " + pages.Count + " >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>");
        for (var i = 0; i < pages.Count; i++)
        {
            var content = new StringBuilder();
            if (i == 0)
            {
                content.Append("BT /F2 15 Tf 56 790 Td (").Append(Escape(title)).Append(") Tj ET\n");
            }
            content.Append("BT /F1 10 Tf 14 TL 56 ").Append(i == 0 ? 760 : 790).Append(" Td\n");
            foreach (var line in pages[i])
            {
                content.Append('(').Append(Escape(line)).Append(") Tj T*\n");
            }
            content.Append("ET\n");
            var stream = content.ToString();
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents "
                + (6 + i * 2) + " 0 R >>");
            objects.Add("<< /Length " + stream.Length + " >>\nstream\n" + stream + "endstream");
        }

        // Everything is ASCII, so a character offset is a byte offset.
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append('\n');
        pdf.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }
        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
