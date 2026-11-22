using System.Globalization;
using System.Text;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>Where an import run comes from (file upload or a sync source).</summary>
public sealed class ImportOrigin
{
    public string Source { get; init; } = "File";
    public string FileName { get; init; } = "";
    public Guid? SyncSourceId { get; init; }
    public string Trigger { get; init; } = "Manual";
}

/// <summary>
/// The generic import pipeline (CONTRACT_V3 section 11), used by file imports and by sync sources:
/// fields -> upload (columns, sample, suggested mapping) -> preview (every row with its action,
/// errors and warnings; nothing written) -> run (rows with errors are skipped, the others are
/// written one by one) -> history. Targets: counterparties, users, contracts. Imported contracts
/// skip approval. Every created/updated record and every run is audited.
/// </summary>
public class ImportService
{
    public const long MaxFileBytes = 10L * 1024 * 1024;
    public const int MaxRows = 5000;
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly AuditService _audit;
    private readonly ITabularFileReader _reader;
    private readonly INotifier _notifier;

    public ImportService(IAppDbContext db, ICurrentUser currentUser, IClock clock, AuditService audit, ITabularFileReader reader, INotifier notifier)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
        _audit = audit;
        _reader = reader;
        _notifier = notifier;
    }

    // ---------- kinds and fields ----------

    public static ImportKind ParseKind(string? kind) => (kind ?? "").Trim() switch
    {
        "counterparties" => ImportKind.Counterparties,
        "users" => ImportKind.Users,
        "contracts" => ImportKind.Contracts,
        _ => throw new NotFoundException("Unknown import kind. Use counterparties, users or contracts.")
    };

    public static string KindName(ImportKind kind) => kind switch
    {
        ImportKind.Counterparties => "counterparties",
        ImportKind.Users => "users",
        _ => "contracts"
    };

    private static ImportFieldDefDto Field(string key, string label, bool required, string type, bool isKey, string? example, string? help = null, List<string>? options = null) => new()
    {
        Key = key, Label = label, Required = required, Type = type, IsKey = isKey, Example = example, Help = help, Options = options
    };

    public async Task<List<ImportFieldDefDto>> FieldsAsync(ImportKind kind, CancellationToken cancellationToken = default)
    {
        switch (kind)
        {
            case ImportKind.Counterparties:
                return new List<ImportFieldDefDto>
                {
                    Field("name", "Name", true, "text", true, "Nordic Polymer Compounds AB"),
                    Field("externalId", "External id", false, "text", true, "S-10010", "Vendor number in the ERP; matched first."),
                    Field("vatNo", "VAT no.", false, "text", true, "SE556100101001", "Matched when there is no external id."),
                    Field("country", "Country", false, "text", false, "SE", "Two-letter country code."),
                    Field("city", "City", false, "text", false, "Malmö"),
                    Field("kind", "Kind", false, "choice", false, "Supplier", "Default Supplier.", Enum.GetNames(typeof(CounterpartyKind)).ToList()),
                    Field("email", "E-mail", false, "email", false, "orders@example.com"),
                    Field("phone", "Phone", false, "text", false, "+46 40 123 45 67"),
                    Field("isActive", "Active", false, "bool", false, "yes", "Default yes.")
                };
            case ImportKind.Users:
                return new List<ImportFieldDefDto>
                {
                    Field("email", "E-mail", true, "email", true, "jane.doe@dafa.dk"),
                    Field("name", "Name", true, "text", false, "Jane Doe"),
                    Field("title", "Title", false, "text", false, "Purchasing manager"),
                    Field("roles", "Roles", false, "role", false, "Reader;ContractOwner", "Separated by ';'. Default Reader.", new List<string> { "Reader", "ContractOwner", "Approver", "Admin" }),
                    Field("departments", "Departments", false, "department", false, "Purchasing", "Names separated by ';'."),
                    Field("managedEntities", "Managed entities", false, "entity", false, "DAFA A/S", "Names separated by ';'."),
                    Field("groupManagement", "Group management", false, "bool", false, "no"),
                    Field("isActive", "Active", false, "bool", false, "yes", "Default yes.")
                };
        }
        var fields = new List<ImportFieldDefDto>
        {
            Field("contractNo", "Contract no.", false, "text", true, "C-2024-0001", "Empty = a new number is generated."),
            Field("title", "Title", true, "text", false, "Supply agreement, EPDM"),
            Field("counterparty", "Counterparty", true, "counterparty", false, "Nordic Polymer Compounds AB", "External id, VAT no. or name of a counterparty."),
            Field("type", "Contract type", true, "type", false, "Supplier – direct materials"),
            Field("entity", "Legal entity", true, "entity", false, "DAFA A/S"),
            Field("department", "Department", true, "department", false, "Purchasing"),
            Field("owner", "Owner", true, "person", false, "jonas@dafa.dk", "E-mail or exact name; needs the contract owner role."),
            Field("delegate", "Delegate", false, "person", false, null),
            Field("responsible", "Responsible", false, "person", false, null),
            Field("reminderRecipient", "Reminder recipient", false, "choice", false, "Owner", null, new List<string> { "Owner", "Delegate", "Responsible" }),
            Field("startDate", "Start date", true, "date", false, "2024-01-01"),
            Field("endDate", "End date", false, "date", false, "2026-12-31", "Empty = open-ended."),
            Field("noticePeriodDays", "Notice period (days)", false, "number", false, "90"),
            Field("autoRenew", "Auto-renew", false, "bool", false, "yes"),
            Field("renewalMonths", "Renewal period (months)", false, "number", false, "12"),
            Field("annualValue", "Annual value", false, "money", false, "1200000"),
            Field("totalValue", "Total value", false, "money", false, null),
            Field("currency", "Currency", false, "text", false, "DKK", "Default: the type's default, then the entity's currency."),
            Field("paymentTerms", "Payment terms", false, "text", false, "30 days net"),
            Field("governingLaw", "Governing law", false, "text", false, "Danish law"),
            Field("liabilityCap", "Liability cap", false, "text", false, null),
            Field("exclusivity", "Exclusivity", false, "text", false, null),
            Field("changeOfControl", "Change of control", false, "bool", false, "no", "Empty = missing data."),
            Field("signedDate", "Signed date", false, "date", false, "2023-12-15"),
            Field("summary", "Summary", false, "text", false, null),
            Field("tags", "Tags", false, "multi", false, null, "Tag names separated by ';'.")
        };
        var definitions = await _db.CustomFieldDefinitions.AsNoTracking().Where(d => d.IsActive).ToListAsync(cancellationToken);
        foreach (var definition in definitions.OrderBy(d => d.SortOrder))
        {
            var type = definition.FieldType switch
            {
                CustomFieldType.Number => "number",
                CustomFieldType.Money => "money",
                CustomFieldType.Date => "date",
                CustomFieldType.YesNo => "bool",
                CustomFieldType.Choice => "choice",
                CustomFieldType.MultiChoice => "multi",
                CustomFieldType.Person => "person",
                _ => "text"
            };
            fields.Add(Field("cf." + definition.Key, definition.Label, false, type, false, null, definition.HelpText,
                type == "choice" || type == "multi" ? CustomFieldService.ReadOptions(definition) : null));
        }
        return fields;
    }

    public async Task<byte[]> TemplateAsync(ImportKind kind, CancellationToken cancellationToken = default)
    {
        var fields = await FieldsAsync(kind, cancellationToken);
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", fields.Select(f => ContractQueryService.CsvCell(f.Label))));
        csv.AppendLine(string.Join(",", fields.Select(f => ContractQueryService.CsvCell(f.Example ?? ""))));
        return ContractQueryService.WithBom(csv.ToString());
    }

    private static string Simplify(string text) =>
        new string((text ?? "").Where(ch => !char.IsWhiteSpace(ch) && ch != '-' && ch != '_').ToArray()).ToLowerInvariant();

    /// <summary>Headers matched to field keys or labels, ignoring case, spaces, '-' and '_'.</summary>
    public static Dictionary<string, string?> SuggestMapping(IEnumerable<ImportFieldDefDto> fields, IReadOnlyList<string> columns)
    {
        var result = new Dictionary<string, string?>();
        foreach (var field in fields)
        {
            var match = columns.FirstOrDefault(c => Simplify(c) == Simplify(field.Key) || Simplify(c) == Simplify(field.Label)
                || (field.Key.StartsWith("cf.", StringComparison.Ordinal) && Simplify(c) == Simplify(field.Key.Substring(3))));
            result[field.Key] = match;
        }
        return result;
    }

    // ---------- sessions ----------

    public async Task<ImportSessionDto> UploadAsync(ImportKind kind, string fileName, byte[] content, CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        if (content.LongLength > MaxFileBytes)
        {
            throw new InputValidationException("file", "The file is larger than 10 MB.");
        }
        var table = _reader.Read(fileName, content);
        var rows = NonEmptyRows(table);
        if (rows.Count > MaxRows)
        {
            throw new InputValidationException("file", "The file has more than " + MaxRows + " data rows.");
        }
        var now = _clock.UtcNow;
        var session = new ImportFile
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            FileName = DocumentWriter.SafeFileName(fileName),
            Content = content,
            CreatedById = profile.UserId,
            CreatedAt = now,
            ExpiresAt = now.Add(SessionLifetime)
        };
        _db.ImportFiles.Add(session);
        await _db.SaveChangesAsync(cancellationToken);
        var fields = await FieldsAsync(kind, cancellationToken);
        return new ImportSessionDto
        {
            Id = session.Id,
            Kind = KindName(kind),
            FileName = session.FileName,
            Columns = table.Columns,
            Sample = rows.Take(5).Select(r => r.Cells.ToList()).ToList(),
            RowCount = rows.Count,
            SuggestedMapping = SuggestMapping(fields, table.Columns),
            ExpiresAt = session.ExpiresAt
        };
    }

    private sealed class SourceRow
    {
        public int RowNo { get; init; }
        public string[] Cells { get; init; } = Array.Empty<string>();
    }

    /// <summary>Data rows numbered from 1 (the header is row 0); empty rows are left out.</summary>
    private static List<SourceRow> NonEmptyRows(TabularData table)
    {
        var result = new List<SourceRow>();
        for (var i = 0; i < table.Rows.Count; i++)
        {
            if (table.Rows[i].Any(c => !string.IsNullOrWhiteSpace(c)))
            {
                result.Add(new SourceRow { RowNo = i + 1, Cells = table.Rows[i] });
            }
        }
        return result;
    }

    private async Task<(ImportFile Session, TabularData Table)> OpenSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await _db.ImportFiles.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null || session.ExpiresAt <= _clock.UtcNow)
        {
            throw new ConflictException("The import session is unknown or has expired. Upload the file again.", ProblemTypesV3.ImportState);
        }
        if (session.RanAt is not null)
        {
            throw new ConflictException("This import session has already been run. Upload the file again to import it once more.", ProblemTypesV3.ImportState);
        }
        return (session, _reader.Read(session.FileName, session.Content));
    }

    public async Task<ImportPreviewDto> PreviewAsync(Guid sessionId, ImportRunInput input, CancellationToken cancellationToken = default)
    {
        await _currentUser.GetProfileAsync(cancellationToken);
        var (session, table) = await OpenSessionAsync(sessionId, cancellationToken);
        var plan = await PlanAsync(session.Kind, table, input, session.FileName, cancellationToken);
        var preview = new ImportPreviewDto
        {
            SessionId = session.Id,
            RowCount = plan.Rows.Count,
            Create = plan.Rows.Count(r => r.Preview.Action == "Create"),
            Update = plan.Rows.Count(r => r.Preview.Action == "Update"),
            Skip = plan.Rows.Count(r => r.Preview.Action == "Skip"),
            Error = plan.Rows.Count(r => r.Preview.Action == "Error"),
            Rows = plan.Rows.Select(r => r.Preview).ToList(),
            MappingErrors = plan.MappingErrors
        };
        return preview;
    }

    public async Task<ImportResultDto> RunAsync(Guid sessionId, ImportRunInput input, CancellationToken cancellationToken = default)
    {
        await _currentUser.GetProfileAsync(cancellationToken);
        var (session, table) = await OpenSessionAsync(sessionId, cancellationToken);
        // Mapping and options errors are 400 before anything is written.
        await PlanAsync(session.Kind, table, input, session.FileName, cancellationToken);
        // Mark the session first, so a second run cannot import the same rows again.
        session.RanAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        var origin = new ImportOrigin { Source = "File", FileName = session.FileName };
        var run = await RunRowsAsync(session.Kind, table, input, origin, cancellationToken);
        var stored = await _db.ImportFiles.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (stored is not null)
        {
            stored.RunId = run.Id;
            await _db.SaveChangesAsync(cancellationToken);
        }
        return ToResult(run);
    }

    // ---------- history ----------

    public static ImportResultDto ToResult(ImportRun run) => new()
    {
        Id = run.Id,
        Kind = KindName(run.Kind),
        FileName = run.FileName,
        RanAt = run.CompletedAt ?? run.StartedAt,
        RanBy = run.StartedByName,
        Options = ReadOptions(run.OptionsJson),
        Created = run.Created,
        Updated = run.Updated,
        Skipped = run.Skipped,
        Failed = run.Failed,
        Errors = JsonDefaults.ReadList<ImportErrorDto>(run.ErrorsJson),
        Source = run.Source
    };

    private static ImportOptionsDto ReadOptions(string? json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<ImportOptionsDto>(string.IsNullOrWhiteSpace(json) ? "{}" : json, JsonDefaults.Options) ?? new ImportOptionsDto();
        }
        catch (System.Text.Json.JsonException)
        {
            return new ImportOptionsDto();
        }
    }

    public async Task<List<ImportResultDto>> HistoryAsync(string? kind, CancellationToken cancellationToken = default)
    {
        var query = _db.ImportRuns.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(kind))
        {
            var parsed = ParseKind(kind);
            query = query.Where(r => r.Kind == parsed);
        }
        var runs = await query.OrderByDescending(r => r.StartedAt).Take(100).ToListAsync(cancellationToken);
        return runs.Select(ToResult).ToList();
    }

    // ---------- planning ----------

    private sealed class PlannedRow
    {
        public ImportRowPreviewDto Preview { get; init; } = new();
        /// <summary>Writes the row (unit of work; the caller saves). Null for Skip and Error.</summary>
        public Func<CancellationToken, Task>? Apply { get; set; }
        public bool IsUpdate { get; set; }
    }

    private sealed class Plan
    {
        public List<PlannedRow> Rows { get; } = new();
        public List<string> MappingErrors { get; } = new();
    }

    /// <summary>Cell access by field key through the mapping.</summary>
    private sealed class RowReader
    {
        private readonly Dictionary<string, int> _columns;
        private readonly string[] _cells;

        public RowReader(Dictionary<string, int> columns, string[] cells)
        {
            _columns = columns;
            _cells = cells;
        }

        public bool IsMapped(string key) => _columns.ContainsKey(key);

        /// <summary>The trimmed cell, or null when not mapped or empty.</summary>
        public string? Get(string key)
        {
            if (!_columns.TryGetValue(key, out var index) || index >= _cells.Length)
            {
                return null;
            }
            var value = _cells[index]?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }

    private static ImportMode ParseMode(string? mode) => mode switch
    {
        "CreateOnly" => ImportMode.CreateOnly,
        "CreateAndUpdate" => ImportMode.CreateAndUpdate,
        "SkipDuplicates" => ImportMode.SkipDuplicates,
        _ => throw new InputValidationException("options.mode", "Use CreateOnly, CreateAndUpdate or SkipDuplicates.")
    };

    private async Task<Plan> PlanAsync(ImportKind kind, TabularData table, ImportRunInput input, string fileName, CancellationToken cancellationToken)
    {
        var options = input?.Options ?? new ImportOptionsDto();
        var mode = ParseMode(options.Mode);
        if (options.ContractStatus is not null && options.ContractStatus != "Draft" && options.ContractStatus != "Active")
            throw new InputValidationException("options.contractStatus", "Use Draft or Active.");
        var formats = new[] { "auto", "yyyy-MM-dd", "dd-MM-yyyy", "dd.MM.yyyy", "dd/MM/yyyy", "MM/dd/yyyy" };
        if (options.DateFormat is not null && !formats.Contains(options.DateFormat))
            throw new InputValidationException("options.dateFormat", "Unknown date format.");
        if (options.DecimalSeparator is not null && options.DecimalSeparator != "." && options.DecimalSeparator != ",")
            throw new InputValidationException("options.decimalSeparator", "Use '.' or ','.");

        var fields = await FieldsAsync(kind, cancellationToken);
        var mapping = input?.Mapping ?? new Dictionary<string, string?>();
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        var errors = new Dictionary<string, string[]>();
        foreach (var pair in mapping)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }
            var field = fields.FirstOrDefault(f => f.Key == pair.Key);
            if (field is null)
            {
                ContractValidator.Add(errors, "mapping." + pair.Key, "Unknown field.");
                continue;
            }
            var index = table.Columns.FindIndex(c => c == pair.Value);
            if (index < 0)
            {
                ContractValidator.Add(errors, "mapping." + pair.Key, "The column '" + pair.Value + "' is not in the file.");
                continue;
            }
            columns[pair.Key] = index;
        }
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }
        var plan = new Plan();
        foreach (var field in fields.Where(f => f.Required && !columns.ContainsKey(f.Key)))
        {
            plan.MappingErrors.Add("Map a column to the required field " + field.Label + ".");
        }
        var parser = new ValueParser(options);
        var rows = NonEmptyRows(table);
        if (rows.Count > MaxRows)
        {
            throw new InputValidationException("rows", "At most " + MaxRows + " rows can be imported at once.");
        }
        switch (kind)
        {
            case ImportKind.Counterparties:
                await PlanCounterpartiesAsync(plan, rows, columns, mode, parser, cancellationToken);
                break;
            case ImportKind.Users:
                await PlanUsersAsync(plan, rows, columns, mode, parser, cancellationToken);
                break;
            default:
                await PlanContractsAsync(plan, rows, columns, mode, parser, options, fileName, cancellationToken);
                break;
        }
        if (plan.MappingErrors.Count > 0)
        {
            foreach (var row in plan.Rows)
            {
                row.Preview.Action = "Error";
                row.Apply = null;
                row.Preview.Errors.Add(new ImportRowMessageDto { Field = null, Message = "Required fields are not mapped." });
            }
        }
        return plan;
    }

    private static PlannedRow NewRow(SourceRow row) => new() { Preview = new ImportRowPreviewDto { RowNo = row.RowNo } };

    private static void Error(PlannedRow row, string? field, string message)
    {
        row.Preview.Errors.Add(new ImportRowMessageDto { Field = field, Message = message });
    }

    /// <summary>Sets the action: errors win; then duplicates by mode; else Create.</summary>
    private static bool Decide(PlannedRow row, ImportMode mode, string? existingDisplay, Dictionary<string, int> seenKeys, string? key)
    {
        if (key is not null)
        {
            if (seenKeys.TryGetValue(key, out var firstRow))
            {
                Error(row, null, "Duplicate of row " + firstRow);
            }
            else
            {
                seenKeys[key] = row.Preview.RowNo;
            }
        }
        if (row.Preview.Errors.Count > 0)
        {
            row.Preview.Action = "Error";
            return false;
        }
        if (existingDisplay is null)
        {
            row.Preview.Action = "Create";
            return true;
        }
        row.Preview.Match = existingDisplay;
        switch (mode)
        {
            case ImportMode.CreateOnly:
                Error(row, null, "Already exists: " + existingDisplay);
                row.Preview.Action = "Error";
                return false;
            case ImportMode.SkipDuplicates:
                row.Preview.Action = "Skip";
                return false;
            default:
                row.Preview.Action = "Update";
                row.IsUpdate = true;
                return true;
        }
    }

    // ---------- counterparties ----------

    private async Task PlanCounterpartiesAsync(Plan plan, List<SourceRow> rows, Dictionary<string, int> columns, ImportMode mode, ValueParser parser, CancellationToken cancellationToken)
    {
        var suppliers = await _db.Suppliers.AsNoTracking().ToListAsync(cancellationToken);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in rows)
        {
            var row = NewRow(source);
            var reader = new RowReader(columns, source.Cells);
            var name = reader.Get("name");
            var externalId = reader.Get("externalId");
            var vatNo = reader.Get("vatNo");
            if (name is null) Error(row, "name", "Name is required.");
            else if (name.Length > 300) Error(row, "name", "At most 300 characters.");
            var country = reader.Get("country")?.ToUpperInvariant();
            if (country is not null && (country.Length != 2 || !country.All(char.IsLetter))) Error(row, "country", "Use a two-letter country code.");
            CounterpartyKind? kind = null;
            var kindText = reader.Get("kind");
            if (kindText is not null)
            {
                var match = Enum.GetNames(typeof(CounterpartyKind)).FirstOrDefault(n => string.Equals(n, kindText, StringComparison.OrdinalIgnoreCase));
                if (match is null) Error(row, "kind", "Use Supplier, Customer, Partner, Authority or Other.");
                else kind = Enum.Parse<CounterpartyKind>(match);
            }
            var email = reader.Get("email");
            if (email is not null && (!email.Contains('@') || email.Length > 256)) Error(row, "email", "Enter a valid e-mail address.");
            var isActive = parser.Bool(reader.Get("isActive"), row.Preview, "isActive");

            // Key order: external id, VAT no., name.
            Supplier? existing = null;
            string? key = null;
            if (externalId is not null)
            {
                key = "ext:" + externalId;
                existing = suppliers.FirstOrDefault(s => string.Equals(s.ExternalId, externalId, StringComparison.OrdinalIgnoreCase));
            }
            else if (vatNo is not null)
            {
                key = "vat:" + vatNo;
                existing = suppliers.FirstOrDefault(s => string.Equals(s.VatNo, vatNo, StringComparison.OrdinalIgnoreCase));
            }
            else if (name is not null)
            {
                key = "name:" + name.ToLowerInvariant();
                existing = suppliers.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            }
            foreach (var field in new[] { "name", "externalId", "vatNo", "country", "city", "kind", "email", "phone", "isActive" })
            {
                var value = reader.Get(field);
                if (value is not null) row.Preview.Values[field] = value;
            }
            var proceed = Decide(row, mode, existing is null ? null : existing.Name + (existing.ExternalId is null ? "" : " (" + existing.ExternalId + ")"), seen, key);
            if (!proceed)
            {
                plan.Rows.Add(row);
                continue;
            }
            var target = existing;
            var city = reader.Get("city");
            var phone = reader.Get("phone");
            row.Apply = async cancellation =>
            {
                var now = _clock.UtcNow;
                if (target is null)
                {
                    var created = new Supplier
                    {
                        Id = Guid.NewGuid(),
                        Name = name!,
                        ExternalId = externalId,
                        VatNo = vatNo,
                        Country = country,
                        City = city,
                        Kind = kind ?? CounterpartyKind.Supplier,
                        Email = email,
                        Phone = phone,
                        IsActive = isActive ?? true,
                        Source = "Import",
                        UpdatedAt = now
                    };
                    _db.Suppliers.Add(created);
                    _audit.Record("import.counterparty-created", "Counterparty " + created.Name + " imported");
                }
                else
                {
                    // Only mapped, non-empty cells change. The row is read again so each row is written on its own.
                    var targetId = target.Id;
                    target = await _db.Suppliers.FirstOrDefaultAsync(x => x.Id == targetId, cancellation)
                        ?? throw new InvalidOperationException("The counterparty was deleted meanwhile.");
                    if (name is not null) target.Name = name;
                    if (externalId is not null) target.ExternalId = externalId;
                    if (vatNo is not null) target.VatNo = vatNo;
                    if (country is not null) target.Country = country;
                    if (city is not null) target.City = city;
                    if (kind is not null) target.Kind = kind.Value;
                    if (email is not null) target.Email = email;
                    if (phone is not null) target.Phone = phone;
                    if (isActive is not null) target.IsActive = isActive.Value;
                    target.UpdatedAt = now;
                    _audit.Record("import.counterparty-updated", "Counterparty " + target.Name + " updated by import");
                }
            };
            plan.Rows.Add(row);
        }
    }

    // ---------- users ----------

    private async Task PlanUsersAsync(Plan plan, List<SourceRow> rows, Dictionary<string, int> columns, ImportMode mode, ValueParser parser, CancellationToken cancellationToken)
    {
        var users = await _db.Users.AsNoTracking().ToListAsync(cancellationToken);
        var departments = await _db.Departments.AsNoTracking().ToListAsync(cancellationToken);
        var entities = await _db.LegalEntities.AsNoTracking().ToListAsync(cancellationToken);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in rows)
        {
            var row = NewRow(source);
            var reader = new RowReader(columns, source.Cells);
            var email = reader.Get("email");
            var name = reader.Get("name");
            if (email is null || !email.Contains('@') || email.Length > 256) Error(row, "email", "Enter a valid e-mail address.");
            var existing = email is null ? null : users.FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
            if (name is null && existing is null) Error(row, "name", "Name is required.");
            var title = reader.Get("title");
            AppRole? roles = null;
            var rolesText = reader.Get("roles");
            if (rolesText is not null)
            {
                var value = AppRole.None;
                foreach (var part in ValueParser.List(rolesText))
                {
                    var match = new[] { "Reader", "ContractOwner", "Approver", "Admin" }.FirstOrDefault(r => string.Equals(r, part, StringComparison.OrdinalIgnoreCase));
                    if (match is null) Error(row, "roles", "Unknown role '" + part + "'.");
                    else value |= Enum.Parse<AppRole>(match);
                }
                roles = value == AppRole.None ? AppRole.Reader : value;
            }
            List<Guid>? departmentIds = null;
            var departmentsText = reader.Get("departments");
            if (departmentsText is not null)
            {
                departmentIds = new List<Guid>();
                foreach (var part in ValueParser.List(departmentsText))
                {
                    var department = departments.FirstOrDefault(d => string.Equals(d.Name, part, StringComparison.OrdinalIgnoreCase));
                    if (department is null) Error(row, "departments", "Unknown department '" + part + "'.");
                    else departmentIds.Add(department.Id);
                }
            }
            List<Guid>? entityIds = null;
            var entitiesText = reader.Get("managedEntities");
            if (entitiesText is not null)
            {
                entityIds = new List<Guid>();
                foreach (var part in ValueParser.List(entitiesText))
                {
                    var entity = entities.FirstOrDefault(e => string.Equals(e.Name, part, StringComparison.OrdinalIgnoreCase));
                    if (entity is null) Error(row, "managedEntities", "Unknown legal entity '" + part + "'.");
                    else entityIds.Add(entity.Id);
                }
            }
            var groupManagement = parser.Bool(reader.Get("groupManagement"), row.Preview, "groupManagement");
            var isActive = parser.Bool(reader.Get("isActive"), row.Preview, "isActive");
            foreach (var field in new[] { "email", "name", "title", "roles", "departments", "managedEntities", "groupManagement", "isActive" })
            {
                var value = reader.Get(field);
                if (value is not null) row.Preview.Values[field] = value;
            }
            var proceed = Decide(row, mode, existing is null ? null : existing.Name + " <" + existing.Email + ">", seen, email);
            var synced = existing is not null && !string.IsNullOrEmpty(existing.ExternalId);
            if (proceed && synced && (name is not null || title is not null || isActive is not null))
            {
                row.Preview.Warnings.Add("Name, title and active come from Entra ID and are not updated.");
            }
            if (!proceed)
            {
                plan.Rows.Add(row);
                continue;
            }
            var target = existing;
            row.Apply = async cancellation =>
            {
                AppUser user;
                if (target is null)
                {
                    user = new AppUser
                    {
                        Id = Guid.NewGuid(),
                        Name = name!,
                        Email = email!,
                        Initials = AdminDirectoryService.Initials(name!),
                        JobTitle = title,
                        Roles = roles ?? AppRole.Reader,
                        IsGroupManagement = groupManagement ?? false,
                        IsActive = isActive ?? true,
                        Source = "Import"
                    };
                    _db.Users.Add(user);
                    _audit.Record("import.user-created", "User " + user.Name + " imported");
                }
                else
                {
                    var targetId = target.Id;
                    user = await _db.Users.FirstOrDefaultAsync(x => x.Id == targetId, cancellation)
                        ?? throw new InvalidOperationException("The user was deleted meanwhile.");
                    if (!synced)
                    {
                        if (name is not null) { user.Name = name; user.Initials = AdminDirectoryService.Initials(name); }
                        if (title is not null) user.JobTitle = title;
                        if (isActive is not null) user.IsActive = isActive.Value;
                    }
                    if (roles is not null) user.Roles = roles.Value;
                    if (groupManagement is not null) user.IsGroupManagement = groupManagement.Value;
                    _audit.Record("import.user-updated", "User " + user.Name + " updated by import");
                }
                var userId = user.Id;
                if (departmentIds is not null)
                {
                    var current = await _db.UserDepartments.Where(d => d.UserId == userId).ToListAsync(cancellation);
                    _db.UserDepartments.RemoveRange(current.Where(c => !departmentIds.Contains(c.DepartmentId)));
                    foreach (var id in departmentIds.Distinct().Where(id => current.All(c => c.DepartmentId != id)))
                    {
                        _db.UserDepartments.Add(new UserDepartment { UserId = userId, DepartmentId = id });
                    }
                }
                if (entityIds is not null)
                {
                    var current = await _db.UserEntityManagements.Where(m => m.UserId == userId).ToListAsync(cancellation);
                    _db.UserEntityManagements.RemoveRange(current.Where(c => !entityIds.Contains(c.EntityId)));
                    foreach (var id in entityIds.Distinct().Where(id => current.All(c => c.EntityId != id)))
                    {
                        _db.UserEntityManagements.Add(new UserEntityManagement { UserId = userId, EntityId = id });
                    }
                }
            };
            plan.Rows.Add(row);
        }
    }

    // ---------- contracts ----------

    private async Task PlanContractsAsync(
        Plan plan, List<SourceRow> rows, Dictionary<string, int> columns, ImportMode mode, ValueParser parser,
        ImportOptionsDto options, string fileName, CancellationToken cancellationToken)
    {
        var today = _clock.Today;
        var types = await _db.ContractTypes.AsNoTracking().ToListAsync(cancellationToken);
        var entities = await _db.LegalEntities.AsNoTracking().ToListAsync(cancellationToken);
        var departments = await _db.Departments.AsNoTracking().ToListAsync(cancellationToken);
        var users = await _db.Users.AsNoTracking().ToListAsync(cancellationToken);
        var suppliers = await _db.Suppliers.AsNoTracking().ToListAsync(cancellationToken);
        var tags = await _db.Tags.AsNoTracking().ToListAsync(cancellationToken);
        var rules = await _db.FieldRules.AsNoTracking().ToListAsync(cancellationToken);
        var definitions = await _db.CustomFieldDefinitions.AsNoTracking().Where(d => d.IsActive).ToListAsync(cancellationToken);
        var currencies = await CurrencyTable.ActiveCodesAsync(_db, cancellationToken);
        var numbers = await _db.Contracts.AsNoTracking().Select(c => c.ContractNo).ToListAsync(cancellationToken);
        var numberSet = new HashSet<string>(numbers, StringComparer.OrdinalIgnoreCase);
        var prefix = "C-" + today.Year.ToString("0000", CultureInfo.InvariantCulture) + "-";
        var nextNumber = numbers
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => int.TryParse(n.Substring(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0)
            .DefaultIfEmpty(0).Max();
        var status = options.ContractStatus == "Active" ? ContractStatus.Active : ContractStatus.Draft;
        var createCounterparties = options.CreateMissingCounterparties == true;
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var newCounterparties = new Dictionary<string, Supplier>(StringComparer.OrdinalIgnoreCase);
        var profile = await _currentUser.GetProfileAsync(cancellationToken);

        AppUser? Person(string? text) => text is null ? null
            : users.FirstOrDefault(u => string.Equals(u.Email, text, StringComparison.OrdinalIgnoreCase))
              ?? users.FirstOrDefault(u => string.Equals(u.Name, text, StringComparison.OrdinalIgnoreCase));

        foreach (var source in rows)
        {
            var row = NewRow(source);
            var reader = new RowReader(columns, source.Cells);
            foreach (var pair in columns)
            {
                var value = reader.Get(pair.Key);
                if (value is not null) row.Preview.Values[pair.Key] = value;
            }

            var contractNo = reader.Get("contractNo");
            if (contractNo is not null && contractNo.Length > 32) Error(row, "contractNo", "At most 32 characters.");
            Contract? existing = null;
            if (contractNo is not null && numberSet.Contains(contractNo))
            {
                existing = await _db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.ContractNo == contractNo, cancellationToken);
            }
            var isUpdate = existing is not null;

            var title = reader.Get("title");
            if (title is null && !isUpdate) Error(row, "title", "Title is required.");
            else if (title is not null && title.Length > 200) Error(row, "title", "At most 200 characters.");

            var typeText = reader.Get("type");
            var type = typeText is null ? null : types.FirstOrDefault(t => string.Equals(t.Name, typeText, StringComparison.OrdinalIgnoreCase));
            if (typeText is not null && type is null) Error(row, "type", "Unknown contract type '" + typeText + "'.");
            if (typeText is null && !isUpdate) Error(row, "type", "Contract type is required.");

            var entityText = reader.Get("entity");
            var entity = entityText is null ? null : entities.FirstOrDefault(e => string.Equals(e.Name, entityText, StringComparison.OrdinalIgnoreCase));
            if (entityText is not null && entity is null) Error(row, "entity", "Unknown legal entity '" + entityText + "'.");
            if (entityText is null && !isUpdate) Error(row, "entity", "Legal entity is required.");

            var departmentText = reader.Get("department");
            var department = departmentText is null ? null : departments.FirstOrDefault(d => string.Equals(d.Name, departmentText, StringComparison.OrdinalIgnoreCase));
            if (departmentText is not null && department is null) Error(row, "department", "Unknown department '" + departmentText + "'.");
            if (departmentText is null && !isUpdate) Error(row, "department", "Department is required.");

            var ownerText = reader.Get("owner");
            var owner = Person(ownerText);
            if (ownerText is not null && owner is null) Error(row, "owner", "Unknown person '" + ownerText + "'.");
            else if (owner is not null && (owner.Roles & (AppRole.ContractOwner | AppRole.Admin)) == AppRole.None) Error(row, "owner", owner.Name + " does not have the contract owner role.");
            else if (owner is not null && !owner.IsActive) Error(row, "owner", owner.Name + " is inactive.");
            if (ownerText is null && !isUpdate) Error(row, "owner", "Owner is required.");
            var delegateText = reader.Get("delegate");
            var delegateUser = Person(delegateText);
            if (delegateText is not null && delegateUser is null) Error(row, "delegate", "Unknown person '" + delegateText + "'.");
            var responsibleText = reader.Get("responsible");
            var responsible = Person(responsibleText);
            if (responsibleText is not null && responsible is null) Error(row, "responsible", "Unknown person '" + responsibleText + "'.");

            // Counterparty by external id, VAT no. or name.
            var counterpartyText = reader.Get("counterparty");
            Supplier? supplier = null;
            var newCounterparty = false;
            if (counterpartyText is not null)
            {
                supplier = suppliers.FirstOrDefault(s => s.IsActive && string.Equals(s.ExternalId, counterpartyText, StringComparison.OrdinalIgnoreCase))
                    ?? suppliers.FirstOrDefault(s => s.IsActive && string.Equals(s.VatNo, counterpartyText, StringComparison.OrdinalIgnoreCase))
                    ?? suppliers.FirstOrDefault(s => s.IsActive && string.Equals(s.Name, counterpartyText, StringComparison.OrdinalIgnoreCase));
                if (supplier is null)
                {
                    if (createCounterparties)
                    {
                        newCounterparty = true;
                        row.Preview.Warnings.Add("New counterparty: " + counterpartyText);
                    }
                    else
                    {
                        Error(row, "counterparty", "Unknown counterparty '" + counterpartyText + "'.");
                    }
                }
            }
            else if (!isUpdate)
            {
                Error(row, "counterparty", "Counterparty is required.");
            }

            var startDate = parser.Date(reader.Get("startDate"), row.Preview, "startDate");
            if (reader.Get("startDate") is null && !isUpdate) Error(row, "startDate", "Start date is required.");
            var endDate = parser.Date(reader.Get("endDate"), row.Preview, "endDate");
            if (startDate is not null && endDate is not null && endDate.Value < startDate.Value) Error(row, "endDate", "End date cannot be before the start date.");
            var noticeDays = parser.Int(reader.Get("noticePeriodDays"), row.Preview, "noticePeriodDays");
            if (noticeDays is not null && (noticeDays < 0 || noticeDays > 3650)) Error(row, "noticePeriodDays", "Between 0 and 3650 days.");
            var autoRenew = parser.Bool(reader.Get("autoRenew"), row.Preview, "autoRenew");
            var renewalMonths = parser.Int(reader.Get("renewalMonths"), row.Preview, "renewalMonths");
            if (renewalMonths is not null && (renewalMonths < 1 || renewalMonths > 240)) Error(row, "renewalMonths", "Between 1 and 240 months.");
            var annualValue = parser.Decimal(reader.Get("annualValue"), row.Preview, "annualValue");
            var totalValue = parser.Decimal(reader.Get("totalValue"), row.Preview, "totalValue");
            if (annualValue < 0 || totalValue < 0) Error(row, "annualValue", "Values cannot be negative.");
            var currency = reader.Get("currency")?.ToUpperInvariant();
            if (currency is not null && !currencies.Contains(currency)) Error(row, "currency", "Unknown or inactive currency '" + currency + "'.");
            var changeOfControl = parser.Bool(reader.Get("changeOfControl"), row.Preview, "changeOfControl");
            var signedDate = parser.Date(reader.Get("signedDate"), row.Preview, "signedDate");
            var reminderText = reader.Get("reminderRecipient");
            ReminderRecipient? reminder = null;
            if (reminderText is not null)
            {
                if (Enum.TryParse<ReminderRecipient>(reminderText, true, out var parsedReminder) && Enum.IsDefined(typeof(ReminderRecipient), parsedReminder) && !int.TryParse(reminderText, out _))
                    reminder = parsedReminder;
                else
                    Error(row, "reminderRecipient", "Use Owner, Delegate or Responsible.");
            }
            var tagIds = new List<Guid>();
            var tagsText = reader.Get("tags");
            if (tagsText is not null)
            {
                foreach (var part in ValueParser.List(tagsText))
                {
                    var tag = tags.FirstOrDefault(t => string.Equals(t.Name, part, StringComparison.OrdinalIgnoreCase));
                    if (tag is null) Error(row, "tags", "Unknown tag '" + part + "'.");
                    else tagIds.Add(tag.Id);
                }
            }

            // Defaults from the type's field rules (never the notice period), then the entity's currency.
            var typeId = type?.Id ?? existing?.TypeId;
            var defaults = typeId is null ? new Dictionary<string, string>() : CustomFieldService.DefaultsFor(rules, typeId.Value);
            if (!isUpdate)
            {
                if (currency is null && defaults.TryGetValue("currency", out var defaultCurrency)) currency = defaultCurrency;
                if (currency is null && entity?.Currency is not null) currency = entity.Currency;
                currency ??= "DKK";
                if (reminder is null && defaults.TryGetValue("reminderRecipient", out var defaultRecipient)
                    && Enum.TryParse<ReminderRecipient>(defaultRecipient, false, out var recipientValue)) reminder = recipientValue;
                if (autoRenew is null && defaults.TryGetValue("autoRenew", out var defaultAutoRenew) && bool.TryParse(defaultAutoRenew, out var autoValue)) autoRenew = autoValue;
                if (renewalMonths is null && defaults.TryGetValue("renewalMonths", out var defaultMonths)
                    && int.TryParse(defaultMonths, NumberStyles.Integer, CultureInfo.InvariantCulture, out var monthsValue)) renewalMonths = monthsValue;
            }
            var recipient = reminder ?? existing?.ReminderRecipient ?? ReminderRecipient.Owner;
            if (recipient == ReminderRecipient.Delegate && (delegateUser?.Id ?? existing?.DelegateId) is null)
                Error(row, "reminderRecipient", "Reminders go to the delegate, but there is no delegate.");
            if (recipient == ReminderRecipient.Responsible && (responsible?.Id ?? existing?.ResponsibleId) is null)
                Error(row, "reminderRecipient", "Reminders go to the responsible, but there is no responsible.");

            // Custom fields (lenient text parsing; required ones are warnings).
            var customValues = new List<ParsedCustomValue>();
            foreach (var definition in definitions)
            {
                var key = "cf." + definition.Key;
                var text = reader.Get(key);
                if (text is null)
                {
                    continue;
                }
                if (typeId is not null && !ReferenceData.Applies(definition, typeId.Value))
                {
                    Error(row, key, definition.Label + " does not apply to this contract type.");
                    continue;
                }
                if (definition.FieldType == CustomFieldType.Person)
                {
                    var person = Person(text);
                    if (person is null)
                    {
                        Error(row, key, "Unknown person '" + text + "'.");
                        continue;
                    }
                    text = person.Id.ToString();
                }
                if (definition.FieldType == CustomFieldType.Date)
                {
                    var date = parser.Date(text, row.Preview, key);
                    if (date is null) continue;
                    text = date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
                if (definition.FieldType == CustomFieldType.Number || definition.FieldType == CustomFieldType.Money)
                {
                    var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var amount = parser.Decimal(parts.Length > 1 && parts[parts.Length - 1].Length == 3 && parts[parts.Length - 1].All(char.IsLetter)
                        ? string.Join("", parts.Take(parts.Length - 1)) : text, row.Preview, key);
                    if (amount is null) continue;
                    text = amount.Value.ToString(CultureInfo.InvariantCulture)
                        + (parts.Length > 1 && parts[parts.Length - 1].Length == 3 && parts[parts.Length - 1].All(char.IsLetter) ? " " + parts[parts.Length - 1].ToUpperInvariant() : "");
                }
                var error = CustomFieldService.ParseText(definition, text, currency ?? existing?.Currency, out var parsed);
                if (error is not null) Error(row, key, definition.Label + ": " + error);
                else customValues.Add(parsed);
            }

            // Warnings that become missing data.
            if (!isUpdate)
            {
                if (noticeDays is null) row.Preview.Warnings.Add("Notice period is missing.");
                if (annualValue is null && totalValue is null) row.Preview.Warnings.Add("Contract value is missing.");
                if (changeOfControl is null) row.Preview.Warnings.Add("Change of control is missing.");
                if (endDate is null) row.Preview.Warnings.Add("No end date: the contract is open-ended.");
                if (status == ContractStatus.Active && signedDate is null && startDate is not null) row.Preview.Warnings.Add("No signed date: the start date is used.");
                if (status == ContractStatus.Active && endDate is not null && endDate.Value < today) row.Preview.Warnings.Add("The end date has passed: imported as Expired.");
                if (typeId is not null)
                {
                    foreach (var definition in definitions.Where(d => d.Required && ReferenceData.Applies(d, typeId.Value)))
                    {
                        if (customValues.All(v => v.Definition.Id != definition.Id || v.IsEmpty))
                            row.Preview.Warnings.Add(definition.Label + " is missing.");
                    }
                }
            }

            var duplicateKey = contractNo is null ? null : "no:" + contractNo;
            var display = existing is null ? null : existing.ContractNo + " " + existing.Title;
            var proceed = Decide(row, mode, display, seen, duplicateKey);
            if (!proceed)
            {
                plan.Rows.Add(row);
                continue;
            }

            var target = existing;
            var counterpartyName = counterpartyText;
            row.Apply = async cancellation =>
            {
                var now = _clock.UtcNow;
                Supplier? link = supplier;
                if (newCounterparty && counterpartyName is not null)
                {
                    if (!newCounterparties.TryGetValue(counterpartyName, out var created))
                    {
                        created = new Supplier { Id = Guid.NewGuid(), Name = counterpartyName, Source = "Import", IsActive = true, UpdatedAt = now };
                        _db.Suppliers.Add(created);
                        suppliers.Add(created);
                        newCounterparties[counterpartyName] = created;
                        _audit.Record("import.counterparty-created", "Counterparty " + created.Name + " created by contract import");
                    }
                    link = created;
                }
                if (target is null)
                {
                    var number = contractNo;
                    if (number is null)
                    {
                        do
                        {
                            nextNumber++;
                            number = ContractCommandService.FormatContractNo(today.Year, nextNumber);
                        }
                        while (numberSet.Contains(number));
                    }
                    numberSet.Add(number);
                    var finalStatus = status;
                    if (status == ContractStatus.Active && endDate is not null && endDate.Value < today)
                    {
                        finalStatus = ContractStatus.Expired;
                    }
                    var contract = new Contract
                    {
                        Id = Guid.NewGuid(),
                        ContractNo = number,
                        Title = title!,
                        TypeId = type!.Id,
                        SupplierId = link?.Id,
                        CounterpartyName = link?.Name ?? counterpartyName ?? "",
                        EntityId = entity!.Id,
                        DepartmentId = department!.Id,
                        OwnerId = owner!.Id,
                        DelegateId = delegateUser?.Id,
                        ResponsibleId = responsible?.Id,
                        ReminderRecipient = recipient,
                        StartDate = startDate!.Value,
                        EndDate = endDate,
                        NoticePeriodDays = noticeDays,
                        AutoRenew = autoRenew ?? false,
                        RenewalMonths = renewalMonths,
                        AnnualValue = annualValue,
                        TotalValue = totalValue,
                        Currency = currency ?? "DKK",
                        PaymentTerms = reader.Get("paymentTerms"),
                        GoverningLaw = reader.Get("governingLaw"),
                        LiabilityCap = reader.Get("liabilityCap"),
                        Exclusivity = reader.Get("exclusivity"),
                        ChangeOfControl = changeOfControl,
                        Summary = reader.Get("summary"),
                        Status = finalStatus,
                        SignedDate = finalStatus == ContractStatus.Draft ? signedDate : (signedDate ?? startDate),
                        SigningMethod = finalStatus == ContractStatus.Draft ? null : "Imported",
                        CreatedById = _currentUser.UserId ?? owner.Id,
                        CreatedAt = now,
                        UpdatedAt = now
                    };
                    _db.Contracts.Add(contract);
                    foreach (var tagId in tagIds.Distinct())
                    {
                        _db.ContractTags.Add(new ContractTag { ContractId = contract.Id, TagId = tagId });
                    }
                    await new CustomFieldService(_db, _audit, _currentUser).SaveValuesAsync(contract.Id, customValues, true, cancellation);
                    _audit.Record("import.contract-created", "Contract imported from " + fileName, contract.Id,
                        ContractChanges.ToJson(new[] { new FieldChange("status", "Status", "(empty)", finalStatus == ContractStatus.InApproval ? "In approval" : finalStatus.ToString()) }));
                    return;
                }

                // Update: only mapped, non-empty cells change; status is not changed by an import.
                var targetId = target.Id;
                target = await _db.Contracts.FirstOrDefaultAsync(c => c.Id == targetId, cancellation)
                    ?? throw new InvalidOperationException("The contract was deleted meanwhile.");
                var reference = await ReferenceData.LoadAsync(_db, cancellation);
                var before = new Contract();
                CopyTerms(target, before);
                if (title is not null) target.Title = title;
                if (type is not null) target.TypeId = type.Id;
                if (link is not null) { target.SupplierId = link.Id; target.CounterpartyName = link.Name; }
                if (entity is not null) target.EntityId = entity.Id;
                if (department is not null) target.DepartmentId = department.Id;
                if (owner is not null) target.OwnerId = owner.Id;
                if (delegateUser is not null) target.DelegateId = delegateUser.Id;
                if (responsible is not null) target.ResponsibleId = responsible.Id;
                if (reminder is not null) target.ReminderRecipient = reminder.Value;
                if (startDate is not null) target.StartDate = startDate.Value;
                if (endDate is not null) target.EndDate = endDate;
                if (noticeDays is not null) target.NoticePeriodDays = noticeDays;
                if (autoRenew is not null) target.AutoRenew = autoRenew.Value;
                if (renewalMonths is not null) target.RenewalMonths = renewalMonths;
                if (annualValue is not null) target.AnnualValue = annualValue;
                if (totalValue is not null) target.TotalValue = totalValue;
                if (currency is not null) target.Currency = currency;
                var paymentTerms = reader.Get("paymentTerms");
                if (paymentTerms is not null) target.PaymentTerms = paymentTerms;
                var governingLaw = reader.Get("governingLaw");
                if (governingLaw is not null) target.GoverningLaw = governingLaw;
                var liabilityCap = reader.Get("liabilityCap");
                if (liabilityCap is not null) target.LiabilityCap = liabilityCap;
                var exclusivity = reader.Get("exclusivity");
                if (exclusivity is not null) target.Exclusivity = exclusivity;
                if (changeOfControl is not null) target.ChangeOfControl = changeOfControl;
                if (signedDate is not null) target.SignedDate = signedDate;
                var summary = reader.Get("summary");
                if (summary is not null) target.Summary = summary;
                target.UpdatedAt = now;
                var supplierNames = suppliers.ToDictionary(s => s.Id, s => s.Name);
                var changes = ContractChanges.Diff(before, target, reference, supplierNames);
                if (tagsText is not null)
                {
                    await ContractTagWriter.ReplaceAsync(_db, target.Id, tagIds, false, cancellation);
                }
                var customChanges = await new CustomFieldService(_db, _audit, _currentUser).SaveValuesAsync(target.Id, customValues, false, cancellation);
                if (ContractCalculations.NoticeDeadline(before) != ContractCalculations.NoticeDeadline(target))
                {
                    await NoticeCycle.RemoveAsync(_db, target.Id, cancellation);
                }
                _audit.Record("import.contract-updated",
                    "Contract updated from " + fileName + (changes.Count + customChanges.Count == 0 ? " (no changes)" : ". " + ContractChanges.Describe(changes.Concat(customChanges))),
                    target.Id, ContractChanges.ToJson(changes.Concat(customChanges)));
            };
            plan.Rows.Add(row);
        }
    }

    private static void CopyTerms(Contract from, Contract to)
    {
        to.Id = from.Id;
        to.Title = from.Title;
        to.TypeId = from.TypeId;
        to.SupplierId = from.SupplierId;
        to.CounterpartyName = from.CounterpartyName;
        to.EntityId = from.EntityId;
        to.DepartmentId = from.DepartmentId;
        to.OwnerId = from.OwnerId;
        to.DelegateId = from.DelegateId;
        to.ResponsibleId = from.ResponsibleId;
        to.ReminderRecipient = from.ReminderRecipient;
        to.StartDate = from.StartDate;
        to.EndDate = from.EndDate;
        to.NoticePeriodDays = from.NoticePeriodDays;
        to.AutoRenew = from.AutoRenew;
        to.RenewalMonths = from.RenewalMonths;
        to.AnnualValue = from.AnnualValue;
        to.TotalValue = from.TotalValue;
        to.Currency = from.Currency;
        to.PaymentTerms = from.PaymentTerms;
        to.PriceAdjustment = from.PriceAdjustment;
        to.PriceAdjustmentDate = from.PriceAdjustmentDate;
        to.GoverningLaw = from.GoverningLaw;
        to.LiabilityCap = from.LiabilityCap;
        to.Exclusivity = from.Exclusivity;
        to.ChangeOfControl = from.ChangeOfControl;
        to.Summary = from.Summary;
        to.Status = from.Status;
    }

    // ---------- execution ----------

    /// <summary>
    /// Plans and writes the rows (each row saved on its own; a failing row is reported and the rest
    /// continue), records an ImportRun and one audit event, and notifies ImportFinished.
    /// </summary>
    public async Task<ImportRun> RunRowsAsync(ImportKind kind, TabularData table, ImportRunInput input, ImportOrigin origin, CancellationToken cancellationToken = default)
    {
        var plan = await PlanAsync(kind, table, input, origin.FileName, cancellationToken);
        var now = _clock.UtcNow;
        var run = new ImportRun
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Source = origin.Source,
            SyncSourceId = origin.SyncSourceId,
            Trigger = origin.Trigger,
            FileName = origin.FileName,
            Status = "Running",
            OptionsJson = JsonDefaults.Write(input?.Options ?? new ImportOptionsDto()),
            MappingJson = JsonDefaults.Write(input?.Mapping ?? new Dictionary<string, string?>()),
            Read = plan.Rows.Count,
            StartedById = _currentUser.UserId,
            StartedByName = string.IsNullOrWhiteSpace(_currentUser.UserName) ? "System" : _currentUser.UserName!,
            StartedAt = now
        };
        var errors = new List<ImportErrorDto>();
        foreach (var row in plan.Rows)
        {
            if (row.Preview.Action == "Skip")
            {
                run.Skipped++;
                continue;
            }
            if (row.Preview.Action == "Error" || row.Apply is null)
            {
                run.Failed++;
                errors.Add(new ImportErrorDto { RowNo = row.Preview.RowNo, Message = string.Join(" ", row.Preview.Errors.Select(e => (e.Field is null ? "" : e.Field + ": ") + e.Message)) });
                continue;
            }
            try
            {
                await row.Apply(cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
                if (row.IsUpdate) run.Updated++; else run.Created++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is DbUpdateException || ex is AppException || ex is InvalidOperationException)
            {
                _db.DiscardChanges();
                run.Failed++;
                errors.Add(new ImportErrorDto { RowNo = row.Preview.RowNo, Message = "Could not be saved: " + (ex.InnerException?.Message ?? ex.Message) });
            }
        }
        run.ErrorsJson = JsonDefaults.Write(errors.Take(1000).ToList());
        run.Status = plan.MappingErrors.Count > 0 || (run.Failed > 0 && run.Created + run.Updated == 0)
            ? (run.Created + run.Updated == 0 && plan.Rows.Count > 0 ? "Failed" : "PartiallySucceeded")
            : (run.Failed > 0 ? "PartiallySucceeded" : "Succeeded");
        if (plan.MappingErrors.Count > 0)
        {
            run.Message = string.Join(" ", plan.MappingErrors);
        }
        run.CompletedAt = _clock.UtcNow;
        _db.ImportRuns.Add(run);
        _audit.Record(origin.Source == "Sync" ? "sync.run" : "import.run",
            KindName(kind) + " import from " + origin.FileName + ": " + run.Created + " created, " + run.Updated + " updated, " + run.Skipped + " skipped, " + run.Failed + " failed");
        if (_currentUser.UserId is not null)
        {
            await _notifier.SendAsync(new Notification(
                _currentUser.UserId.Value, NotificationCatalog.ImportFinished,
                "Import of " + origin.FileName + " finished",
                run.Created + " created, " + run.Updated + " updated, " + run.Skipped + " skipped, " + run.Failed + " failed.",
                null,
                new Dictionary<string, string>
                {
                    ["import.file"] = origin.FileName,
                    ["import.created"] = run.Created.ToString(CultureInfo.InvariantCulture),
                    ["import.failed"] = run.Failed.ToString(CultureInfo.InvariantCulture),
                    ["source.name"] = origin.Source == "Sync" ? origin.FileName : "File upload"
                }), cancellationToken);
        }
        await _db.SaveChangesAsync(cancellationToken);
        return run;
    }

    /// <summary>Deletes expired import sessions (UploadCleanup job).</summary>
    public static async Task<int> PurgeSessionsAsync(IAppDbContext db, IClock clock, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var ids = await db.ImportFiles.AsNoTracking().Where(f => f.ExpiresAt < now).Select(f => f.Id).ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            db.ImportFiles.Remove(new ImportFile { Id = id });
        }
        if (ids.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        return ids.Count;
    }
}

