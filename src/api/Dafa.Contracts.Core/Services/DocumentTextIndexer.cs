using System.Text;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>
/// Stores the plain text of documents for search (table DocumentTexts). Text is extracted with the
/// bounded analyzer extractor at upload, and documents without text (older uploads, approval logs,
/// intake files, seeded documents) are backfilled by the UploadCleanup job in batches.
/// </summary>
public class DocumentTextIndexer
{
    public const int DefaultBatch = 100;

    private readonly IAppDbContext _db;
    private readonly IDocumentStore _store;
    private readonly IDocumentTextExtractor _extractor;
    private readonly IClock _clock;

    public DocumentTextIndexer(IAppDbContext db, IDocumentStore store, IDocumentTextExtractor extractor, IClock clock)
    {
        _db = db;
        _store = store;
        _extractor = extractor;
        _clock = clock;
    }

    /// <summary>Whitespace collapsed to single spaces, cut at DocumentText.MaxCharacters.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        var builder = new StringBuilder(Math.Min(text.Length, DocumentText.MaxCharacters));
        var space = false;
        foreach (var ch in text)
        {
            if (builder.Length >= DocumentText.MaxCharacters)
            {
                break;
            }
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                space = builder.Length > 0;
                continue;
            }
            if (space)
            {
                builder.Append(' ');
                space = false;
                if (builder.Length >= DocumentText.MaxCharacters)
                {
                    break;
                }
            }
            builder.Append(ch);
        }
        return builder.ToString();
    }

    /// <summary>Extracts and stores the text of one document (adds to the unit of work; the caller saves).</summary>
    public async Task<DocumentText> IndexAsync(ContractDocument document, byte[] content, CancellationToken cancellationToken = default)
    {
        string text;
        string? note;
        string status;
        try
        {
            var extracted = await _extractor.ExtractAsync(document.Name, content, cancellationToken);
            text = Normalize(extracted.Text);
            note = extracted.Note;
            status = text.Length > 0 ? "Indexed" : "Empty";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            text = "";
            note = ex.Message.Length > 400 ? ex.Message.Substring(0, 400) : ex.Message;
            status = "Failed";
        }
        var documentId = document.Id;
        var row = await _db.DocumentTexts.FirstOrDefaultAsync(t => t.DocumentId == documentId, cancellationToken);
        if (row is null)
        {
            row = new DocumentText { DocumentId = document.Id };
            _db.DocumentTexts.Add(row);
        }
        row.ContractId = document.ContractId;
        row.Text = text;
        // ToLowerInvariant maps char by char, so offsets in NormalizedText are offsets in Text.
        row.NormalizedText = text.ToLowerInvariant();
        row.Status = status;
        row.Note = note is null ? null : (note.Length > 400 ? note.Substring(0, 400) : note);
        row.ExtractedAt = _clock.UtcNow;
        return row;
    }

    /// <summary>Indexes up to <paramref name="max"/> documents that have no text row yet. Returns how many were indexed.</summary>
    public async Task<int> BackfillAsync(int max = DefaultBatch, CancellationToken cancellationToken = default)
    {
        var texts = _db.DocumentTexts.AsNoTracking();
        var pending = await _db.Documents.AsNoTracking()
            .Where(d => !texts.Any(t => t.DocumentId == d.Id))
            .OrderBy(d => d.UploadedAt)
            .Take(Math.Max(1, max))
            .ToListAsync(cancellationToken);
        var count = 0;
        foreach (var document in pending)
        {
            byte[] content;
            try
            {
                await using var stream = await _store.OpenReadAsync(document.StoreKey, cancellationToken);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                content = buffer.ToArray();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A missing or unreadable file is recorded, so it is not retried every run.
                _db.DocumentTexts.Add(new DocumentText
                {
                    DocumentId = document.Id,
                    ContractId = document.ContractId,
                    Status = "Failed",
                    Note = ex.Message.Length > 400 ? ex.Message.Substring(0, 400) : ex.Message,
                    ExtractedAt = _clock.UtcNow
                });
                await _db.SaveChangesAsync(cancellationToken);
                continue;
            }
            await IndexAsync(document, content, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            count++;
        }
        return count;
    }
}
