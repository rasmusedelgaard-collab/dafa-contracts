using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dafa.Contracts.Infrastructure.Data;

public sealed class SchemaUpgradeResult
{
    /// <summary>Versions applied in this run (empty when the database was already current).</summary>
    public List<int> AppliedVersions { get; } = new();
    /// <summary>DDL statements that were executed, in order.</summary>
    public List<string> Statements { get; } = new();
    /// <summary>Index statements that failed (logged, not fatal), e.g. because existing rows break a unique index.</summary>
    public List<string> Warnings { get; } = new();
    public int CurrentVersion { get; set; }
}

/// <summary>
/// Idempotent, versioned startup schema upgrade for SQL Server and Sqlite (no EF migrations yet).
///
/// How it works:
/// - <c>EnsureCreated</c> still creates a brand-new database from the model. On a database that
///   already has tables it does nothing, so new tables and columns are added here.
/// - A <c>SchemaVersion</c> table (outside the EF model) records which numbered steps have run.
/// - Each step only ADDS: it creates missing tables, adds missing columns (NOT NULL columns get a
///   default so existing rows stay valid) and creates missing indexes. Every statement is guarded by
///   an existence check, so a step can run any number of times; nothing is ever dropped or rewritten.
/// - Column types, nullability, keys and indexes are read from the EF Core relational model for the
///   current provider, so the DDL is exactly what EnsureCreated would have produced.
/// - A failed table or column statement stops startup (better than running on a half schema); a
///   failed index (for example duplicates already in the data) is logged as a warning.
/// </summary>
public sealed class SchemaUpgrader
{
    private readonly AppDbContext _db;
    private readonly ILogger _logger;

    public SchemaUpgrader(AppDbContext db, ILogger? logger = null)
    {
        _db = db;
        _logger = logger ?? NullLogger.Instance;
    }

    public sealed record ColumnRef(string Table, string Column);

    public sealed class Step
    {
        public Step(int version, string description, string[] tables, ColumnRef[] columns, string[] indexTables)
        {
            Version = version;
            Description = description;
            Tables = tables;
            Columns = columns;
            IndexTables = indexTables;
        }

        public int Version { get; }
        public string Description { get; }
        /// <summary>Tables to create when missing.</summary>
        public string[] Tables { get; }
        /// <summary>Columns to add when missing (the table must exist).</summary>
        public ColumnRef[] Columns { get; }
        /// <summary>Tables whose model indexes are created when missing.</summary>
        public string[] IndexTables { get; }
    }

    /// <summary>The ordered steps. Append new steps at the end; never change a released one.</summary>
    public static readonly IReadOnlyList<Step> Steps = new List<Step>
    {
        new Step(1,
            "Baseline of phase 2: concurrency tokens, renewal term, reminder term key and attempts, unique indexes",
            Array.Empty<string>(),
            new[]
            {
                new ColumnRef("Contracts", "Version"),
                new ColumnRef("Contracts", "LastRenewedDeadline"),
                new ColumnRef("ApprovalInstances", "Version"),
                new ColumnRef("ApprovalSteps", "Version"),
                new ColumnRef("Reminders", "TermKey"),
                new ColumnRef("Reminders", "Attempts")
            },
            new[] { "Users", "Documents", "Reminders", "ApprovalInstances" }),
        new Step(2,
            "Release 3: search text, custom fields, field rules, currencies, tags, imports, sync sources, notification and job settings, saved views, watchers, comments, in-app notifications, preferences",
            new[]
            {
                "DocumentTexts", "CustomFieldDefinitions", "CustomFieldValues", "ContractTypeFieldRules", "Currencies",
                "Tags", "ContractTags", "ImportFiles", "ImportRuns", "SyncSources", "NotificationSettings",
                "ReminderSchedules", "JobSchedules", "AppSettings", "SavedViews", "ContractWatchers", "ContractComments",
                "InAppNotifications", "UserPreferences", "DataRoomExports"
            },
            new[]
            {
                new ColumnRef("LegalEntities", "Country"),
                new ColumnRef("LegalEntities", "Currency"),
                new ColumnRef("LegalEntities", "IsActive"),
                new ColumnRef("Suppliers", "MergedIntoId"),
                new ColumnRef("Suppliers", "Kind"),
                new ColumnRef("Suppliers", "City"),
                new ColumnRef("Suppliers", "Email"),
                new ColumnRef("Suppliers", "Phone"),
                new ColumnRef("Suppliers", "Source"),
                new ColumnRef("Suppliers", "UpdatedAt"),
                new ColumnRef("Users", "Source"),
                new ColumnRef("Users", "LastSignInAt"),
                new ColumnRef("Contracts", "ExternalRef"),
                new ColumnRef("AuditEvents", "DataJson"),
                new ColumnRef("OutboxMessages", "Channel")
            },
            new[]
            {
                "Contracts", "AuditEvents", "DocumentTexts", "CustomFieldDefinitions", "CustomFieldValues",
                "ContractTypeFieldRules", "ContractTags", "ImportRuns", "NotificationSettings", "ReminderSchedules",
                "SavedViews", "ContractWatchers", "ContractComments", "InAppNotifications", "DataRoomExports"
            })
    };

