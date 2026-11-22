using System.Globalization;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>Snippets with highlight offsets (CONTRACT_V3 section 3).</summary>
public static class Snippets
{
    public const int MaxLength = 200;

    /// <summary>Lowercased whitespace-separated terms; at most 10, each at most 100 characters.</summary>
    public static List<string> Terms(string? q)
    {
        return (q ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Select(t => t.Length > 100 ? t.Substring(0, 100) : t)
            .Distinct()
            .Take(10)
            .ToList();
    }

    public static bool ContainsAll(string text, IReadOnlyList<string> terms)
    {
        var lower = text.ToLowerInvariant();
        return terms.All(t => lower.Contains(t, StringComparison.Ordinal));
    }

    public static bool ContainsAny(string? text, IReadOnlyList<string> terms)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        var lower = text.ToLowerInvariant();
        return terms.Any(t => lower.Contains(t, StringComparison.Ordinal));
    }

    /// <summary>
    /// At most 200 characters around the first term found in <paramref name="text"/>, cut on word
    /// boundaries with "…" where cut; highlights are [start, end) offsets of every term occurrence.
    /// Null when no term occurs. <paramref name="startsAtBeginning"/>/<paramref name="endsAtEnd"/>
    /// say whether the text is the whole source (so no "…" is added at that side).
    /// </summary>
    public static SnippetDto? Build(string? text, IReadOnlyList<string> terms, bool startsAtBeginning = true, bool endsAtEnd = true)
    {
        if (string.IsNullOrEmpty(text) || terms.Count == 0)
        {
            return null;
        }
        var lower = text.ToLowerInvariant();
        var first = -1;
        foreach (var term in terms)
        {
            var index = lower.IndexOf(term, StringComparison.Ordinal);
            if (index >= 0 && (first < 0 || index < first))
            {
                first = index;
            }
        }
        if (first < 0)
        {
            return null;
        }
        var start = Math.Max(0, first - 60);
        var end = Math.Min(text.Length, start + MaxLength - 2);
        if (start > 0)
        {
            var space = text.IndexOf(' ', start);
            if (space >= 0 && space < first)
            {
                start = space + 1;
            }
        }
        if (end < text.Length)
        {
            var space = text.LastIndexOf(' ', end - 1);
            if (space > first)
            {
                end = space;
            }
        }
        var prefix = start > 0 || !startsAtBeginning ? "…" : "";
        var suffix = end < text.Length || !endsAtEnd ? "…" : "";
        var body = text.Substring(start, end - start);
        var snippet = new SnippetDto { Text = prefix + body + suffix };
        var snippetLower = snippet.Text.ToLowerInvariant();
        foreach (var term in terms)
        {
            var from = 0;
            while (from <= snippetLower.Length - term.Length)
            {
                var index = snippetLower.IndexOf(term, from, StringComparison.Ordinal);
                if (index < 0)
                {
                    break;
                }
                snippet.Highlights.Add(new[] { index, index + term.Length });
                from = index + term.Length;
            }
        }
        snippet.Highlights = snippet.Highlights.OrderBy(h => h[0]).ToList();
        return snippet;
    }
}

/// <summary>Search, person page and counterparty page. Every contract-derived result is access scoped.</summary>
public class SearchService
{
    private const int DocumentCap = 1000;

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IContractAccessService _access;
    private readonly IClock _clock;
    private readonly ContractQueryService _queries;

    public SearchService(IAppDbContext db, ICurrentUser currentUser, IContractAccessService access, IClock clock, ContractQueryService queries)
    {
        _db = db;
        _currentUser = currentUser;
        _access = access;
        _clock = clock;
        _queries = queries;
    }

    private async Task<IQueryable<Contract>> VisibleAsync(CancellationToken cancellationToken) =>
        await _access.FilterAsync(_db.Contracts.AsNoTracking(), cancellationToken);

    // ---------- contracts ----------

    private sealed class ContractMatch
    {
        public Contract Contract { get; init; } = new();
        public List<string> MatchedIn { get; } = new();
        public SnippetDto? Snippet { get; set; }
        public int Rank { get; set; }
    }

