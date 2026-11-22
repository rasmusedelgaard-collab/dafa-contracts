using System.Text;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Dafa.Contracts.Infrastructure.Providers;

public sealed record ExtractedText(string Text, int Pages);

/// <summary>Plain text from PDF (PdfPig) and DOCX (Open XML SDK). Scanned PDFs give no text; there is no OCR.</summary>
public static class TextExtractor
{
    public static ExtractedText Extract(string fileName, byte[] content)
    {
        var extension = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        try
        {
            if (extension == ".pdf")
            {
                return ExtractPdf(content);
            }
            if (extension == ".docx")
            {
                return ExtractDocx(content);
            }
            if (extension == ".txt")
            {
                return new ExtractedText(Encoding.UTF8.GetString(content), 1);
            }
        }
        catch (Exception)
        {
            // Unreadable or damaged file: treated as a file without text.
        }
        return new ExtractedText("", 0);
    }

    private static ExtractedText ExtractPdf(byte[] content)
    {
        var text = new StringBuilder();
        using var document = PdfDocument.Open(content);
        var pages = 0;
        foreach (var page in document.GetPages())
        {
            pages++;
            var words = page.GetWords().Select(w => w.Text);
            text.AppendLine(string.Join(" ", words));
        }
        return new ExtractedText(text.ToString(), pages);
    }

    private static ExtractedText ExtractDocx(byte[] content)
    {
        var text = new StringBuilder();
        using var stream = new MemoryStream(content, false);
        using var document = WordprocessingDocument.Open(stream, false);
        var body = document.MainDocumentPart?.Document?.Body;
        if (body is not null)
        {
            foreach (var paragraph in body.Descendants<W.Paragraph>())
            {
                text.AppendLine(paragraph.InnerText);
            }
        }

        var pages = 0;
        var pagesText = document.ExtendedFilePropertiesPart?.Properties?.Pages?.Text;
        if (!int.TryParse(pagesText, out pages) || pages <= 0)
        {
            // Word did not store a page count: estimate from the amount of text.
            pages = Math.Max(1, text.Length / 3000);
        }
        return new ExtractedText(text.ToString(), pages);
    }
}
