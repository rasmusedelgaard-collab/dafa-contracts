using System.Text.Json;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>
/// Sync sources (CONTRACT_V3 section 12): admin-configured Power BI or SQL queries that feed the
/// import pipeline on a schedule. Secrets never enter the database or the API: a SQL source names a
/// server-side connection reference, Power BI uses the server's app registration or managed identity.
/// </summary>
public class SyncService
{
    public const int MaxRows = ImportService.MaxRows;

    private readonly IAppDbContext _db;
    private readonly IClock _clock;
    private readonly AuditService _audit;
    private readonly ISyncSourceReader _reader;
    private readonly ImportService _imports;
    private readonly INotifier _notifier;

    public SyncService(IAppDbContext db, IClock clock, AuditService audit, ISyncSourceReader reader, ImportService imports, INotifier notifier)
    {
        _db = db;
        _clock = clock;
        _audit = audit;
        _reader = reader;
        _imports = imports;
        _notifier = notifier;
    }

    public List<SyncConnectionDto> GetConnections() =>
        _reader.GetConnections().Select(c => new SyncConnectionDto { Name = c.Name, Kind = c.Kind, Description = c.Description }).ToList();

    public static SyncRunDto ToRunDto(ImportRun run) => new()
    {
        Id = run.Id,
        SourceId = run.SyncSourceId ?? Guid.Empty,
        Trigger = run.Trigger,
        StartedAt = run.StartedAt,
        FinishedAt = run.CompletedAt,
        Status = run.Status,
        Read = run.Read,
        Created = run.Created,
        Updated = run.Updated,
        Skipped = run.Skipped,
        Failed = run.Failed,
        Errors = JsonDefaults.ReadList<ImportErrorDto>(run.ErrorsJson).Select(e => (e.RowNo > 0 ? "Row " + e.RowNo + ": " : "") + e.Message)
            .Concat(string.IsNullOrEmpty(run.Message) ? Enumerable.Empty<string>() : new[] { run.Message! })
            .Take(200).ToList()
    };