    /// <summary>Defaults for NOT NULL columns added to tables that already have rows, where 0/''/empty is wrong.</summary>
    private static readonly Dictionary<string, string> DefaultOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LegalEntities.IsActive"] = "1"
    };

    private bool IsSqlServer => (_db.Database.ProviderName ?? "").IndexOf("SqlServer", StringComparison.OrdinalIgnoreCase) >= 0;

    private string Quote(string name) => IsSqlServer ? "[" + name.Replace("]", "]]") + "]" : "\"" + name.Replace("\"", "\"\"") + "\"";

    private static IRelationalModel GetRelationalModel(DbContext db)
    {
        try
        {
            // The design-time model carries the full relational model (what migrations use).
            return db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        }
        catch (Exception)
        {
            return db.Model.GetRelationalModel();
        }
    }

    public async Task<SchemaUpgradeResult> UpgradeAsync(CancellationToken cancellationToken = default)
    {
        var result = new SchemaUpgradeResult();
        var model = GetRelationalModel(_db);
        await _db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await EnsureVersionTableAsync(result, cancellationToken);
            var applied = await ReadAppliedVersionsAsync(cancellationToken);
            foreach (var step in Steps.OrderBy(s => s.Version))
            {
                if (applied.Contains(step.Version))
                {
                    continue;
                }
                _logger.LogInformation("Applying schema step {Version}: {Description}", step.Version, step.Description);
                foreach (var tableName in step.Tables)
                {
                    await EnsureTableAsync(model, tableName, result, cancellationToken);
                }
                foreach (var column in step.Columns)
                {
                    await EnsureColumnAsync(model, column, result, cancellationToken);
                }
                foreach (var tableName in step.IndexTables)
                {
                    await EnsureIndexesAsync(model, tableName, result, cancellationToken);
                }
                await RecordVersionAsync(step, cancellationToken);
                result.AppliedVersions.Add(step.Version);
                applied.Add(step.Version);
            }
            result.CurrentVersion = applied.Count == 0 ? 0 : applied.Max();
            await LogDriftAsync(model, cancellationToken);
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
        return result;
    }

    // ---------- version table ----------

    private async Task EnsureVersionTableAsync(SchemaUpgradeResult result, CancellationToken cancellationToken)
    {
        if (await TableExistsAsync(SchemaVersionsTable, cancellationToken))
        {
            return;
        }
        var sql = IsSqlServer
            ? "CREATE TABLE [SchemaVersion] ([Version] int NOT NULL, [Description] nvarchar(400) NOT NULL, [AppliedAt] datetime2 NOT NULL, CONSTRAINT [PK_SchemaVersion] PRIMARY KEY ([Version]))"
            : "CREATE TABLE IF NOT EXISTS \"SchemaVersion\" (\"Version\" INTEGER NOT NULL, \"Description\" TEXT NOT NULL, \"AppliedAt\" TEXT NOT NULL, CONSTRAINT \"PK_SchemaVersion\" PRIMARY KEY (\"Version\"))";
        await ExecuteAsync(sql, cancellationToken);
        result.Statements.Add(sql);
    }

    private const string SchemaVersionsTable = "SchemaVersion";

    private async Task<HashSet<int>> ReadAppliedVersionsAsync(CancellationToken cancellationToken)
    {
        var versions = new HashSet<int>();
        using var command = CreateCommand("SELECT " + Quote("Version") + " FROM " + Quote(SchemaVersionsTable));
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
        }
        return versions;
    }

    private async Task RecordVersionAsync(Step step, CancellationToken cancellationToken)
    {
        using var command = CreateCommand(
            "INSERT INTO " + Quote(SchemaVersionsTable) + " (" + Quote("Version") + ", " + Quote("Description") + ", " + Quote("AppliedAt") + ") VALUES (@version, @description, @appliedAt)");
        AddParameter(command, "@version", step.Version);
        AddParameter(command, "@description", step.Description.Length > 400 ? step.Description.Substring(0, 400) : step.Description);
        AddParameter(command, "@appliedAt", DateTime.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The versions recorded in the database (for tests and the runbook check).</summary>
    public async Task<List<int>> GetRecordedVersionsAsync(CancellationToken cancellationToken = default)
    {
        await _db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (!await TableExistsAsync(SchemaVersionsTable, cancellationToken))
            {
                return new List<int>();
            }
            var list = new List<int>();
            using var command = CreateCommand("SELECT " + Quote("Version") + " FROM " + Quote(SchemaVersionsTable) + " ORDER BY " + Quote("Version"));
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                list.Add(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
            }
            return list;
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }

    // ---------- tables, columns, indexes ----------

    private static ITable FindTable(IRelationalModel model, string tableName)
    {
        var table = model.Tables.FirstOrDefault(t => string.Equals(t.Name, tableName, StringComparison.Ordinal));
        if (table is null)
        {
            throw new InvalidOperationException("Schema step refers to table '" + tableName + "', which is not in the EF model.");
        }
        return table;
    }

    private async Task EnsureTableAsync(IRelationalModel model, string tableName, SchemaUpgradeResult result, CancellationToken cancellationToken)
    {
        if (await TableExistsAsync(tableName, cancellationToken))
        {
            return;
        }
        var table = FindTable(model, tableName);
        var sql = new StringBuilder();
        sql.Append(IsSqlServer ? "CREATE TABLE " : "CREATE TABLE IF NOT EXISTS ").Append(Quote(table.Name)).Append(" (");
        var first = true;
        foreach (var column in table.Columns)
        {
            if (!first)
            {
                sql.Append(", ");
            }
            first = false;
            sql.Append(Quote(column.Name)).Append(' ').Append(column.StoreType).Append(column.IsNullable ? " NULL" : " NOT NULL");
        }
        var key = table.PrimaryKey;
        if (key is not null)
        {
            sql.Append(", CONSTRAINT ").Append(Quote(key.Name)).Append(" PRIMARY KEY (")
                .Append(string.Join(", ", key.Columns.Select(c => Quote(c.Name)))).Append(')');
        }
        sql.Append(')');
        var text = sql.ToString();
        await ExecuteAsync(text, cancellationToken);
        result.Statements.Add(text);
    }

    private async Task EnsureColumnAsync(IRelationalModel model, ColumnRef reference, SchemaUpgradeResult result, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(reference.Table, cancellationToken))
        {
            // A table the model knows but the database does not have: create it whole.
            await EnsureTableAsync(model, reference.Table, result, cancellationToken);
            return;
        }
        if (await ColumnExistsAsync(reference.Table, reference.Column, cancellationToken))
        {
            return;
        }
        var table = FindTable(model, reference.Table);
        var column = table.Columns.FirstOrDefault(c => string.Equals(c.Name, reference.Column, StringComparison.Ordinal));
        if (column is null)
        {
            throw new InvalidOperationException("Schema step refers to column '" + reference.Table + "." + reference.Column + "', which is not in the EF model.");
        }

        string sql;
        if (column.IsNullable)
        {
            sql = IsSqlServer
                ? "ALTER TABLE " + Quote(table.Name) + " ADD " + Quote(column.Name) + " " + column.StoreType + " NULL"
                : "ALTER TABLE " + Quote(table.Name) + " ADD COLUMN " + Quote(column.Name) + " " + column.StoreType + " NULL";
        }
        else
        {
            var defaultValue = DefaultFor(table.Name, column);
            sql = IsSqlServer
                ? "ALTER TABLE " + Quote(table.Name) + " ADD " + Quote(column.Name) + " " + column.StoreType + " NOT NULL CONSTRAINT "
                    + Quote("DF_" + table.Name + "_" + column.Name) + " DEFAULT " + defaultValue
                : "ALTER TABLE " + Quote(table.Name) + " ADD COLUMN " + Quote(column.Name) + " " + column.StoreType + " NOT NULL DEFAULT " + defaultValue;
        }
        await ExecuteAsync(sql, cancellationToken);
        result.Statements.Add(sql);
    }

    private static Type ClrTypeOf(IColumn column)
    {
        var mapping = column.PropertyMappings.FirstOrDefault();
        var type = mapping?.Property.ClrType ?? typeof(string);
        return Nullable.GetUnderlyingType(type) ?? type;
    }

    /// <summary>A SQL literal used as the value of existing rows for a new NOT NULL column.</summary>
    private string DefaultFor(string tableName, IColumn column)
    {
        if (DefaultOverrides.TryGetValue(tableName + "." + column.Name, out var overridden))
        {
            return overridden;
        }
        var type = ClrTypeOf(column);
        if (type == typeof(bool) || type == typeof(int) || type == typeof(long) || type == typeof(short)
            || type == typeof(double) || type == typeof(float) || type.IsEnum)
        {
            return "0";
        }
        if (type == typeof(decimal))
        {
            return IsSqlServer ? "0" : "'0.0'";
        }
        if (type == typeof(Guid))
        {
            return "'00000000-0000-0000-0000-000000000000'";
        }
        if (type == typeof(DateOnly))
        {
            return "'0001-01-01'";
        }
        if (type == typeof(DateTime))
        {
            return IsSqlServer ? "'0001-01-01T00:00:00'" : "'0001-01-01 00:00:00'";
        }
        if (type == typeof(byte[]))
        {
            return IsSqlServer ? "0x" : "X''";
        }
        return IsSqlServer ? "N''" : "''";
    }

    private async Task EnsureIndexesAsync(IRelationalModel model, string tableName, SchemaUpgradeResult result, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(tableName, cancellationToken))
        {
            return;
        }
        var table = FindTable(model, tableName);
        foreach (var index in table.Indexes)
        {
            if (await IndexExistsAsync(index.Name, cancellationToken))
            {
                continue;
            }
            var missingColumn = false;
            foreach (var column in index.Columns)
            {
                if (!await ColumnExistsAsync(tableName, column.Name, cancellationToken))
                {
                    missingColumn = true;
                }
            }
            if (missingColumn)
            {
                result.Warnings.Add("Index " + index.Name + " skipped: a column is missing.");
                continue;
            }
            var sql = new StringBuilder();
            sql.Append("CREATE ").Append(index.IsUnique ? "UNIQUE " : "").Append("INDEX ")
                .Append(IsSqlServer ? "" : "IF NOT EXISTS ")
                .Append(Quote(index.Name)).Append(" ON ").Append(Quote(table.Name)).Append(" (")
                .Append(string.Join(", ", index.Columns.Select(c => Quote(c.Name)))).Append(')');
            if (!string.IsNullOrWhiteSpace(index.Filter))
            {
                sql.Append(" WHERE ").Append(index.Filter);
            }
            var text = sql.ToString();
            try
            {
                await ExecuteAsync(text, cancellationToken);
                result.Statements.Add(text);
            }
            catch (DbException ex)
            {
                // For example duplicates in old data under a new unique index. The app still works;
                // the index is missing until the data is cleaned up and the app restarts.
                _logger.LogWarning(ex, "Could not create index {Index}", index.Name);
                result.Warnings.Add("Index " + index.Name + " could not be created: " + ex.Message);
            }
        }
    }

    /// <summary>Logs model tables or columns that are still missing after all steps (a step was forgotten).</summary>
    private async Task LogDriftAsync(IRelationalModel model, CancellationToken cancellationToken)
    {
        foreach (var table in model.Tables)
        {
            if (!await TableExistsAsync(table.Name, cancellationToken))
            {
                _logger.LogError("Schema drift: table {Table} is in the model but not in the database. Add a schema step.", table.Name);
                continue;
            }
            foreach (var column in table.Columns)
            {
                if (!await ColumnExistsAsync(table.Name, column.Name, cancellationToken))
                {
                    _logger.LogError("Schema drift: column {Table}.{Column} is in the model but not in the database. Add a schema step.", table.Name, column.Name);
                }
            }
        }
    }

    // ---------- catalog queries ----------

    private async Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken)
    {
        var sql = IsSqlServer
            ? "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @name"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name";
        using var command = CreateCommand(sql);
        AddParameter(command, "@name", tableName);
        return await ScalarCountAsync(command, cancellationToken) > 0;
    }

    private async Task<bool> ColumnExistsAsync(string tableName, string columnName, CancellationToken cancellationToken)
    {
        var sql = IsSqlServer
            ? "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @table AND COLUMN_NAME = @column"
            : "SELECT COUNT(*) FROM pragma_table_info(@table) WHERE name = @column";
        using var command = CreateCommand(sql);
        AddParameter(command, "@table", tableName);
        AddParameter(command, "@column", columnName);
        return await ScalarCountAsync(command, cancellationToken) > 0;
    }

    private async Task<bool> IndexExistsAsync(string indexName, CancellationToken cancellationToken)
    {
        var sql = IsSqlServer
            ? "SELECT COUNT(*) FROM sys.indexes WHERE name = @name"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = @name";
        using var command = CreateCommand(sql);
        AddParameter(command, "@name", indexName);
        return await ScalarCountAsync(command, cancellationToken) > 0;
    }

    private static async Task<long> ScalarCountAsync(DbCommand command, CancellationToken cancellationToken)
    {
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null || value is DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private DbCommand CreateCommand(string sql)
    {
        var command = _db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        using var command = CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
