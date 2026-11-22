namespace Dafa.Contracts.Core.Domain;

// Release 3 persistence model (docs/CONTRACT_V3.md). Same rules as Entities.cs: plain Guid foreign
// keys, no navigation properties, no cascades, DateTime in UTC. Every table and column here is also
// created on existing databases by the startup schema upgrader (Infrastructure/Data/SchemaUpgrader.cs),
// so a column added to an existing table must be nullable or have a default the upgrader knows.

/// <summary>Stored as int; the API names are the member names.</summary>
public enum CustomFieldType { Text, Number, Money, Date, YesNo, Choice, MultiChoice, Person }

/// <summary>API names: counterparties, users, contracts.</summary>
public enum ImportKind { Counterparties, Users, Contracts }

/// <summary>
/// CreateOnly: a row matching an existing record is an error. CreateAndUpdate: matching records are
/// updated (mapped, non-empty cells only). SkipDuplicates: matching records are left alone.
/// </summary>
public enum ImportMode { CreateOnly, CreateAndUpdate, SkipDuplicates }

public enum SyncSourceKind { PowerBi, Sql }

public enum ScheduleFrequency { Off, Hourly, Daily, Weekly }

public enum CounterpartyKind { Supplier, Customer, Partner, Authority, Other }

/// <summary>Text extracted from a stored document, for search. One row per document.</summary>
public class DocumentText
{
    public Guid DocumentId { get; set; }
    public Guid ContractId { get; set; }
    /// <summary>Extracted text with whitespace collapsed, cut at <see cref="MaxCharacters"/>.</summary>
    public string Text { get; set; } = "";
    /// <summary>Text.ToLowerInvariant(): same length, so an offset found here is an offset in Text.</summary>
    public string NormalizedText { get; set; } = "";
    /// <summary>Indexed, Empty (no text: a scan, an image, an unsupported type), Failed.</summary>
    public string Status { get; set; } = "";
    public string? Note { get; set; }
    public DateTime ExtractedAt { get; set; }
    /// <summary>
    /// SharePoint hardening (schema step 4): Status Retry = the file could not be read from the
    /// store; Attempts counts the tries and NextAttemptAt is when the backfill tries again
    /// (backoff). After MaxAttempts the status becomes Failed for good.
    /// </summary>
    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }

    public const int MaxAttempts = 6;

    public const int MaxCharacters = 400_000;
}