/// <summary>Cell parsing for imports (booleans, numbers, dates, lists) with the run's options.</summary>
public sealed class ValueParser
{
    private static readonly string[] AutoFormats = { "yyyy-MM-dd", "dd-MM-yyyy", "dd.MM.yyyy", "dd/MM/yyyy" };
    private readonly ImportOptionsDto _options;

    public ValueParser(ImportOptionsDto options)
    {
        _options = options;
    }

    public static List<string> List(string text) =>
        text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static void Report(ImportRowPreviewDto row, string field, string message)
    {
        row.Errors.Add(new ImportRowMessageDto { Field = field, Message = message });
    }

    /// <summary>true/false, yes/no, ja/nej, 1/0, x; empty = null.</summary>
    public static bool? ParseBool(string? text, out bool ok)
    {
        ok = true;
        if (text is null) return null;
        switch (text.Trim().ToLowerInvariant())
        {
            case "true": case "yes": case "ja": case "1": case "x": case "y": return true;
            case "false": case "no": case "nej": case "0": case "n": return false;
        }
        ok = false;
        return null;
    }

    public bool? Bool(string? text, ImportRowPreviewDto row, string field)
    {
        var value = ParseBool(text, out var ok);
        if (!ok) Report(row, field, "Use yes or no.");
        return value;
    }

