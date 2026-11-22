using System.Text.Json;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>Replaces a contract's tags (unit of work). Returns true when something changed.</summary>
public static class ContractTagWriter
{
    public static async Task<bool> ReplaceAsync(IAppDbContext db, Guid contractId, IEnumerable<Guid> tagIds, bool isNewContract, CancellationToken cancellationToken = default)
    {
        var wanted = tagIds.Distinct().ToList();
        var existing = isNewContract
            ? new List<ContractTag>()
            : await db.ContractTags.Where(t => t.ContractId == contractId).ToListAsync(cancellationToken);
        var changed = false;
        foreach (var link in existing.Where(e => !wanted.Contains(e.TagId)).ToList())
        {
            db.ContractTags.Remove(link);
            changed = true;
        }
        foreach (var tagId in wanted.Where(id => existing.All(e => e.TagId != id)))
        {
            db.ContractTags.Add(new ContractTag { ContractId = contractId, TagId = tagId });
            changed = true;
        }
        return changed;
    }
}

/// <summary>Preference autoWatch: people who create a contract or are on it start watching it.</summary>
public static class AutoWatch
{
    public static async Task ApplyAsync(IAppDbContext db, Contract contract, Guid? creatorId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var people = new List<Guid>();
        foreach (var id in new[] { creatorId, contract.OwnerId, contract.DelegateId, contract.ResponsibleId })
        {
            if (id is not null && !people.Contains(id.Value))
            {
                people.Add(id.Value);
            }
        }
        var wanting = await db.UserPreferences.AsNoTracking()
            .Where(p => people.Contains(p.UserId) && p.AutoWatch)
            .Select(p => p.UserId)
            .ToListAsync(cancellationToken);
        if (wanting.Count == 0)
        {
            return;
        }
        var contractId = contract.Id;
        var watching = await db.ContractWatchers.AsNoTracking().Where(w => w.ContractId == contractId).Select(w => w.UserId).ToListAsync(cancellationToken);
        foreach (var userId in wanting)
        {
            if (!watching.Contains(userId) && !db.ContractWatchers.Local.Any(w => w.ContractId == contractId && w.UserId == userId))
            {
                db.ContractWatchers.Add(new ContractWatcher { ContractId = contractId, UserId = userId, CreatedAt = nowUtc });
            }
        }
    }
}

/// <summary>Tags, watching, comments, in-app notifications, saved views, bulk actions (CONTRACT_V3 section 6).</summary>
public class CollaborationService
{
    public static readonly string[] TagColors = { "gray", "blue", "green", "amber", "red", "purple" };

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IContractAccessService _access;
    private readonly IClock _clock;
    private readonly AuditService _audit;
    private readonly ContractQueryService _queries;
    private readonly INotifier _notifier;

    public CollaborationService(
        IAppDbContext db, ICurrentUser currentUser, IContractAccessService access, IClock clock, AuditService audit,
        ContractQueryService queries, INotifier notifier)
    {
        _db = db;
        _currentUser = currentUser;
        _access = access;
        _clock = clock;
        _audit = audit;
        _queries = queries;
        _notifier = notifier;
    }

    /// <summary>404 (not 403) for a contract that does not exist or is not visible.</summary>
    private async Task<Contract> VisibleAsync(Guid contractId, bool tracked, CancellationToken cancellationToken)
    {
        try
        {
            return await _access.GetReadableAsync(contractId, tracked, cancellationToken);
        }
        catch (ForbiddenException)
        {
            throw new NotFoundException("Contract not found.");
        }
    }

    // ---------- tags ----------

