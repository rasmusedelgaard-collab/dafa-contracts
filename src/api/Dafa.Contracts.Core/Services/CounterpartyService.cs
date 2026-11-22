using System.Text;
using System.Text.RegularExpressions;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>Counterparty master data (admin): list, create, update, deactivate, duplicates, merge (CONTRACT_V3 section 10).</summary>
public class CounterpartyService
{
    private static readonly Regex CountryPattern = new("^[A-Za-z]{2}$", RegexOptions.CultureInvariant);
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant);

    private static readonly string[] LegalSuffixes =
    {
        "a/s", "aps", "ab", "gmbh", "ltd", "llc", "inc", "s.r.l.", "sp. z o.o.", "b.v.", "n.v.", "oü", "s.a.", "s.p.a."
    };

    private readonly IAppDbContext _db;
    private readonly IClock _clock;
    private readonly AuditService _audit;

    public CounterpartyService(IAppDbContext db, IClock clock, AuditService audit)
    {
        _db = db;
        _clock = clock;
        _audit = audit;
    }

    /// <summary>Lowercase, legal suffixes removed, letters and digits only.</summary>
    public static string NormalizeName(string? name)
    {
        var text = " " + (name ?? "").ToLowerInvariant().Trim() + " ";
        foreach (var suffix in LegalSuffixes.OrderByDescending(s => s.Length))
        {
            text = text.Replace(" " + suffix + " ", " ").Replace(" " + suffix.TrimEnd('.') + " ", " ");
            if (text.EndsWith(" " + suffix + " ", StringComparison.Ordinal))
            {
                text = text.Substring(0, text.Length - suffix.Length - 1);
            }
        }
        var builder = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
        }
        return builder.ToString();
    }

    private async Task<Dictionary<Guid, int>> CountsAsync(CancellationToken cancellationToken)
    {
        return (await _db.Contracts.AsNoTracking().Where(c => c.SupplierId != null).Select(c => c.SupplierId!.Value).ToListAsync(cancellationToken))
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
    }

    public async Task<PagedDto<CounterpartyDto>> ListAsync(string? q, bool? active, string? kind, int? page, int? pageSize, CancellationToken cancellationToken = default)
    {
        var term = (q ?? "").Trim();
        CounterpartyKind? kindValue = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!Enum.TryParse<CounterpartyKind>(kind.Trim(), false, out var parsed))
            {
                throw new InputValidationException("kind", "Unknown kind.");
            }
            kindValue = parsed;
        }
        var counts = await CountsAsync(cancellationToken);
        var all = (await _db.Suppliers.AsNoTracking().ToListAsync(cancellationToken))
            .Where(s => active is null || s.IsActive == active.Value)
            .Where(s => kindValue is null || s.Kind == kindValue.Value)
            .Where(s => term.Length == 0
                || s.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (s.VatNo ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)
                || (s.ExternalId ?? "").Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var size = Math.Clamp(pageSize ?? 25, 1, 200);
        var number = Math.Max(1, page ?? 1);
        return new PagedDto<CounterpartyDto>
        {
            Items = all.Skip((number - 1) * size).Take(size).Select(s => SearchService.ToCounterpartyDto(s, counts.TryGetValue(s.Id, out var n) ? n : 0)).ToList(),
            Total = all.Count,
            Page = number,
            PageSize = size
        };
    }

    private async Task ApplyAsync(Supplier supplier, CounterpartyInput input, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var name = (input?.Name ?? "").Trim();
        if (name.Length < 1 || name.Length > 300) ContractValidator.Add(errors, "name", "Name is required (at most 300 characters).");
        var country = string.IsNullOrWhiteSpace(input?.Country) ? null : input!.Country!.Trim().ToUpperInvariant();
        if (country is not null && !CountryPattern.IsMatch(country)) ContractValidator.Add(errors, "country", "Use a two-letter country code.");
        var email = string.IsNullOrWhiteSpace(input?.Email) ? null : input!.Email!.Trim();
        if (email is not null && !EmailPattern.IsMatch(email)) ContractValidator.Add(errors, "email", "Enter a valid e-mail address.");
        var kind = CounterpartyKind.Supplier;
        if (!string.IsNullOrWhiteSpace(input?.Kind) && !Enum.TryParse(input!.Kind!.Trim(), false, out kind))
            ContractValidator.Add(errors, "kind", "Use Supplier, Customer, Partner, Authority or Other.");
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }
        var vat = string.IsNullOrWhiteSpace(input!.VatNo) ? null : input.VatNo.Trim();
        var externalId = string.IsNullOrWhiteSpace(input.ExternalId) ? null : input.ExternalId.Trim();
        var isActive = input.IsActive ?? true;
        var id = supplier.Id;
        if (isActive && vat is not null && await _db.Suppliers.AnyAsync(s => s.Id != id && s.IsActive && s.VatNo == vat, cancellationToken))
            throw new ConflictException("Another active counterparty has this VAT number.", ProblemTypes.Duplicate);
        if (isActive && externalId is not null && await _db.Suppliers.AnyAsync(s => s.Id != id && s.IsActive && s.ExternalId == externalId, cancellationToken))
            throw new ConflictException("Another active counterparty has this external id.", ProblemTypes.Duplicate);

        var renamed = supplier.Name.Length > 0 && supplier.Name != name;
        var oldName = supplier.Name;
        supplier.Name = name;
        supplier.Country = country;
        supplier.VatNo = vat;
        supplier.ExternalId = externalId;
        supplier.Kind = kind;
        supplier.City = string.IsNullOrWhiteSpace(input.City) ? null : input.City.Trim();
        supplier.Email = email;
        supplier.Phone = string.IsNullOrWhiteSpace(input.Phone) ? null : input.Phone.Trim();
        supplier.IsActive = isActive;
        supplier.UpdatedAt = _clock.UtcNow;
        if (renamed)
        {
            // Renaming updates the counterparty name on all its contracts.
            var contracts = await _db.Contracts.Where(c => c.SupplierId == id).ToListAsync(cancellationToken);
            foreach (var contract in contracts)
            {
                contract.CounterpartyName = name;
                contract.UpdatedAt = _clock.UtcNow;
                _audit.Record("counterparty.updated", "Counterparty renamed from " + oldName + " to " + name, contract.Id);
            }
        }
    }

    public async Task<CounterpartyDto> CreateAsync(CounterpartyInput input, CancellationToken cancellationToken = default)
    {
        var supplier = new Supplier { Id = Guid.NewGuid(), Source = "Manual" };
        await ApplyAsync(supplier, input, cancellationToken);
        _db.Suppliers.Add(supplier);
        _audit.Record("counterparty.created", "Created counterparty " + supplier.Name);
        await _db.SaveChangesAsync(cancellationToken);
        return SearchService.ToCounterpartyDto(supplier, 0);
    }

    public async Task<CounterpartyDto> UpdateAsync(Guid id, CounterpartyInput input, CancellationToken cancellationToken = default)
    {
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == id, cancellationToken) ?? throw new NotFoundException("Counterparty not found.");
        var wasActive = supplier.IsActive;
        await ApplyAsync(supplier, input, cancellationToken);
        _audit.Record(wasActive && !supplier.IsActive ? "counterparty.deactivated" : "counterparty.updated", "Updated counterparty " + supplier.Name);
        await _db.SaveChangesAsync(cancellationToken);
        return SearchService.ToCounterpartyDto(supplier, await _db.Contracts.CountAsync(c => c.SupplierId == id, cancellationToken));
    }

    public async Task<List<DuplicateGroupDto>> DuplicatesAsync(CancellationToken cancellationToken = default)
    {
        var counts = await CountsAsync(cancellationToken);
        var active = await _db.Suppliers.AsNoTracking().Where(s => s.IsActive).ToListAsync(cancellationToken);
        var groups = new List<DuplicateGroupDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void AddGroups(string reason, Func<Supplier, string?> key)
        {
            foreach (var group in active.Where(s => !string.IsNullOrWhiteSpace(key(s))).GroupBy(s => key(s)!.Trim().ToLowerInvariant()).Where(g => g.Count() > 1))
            {
                var ids = string.Join(",", group.Select(s => s.Id).OrderBy(i => i));
                if (!seen.Add(ids))
                {
                    continue;
                }
                groups.Add(new DuplicateGroupDto
                {
                    Reason = reason,
                    Items = group.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(s => SearchService.ToCounterpartyDto(s, counts.TryGetValue(s.Id, out var n) ? n : 0)).ToList()
                });
            }
        }
        AddGroups("VatNo", s => s.VatNo);
        AddGroups("ExternalId", s => s.ExternalId);
        AddGroups("Name", s => { var normalized = NormalizeName(s.Name); return normalized.Length == 0 ? null : normalized; });
        return groups;
    }

    public async Task<MergeResultDto> MergeAsync(MergeInput input, CancellationToken cancellationToken = default)
    {
        if (input?.TargetId is null) throw new InputValidationException("targetId", "Choose the counterparty to keep.");
        var sourceIds = (input.SourceIds ?? new List<Guid>()).Distinct().ToList();
        if (sourceIds.Count == 0) throw new InputValidationException("sourceIds", "Choose the duplicates to merge.");
        var targetId = input.TargetId.Value;
        if (sourceIds.Contains(targetId)) throw new InputValidationException("sourceIds", "The target cannot be one of the sources.");
        var target = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == targetId, cancellationToken)
            ?? throw new InputValidationException("targetId", "Unknown counterparty.");
        if (!target.IsActive) throw new InputValidationException("targetId", "The target must be active.");
        var sources = await _db.Suppliers.Where(s => sourceIds.Contains(s.Id)).ToListAsync(cancellationToken);
        if (sources.Count != sourceIds.Count) throw new InputValidationException("sourceIds", "Unknown counterparty.");

        var contracts = await _db.Contracts.Where(c => c.SupplierId != null && sourceIds.Contains(c.SupplierId.Value)).ToListAsync(cancellationToken);
        var now = _clock.UtcNow;
        foreach (var contract in contracts)
        {
            var before = contract.CounterpartyName;
            contract.SupplierId = target.Id;
            contract.CounterpartyName = target.Name;
            contract.UpdatedAt = now;
            _audit.Record("counterparty.merged", "Counterparty " + before + " merged into " + target.Name, contract.Id);
        }
        foreach (var source in sources)
        {
            source.IsActive = false;
            source.MergedIntoId = target.Id;
            source.UpdatedAt = now;
        }
        target.UpdatedAt = now;
        _audit.Record("counterparty.merged", "Merged " + string.Join(", ", sources.Select(s => s.Name)) + " into " + target.Name + " (" + contracts.Count + " contracts moved)");
        await _db.SaveChangesAsync(cancellationToken);
        var count = await _db.Contracts.CountAsync(c => c.SupplierId == targetId, cancellationToken);
        return new MergeResultDto
        {
            Target = SearchService.ToCounterpartyDto(target, count),
            MovedContracts = contracts.Count,
            Deactivated = sources.Count
        };
    }
}