    public decimal? ParseDecimal(string? text, out bool ok)
    {
        ok = true;
        if (text is null) return null;
        var decimalSeparator = _options.DecimalSeparator ?? ".";
        var thousands = decimalSeparator == "." ? ',' : '.';
        var cleaned = new string(text.Trim().Where(ch => ch != ' ' && ch != ' ' && ch != thousands).ToArray());
        if (decimalSeparator == ",")
        {
            cleaned = cleaned.Replace(',', '.');
        }
        if (decimal.TryParse(cleaned, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }
        ok = false;
        return null;
    }

    public decimal? Decimal(string? text, ImportRowPreviewDto row, string field)
    {
        var value = ParseDecimal(text, out var ok);
        if (!ok) Report(row, field, "Enter a number.");
        return value;
    }

    public int? Int(string? text, ImportRowPreviewDto row, string field)
    {
        var value = ParseDecimal(text, out var ok);
        if (!ok || (value is not null && value.Value != Math.Truncate(value.Value)))
        {
            Report(row, field, "Enter a whole number.");
            return null;
        }
        return value is null ? null : (int)value.Value;
    }

    public DateOnly? ParseDate(string? text, out string? error)
    {
        error = null;
        if (text is null) return null;
        var value = text.Trim();
        var format = _options.DateFormat ?? "auto";
        var formats = format == "auto" ? AutoFormats : new[] { format };
        var matches = new List<DateOnly>();
        foreach (var candidate in formats)
        {
            if (DateOnly.TryParseExact(value, candidate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && !matches.Contains(date))
            {
                matches.Add(date);
            }
        }
        // xlsx date cells always arrive as yyyy-MM-dd.
        if (matches.Count == 0 && format != "auto"
            && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
        {
            matches.Add(iso);
        }
        if (matches.Count == 1) return matches[0];
        error = matches.Count > 1 ? "The date '" + value + "' is ambiguous." : "Enter a date as " + (format == "auto" ? "yyyy-MM-dd" : format) + ".";
        return null;
    }

    public DateOnly? Date(string? text, ImportRowPreviewDto row, string field)
    {
        var value = ParseDate(text, out var error);
        if (error is not null) Report(row, field, error);
        return value;
    }
}
