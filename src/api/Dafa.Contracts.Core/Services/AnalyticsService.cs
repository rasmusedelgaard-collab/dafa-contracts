using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>
/// GET overview/analytics (CONTRACT_V3 section 5): KPIs, breakdowns, notice and expiry timelines,
/// data quality and due-diligence lists over the visible contracts that match the filter. The
/// filter (access, ids, entity, department, type, status, counterparty, person, tags, custom
/// fields, flags) runs in SQL through ContractFilterEngine; the figures are then computed in one
/// pass over the matching rows (decimal sums cannot run in SQL on Sqlite).
/// </summary>
public class AnalyticsService
{
    private static readonly (ContractStatus Status, string Label)[] Funnel =
    {
        (ContractStatus.Draft, "Draft"), (ContractStatus.InApproval, "In approval"), (ContractStatus.Approved, "Approved"),
        (ContractStatus.Active, "Active"), (ContractStatus.NoticeGiven, "Notice given"), (ContractStatus.Expired, "Expired"),
        (ContractStatus.Terminated, "Terminated")
    };

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IContractAccessService _access;
    private readonly IClock _clock;

    public AnalyticsService(IAppDbContext db, ICurrentUser currentUser, IContractAccessService access, IClock clock)
    {
        _db = db;
        _currentUser = currentUser;
        _access = access;
        _clock = clock;
    }

    private static decimal Share(decimal part, decimal total) =>
        total <= 0m ? 0m : decimal.Round(part / total * 100m, 1, MidpointRounding.AwayFromZero);

