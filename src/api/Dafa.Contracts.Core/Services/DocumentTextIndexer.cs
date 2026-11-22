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

    /// <summary>How long to wait before the next try: 15 minutes, doubling, at most a day.</summary>
    public static TimeSpan Backoff(int attempts)
    {
        var minutes = 15d * Math.Pow(2, Math.Clamp(attempts - 1, 0, 10));
        return TimeSpan.FromMinutes(Math.Min(minutes, 24 * 60));
    }

    /// <summary>
    /// Indexes up to <paramref name="max"/> documents that have no text row yet, and retries the
    /// ones whose file could not be read earlier (status Retry, when their next attempt is due).
    /// A store that is down is not a reason to give up on a document: it is retried with a
    /// growing pause, DocumentText.MaxAttempts times. Only a file that is really gone, or that
    /// still cannot be read after all attempts, ends as Failed. Returns how many were indexed.
    /// </summary>
    public async Task<int> BackfillAsync(int max = DefaultBatch, CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var texts = _db.DocumentTexts.AsNoTracking();
        var due = _db.DocumentTexts.AsNoTracking().Where(t => t.Status == "Retry" && (t.NextAttemptAt == null || t.NextAttemptAt <= now));
        var pending = await _db.Documents.AsNoTracking()
            .Where(d => !texts.Any(t => t.DocumentId == d.Id) || due.Any(t => t.DocumentId == d.Id))
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
                var documentId = document.Id;
                var row = await _db.DocumentTexts.FirstOrDefaultAsync(t => t.DocumentId == documentId, cancellationToken);
                if (row is null)
                {
                    row = new DocumentText { DocumentId = document.Id, ContractId = document.ContractId };
                    _db.DocumentTexts.Add(row);
                }
                row.Attempts++;
                var gone = ex is FileNotFoundException || ex is Dafa.Contracts.Core.Common.StorageFileMissingException;
                var giveUp = gone || row.Attempts >= DocumentText.MaxAttempts;
                row.Status = giveUp ? "Failed" : "Retry";
                row.NextAttemptAt = giveUp ? null : _clock.UtcNow.Add(Backoff(row.Attempts));
                var note = (gone ? "The file is missing from storage. " : "Attempt " + row.Attempts + " of " + DocumentText.MaxAttempts + ": ") + ex.Message;
                row.Note = note.Length > 400 ? note.Substring(0, 400) : note;
                row.ExtractedAt = _clock.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                continue;
            }
            var indexed = await IndexAsync(document, content, cancellationToken);
            indexed.NextAttemptAt = null;
            await _db.SaveChangesAsync(cancellationToken);
            count++;
        }
        return count;
    }
}
