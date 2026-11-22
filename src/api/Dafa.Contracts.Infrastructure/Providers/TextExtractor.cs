using System.IO.Compression;
using System.Text;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Services;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Dafa.Contracts.Infrastructure.Providers;

/// <summary>Note is set when the file was not (fully) read because it hit an extraction limit.</summary>
public sealed record ExtractedText(string Text, int Pages, string? Note = null);

/// <summary>
/// Plain text from PDF (PdfPig) and DOCX (Open XML SDK). Scanned PDFs give no text; there is no OCR.
/// Extraction is bounded (M7): file size, pages, extracted characters, decompressed size of a
/// .docx and a time budget. A file over a limit gives no suggestions and a note instead of an error.
/// </summary>
public static class TextExtractor
{
    public const long MaxFileBytes = 25L * 1024 * 1024;
    public const int MaxPages = 300;
    /// <summary>The analyzer never looks further than this.</summary>
    public const int MaxCharacters = 400_000;
    public const long MaxDocxPartBytes = 50L * 1024 * 1024;
    public const long MaxDocxTotalBytes = 150L * 1024 * 1024;
    public const int MaxDocxEntries = 2000;
    public static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(20);

    public const string TooLargeNote =
        "The file is too large or too complex to read automatically, so there are no suggestions. Enter the fields manually.";
    public const string TooSlowNote =
        "Reading the file took too long, so there are no suggestions. Enter the fields manually.";
    public const string TruncatedNote =
        "Only the first part of the file was read; check the suggestions against the document.";

    /// <summary>Runs the extraction off the request thread with the time budget. Never throws for a bad file.</summary>
    public static async Task<ExtractedText> ExtractAsync(string fileName, byte[] content, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeBudget);
        var token = budget.Token;
        var work = Task.Run(() => Extract(fileName, content, token));
        try
        {
            // WaitAsync returns at the deadline even when a parser is stuck inside one page.
            return await work.WaitAsync(TimeBudget + TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (TimeoutException)
        {
            return new ExtractedText("", 0, TooSlowNote);
        }
    }

    /// <summary>The heuristic analysis of an extraction result, carrying its note.</summary>
    public static ContractAnalysis ToAnalysis(ExtractedText extracted, string fileName)
    {
        var analysis = HeuristicExtraction.Analyze(extracted.Text, fileName, extracted.Pages);
        if (extracted.Note is not null)
        {
            analysis.Note = extracted.Note;
            if (string.IsNullOrWhiteSpace(extracted.Text))
            {
                analysis.Summary = extracted.Note;
            }
        }
        return analysis;
    }

    public static ExtractedText Extract(string fileName, byte[] content) =>
        Extract(fileName, content, CancellationToken.None);

    public static ExtractedText Extract(string fileName, byte[] content, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (content.LongLength > MaxFileBytes)
        {
            return new ExtractedText("", 0, TooLargeNote);
        }
        try
        {
            if (extension == ".pdf")
            {
                return ExtractPdf(content, cancellationToken);
            }
            if (extension == ".docx")
            {
                return ExtractDocx(content, cancellationToken);
            }
            if (extension == ".txt")
            {
                var text = Encoding.UTF8.GetString(content, 0, Math.Min(content.Length, MaxCharacters * 4));
                return text.Length > MaxCharacters
                    ? new ExtractedText(text.Substring(0, MaxCharacters), 1, TruncatedNote)
                    : new ExtractedText(text, 1);
            }
        }
        catch (OperationCanceledException)
        {
            return new ExtractedText("", 0, TooSlowNote);
        }
        catch (Exception)
        {
            // Unreadable or damaged file: treated as a file without text.
        }
        return new ExtractedText("", 0);
    }

    private static ExtractedText ExtractPdf(byte[] content, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        using var document = PdfDocument.Open(content);
        var pages = 0;
        string? note = null;
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pages >= MaxPages || text.Length >= MaxCharacters)
            {
                note = TruncatedNote;
                break;
            }
            pages++;
            var words = page.GetWords().Select(w => w.Text);
            text.AppendLine(string.Join(" ", words));
        }
        return new ExtractedText(Limit(text), pages, note);
    }

    private static string Limit(StringBuilder text) =>
        text.Length > MaxCharacters ? text.ToString(0, MaxCharacters) : text.ToString();

    /// <summary>
    /// A .docx is a zip file. Decompresses every part into nothing, counting the bytes, and stops as
    /// soon as one part or the whole archive is larger than allowed. The declared sizes in the zip
    /// directory are not trusted.
    /// </summary>
    public static bool DocxIsWithinLimits(byte[] content, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content, false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > MaxDocxEntries)
        {
            return false;
        }
        var buffer = new byte[81920];
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            long part = 0;
            using var entryStream = entry.Open();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = entryStream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }
                part += read;
                total += read;
                if (part > MaxDocxPartBytes || total > MaxDocxTotalBytes)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static ExtractedText ExtractDocx(byte[] content, CancellationToken cancellationToken)
    {
        if (!DocxIsWithinLimits(content, cancellationToken))
        {
            return new ExtractedText("", 0, TooLargeNote);
        }

        var text = new StringBuilder();
        string? note = null;
        using var stream = new MemoryStream(content, false);
        // A second guard inside the Open XML SDK: a part with more characters than this is not loaded.
        var settings = new OpenSettings { MaxCharactersInPart = MaxDocxPartBytes };
        using var document = WordprocessingDocument.Open(stream, false, settings);
        var body = document.MainDocumentPart?.Document?.Body;
        if (body is not null)
        {
            foreach (var paragraph in body.Descendants<W.Paragraph>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (text.Length >= MaxCharacters)
                {
                    note = TruncatedNote;
                    break;
                }
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
        return new ExtractedText(Limit(text), pages, note);
    }
}