    private static List<BucketDto> Buckets(IEnumerable<IGrouping<string, Contract>> groups, Func<string, string> label, ReferenceData reference, decimal total)
    {
        return groups
            .Select(g => new BucketDto
            {
                Key = g.Key,
                Label = label(g.Key),
                Count = g.Count(),
                AnnualValueDkk = g.Sum(c => reference.AnnualDkk(c)),
                TotalValueDkk = g.Sum(c => reference.TotalDkk(c))
            })
            .Select(b =>
            {
                b.SharePct = Share(b.AnnualValueDkk, total);
                return b;
            })
            .OrderByDescending(b => b.AnnualValueDkk)
            .ThenByDescending(b => b.Count)
            .ThenBy(b => b.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<string> ScopeLabelAsync(AccessProfile profile, ReferenceData reference, CancellationToken cancellationToken)
    {
        if (profile.IsAdmin || profile.IsGroupManagement)
        {
            return "All contracts";
        }
        var parts = new List<string>();
        parts.AddRange(profile.DepartmentIds.Select(reference.Department).Where(n => n.Length > 0).OrderBy(n => n));
        parts.AddRange(profile.ManagedEntityIds.Select(reference.Entity).Where(n => n.Length > 0).OrderBy(n => n));
        await Task.CompletedTask;
        return parts.Count == 0 ? "Your contracts" : string.Join(", ", parts);
    }

    public async Task<OverviewAnalyticsDto> GetAsync(ContractFilter filter, decimal? ddThresholdDkk, CancellationToken cancellationToken = default)
    {
        var engine = new ContractFilterEngine(_db, _access, _currentUser, _clock);
        var run = await engine.RunAsync(filter, false, cancellationToken);
        var reference = run.Reference;
        var contracts = run.Items;
        var today = _clock.Today;
        var visibleCount = await (await _access.FilterAsync(_db.Contracts.AsNoTracking(), cancellationToken)).CountAsync(cancellationToken);

        var annualTotal = contracts.Sum(c => reference.AnnualDkk(c));
        var result = new OverviewAnalyticsDto
        {
            AsOf = _clock.UtcNow,
            Scope = new AnalyticsScopeDto
            {
                Label = await ScopeLabelAsync(run.Profile, reference, cancellationToken),
                VisibleContracts = visibleCount
            },
            Kpis = new AnalyticsKpisDto
            {
                Count = contracts.Count,
                AnnualValueDkk = annualTotal,
                TotalValueDkk = contracts.Sum(c => reference.TotalDkk(c)),
                Notice30 = contracts.Count(c => ContractFilterEngine.IsDueWithin(c, today, 30)),
                Notice90 = contracts.Count(c => ContractFilterEngine.IsDueWithin(c, today, 90)),
                InApproval = contracts.Count(c => c.Status == ContractStatus.InApproval),
                MissingData = contracts.Count(c => reference.Missing(c).Count > 0),
                AutoRenewCount = contracts.Count(c => c.AutoRenew)
            }
        };
        result.Kpis.AutoRenewSharePct = contracts.Count == 0 ? 0m : decimal.Round((decimal)result.Kpis.AutoRenewCount / contracts.Count * 100m, 1, MidpointRounding.AwayFromZero);

        result.ByEntity = Buckets(contracts.GroupBy(c => c.EntityId.ToString()), key => reference.Entity(Guid.Parse(key)), reference, annualTotal);
        result.ByDepartment = Buckets(contracts.GroupBy(c => c.DepartmentId.ToString()), key => reference.Department(Guid.Parse(key)), reference, annualTotal);
        result.ByType = Buckets(contracts.GroupBy(c => c.TypeId.ToString()), key => reference.Type(Guid.Parse(key)), reference, annualTotal);

        // Counterparties: by master record; contracts without one are grouped by name under key ''.
        var counterparties = contracts
            .GroupBy(c => c.SupplierId is null ? "name:" + c.CounterpartyName.Trim().ToLowerInvariant() : c.SupplierId.Value.ToString())
            .Select(g =>
            {
                var first = g.First();
                return new BucketDto
                {
                    Key = first.SupplierId is null ? "" : first.SupplierId.Value.ToString(),
                    Label = first.CounterpartyName,
                    Count = g.Count(),
                    AnnualValueDkk = g.Sum(c => reference.AnnualDkk(c)),
                    TotalValueDkk = g.Sum(c => reference.TotalDkk(c)),
                    SharePct = Share(g.Sum(c => reference.AnnualDkk(c)), annualTotal)
                };
            })
            .OrderByDescending(b => b.AnnualValueDkk)
            .ThenByDescending(b => b.Count)
            .ThenBy(b => b.Label, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
        result.TopCounterparties = counterparties;
        result.Top10SharePct = Share(counterparties.Sum(b => b.AnnualValueDkk), annualTotal);

        // Notice deadlines: 24 months from the current one.
        var firstMonth = new DateOnly(today.Year, today.Month, 1);
        for (var i = 0; i < 24; i++)
        {
            var month = firstMonth.AddMonths(i);
            result.NoticeByMonth.Add(new NoticeMonthDto { Month = month.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture) });
        }
        foreach (var contract in contracts.Where(c => c.Status == ContractStatus.Active))
        {
            var deadline = ContractCalculations.NoticeDeadline(contract);
            if (deadline is null)
            {
                continue;
            }
            NoticeMonthDto? row;
            var days = deadline.Value.DayNumber - today.DayNumber;
            if (days < 0)
            {
                if (contract.RenewalDecision is null)
                {
                    // Overdue and undecided while the contract still runs: the current month, as high.
                    if (contract.EndDate is null || contract.EndDate.Value < today)
                    {
                        continue;
                    }
                    row = result.NoticeByMonth[0];
                    row.High++;
                    row.AnnualValueDkk += reference.AnnualDkk(contract);
                    continue;
                }
                if (deadline.Value < firstMonth)
                {
                    continue;
                }
            }
            var index = (deadline.Value.Year - firstMonth.Year) * 12 + deadline.Value.Month - firstMonth.Month;
            if (index < 0 || index >= 24)
            {
                continue;
            }
            row = result.NoticeByMonth[index];
            if (contract.RenewalDecision is not null) row.Decided++;
            else if (days <= 30) row.High++;
            else if (days <= 90) row.Medium++;
            else row.Low++;
            row.AnnualValueDkk += reference.AnnualDkk(contract);
        }

        // Expiries: 8 quarters from the current one.
        var quarterStart = new DateOnly(today.Year, ((today.Month - 1) / 3) * 3 + 1, 1);
        for (var i = 0; i < 8; i++)
        {
            var from = quarterStart.AddMonths(3 * i);
            var to = from.AddMonths(3).AddDays(-1);
            var inQuarter = contracts.Where(c => c.EndDate is not null && c.EndDate.Value >= from && c.EndDate.Value <= to).ToList();
            result.ExpiringByQuarter.Add(new ExpiryQuarterDto
            {
                Quarter = from.Year + "-Q" + ((from.Month - 1) / 3 + 1),
                From = from,
                To = to,
                Count = inQuarter.Count,
                AutoRenew = inQuarter.Count(c => c.AutoRenew),
                AnnualValueDkk = inQuarter.Sum(c => reference.AnnualDkk(c))
            });
        }

        foreach (var (status, label) in Funnel)
        {
            var inStatus = contracts.Where(c => c.Status == status).ToList();
            var annual = inStatus.Sum(c => reference.AnnualDkk(c));
            result.StatusFunnel.Add(new BucketDto
            {
                Key = status.ToString(),
                Label = label,
                Count = inStatus.Count,
                AnnualValueDkk = annual,
                TotalValueDkk = inStatus.Sum(c => reference.TotalDkk(c)),
                SharePct = Share(annual, annualTotal)
            });
        }

        result.DataQuality = contracts
            .GroupBy(c => c.EntityId)
            .Select(g =>
            {
                var row = new DataQualityRowDto { EntityId = g.Key, Name = reference.Entity(g.Key), Contracts = g.Count() };
                foreach (var contract in g)
                {
                    var missing = reference.MissingEntries(contract);
                    if (missing.Count == 0)
                    {
                        row.Complete++;
                    }
                    foreach (var key in missing.Select(m => m.Key).Distinct())
                    {
                        row.Missing[key] = row.Missing.TryGetValue(key, out var n) ? n + 1 : 1;
                    }
                }
                row.Score = row.Contracts == 0 ? 100 : (int)Math.Round(row.Complete * 100.0 / row.Contracts, MidpointRounding.AwayFromZero);
                return row;
            })
            .OrderBy(r => r.Score)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var threshold = ddThresholdDkk ?? reference.Settings.DdThresholdDkk;
        DueDiligenceItemDto Item(Contract c, string? note) => new()
        {
            Id = c.Id,
            ContractNo = c.ContractNo,
            Title = c.Title,
            Counterparty = c.CounterpartyName,
            EntityName = reference.Entity(c.EntityId),
            Status = c.Status.ToString(),
            AnnualValueDkk = reference.AnnualDkk(c),
            TotalValueDkk = reference.TotalDkk(c),
            Note = note
        };
        List<DueDiligenceItemDto> Sorted(IEnumerable<DueDiligenceItemDto> items) => items
            .OrderByDescending(i => i.TotalValueDkk)
            .ThenByDescending(i => i.AnnualValueDkk)
            .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.DueDiligence = new DueDiligenceDto
        {
            ThresholdDkk = threshold,
            ChangeOfControl = Sorted(contracts.Where(c => c.ChangeOfControl == true).Select(c => Item(c, null))),
            Exclusivity = Sorted(contracts.Where(ContractFilterEngine.HasExclusivity).Select(c => Item(c, c.Exclusivity))),
            AboveThreshold = Sorted(contracts.Where(c => reference.ContractValueDkk(c) >= threshold).Select(c => Item(c, null))),
            MissingSignedDocument = Sorted(contracts.Where(c => DataQuality.IsLive(c) && !reference.HasSignedDocument(c)).Select(c => Item(c, "No main document")))
        };
        return result;
    }
}