public class CustomFieldDefinition
{
    public Guid Id { get; set; }
    /// <summary>camelCase, unique, immutable: ^[a-z][a-zA-Z0-9]{1,39}$.</summary>
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public CustomFieldType FieldType { get; set; }
    /// <summary>JSON array of contract type ids; empty or null = every type.</summary>
    public string? ContractTypeIdsJson { get; set; }
    public bool Required { get; set; }
    public string? HelpText { get; set; }
    public int SortOrder { get; set; }
    /// <summary>JSON array of option strings for Choice / MultiChoice.</summary>
    public string? OptionsJson { get; set; }
    public bool ShowInRegister { get; set; }
    public bool ShowInFilters { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// One value of a custom field on a contract (EAV, portable and filterable in SQL on both providers).
/// Text/Choice -> TextValue; MultiChoice -> TextValue as "|a|b|" (so a contains-test on "|a|" finds an
/// option); Number -> NumberValue; Money -> NumberValue + TextValue (currency); Date -> DateValue;
/// YesNo -> BoolValue; Person -> UserValue. NumberValue is a double so it compares correctly on Sqlite.
/// </summary>
public class CustomFieldValue
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public Guid FieldId { get; set; }
    public string? TextValue { get; set; }
    public double? NumberValue { get; set; }
    public DateOnly? DateValue { get; set; }
    public bool? BoolValue { get; set; }
    public Guid? UserValue { get; set; }
}

/// <summary>
/// Field rules per contract type, one row per field: Required rows list the extra required standard
/// fields; rows with a DefaultValue carry the defaults (currency, noticePeriodDays, reminderRecipient,
/// autoRenew, renewalMonths).
/// </summary>
public class ContractTypeFieldRule
{
    public Guid Id { get; set; }
    public Guid ContractTypeId { get; set; }
    public string Field { get; set; } = "";
    public bool Required { get; set; }
    public string? DefaultValue { get; set; }
}

public class CurrencyDefinition
{
    /// <summary>ISO code, 3 uppercase letters.</summary>
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>DKK per one unit; used for every DKK figure, including the approval value.</summary>
    public decimal RateToDkk { get; set; } = 1m;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

public class Tag
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>gray, blue, green, amber, red, purple.</summary>
    public string Color { get; set; } = "gray";
    public string? Description { get; set; }
}

public class ContractTag
{
    public Guid ContractId { get; set; }
    public Guid TagId { get; set; }
}

/// <summary>An import session: the uploaded file, kept 24 hours, run at most once.</summary>
public class ImportFile
{
    public Guid Id { get; set; }
    public ImportKind Kind { get; set; }
    public string FileName { get; set; } = "";
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public Guid CreatedById { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    /// <summary>Set when the session was run (a second run is 409 import-state).</summary>
    public DateTime? RanAt { get; set; }
    public Guid? RunId { get; set; }
}

/// <summary>One import: a file import (Source File) or a sync source run (Source Sync).</summary>
public class ImportRun
{
    public Guid Id { get; set; }
    public ImportKind Kind { get; set; }
    /// <summary>File or Sync.</summary>
    public string Source { get; set; } = "File";
    public Guid? SyncSourceId { get; set; }
    /// <summary>Manual or Schedule (sync runs).</summary>
    public string Trigger { get; set; } = "Manual";
    public string FileName { get; set; } = "";
    /// <summary>Running, Succeeded, PartiallySucceeded, Failed.</summary>
    public string Status { get; set; } = "Running";
    public string OptionsJson { get; set; } = "{}";
    public string MappingJson { get; set; } = "{}";
    public int Read { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    /// <summary>JSON array of { rowNo, message } (first 1000).</summary>
    public string ErrorsJson { get; set; } = "[]";
    public string? Message { get; set; }
    public Guid? StartedById { get; set; }
    public string StartedByName { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// An admin-configured data source that feeds the import pipeline. No secret is stored here: a SQL
/// source names a server-side connection (Sync:Connections:&lt;name&gt;), Power BI uses the app
/// registration or managed identity from server configuration.
/// </summary>
public class SyncSource
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public SyncSourceKind Kind { get; set; }
    public ImportKind Target { get; set; }
    public string? WorkspaceId { get; set; }
    public string? DatasetId { get; set; }
    public string? DaxQuery { get; set; }
    public string? TableName { get; set; }
    public string? ConnectionName { get; set; }
    public string? SqlQuery { get; set; }
    public string MappingJson { get; set; } = "{}";
    public string OptionsJson { get; set; } = "{}";
    public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Off;
    /// <summary>"HH:mm", Europe/Copenhagen.</summary>
    public string Time { get; set; } = "06:00";
    /// <summary>1 = Monday ... 7 = Sunday (Weekly).</summary>
    public int? DayOfWeek { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? NextRunAt { get; set; }
    public Guid? LastRunId { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>How one notification event is sent. A missing row means: the built-in defaults.</summary>
public class NotificationSetting
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Comma separated: Email, Teams, InApp.</summary>
    public string Channels { get; set; } = "";
    /// <summary>Comma separated RecipientRule values.</summary>
    public string Recipients { get; set; } = "";
    /// <summary>Comma separated user ids.</summary>
    public string? ExtraRecipientIds { get; set; }
    public string? SubjectTemplate { get; set; }
    public string? BodyTemplate { get; set; }
}

/// <summary>Notice reminder offsets and escalation for one contract type (overrides ReminderSettings).</summary>
public class ReminderSchedule
{
    public Guid Id { get; set; }
    public Guid ContractTypeId { get; set; }
    public string NoticeDays { get; set; } = "120,60,30";
    public int EscalationDays { get; set; } = 14;
    public int ObligationDays { get; set; } = 14;
}

/// <summary>Schedule and last result of one background job (keys in JobKeys).</summary>
public class JobSchedule
{
    public string JobKey { get; set; } = "";
    public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Hourly;
    public string Time { get; set; } = "06:00";
    /// <summary>1 = Monday ... 7 = Sunday (Weekly).</summary>
    public int? DayOfWeek { get; set; }
    public DateTime? LastRunAt { get; set; }
    /// <summary>Succeeded, PartiallySucceeded, Failed, Running.</summary>
    public string? LastStatus { get; set; }
    public string? LastMessage { get; set; }
    public int? LastDurationMs { get; set; }
    public DateTime? NextRunAt { get; set; }
}

/// <summary>Key/value settings; general settings are one JSON document under key "general".</summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

public class SavedView
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Personal or Shared.</summary>
    public string Scope { get; set; } = "Personal";
    public string ColumnsJson { get; set; } = "[]";
    /// <summary>The register query object, stored as given.</summary>
    public string QueryJson { get; set; } = "{}";
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ContractWatcher
{
    public Guid ContractId { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ContractComment
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public Guid AuthorId { get; set; }
    public string Body { get; set; } = "";
    /// <summary>JSON array of mentioned user ids.</summary>
    public string MentionsJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; }
    public DateTime? EditedAt { get; set; }
}

public class InAppNotification
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>A NotificationEvent name.</summary>
    public string Event { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public Guid? ContractId { get; set; }
    /// <summary>App path, e.g. /contracts/&lt;id&gt;#comments.</summary>
    public string? Link { get; set; }
    public string? ActorName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
}

public class UserPreference
{
    public Guid UserId { get; set; }
    /// <summary>Off, Daily, Weekly; null = the admin default.</summary>
    public string? Digest { get; set; }
    /// <summary>JSON array of { event, email, teams, inApp }.</summary>
    public string EventsJson { get; set; } = "[]";
    public Guid? DefaultEntityId { get; set; }
    public string? LandingPage { get; set; }
    public bool AutoWatch { get; set; }
    /// <summary>Release 4: light, dark or system. NOT NULL; existing rows get 'light' from schema step 3.</summary>
    public string Theme { get; set; } = "light";
    public DateTime? LastDigestAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A due-diligence data room export (zip stored by IExportStorage).</summary>
public class DataRoomExport
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Queued, Running, Ready, Failed.</summary>
    public string Status { get; set; } = "Queued";
    public string FilterJson { get; set; } = "{}";
    /// <summary>All, MainOnly, None.</summary>
    public string Documents { get; set; } = "All";
    /// <summary>Latest, All.</summary>
    public string Versions { get; set; } = "Latest";
    public int ContractCount { get; set; }
    public int DocumentCount { get; set; }
    public long? SizeBytes { get; set; }
    public string FileName { get; set; } = "";
    public string? StorageKey { get; set; }
    public string? Error { get; set; }
    public Guid CreatedById { get; set; }
    public string CreatedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}