    public async Task<List<TagRefDto>> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        await _currentUser.GetProfileAsync(cancellationToken);
        var tags = await _db.Tags.AsNoTracking().ToListAsync(cancellationToken);
        return tags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new TagRefDto { Id = t.Id, Name = t.Name, Color = t.Color })
            .ToList();
    }

    public async Task<ContractDetailDto> SetContractTagsAsync(Guid contractId, ContractTagsInput input, CancellationToken cancellationToken = default)
    {
        await VisibleAsync(contractId, false, cancellationToken);
        var contract = await _access.GetEditableAsync(contractId, cancellationToken);
        var tagIds = (input?.TagIds ?? new List<Guid>()).Distinct().ToList();
        var known = await _db.Tags.AsNoTracking().Where(t => tagIds.Contains(t.Id)).Select(t => t.Id).ToListAsync(cancellationToken);
        if (known.Count != tagIds.Count)
        {
            throw new InputValidationException("tagIds", "Unknown tag.");
        }
        if (await ContractTagWriter.ReplaceAsync(_db, contract.Id, tagIds, false, cancellationToken))
        {
            var names = await _db.Tags.AsNoTracking().Where(t => tagIds.Contains(t.Id)).Select(t => t.Name).ToListAsync(cancellationToken);
            _audit.Record("contract.tags-changed", "Tags: " + (names.Count == 0 ? "(none)" : string.Join(", ", names.OrderBy(n => n))), contract.Id);
            await _db.SaveChangesAsync(cancellationToken);
        }
        return await _queries.GetDetailAsync(contract.Id, false, cancellationToken);
    }

    // ---------- admin tags ----------

    public async Task<List<TagDto>> GetAdminTagsAsync(CancellationToken cancellationToken = default)
    {
        var tags = await _db.Tags.AsNoTracking().ToListAsync(cancellationToken);
        var usage = (await _db.ContractTags.AsNoTracking().GroupBy(t => t.TagId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken))
            .ToDictionary(x => x.Key, x => x.Count);
        return tags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new TagDto { Id = t.Id, Name = t.Name, Color = t.Color, Description = t.Description, UsageCount = usage.TryGetValue(t.Id, out var n) ? n : 0 })
            .ToList();
    }

    private async Task ApplyTagAsync(Tag tag, TagInput input, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var name = (input?.Name ?? "").Trim();
        if (name.Length < 1 || name.Length > 40) ContractValidator.Add(errors, "name", "Name is required (at most 40 characters).");
        var color = (input?.Color ?? "").Trim();
        if (!TagColors.Contains(color)) ContractValidator.Add(errors, "color", "Use gray, blue, green, amber, red or purple.");
        if (input?.Description is not null && input.Description.Length > 400) ContractValidator.Add(errors, "description", "At most 400 characters.");
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }
        var lower = name.ToLower();
        var tagId = tag.Id;
        if (await _db.Tags.AnyAsync(t => t.Id != tagId && t.Name.ToLower() == lower, cancellationToken))
        {
            throw new ConflictException("A tag with this name already exists.", ProblemTypes.Duplicate);
        }
        tag.Name = name;
        tag.Color = color;
        tag.Description = string.IsNullOrWhiteSpace(input!.Description) ? null : input.Description.Trim();
    }

    public async Task<TagDto> CreateTagAsync(TagInput input, CancellationToken cancellationToken = default)
    {
        var tag = new Tag { Id = Guid.NewGuid() };
        await ApplyTagAsync(tag, input, cancellationToken);
        _db.Tags.Add(tag);
        _audit.Record("settings.tags", "Created tag " + tag.Name);
        await _db.SaveChangesAsync(cancellationToken);
        return new TagDto { Id = tag.Id, Name = tag.Name, Color = tag.Color, Description = tag.Description, UsageCount = 0 };
    }

    public async Task<TagDto> UpdateTagAsync(Guid id, TagInput input, CancellationToken cancellationToken = default)
    {
        var tag = await _db.Tags.FirstOrDefaultAsync(t => t.Id == id, cancellationToken) ?? throw new NotFoundException("Tag not found.");
        await ApplyTagAsync(tag, input, cancellationToken);
        _audit.Record("settings.tags", "Updated tag " + tag.Name);
        await _db.SaveChangesAsync(cancellationToken);
        var usage = await _db.ContractTags.CountAsync(t => t.TagId == id, cancellationToken);
        return new TagDto { Id = tag.Id, Name = tag.Name, Color = tag.Color, Description = tag.Description, UsageCount = usage };
    }

    public async Task DeleteTagAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tag = await _db.Tags.FirstOrDefaultAsync(t => t.Id == id, cancellationToken) ?? throw new NotFoundException("Tag not found.");
        var links = await _db.ContractTags.Where(t => t.TagId == id).ToListAsync(cancellationToken);
        _db.ContractTags.RemoveRange(links);
        _db.Tags.Remove(tag);
        _audit.Record("settings.tags", "Deleted tag " + tag.Name + " (removed from " + links.Count + " contracts)");
        await _db.SaveChangesAsync(cancellationToken);
    }

    // ---------- watch ----------

    public async Task WatchAsync(Guid contractId, bool watch, CancellationToken cancellationToken = default)
    {
        var contract = await VisibleAsync(contractId, false, cancellationToken);
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var userId = profile.UserId;
        var row = await _db.ContractWatchers.FirstOrDefaultAsync(w => w.ContractId == contractId && w.UserId == userId, cancellationToken);
        if (watch && row is null)
        {
            _db.ContractWatchers.Add(new ContractWatcher { ContractId = contract.Id, UserId = userId, CreatedAt = _clock.UtcNow });
            _audit.Record("contract.watch", profile.Name + " started watching", contract.Id);
        }
        else if (!watch && row is not null)
        {
            _db.ContractWatchers.Remove(row);
            _audit.Record("contract.watch", profile.Name + " stopped watching", contract.Id);
        }
        else
        {
            return;
        }
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A second identical request at the same moment: the state is already what was asked for.
            _db.DiscardChanges();
        }
    }

    /// <summary>GET watching: watched contracts that are still visible, nearest notice first.</summary>
    public async Task<List<ContractListItemDto>> WatchingAsync(CancellationToken cancellationToken = default)
    {
        var filter = new ContractFilter { Watching = true, IncludeEnded = true, Sort = "notice", PageSize = 200 };
        var engine = new ContractFilterEngine(_db, _access, _currentUser, _clock);
        var result = await engine.RunAsync(filter, true, cancellationToken);
        return await _queries.ToListItemsAsync(result.Items, result.Reference, cancellationToken);
    }

    // ---------- comments ----------

    private static CommentDto ToDto(ContractComment comment, ReferenceData reference, AccessProfile profile)
    {
        var mentions = JsonDefaults.ReadList<Guid>(comment.MentionsJson);
        return new CommentDto
        {
            Id = comment.Id,
            ContractId = comment.ContractId,
            AuthorId = comment.AuthorId,
            AuthorName = reference.UserName(comment.AuthorId),
            AuthorInitials = reference.UserInitials(comment.AuthorId),
            Body = comment.Body,
            Mentions = mentions.Select(m => new CommentMentionDto { UserId = m, Name = reference.UserName(m) }).ToList(),
            CreatedAt = comment.CreatedAt,
            EditedAt = comment.EditedAt,
            CanEdit = comment.AuthorId == profile.UserId,
            CanDelete = comment.AuthorId == profile.UserId || profile.IsAdmin
        };
    }

    public async Task<List<CommentDto>> GetCommentsAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await VisibleAsync(contractId, false, cancellationToken);
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        var comments = await _db.ContractComments.AsNoTracking().Where(c => c.ContractId == contractId).ToListAsync(cancellationToken);
        return comments.OrderBy(c => c.CreatedAt).Select(c => ToDto(c, reference, profile)).ToList();
    }

    private static string CleanBody(string? body)
    {
        var text = (body ?? "").Trim();
        if (text.Length < 1 || text.Length > 4000)
        {
            throw new InputValidationException("body", "Write a comment of 1 to 4000 characters.");
        }
        return text;
    }

    /// <summary>Every mentioned user must exist, be active and see the contract (400 mentionIds otherwise).</summary>
    private async Task<List<Guid>> CheckMentionsAsync(Contract contract, List<Guid>? mentionIds, CancellationToken cancellationToken)
    {
        var result = new List<Guid>();
        foreach (var userId in (mentionIds ?? new List<Guid>()).Distinct())
        {
            var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is null || !user.IsActive)
            {
                throw new InputValidationException("mentionIds", "Unknown or inactive user.");
            }
            if (!await ContractAccessService.CanUserReadAsync(_db, userId, contract, cancellationToken))
            {
                throw new InputValidationException("mentionIds", user.Name + " cannot see this contract");
            }
            result.Add(userId);
        }
        return result;
    }

    public async Task<CommentDto> AddCommentAsync(Guid contractId, CommentInput input, CancellationToken cancellationToken = default)
    {
        var contract = await VisibleAsync(contractId, false, cancellationToken);
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var body = CleanBody(input?.Body);
        var mentions = await CheckMentionsAsync(contract, input?.MentionIds, cancellationToken);
        var comment = new ContractComment
        {
            Id = Guid.NewGuid(),
            ContractId = contract.Id,
            AuthorId = profile.UserId,
            Body = body,
            MentionsJson = JsonDefaults.Write(mentions),
            CreatedAt = _clock.UtcNow
        };
        _db.ContractComments.Add(comment);
        _audit.Record("comment.added", "Comment: " + (body.Length > 120 ? body.Substring(0, 120) + "..." : body), contract.Id);
        var data = new Dictionary<string, string> { ["comment"] = body.Length > 500 ? body.Substring(0, 500) + "..." : body };
        foreach (var userId in mentions.Where(m => m != profile.UserId))
        {
            await _notifier.SendAsync(new Notification(
                userId, NotificationCatalog.Mention,
                profile.Name + " mentioned you on " + contract.Title,
                profile.Name + " wrote on contract " + contract.ContractNo + " (" + contract.Title + "): " + data["comment"],
                contract.Id, data), cancellationToken);
        }
        await _db.SaveChangesAsync(cancellationToken);
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        return ToDto(comment, reference, profile);
    }

    public async Task<CommentDto> EditCommentAsync(Guid commentId, CommentInput input, CancellationToken cancellationToken = default)
    {
        var comment = await _db.ContractComments.FirstOrDefaultAsync(c => c.Id == commentId, cancellationToken)
            ?? throw new NotFoundException("Comment not found.");
        var contract = await VisibleAsync(comment.ContractId, false, cancellationToken);
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        if (comment.AuthorId != profile.UserId)
        {
            throw new ForbiddenException("Only the author can edit a comment.");
        }
        comment.Body = CleanBody(input?.Body);
        var before = JsonDefaults.ReadList<Guid>(comment.MentionsJson);
        var mentions = await CheckMentionsAsync(contract, input?.MentionIds, cancellationToken);
        comment.MentionsJson = JsonDefaults.Write(mentions);
        comment.EditedAt = _clock.UtcNow;
        _audit.Record("comment.edited", "Edited a comment", contract.Id);
        var data = new Dictionary<string, string> { ["comment"] = comment.Body.Length > 500 ? comment.Body.Substring(0, 500) + "..." : comment.Body };
        foreach (var userId in mentions.Where(m => !before.Contains(m) && m != profile.UserId))
        {
            await _notifier.SendAsync(new Notification(
                userId, NotificationCatalog.Mention,
                profile.Name + " mentioned you on " + contract.Title,
                profile.Name + " wrote on contract " + contract.ContractNo + " (" + contract.Title + "): " + data["comment"],
                contract.Id, data), cancellationToken);
        }
        await _db.SaveChangesAsync(cancellationToken);
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        return ToDto(comment, reference, profile);
    }

    public async Task DeleteCommentAsync(Guid commentId, CancellationToken cancellationToken = default)
    {
        var comment = await _db.ContractComments.FirstOrDefaultAsync(c => c.Id == commentId, cancellationToken)
            ?? throw new NotFoundException("Comment not found.");
        await VisibleAsync(comment.ContractId, false, cancellationToken);
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        if (comment.AuthorId != profile.UserId && !profile.IsAdmin)
        {
            throw new ForbiddenException("Only the author or an administrator can delete a comment.");
        }
        _db.ContractComments.Remove(comment);
        _audit.Record("comment.deleted", "Deleted a comment", comment.ContractId);
        await _db.SaveChangesAsync(cancellationToken);
    }

    // ---------- in-app notifications ----------

    /// <summary>The caller's notifications, without those about contracts the caller can no longer see.</summary>
    private async Task<IQueryable<InAppNotification>> OwnVisibleAsync(CancellationToken cancellationToken)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var userId = profile.UserId;
        var visible = (await _access.FilterAsync(_db.Contracts.AsNoTracking(), cancellationToken)).Select(c => c.Id);
        return _db.Notifications.AsNoTracking()
            .Where(n => n.UserId == userId && (n.ContractId == null || visible.Contains(n.ContractId.Value)));
    }

    public async Task<NotificationPageDto> GetNotificationsAsync(bool unreadOnly, int? page, int? pageSize, CancellationToken cancellationToken = default)
    {
        var query = await OwnVisibleAsync(cancellationToken);
        var unread = await query.CountAsync(n => n.ReadAt == null, cancellationToken);
        if (unreadOnly)
        {
            query = query.Where(n => n.ReadAt == null);
        }
        var total = await query.CountAsync(cancellationToken);
        var size = Math.Clamp(pageSize ?? 25, 1, 200);
        var number = Math.Max(1, page ?? 1);
        var rows = await query.OrderByDescending(n => n.CreatedAt).Skip((number - 1) * size).Take(size).ToListAsync(cancellationToken);
        return new NotificationPageDto
        {
            Items = rows.Select(n => new AppNotificationDto
            {
                Id = n.Id,
                Event = n.Event,
                Title = n.Title,
                Body = n.Body,
                ContractId = n.ContractId,
                Link = n.Link,
                ActorName = n.ActorName,
                CreatedAt = n.CreatedAt,
                ReadAt = n.ReadAt
            }).ToList(),
            Total = total,
            Page = number,
            PageSize = size,
            Unread = unread
        };
    }

    public async Task<CountDto> UnreadCountAsync(CancellationToken cancellationToken = default)
    {
        var query = await OwnVisibleAsync(cancellationToken);
        return new CountDto { Count = await query.CountAsync(n => n.ReadAt == null, cancellationToken) };
    }

    public async Task MarkReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var userId = profile.UserId;
        var row = await _db.Notifications.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId, cancellationToken)
            ?? throw new NotFoundException("Notification not found.");
        if (row.ReadAt is null)
        {
            row.ReadAt = _clock.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task MarkAllReadAsync(CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var userId = profile.UserId;
        var rows = await _db.Notifications.Where(n => n.UserId == userId && n.ReadAt == null).ToListAsync(cancellationToken);
        var now = _clock.UtcNow;
        foreach (var row in rows)
        {
            row.ReadAt = now;
        }
        if (rows.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    // ---------- saved views ----------

    private static SavedViewDto ToDto(SavedView view, ReferenceData reference, AccessProfile profile)
    {
        JsonElement query;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(view.QueryJson) ? "{}" : view.QueryJson);
            query = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse("{}");
            query = empty.RootElement.Clone();
        }
        return new SavedViewDto
        {
            Id = view.Id,
            Name = view.Name,
            Scope = view.Scope,
            OwnerId = view.OwnerId,
            OwnerName = reference.UserName(view.OwnerId),
            Columns = JsonDefaults.ReadList<string>(view.ColumnsJson),
            Query = query,
            IsDefault = view.IsDefault,
            CanEdit = view.OwnerId == profile.UserId || (view.Scope == "Shared" && profile.IsAdmin),
            UpdatedAt = view.UpdatedAt
        };
    }

    public async Task<List<SavedViewDto>> GetViewsAsync(CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var userId = profile.UserId;
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        var views = await _db.SavedViews.AsNoTracking().Where(v => v.OwnerId == userId || v.Scope == "Shared").ToListAsync(cancellationToken);
        return views
            .OrderBy(v => v.OwnerId == userId && v.Scope == "Personal" ? 0 : 1)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(v => ToDto(v, reference, profile))
            .ToList();
    }

    private async Task ApplyViewAsync(SavedView view, SavedViewInput input, AccessProfile profile, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var name = (input?.Name ?? "").Trim();
        if (name.Length < 1 || name.Length > 80) ContractValidator.Add(errors, "name", "Name is required (at most 80 characters).");
        var scope = input?.Scope ?? "Personal";
        if (scope != "Personal" && scope != "Shared") ContractValidator.Add(errors, "scope", "Use Personal or Shared.");
        if (input?.IsDefault == true && scope != "Personal") ContractValidator.Add(errors, "isDefault", "Only a personal view can be your default.");
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        var columns = input?.Columns ?? new List<string>();
        foreach (var column in columns)
        {
            var known = ContractQueryService.ExportColumns.Any(c => c.Key == column)
                || (column.StartsWith("cf.", StringComparison.Ordinal) && reference.CustomFields.Any(d => d.Key == column.Substring(3)));
            if (!known) ContractValidator.Add(errors, "columns", "Unknown column '" + column + "'.");
        }
        var queryJson = input?.Query is null || input.Query.Value.ValueKind == JsonValueKind.Undefined || input.Query.Value.ValueKind == JsonValueKind.Null
            ? "{}"
            : input.Query.Value.GetRawText();
        if (input?.Query is not null && input.Query.Value.ValueKind != JsonValueKind.Object && input.Query.Value.ValueKind != JsonValueKind.Undefined && input.Query.Value.ValueKind != JsonValueKind.Null)
            ContractValidator.Add(errors, "query", "The query must be an object.");
        if (queryJson.Length > 16_000) ContractValidator.Add(errors, "query", "The query is too large.");
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }
        if (scope == "Shared" && (isNew || view.Scope != "Shared") && !profile.IsAdmin && !profile.HasRole(AppRole.ContractOwner))
        {
            throw new ForbiddenException("Shared views can be created by contract owners and administrators.");
        }
        var ownerId = isNew ? profile.UserId : view.OwnerId;
        var viewId = view.Id;
        var lower = name.ToLower();
        if (await _db.SavedViews.AnyAsync(v => v.Id != viewId && v.OwnerId == ownerId && v.Scope == scope && v.Name.ToLower() == lower, cancellationToken))
        {
            throw new ConflictException("You already have a view with this name.", ProblemTypes.Duplicate);
        }
        if (input!.IsDefault == true)
        {
            foreach (var other in await _db.SavedViews.Where(v => v.OwnerId == ownerId && v.Id != viewId && v.IsDefault).ToListAsync(cancellationToken))
            {
                other.IsDefault = false;
            }
        }
        if (!isNew && view.Scope != scope)
        {
            _audit.Record("view.shared-changed", "View \"" + name + "\" is now " + scope.ToLowerInvariant());
        }
        else if (isNew && scope == "Shared")
        {
            _audit.Record("view.shared-changed", "Shared view \"" + name + "\" created");
        }
        view.Name = name;
        view.Scope = scope;
        view.ColumnsJson = JsonDefaults.Write(columns.Distinct().ToList());
        view.QueryJson = queryJson;
        view.IsDefault = input.IsDefault == true && scope == "Personal";
        view.UpdatedAt = _clock.UtcNow;
    }

    public async Task<SavedViewDto> CreateViewAsync(SavedViewInput input, CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var view = new SavedView { Id = Guid.NewGuid(), OwnerId = profile.UserId, CreatedAt = _clock.UtcNow };
        await ApplyViewAsync(view, input, profile, true, cancellationToken);
        _db.SavedViews.Add(view);
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(view, await ReferenceData.LoadAsync(_db, cancellationToken), profile);
    }

    private async Task<SavedView> EditableViewAsync(Guid id, AccessProfile profile, CancellationToken cancellationToken)
    {
        var view = await _db.SavedViews.FirstOrDefaultAsync(v => v.Id == id, cancellationToken);
        if (view is null || (view.OwnerId != profile.UserId && view.Scope != "Shared"))
        {
            throw new NotFoundException("View not found.");
        }
        if (view.OwnerId != profile.UserId && !profile.IsAdmin)
        {
            throw new ForbiddenException("Only the owner or an administrator can change this view.");
        }
        return view;
    }

    public async Task<SavedViewDto> UpdateViewAsync(Guid id, SavedViewInput input, CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var view = await EditableViewAsync(id, profile, cancellationToken);
        await ApplyViewAsync(view, input, profile, false, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(view, await ReferenceData.LoadAsync(_db, cancellationToken), profile);
    }

    public async Task DeleteViewAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var view = await EditableViewAsync(id, profile, cancellationToken);
        if (view.Scope == "Shared")
        {
            _audit.Record("view.shared-changed", "Shared view \"" + view.Name + "\" deleted");
        }
        _db.SavedViews.Remove(view);
        await _db.SaveChangesAsync(cancellationToken);
    }

    // ---------- bulk actions ----------

    public async Task<BulkResultDto> BulkAsync(BulkInput input, CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var action = input?.Action ?? "";
        var ids = (input?.ContractIds ?? new List<Guid>()).Distinct().ToList();
        if (ids.Count < 1 || ids.Count > 500)
        {
            throw new InputValidationException("contractIds", "Choose 1 to 500 contracts.");
        }
        if (action != "AddTag" && action != "RemoveTag" && action != "Reassign" && action != "RequestDataCompletion")
        {
            throw new InputValidationException("action", "Use AddTag, RemoveTag, Reassign or RequestDataCompletion.");
        }
        Tag? tag = null;
        AppUser? target = null;
        string? role = null;
        if (action == "AddTag" || action == "RemoveTag")
        {
            var tagId = input!.TagId ?? Guid.Empty;
            tag = await _db.Tags.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tagId, cancellationToken)
                ?? throw new InputValidationException("tagId", "Unknown tag.");
        }
        if (action == "Reassign")
        {
            role = input!.Role;
            if (role != "Owner" && role != "Delegate" && role != "Responsible")
                throw new InputValidationException("role", "Use Owner, Delegate or Responsible.");
            var toUserId = input.ToUserId ?? Guid.Empty;
            target = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == toUserId, cancellationToken);
            if (target is null || !target.IsActive)
                throw new InputValidationException("toUserId", "Choose an active user.");
        }

        var result = new BulkResultDto { Action = action, Requested = ids.Count };
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        foreach (var id in ids)
        {
            var contract = await _db.Contracts.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (contract is null || !await _access.CanReadAsync(contract, cancellationToken))
            {
                result.Failed.Add(new BulkFailureDto { ContractId = id, Title = "", Reason = "Not found" });
                continue;
            }
            var canEdit = ContractAccessRules.CanEdit(profile, contract);
            switch (action)
            {
                case "AddTag":
                case "RemoveTag":
                {
                    if (!canEdit)
                    {
                        result.Failed.Add(new BulkFailureDto { ContractId = id, Title = contract.Title, Reason = "You cannot change this contract" });
                        continue;
                    }
                    var contractId = contract.Id;
                    var tagId = tag!.Id;
                    var link = await _db.ContractTags.FirstOrDefaultAsync(t => t.ContractId == contractId && t.TagId == tagId, cancellationToken);
                    if (action == "AddTag" && link is null)
                    {
                        _db.ContractTags.Add(new ContractTag { ContractId = contractId, TagId = tagId });
                        _audit.Record("contract.tags-changed", "Added tag " + tag.Name, contract.Id);
                    }
                    else if (action == "RemoveTag" && link is not null)
                    {
                        _db.ContractTags.Remove(link);
                        _audit.Record("contract.tags-changed", "Removed tag " + tag.Name, contract.Id);
                    }
                    break;
                }
                case "Reassign":
                {
                    if (!canEdit)
                    {
                        result.Failed.Add(new BulkFailureDto { ContractId = id, Title = contract.Title, Reason = "You cannot change this contract" });
                        continue;
                    }
                    if (contract.Status == ContractStatus.InApproval)
                    {
                        result.Failed.Add(new BulkFailureDto { ContractId = id, Title = contract.Title, Reason = "In approval" });
                        continue;
                    }
                    if (role == "Owner" && (target!.Roles & (AppRole.ContractOwner | AppRole.Admin)) == AppRole.None)
                    {
                        result.Failed.Add(new BulkFailureDto { ContractId = id, Title = contract.Title, Reason = target.Name + " does not have the contract owner role" });
                        continue;
                    }
                    var before = role == "Owner" ? contract.OwnerId : role == "Delegate" ? contract.DelegateId : contract.ResponsibleId;
                    if (before == target!.Id)
                    {
                        break;
                    }
                    if (role == "Owner") contract.OwnerId = target.Id;
                    else if (role == "Delegate") contract.DelegateId = target.Id;
                    else contract.ResponsibleId = target.Id;
                    contract.UpdatedAt = _clock.UtcNow;
                    _audit.Record("contract.reassigned", role + ": " + (before is null ? "(empty)" : reference.UserName(before)) + " -> " + target.Name + " (bulk)", contract.Id);
                    break;
                }
                case "RequestDataCompletion":
                {
                    var missing = reference.Missing(contract);
                    if (missing.Count == 0)
                    {
                        result.Failed.Add(new BulkFailureDto { ContractId = id, Title = contract.Title, Reason = "Nothing is missing" });
                        continue;
                    }
                    var message = (input!.Message ?? "").Trim();
                    if (message.Length > 1000) message = message.Substring(0, 1000);
                    var data = new Dictionary<string, string> { ["missing"] = string.Join(", ", missing), ["message"] = message };
                    // One notification: the owner is the natural recipient; the event's recipient
                    // rules (default Owner and Delegate) add the delegate.
                    await _notifier.SendAsync(new Notification(
                        contract.OwnerId, NotificationCatalog.DataCompletionRequested,
                        "Please complete the data of " + contract.Title,
                        profile.Name + " asks you to complete contract " + contract.ContractNo + " (" + contract.Title + "). Missing: "
                            + data["missing"] + "." + (message.Length > 0 ? " " + message : ""),
                        contract.Id, data), cancellationToken);
                    _audit.Record("contract.data-completion-requested", "Requested data completion: " + data["missing"], contract.Id);
                    break;
                }
            }
            result.Succeeded++;
        }
        _audit.Record("contract.bulk", action + " on " + result.Succeeded + " of " + result.Requested + " contracts");
        await _db.SaveChangesAsync(cancellationToken);
        return result;
    }
}