    private static Dictionary<string, string?> ReadMapping(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(string.IsNullOrWhiteSpace(json) ? "{}" : json) ?? new Dictionary<string, string?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string?>();
        }
    }

    private static ImportOptionsDto ReadOptions(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ImportOptionsDto>(string.IsNullOrWhiteSpace(json) ? "{}" : json, JsonDefaults.Options) ?? new ImportOptionsDto();
        }
        catch (JsonException)
        {
            return new ImportOptionsDto();
        }
    }

    private async Task<SyncSourceDto> ToDtoAsync(SyncSource source, CancellationToken cancellationToken)
    {
        ImportRun? last = null;
        if (source.LastRunId is not null)
        {
            var runId = source.LastRunId.Value;
            last = await _db.ImportRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
        }
        return new SyncSourceDto
        {
            Id = source.Id,
            Name = source.Name,
            Target = ImportService.KindName(source.Target),
            Kind = source.Kind.ToString(),
            PowerBi = source.Kind == SyncSourceKind.PowerBi
                ? new PowerBiSourceDto { WorkspaceId = source.WorkspaceId, DatasetId = source.DatasetId, DaxQuery = source.DaxQuery, Table = source.TableName }
                : null,
            Sql = source.Kind == SyncSourceKind.Sql ? new SqlSourceDto { ConnectionName = source.ConnectionName, Query = source.SqlQuery } : null,
            Mapping = ReadMapping(source.MappingJson),
            Options = ReadOptions(source.OptionsJson),
            Schedule = new ScheduleDto { Frequency = source.Frequency.ToString(), Time = source.Time, DayOfWeek = source.DayOfWeek },
            IsActive = source.IsActive,
            LastRun = last is null ? null : ToRunDto(last),
            NextRunAt = source.NextRunAt
        };
    }

    public async Task<List<SyncSourceDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var sources = await _db.SyncSources.AsNoTracking().ToListAsync(cancellationToken);
        var result = new List<SyncSourceDto>();
        foreach (var source in sources.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(await ToDtoAsync(source, cancellationToken));
        }
        return result;
    }

    /// <summary>Validates an input into a (detached) source. 400 on any problem.</summary>
    private async Task<SyncSource> ValidateAsync(SyncSourceInput input, SyncSource? existing, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        input ??= new SyncSourceInput();
        var source = existing ?? new SyncSource { Id = Guid.NewGuid() };
        var name = (input.Name ?? "").Trim();
        if (name.Length < 1 || name.Length > 200) ContractValidator.Add(errors, "name", "Name is required (at most 200 characters).");
        ImportKind target = ImportKind.Counterparties;
        try
        {
            target = ImportService.ParseKind(input.Target);
        }
        catch (NotFoundException)
        {
            ContractValidator.Add(errors, "target", "Use counterparties, users or contracts.");
        }
        SyncSourceKind kind = SyncSourceKind.Sql;
        if (input.Kind != "PowerBi" && input.Kind != "Sql") ContractValidator.Add(errors, "kind", "Use PowerBi or Sql.");
        else kind = input.Kind == "PowerBi" ? SyncSourceKind.PowerBi : SyncSourceKind.Sql;
        var connections = _reader.GetConnections();
        if (kind == SyncSourceKind.PowerBi)
        {
            var powerBi = input.PowerBi;
            if (powerBi is null || string.IsNullOrWhiteSpace(powerBi.WorkspaceId) || string.IsNullOrWhiteSpace(powerBi.DatasetId))
                ContractValidator.Add(errors, "powerBi", "Enter the workspace id and the dataset id.");
            var hasDax = !string.IsNullOrWhiteSpace(powerBi?.DaxQuery);
            var hasTable = !string.IsNullOrWhiteSpace(powerBi?.Table);
            if (hasDax == hasTable) ContractValidator.Add(errors, "powerBi", "Enter either a DAX query or a table name.");
            source.WorkspaceId = powerBi?.WorkspaceId?.Trim();
            source.DatasetId = powerBi?.DatasetId?.Trim();
            source.DaxQuery = hasDax ? powerBi!.DaxQuery!.Trim() : null;
            source.TableName = hasTable ? powerBi!.Table!.Trim() : null;
            source.ConnectionName = null;
            source.SqlQuery = null;
        }
        else if (input.Kind == "Sql")
        {
            var sql = input.Sql;
            var connectionName = sql?.ConnectionName?.Trim() ?? "";
            if (!connections.Any(c => c.Name == connectionName && c.Kind == "Sql"))
                ContractValidator.Add(errors, "sql.connectionName", "Choose a SQL connection configured on the server.");
            var query = sql?.Query?.Trim() ?? "";
            if (!IsSelect(query))
                ContractValidator.Add(errors, "sql.query", "The query must start with SELECT or WITH.");
            source.ConnectionName = connectionName;
            source.SqlQuery = query;
            source.WorkspaceId = null;
            source.DatasetId = null;
            source.DaxQuery = null;
            source.TableName = null;
        }
        var options = input.Options ?? new ImportOptionsDto { Mode = "CreateAndUpdate" };
        if (options.Mode != "CreateOnly" && options.Mode != "CreateAndUpdate" && options.Mode != "SkipDuplicates")
            ContractValidator.Add(errors, "options.mode", "Use CreateOnly, CreateAndUpdate or SkipDuplicates.");
        var mapping = input.Mapping ?? new Dictionary<string, string?>();
        if (!errors.ContainsKey("target"))
        {
            var fields = await _imports.FieldsAsync(target, cancellationToken);
            foreach (var key in mapping.Keys)
            {
                if (fields.All(f => f.Key != key)) ContractValidator.Add(errors, "mapping." + key, "Unknown field.");
            }
        }
        var (frequency, time, day) = SettingsService.ValidateSchedule(input.Schedule ?? new ScheduleDto(), "schedule", errors);
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }
        source.Name = name;
        source.Target = target;
        source.Kind = kind;
        source.MappingJson = JsonSerializer.Serialize(mapping);
        source.OptionsJson = JsonDefaults.Write(options);
        source.Frequency = frequency;
        source.Time = time;
        source.DayOfWeek = day;
        source.IsActive = input.IsActive ?? true;
        source.NextRunAt = source.IsActive ? ScheduleCalculator.NextRun(frequency, time, day, _clock.UtcNow) : null;
        source.UpdatedAt = _clock.UtcNow;
        return source;
    }

    public static bool IsSelect(string query)
    {
        var text = query.TrimStart();
        return text.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || text.StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<SyncSourceDto> CreateAsync(SyncSourceInput input, CancellationToken cancellationToken = default)
    {
        var source = await ValidateAsync(input, null, cancellationToken);
        _db.SyncSources.Add(source);
        _audit.Record("settings.sync-sources", "Created sync source " + source.Name + " (" + source.Kind + ", " + ImportService.KindName(source.Target) + ")");
        await _db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(source, cancellationToken);
    }

    public async Task<SyncSourceDto> UpdateAsync(Guid id, SyncSourceInput input, CancellationToken cancellationToken = default)
    {
        var source = await _db.SyncSources.FirstOrDefaultAsync(s => s.Id == id, cancellationToken) ?? throw new NotFoundException("Sync source not found.");
        await ValidateAsync(input, source, cancellationToken);
        _audit.Record("settings.sync-sources", "Updated sync source " + source.Name);
        await _db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(source, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var source = await _db.SyncSources.FirstOrDefaultAsync(s => s.Id == id, cancellationToken) ?? throw new NotFoundException("Sync source not found.");
        _db.SyncSources.Remove(source);
        _audit.Record("settings.sync-sources", "Deleted sync source " + source.Name + " (run history kept)");
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static SyncQuery QueryOf(SyncSource source) => new()
    {
        Kind = source.Kind.ToString(),
        Target = ImportService.KindName(source.Target),
        WorkspaceId = source.WorkspaceId,
        DatasetId = source.DatasetId,
        DaxQuery = source.DaxQuery,
        Table = source.TableName,
        ConnectionName = source.ConnectionName,
        SqlQuery = source.SqlQuery
    };

    /// <summary>POST admin/sync-sources/test: connects and runs the query; writes nothing. Failures are ok=false.</summary>
    public async Task<SyncTestResultDto> TestAsync(SyncSourceInput input, CancellationToken cancellationToken = default)
    {
        var source = await ValidateAsync(input, null, cancellationToken);
        try
        {
            var data = await _reader.ReadAsync(QueryOf(source), MaxRows + 1, cancellationToken);
            return new SyncTestResultDto
            {
                Ok = true,
                Message = "Connected. The query returned " + data.Rows.Count + (data.Rows.Count > MaxRows ? "+" : "") + " rows.",
                Columns = data.Columns,
                Sample = data.Rows.Take(5).Select(r => r.ToList()).ToList(),
                RowCount = data.Rows.Count
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SyncTestResultDto { Ok = false, Message = ex.Message, RowCount = null };
        }
    }

    public async Task<SyncRunDto> RunNowAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var source = await _db.SyncSources.FirstOrDefaultAsync(s => s.Id == id, cancellationToken) ?? throw new NotFoundException("Sync source not found.");
        var run = await RunAsync(source, "Manual", cancellationToken);
        return ToRunDto(run);
    }

    public async Task<List<SyncRunDto>> RunsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!await _db.SyncSources.AnyAsync(s => s.Id == id, cancellationToken) && !await _db.ImportRuns.AnyAsync(r => r.SyncSourceId == id, cancellationToken))
        {
            throw new NotFoundException("Sync source not found.");
        }
        var runs = await _db.ImportRuns.AsNoTracking().Where(r => r.SyncSourceId == id).OrderByDescending(r => r.StartedAt).Take(50).ToListAsync(cancellationToken);
        return runs.Select(ToRunDto).ToList();
    }

    /// <summary>Reads the source and feeds the rows through the import pipeline. A failure is recorded as a Failed run.</summary>
    public async Task<ImportRun> RunAsync(SyncSource source, string trigger, CancellationToken cancellationToken)
    {
        var origin = new ImportOrigin { Source = "Sync", FileName = source.Name, SyncSourceId = source.Id, Trigger = trigger };
        ImportRun run;
        try
        {
            var data = await _reader.ReadAsync(QueryOf(source), MaxRows + 1, cancellationToken);
            if (data.Rows.Count > MaxRows)
            {
                throw new InvalidOperationException("The query returned more than " + MaxRows + " rows.");
            }
            var input = new ImportRunInput { Mapping = ReadMapping(source.MappingJson), Options = ReadOptions(source.OptionsJson) };
            run = await _imports.RunRowsAsync(source.Target, data, input, origin, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _db.DiscardChanges();
            var now = _clock.UtcNow;
            run = new ImportRun
            {
                Id = Guid.NewGuid(),
                Kind = source.Target,
                Source = "Sync",
                SyncSourceId = source.Id,
                Trigger = trigger,
                FileName = source.Name,
                Status = "Failed",
                OptionsJson = source.OptionsJson,
                MappingJson = source.MappingJson,
                Message = ex is InputValidationException validation
                    ? string.Join(" ", validation.Errors.SelectMany(e => e.Value.Select(m => e.Key + ": " + m)))
                    : ex.Message,
                StartedByName = "System",
                StartedAt = now,
                CompletedAt = now
            };
            if (run.Message is not null && run.Message.Length > 1000) run.Message = run.Message.Substring(0, 1000);
            _db.ImportRuns.Add(run);
            _audit.Record("sync.run", "Sync source " + source.Name + " failed: " + run.Message);
            await _db.SaveChangesAsync(cancellationToken);
        }
        // Reload: the import pipeline may have discarded tracked rows.
        var sourceId = source.Id;
        var stored = await _db.SyncSources.FirstOrDefaultAsync(s => s.Id == sourceId, cancellationToken);
        if (stored is not null)
        {
            stored.LastRunId = run.Id;
            stored.NextRunAt = stored.IsActive ? ScheduleCalculator.NextRun(stored.Frequency, stored.Time, stored.DayOfWeek, _clock.UtcNow) : null;
            await _db.SaveChangesAsync(cancellationToken);
        }
        if (run.Status == "Failed")
        {
            await NotifyFailedAsync(source, run, cancellationToken);
        }
        return run;
    }

    private async Task NotifyFailedAsync(SyncSource source, ImportRun run, CancellationToken cancellationToken)
    {
        var admins = (await _db.Users.AsNoTracking().Where(u => u.IsActive).ToListAsync(cancellationToken))
            .Where(u => (u.Roles & AppRole.Admin) == AppRole.Admin)
            .Select(u => u.Id)
            .ToList();
        var message = run.Message ?? "See the run history.";
        foreach (var admin in admins)
        {
            await _notifier.SendAsync(new Notification(
                admin, NotificationCatalog.SyncFailed,
                "Sync failed: " + source.Name,
                "The sync source " + source.Name + " failed: " + message,
                null,
                new Dictionary<string, string> { ["source.name"] = source.Name, ["message"] = message, ["import.file"] = source.Name, ["import.created"] = "0", ["import.failed"] = run.Failed.ToString() }),
                cancellationToken);
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>CounterpartySync job: runs the active sources whose next run is due.</summary>
    public async Task<int> RunDueAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var due = await _db.SyncSources.AsNoTracking()
            .Where(s => s.IsActive && s.Frequency != ScheduleFrequency.Off && s.NextRunAt != null && s.NextRunAt <= now)
            .ToListAsync(cancellationToken);
        foreach (var source in due)
        {
            await RunAsync(source, "Schedule", cancellationToken);
        }
        return due.Count;
    }
}