    private async Task<List<ContractMatch>> MatchContractsAsync(
        List<string> terms, Guid? personId, List<Guid> entityIds, Guid? departmentId, Guid? typeId, ReferenceData reference, CancellationToken cancellationToken)
    {
        var query = await VisibleAsync(cancellationToken);
        if (personId is not null)
        {
            var person = personId.Value;
            query = query.Where(c => c.OwnerId == person || c.DelegateId == person || c.ResponsibleId == person);
        }
        if (entityIds.Count > 0) { var scope = entityIds.Distinct().ToList(); query = query.Where(c => scope.Contains(c.EntityId)); }
        if (departmentId is not null) { var id = departmentId.Value; query = query.Where(c => c.DepartmentId == id); }
        if (typeId is not null) { var id = typeId.Value; query = query.Where(c => c.TypeId == id); }
        var contracts = await query.ToListAsync(cancellationToken);
        if (contracts.Count == 0)
        {
            return new List<ContractMatch>();
        }
        var ids = query.Select(c => c.Id);
        var obligations = await _db.Obligations.AsNoTracking().Where(o => ids.Contains(o.ContractId))
            .Select(o => new { o.ContractId, o.Description }).ToListAsync(cancellationToken);
        var tagNames = (await _db.Tags.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(t => t.Id, t => t.Name);
        var links = tagNames.Count == 0
            ? new List<ContractTag>()
            : await _db.ContractTags.AsNoTracking().Where(t => ids.Contains(t.ContractId)).ToListAsync(cancellationToken);
        var textFieldIds = reference.CustomFields
            .Where(d => d.FieldType == CustomFieldType.Text || d.FieldType == CustomFieldType.Choice)
            .Select(d => d.Id).ToList();
        var customTexts = textFieldIds.Count == 0
            ? new List<CustomFieldValue>()
            : await _db.CustomFieldValues.AsNoTracking().Where(v => textFieldIds.Contains(v.FieldId) && ids.Contains(v.ContractId)).ToListAsync(cancellationToken);

        var obligationsBy = obligations.GroupBy(o => o.ContractId).ToDictionary(g => g.Key, g => g.Select(o => o.Description).ToList());
        var tagsBy = links.GroupBy(l => l.ContractId).ToDictionary(g => g.Key, g => g.Select(l => tagNames.TryGetValue(l.TagId, out var n) ? n : "").ToList());
        var customBy = customTexts.GroupBy(v => v.ContractId).ToDictionary(g => g.Key, g => g.Select(v => v.TextValue ?? "").ToList());

        var result = new List<ContractMatch>();
        foreach (var contract in contracts)
        {
            var fields = new List<(string Field, string Text)>
            {
                ("title", contract.Title),
                ("contractNo", contract.ContractNo),
                ("counterparty", contract.CounterpartyName),
                ("owner", reference.UserName(contract.OwnerId)),
                ("delegate", reference.UserName(contract.DelegateId)),
                ("responsible", reference.UserName(contract.ResponsibleId)),
                ("summary", contract.Summary ?? "")
            };
            foreach (var description in obligationsBy.TryGetValue(contract.Id, out var list) ? list : new List<string>())
            {
                fields.Add(("obligation", description));
            }
            foreach (var name in tagsBy.TryGetValue(contract.Id, out var tags) ? tags : new List<string>())
            {
                fields.Add(("tag", name));
            }
            foreach (var value in customBy.TryGetValue(contract.Id, out var values) ? values : new List<string>())
            {
                fields.Add(("customField", value));
            }
            var combined = string.Join("\n", fields.Select(f => f.Text));
            if (!Snippets.ContainsAll(combined, terms))
            {
                continue;
            }
            var match = new ContractMatch { Contract = contract };
            foreach (var field in fields)
            {
                if (Snippets.ContainsAny(field.Text, terms) && !match.MatchedIn.Contains(field.Field))
                {
                    match.MatchedIn.Add(field.Field);
                }
                if (match.Snippet is null && (field.Field == "summary" || field.Field == "obligation"))
                {
                    match.Snippet = Snippets.Build(field.Text, terms);
                }
            }
            match.Rank = match.MatchedIn.Contains("title") ? 0 : match.MatchedIn.Contains("contractNo") ? 1 : 2;
            result.Add(match);
        }
        return result
            .OrderBy(m => m.Rank)
            .ThenBy(m => m.Contract.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------- documents ----------

    private sealed class DocumentRow
    {
        public Guid Id { get; init; }
        public Guid ContractId { get; init; }
        public string Name { get; init; } = "";
        public DocumentKind Kind { get; init; }
        public int Version { get; init; }
        public long Size { get; init; }
        public DateTime UploadedAt { get; init; }
        public Guid? UploadedById { get; init; }
    }

    private async Task<List<DocumentRow>> MatchDocumentsAsync(FullSearchQuery query, List<string> terms, CancellationToken cancellationToken)
    {
        var visible = await VisibleAsync(cancellationToken);
        var scopeIds = query.ScopeEntityIds().Distinct().ToList();
        if (scopeIds.Count > 0) { visible = visible.Where(c => scopeIds.Contains(c.EntityId)); }
        if (query.DepartmentId is not null) { var id = query.DepartmentId.Value; visible = visible.Where(c => c.DepartmentId == id); }
        if (query.TypeId is not null) { var id = query.TypeId.Value; visible = visible.Where(c => c.TypeId == id); }
        var visibleIds = visible.Select(c => c.Id);

        var rows = from d in _db.Documents.AsNoTracking()
                   join t in _db.DocumentTexts.AsNoTracking() on d.Id equals t.DocumentId into texts
                   from t in texts.DefaultIfEmpty()
                   where visibleIds.Contains(d.ContractId)
                   select new { d, t };
        if (query.PersonId is not null)
        {
            var person = query.PersonId.Value;
            var personContracts = visible.Where(c => c.OwnerId == person || c.DelegateId == person || c.ResponsibleId == person).Select(c => c.Id);
            rows = rows.Where(x => x.d.UploadedById == person || personContracts.Contains(x.d.ContractId));
        }
        if (!string.IsNullOrWhiteSpace(query.DocumentKind))
        {
            if (!Enum.TryParse<DocumentKind>(query.DocumentKind.Trim(), false, out var kind))
            {
                return new List<DocumentRow>();
            }
            rows = rows.Where(x => x.d.Kind == kind);
        }
        if (query.UploadedFrom is not null)
        {
            var from = query.UploadedFrom.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            rows = rows.Where(x => x.d.UploadedAt >= from);
        }
        if (query.UploadedTo is not null)
        {
            var to = query.UploadedTo.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            rows = rows.Where(x => x.d.UploadedAt < to);
        }
        foreach (var term in terms)
        {
            var value = term;
            rows = rows.Where(x => x.d.Name.ToLower().Contains(value) || (x.t != null && x.t.NormalizedText.Contains(value)));
        }
        var found = await rows
            .OrderByDescending(x => x.d.UploadedAt)
            .Take(DocumentCap)
            .Select(x => new DocumentRow
            {
                Id = x.d.Id,
                ContractId = x.d.ContractId,
                Name = x.d.Name,
                Kind = x.d.Kind,
                Version = x.d.Version,
                Size = x.d.Size,
                UploadedAt = x.d.UploadedAt,
                UploadedById = x.d.UploadedById
            })
            .ToListAsync(cancellationToken);
        // Latest matching version of each name.
        return found
            .GroupBy(r => (r.ContractId, r.Name.ToLowerInvariant()))
            .Select(g => g.OrderByDescending(r => r.Version).First())
            .OrderBy(r => Snippets.ContainsAny(r.Name, terms) ? 0 : 1)
            .ThenByDescending(r => r.UploadedAt)
            .ToList();
    }

    /// <summary>A text snippet for one document hit: finds the first term in SQL and reads only a window of the text.</summary>
    private async Task<SnippetDto?> DocumentSnippetAsync(Guid documentId, List<string> terms, CancellationToken cancellationToken)
    {
        var first = -1;
        foreach (var term in terms)
        {
            var value = term;
            var position = await _db.DocumentTexts.AsNoTracking()
                .Where(t => t.DocumentId == documentId)
                .Select(t => (int?)t.NormalizedText.IndexOf(value))
                .FirstOrDefaultAsync(cancellationToken);
            if (position is not null && position.Value >= 0 && (first < 0 || position.Value < first))
            {
                first = position.Value;
            }
        }
        if (first < 0)
        {
            return null;
        }
        var start = Math.Max(0, first - 120);
        const int length = 400;
        var window = await _db.DocumentTexts.AsNoTracking()
            .Where(t => t.DocumentId == documentId)
            .Select(t => new { Part = t.Text.Substring(start, length), Total = t.Text.Length })
            .FirstOrDefaultAsync(cancellationToken);
        if (window is null || string.IsNullOrEmpty(window.Part))
        {
            return null;
        }
        return Snippets.Build(window.Part, terms, start == 0, start + window.Part.Length >= window.Total);
    }

    private async Task<DocumentHitDto> ToHitAsync(DocumentRow row, Dictionary<Guid, Contract> contracts, ReferenceData reference, List<string> terms, bool withSnippet, CancellationToken cancellationToken)
    {
        contracts.TryGetValue(row.ContractId, out var contract);
        var hit = new DocumentHitDto
        {
            DocumentId = row.Id,
            ContractId = row.ContractId,
            ContractNo = contract?.ContractNo ?? "",
            ContractTitle = contract?.Title ?? "",
            OwnerName = contract is null ? "" : reference.UserName(contract.OwnerId),
            FileName = row.Name,
            Kind = row.Kind.ToString(),
            Version = row.Version,
            Size = row.Size,
            UploadedAt = row.UploadedAt,
            UploadedBy = row.UploadedById is null ? "System" : reference.UserName(row.UploadedById)
        };
        if (terms.Count > 0)
        {
            if (Snippets.ContainsAny(row.Name, terms))
            {
                hit.MatchedIn.Add("name");
            }
            if (withSnippet)
            {
                hit.Snippet = await DocumentSnippetAsync(row.Id, terms, cancellationToken);
                if (hit.Snippet is not null)
                {
                    hit.MatchedIn.Add("text");
                }
            }
        }
        return hit;
    }

    // ---------- full search ----------

    public async Task<FullSearchResultDto> FullSearchAsync(FullSearchQuery query, CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var q = (query.Q ?? "").Trim();
        var result = new FullSearchResultDto { Q = q };
        var terms = Snippets.Terms(q);
        if (q.Length < 2 || terms.Count == 0)
        {
            return result;
        }
        var group = (query.Group ?? "").Trim();
        if (group.Length > 0 && group != "contracts" && group != "documents" && group != "people" && group != "counterparties")
        {
            throw new InputValidationException("group", "Use contracts, documents, people or counterparties.");
        }
        var pageSize = Math.Clamp(query.PageSize ?? 10, 1, 50);
        var page = Math.Max(1, query.Page ?? 1);
        int SkipFor(string name) => group == name ? (page - 1) * pageSize : 0;
        bool Wants(string name) => group.Length == 0 || group == name;

        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        var visibleContracts = await (await VisibleAsync(cancellationToken)).ToListAsync(cancellationToken);
        var byId = visibleContracts.ToDictionary(c => c.Id);

        // Contracts
        var contractMatches = await MatchContractsAsync(terms, query.PersonId, query.ScopeEntityIds(), query.DepartmentId, query.TypeId, reference, cancellationToken);
        result.Contracts.Total = contractMatches.Count;
        if (Wants("contracts"))
        {
            var pageMatches = contractMatches.Skip(SkipFor("contracts")).Take(pageSize).ToList();
            await reference.LoadExtrasAsync(_db, pageMatches.Select(m => m.Contract.Id).ToList(), profile.UserId, cancellationToken);
            var today = _clock.Today;
            result.Contracts.Items = pageMatches.Select(m => new ContractHitDto
            {
                Item = ContractQueryService.ToListItem(m.Contract, reference, today),
                MatchedIn = m.MatchedIn,
                Snippet = m.Snippet
            }).ToList();
        }

        // Documents
        var documents = await MatchDocumentsAsync(query, terms, cancellationToken);
        result.Documents.Total = documents.Count;
        if (Wants("documents"))
        {
            foreach (var row in documents.Skip(SkipFor("documents")).Take(pageSize))
            {
                result.Documents.Items.Add(await ToHitAsync(row, byId, reference, terms, true, cancellationToken));
            }
        }

        // People
        var departmentNames = await DepartmentNamesAsync(cancellationToken);
        var people = reference.Users.Values
            .Where(u => u.IsActive && Snippets.ContainsAll(u.Name + "\n" + u.Email + "\n" + (u.JobTitle ?? ""), terms))
            .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.People.Total = people.Count;
        if (Wants("people"))
        {
            result.People.Items = people.Skip(SkipFor("people")).Take(pageSize).Select(u => new PersonHitDto
            {
                Id = u.Id,
                Name = u.Name,
                Initials = u.Initials,
                Title = u.JobTitle,
                DepartmentNames = departmentNames.TryGetValue(u.Id, out var names) ? names : new List<string>(),
                ContractCount = visibleContracts.Count(c => c.OwnerId == u.Id || c.DelegateId == u.Id || c.ResponsibleId == u.Id)
            }).ToList();
        }

        // Counterparties
        var suppliers = (await _db.Suppliers.AsNoTracking().Where(s => s.IsActive).ToListAsync(cancellationToken))
            .Where(s => Snippets.ContainsAll(s.Name + "\n" + (s.VatNo ?? "") + "\n" + (s.ExternalId ?? ""), terms))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.Counterparties.Total = suppliers.Count;
        if (Wants("counterparties"))
        {
            result.Counterparties.Items = suppliers.Skip(SkipFor("counterparties")).Take(pageSize).Select(s => new CounterpartyHitDto
            {
                Id = s.Id,
                Name = s.Name,
                Country = s.Country,
                ContractCount = visibleContracts.Count(c => c.SupplierId == s.Id)
            }).ToList();
        }
        return result;
    }

    private async Task<Dictionary<Guid, List<string>>> DepartmentNamesAsync(CancellationToken cancellationToken)
    {
        var departments = (await _db.Departments.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(d => d.Id, d => d.Name);
        var memberships = await _db.UserDepartments.AsNoTracking().ToListAsync(cancellationToken);
        return memberships.GroupBy(m => m.UserId).ToDictionary(
            g => g.Key,
            g => g.Select(m => departments.TryGetValue(m.DepartmentId, out var name) ? name : "").Where(n => n.Length > 0).OrderBy(n => n).ToList());
    }

    /// <summary>GET search?q= (Ctrl+K): up to 4 contracts, 2 documents, 2 people, then suppliers; 8 at most.</summary>
    public Task<List<SearchResultDto>> PaletteAsync(string? q, CancellationToken cancellationToken = default) => PaletteAsync(q, null, cancellationToken);

    /// <summary>Release 4: entityIds = the entity scope, for contract and document hits (people and counterparties are not scoped).</summary>
    public async Task<List<SearchResultDto>> PaletteAsync(string? q, IReadOnlyCollection<Guid>? entityIds, CancellationToken cancellationToken = default)
    {
        var results = new List<SearchResultDto>();
        var full = await FullSearchAsync(new FullSearchQuery { Q = q, PageSize = 8, EntityIds = entityIds is null ? new List<Guid>() : entityIds.ToList() }, cancellationToken);
        foreach (var hit in full.Contracts.Items.Take(4))
        {
            results.Add(new SearchResultDto { Kind = "Contract", Id = hit.Item.Id, Title = hit.Item.Title, Subtitle = hit.Item.ContractNo + " · " + hit.Item.Counterparty });
        }
        foreach (var hit in full.Documents.Items.Take(2))
        {
            results.Add(new SearchResultDto { Kind = "Document", Id = hit.DocumentId, Title = hit.FileName, Subtitle = hit.ContractTitle, ContractId = hit.ContractId });
        }
        foreach (var hit in full.People.Items.Take(2))
        {
            results.Add(new SearchResultDto { Kind = "Person", Id = hit.Id, Title = hit.Name, Subtitle = hit.Title ?? "" });
        }
        foreach (var hit in full.Counterparties.Items)
        {
            if (results.Count >= 8)
            {
                break;
            }
            results.Add(new SearchResultDto { Kind = "Supplier", Id = hit.Id, Title = hit.Name, Subtitle = hit.Country ?? "" });
        }
        return results.Take(8).ToList();
    }

    // ---------- person page ----------

    public async Task<PersonPageDto> PersonAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new NotFoundException("Person not found.");
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        var query = (await VisibleAsync(cancellationToken))
            .Where(c => c.OwnerId == userId || c.DelegateId == userId || c.ResponsibleId == userId);
        var contracts = await query.ToListAsync(cancellationToken);
        contracts = contracts
            .OrderBy(c => ContractCalculations.ListNoticeDeadline(c) is null ? 1 : 0)
            .ThenBy(c => ContractCalculations.ListNoticeDeadline(c) ?? DateOnly.MaxValue)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        await reference.LoadExtrasAsync(_db, contracts.Select(c => c.Id).ToList(), profile.UserId, cancellationToken);
        var departmentNames = await DepartmentNamesAsync(cancellationToken);
        var today = _clock.Today;
        var page = new PersonPageDto
        {
            Person = new PersonInfoDto
            {
                Id = user.Id,
                Name = user.Name,
                Initials = user.Initials,
                Email = user.Email,
                Title = user.JobTitle,
                DepartmentNames = departmentNames.TryGetValue(user.Id, out var names) ? names : new List<string>(),
                IsActive = user.IsActive
            },
            Counts = new PersonCountsDto
            {
                Owner = contracts.Count(c => c.OwnerId == userId),
                Delegate = contracts.Count(c => c.DelegateId == userId),
                Responsible = contracts.Count(c => c.ResponsibleId == userId),
                Total = contracts.Count,
                Active = contracts.Count(c => c.Status == ContractStatus.Active)
            },
            AnnualValueDkk = contracts.Where(DataQuality.IsLive).Sum(c => reference.AnnualDkk(c))
        };
        foreach (var contract in contracts)
        {
            var item = new PersonContractDto();
            ContractQueryService.FillListItem(item, contract, reference, today);
            if (contract.OwnerId == userId) item.Roles.Add("Owner");
            if (contract.DelegateId == userId) item.Roles.Add("Delegate");
            if (contract.ResponsibleId == userId) item.Roles.Add("Responsible");
            page.Contracts.Add(item);
        }
        return page;
    }

    // ---------- counterparties ----------

    public static CounterpartyDto ToCounterpartyDto(Supplier supplier, int contractCount) => new()
    {
        Id = supplier.Id,
        Name = supplier.Name,
        Country = supplier.Country,
        VatNo = supplier.VatNo,
        Kind = supplier.Kind.ToString(),
        ExternalId = supplier.ExternalId,
        City = supplier.City,
        Email = supplier.Email,
        Phone = supplier.Phone,
        IsActive = supplier.IsActive,
        Source = string.IsNullOrWhiteSpace(supplier.Source) ? "Seed" : supplier.Source!,
        ContractCount = contractCount,
        UpdatedAt = supplier.UpdatedAt ?? DateTime.SpecifyKind(new DateTime(2026, 1, 1), DateTimeKind.Utc)
    };

    /// <summary>GET counterparties?q=: active master records, max 20, with the caller's visible contract count.</summary>
    public async Task<List<CounterpartyDto>> FindCounterpartiesAsync(string? q, CancellationToken cancellationToken = default)
    {
        await _currentUser.GetProfileAsync(cancellationToken);
        var term = (q ?? "").Trim();
        var suppliers = (await _db.Suppliers.AsNoTracking().Where(s => s.IsActive).ToListAsync(cancellationToken))
            .Where(s => term.Length == 0
                || s.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (s.VatNo ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)
                || (s.ExternalId ?? "").Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
        var ids = suppliers.Select(s => s.Id).ToList();
        var counts = (await (await VisibleAsync(cancellationToken))
                .Where(c => c.SupplierId != null && ids.Contains(c.SupplierId.Value))
                .Select(c => c.SupplierId!.Value)
                .ToListAsync(cancellationToken))
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        return suppliers.Select(s => ToCounterpartyDto(s, counts.TryGetValue(s.Id, out var n) ? n : 0)).ToList();
    }

    public async Task<CounterpartyPageDto> CounterpartyAsync(Guid supplierId, CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var supplier = await _db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == supplierId, cancellationToken)
            ?? throw new NotFoundException("Counterparty not found.");
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        var visible = (await VisibleAsync(cancellationToken)).Where(c => c.SupplierId == supplierId);
        var contracts = (await visible.ToListAsync(cancellationToken))
            .OrderBy(c => ContractCalculations.ListNoticeDeadline(c) is null ? 1 : 0)
            .ThenBy(c => ContractCalculations.ListNoticeDeadline(c) ?? DateOnly.MaxValue)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var today = _clock.Today;
        var live = contracts.Where(DataQuality.IsLive).ToList();
        var page = new CounterpartyPageDto
        {
            Counterparty = ToCounterpartyDto(supplier, contracts.Count),
            Summary = new CounterpartySummaryDto
            {
                Contracts = contracts.Count,
                Live = live.Count,
                AnnualValueDkk = live.Sum(c => reference.AnnualDkk(c)),
                TotalValueDkk = live.Sum(c => reference.TotalDkk(c)),
                AutoRenewing = live.Count(c => c.AutoRenew),
                NextNoticeDeadline = contracts
                    .Where(c => c.Status == ContractStatus.Active && c.RenewalDecision is null)
                    .Select(c => ContractCalculations.NoticeDeadline(c))
                    .Where(d => d is not null && d.Value >= today)
                    .OrderBy(d => d)
                    .FirstOrDefault()
            },
            Contracts = await _queries.ToListItemsAsync(contracts, reference, cancellationToken)
        };
        foreach (var contract in contracts.Where(c => c.Status == ContractStatus.Active))
        {
            var deadline = ContractCalculations.NoticeDeadline(contract);
            if (deadline is null)
            {
                continue;
            }
            if (deadline.Value < today && (contract.EndDate is null || contract.EndDate.Value < today || contract.RenewalDecision is not null))
            {
                continue;
            }
            page.Renewals.Add(new RenewalDto
            {
                Id = contract.Id,
                Title = contract.Title,
                EntityId = contract.EntityId,
                EntityName = reference.Entity(contract.EntityId),
                DepartmentName = reference.Department(contract.DepartmentId),
                NoticeDeadline = deadline.Value,
                DaysToNotice = deadline.Value.DayNumber - today.DayNumber,
                AutoRenew = contract.AutoRenew,
                AnnualValue = contract.AnnualValue,
                Currency = contract.Currency
            });
        }
        page.Renewals = page.Renewals.OrderBy(r => r.NoticeDeadline).ToList();

        var contractIds = visible.Select(c => c.Id);
        var documents = await _db.Documents.AsNoTracking().Where(d => contractIds.Contains(d.ContractId)).ToListAsync(cancellationToken);
        var byId = contracts.ToDictionary(c => c.Id);
        var latest = documents
            .GroupBy(d => (d.ContractId, d.Name.ToLowerInvariant()))
            .Select(g => g.OrderByDescending(d => d.Version).First())
            .OrderByDescending(d => d.UploadedAt)
            .Take(200);
        foreach (var document in latest)
        {
            var row = new DocumentRow
            {
                Id = document.Id,
                ContractId = document.ContractId,
                Name = document.Name,
                Kind = document.Kind,
                Version = document.Version,
                Size = document.Size,
                UploadedAt = document.UploadedAt,
                UploadedById = document.UploadedById
            };
            page.Documents.Add(await ToHitAsync(row, byId, reference, new List<string>(), false, cancellationToken));
        }
        return page;
    }
}
