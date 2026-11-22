using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Dtos;
using Dafa.Contracts.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dafa.Contracts.Api.Controllers;

/// <summary>Builds the shared ContractFilter from the query string (repeatable parameters, cf.&lt;key&gt;).</summary>
public static class QueryFilter
{
    public static ContractFilter From(HttpRequest request)
    {
        return ContractFilterParser.FromQuery(request.Query.Select(pair =>
            new KeyValuePair<string, string[]>(pair.Key, pair.Value.Select(v => v ?? "").ToArray())));
    }
}

/// <summary>Release 3 endpoints for every signed-in user (CONTRACT_V3 sections 1-7). All contract data is access scoped.</summary>
[Route("api")]
public class Release3Controller : ApiControllerBase
{
    private readonly PersonalService _personal;
    private readonly AnalyticsService _analytics;
    private readonly SearchService _search;
    private readonly CollaborationService _collaboration;
    private readonly CustomFieldService _customFields;
    private readonly VersionService _versions;
    private readonly DataRoomService _dataRoom;

    public Release3Controller(
        PersonalService personal, AnalyticsService analytics, SearchService search, CollaborationService collaboration,
        CustomFieldService customFields, VersionService versions, DataRoomService dataRoom)
    {
        _personal = personal;
        _analytics = analytics;
        _search = search;
        _collaboration = collaboration;
        _customFields = customFields;
        _versions = versions;
        _dataRoom = dataRoom;
    }

    // ---------- configuration ----------

    [HttpGet("app-config")]
    public async Task<ActionResult<AppConfigDto>> AppConfig(CancellationToken cancellationToken) =>
        await _personal.GetAppConfigAsync(cancellationToken);

    [HttpGet("me/preferences")]
    public async Task<ActionResult<UserPreferencesDto>> GetPreferences(CancellationToken cancellationToken) =>
        await _personal.GetPreferencesAsync(cancellationToken);

    [HttpPut("me/preferences")]
    public async Task<ActionResult<UserPreferencesDto>> PutPreferences([FromBody] UserPreferencesDto input, CancellationToken cancellationToken) =>
        await _personal.UpdatePreferencesAsync(input, cancellationToken);

    [HttpGet("custom-fields")]
    public async Task<ActionResult<List<CustomFieldDefDto>>> CustomFields(CancellationToken cancellationToken) =>
        await _customFields.GetActiveAsync(cancellationToken);

    [HttpGet("field-rules")]
    public async Task<ActionResult<FieldRulesDto>> FieldRules(CancellationToken cancellationToken) =>
        await _customFields.GetFieldRulesForUserAsync(cancellationToken);

    [HttpGet("tags")]
    public async Task<ActionResult<List<TagRefDto>>> Tags(CancellationToken cancellationToken) =>
        await _collaboration.GetTagsAsync(cancellationToken);

    // ---------- analytics ----------

    [HttpGet("overview/analytics")]
    public async Task<ActionResult<OverviewAnalyticsDto>> Analytics([FromQuery] decimal? ddThresholdDkk, CancellationToken cancellationToken)
    {
        if (ddThresholdDkk is < 0m)
        {
            throw new InputValidationException("ddThresholdDkk", "The threshold cannot be negative.");
        }
        return await _analytics.GetAsync(QueryFilter.From(Request), ddThresholdDkk, cancellationToken);
    }

    // ---------- search, people, counterparties ----------

    [HttpGet("search/full")]
    public async Task<ActionResult<FullSearchResultDto>> FullSearch([FromQuery] FullSearchQuery query, CancellationToken cancellationToken) =>
        await _search.FullSearchAsync(query, cancellationToken);

    [HttpGet("people/{id:guid}")]
    public async Task<ActionResult<PersonPageDto>> Person(Guid id, CancellationToken cancellationToken) =>
        await _search.PersonAsync(id, cancellationToken);

    [HttpGet("counterparties")]
    public async Task<ActionResult<List<CounterpartyDto>>> Counterparties([FromQuery] string? q, CancellationToken cancellationToken) =>
        await _search.FindCounterpartiesAsync(q, cancellationToken);

    [HttpGet("counterparties/{id:guid}")]
    public async Task<ActionResult<CounterpartyPageDto>> Counterparty(Guid id, CancellationToken cancellationToken) =>
        await _search.CounterpartyAsync(id, cancellationToken);

    // ---------- tags, watch ----------

    [HttpPut("contracts/{id:guid}/tags")]
    public async Task<ActionResult<ContractDetailDto>> SetTags(Guid id, [FromBody] ContractTagsInput input, CancellationToken cancellationToken) =>
        await _collaboration.SetContractTagsAsync(id, input, cancellationToken);

    [HttpPut("contracts/{id:guid}/watch")]
    public async Task<IActionResult> Watch(Guid id, CancellationToken cancellationToken)
    {
        await _collaboration.WatchAsync(id, true, cancellationToken);
        return NoContent();
    }

    [HttpDelete("contracts/{id:guid}/watch")]
    public async Task<IActionResult> Unwatch(Guid id, CancellationToken cancellationToken)
    {
        await _collaboration.WatchAsync(id, false, cancellationToken);
        return NoContent();
    }

    [HttpGet("watching")]
    public async Task<ActionResult<List<ContractListItemDto>>> Watching(CancellationToken cancellationToken) =>
        await _collaboration.WatchingAsync(cancellationToken);

    // ---------- comments ----------

    [HttpGet("contracts/{id:guid}/comments")]
    public async Task<ActionResult<List<CommentDto>>> Comments(Guid id, CancellationToken cancellationToken) =>
        await _collaboration.GetCommentsAsync(id, cancellationToken);

    [HttpPost("contracts/{id:guid}/comments")]
    public async Task<ActionResult<CommentDto>> AddComment(Guid id, [FromBody] CommentInput input, CancellationToken cancellationToken) =>
        await _collaboration.AddCommentAsync(id, input, cancellationToken);

    [HttpPut("comments/{id:guid}")]
    public async Task<ActionResult<CommentDto>> EditComment(Guid id, [FromBody] CommentInput input, CancellationToken cancellationToken) =>
        await _collaboration.EditCommentAsync(id, input, cancellationToken);

    [HttpDelete("comments/{id:guid}")]
    public async Task<IActionResult> DeleteComment(Guid id, CancellationToken cancellationToken)
    {
        await _collaboration.DeleteCommentAsync(id, cancellationToken);
        return NoContent();
    }

    // ---------- notifications ----------

    [HttpGet("notifications")]
    public async Task<ActionResult<NotificationPageDto>> Notifications(
        [FromQuery] bool? unreadOnly, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken) =>
        await _collaboration.GetNotificationsAsync(unreadOnly == true, page, pageSize, cancellationToken);

    [HttpGet("notifications/unread-count")]
    public async Task<ActionResult<CountDto>> UnreadCount(CancellationToken cancellationToken) =>
        await _collaboration.UnreadCountAsync(cancellationToken);

    [HttpPost("notifications/{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken)
    {
        await _collaboration.MarkReadAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPost("notifications/read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        await _collaboration.MarkAllReadAsync(cancellationToken);
        return NoContent();
    }

    // ---------- saved views ----------

    [HttpGet("views")]
    public async Task<ActionResult<List<SavedViewDto>>> Views(CancellationToken cancellationToken) =>
        await _collaboration.GetViewsAsync(cancellationToken);

    [HttpPost("views")]
    public async Task<ActionResult<SavedViewDto>> CreateView([FromBody] SavedViewInput input, CancellationToken cancellationToken) =>
        await _collaboration.CreateViewAsync(input, cancellationToken);

    [HttpPut("views/{id:guid}")]
    public async Task<ActionResult<SavedViewDto>> UpdateView(Guid id, [FromBody] SavedViewInput input, CancellationToken cancellationToken) =>
        await _collaboration.UpdateViewAsync(id, input, cancellationToken);

    [HttpDelete("views/{id:guid}")]
    public async Task<IActionResult> DeleteView(Guid id, CancellationToken cancellationToken)
    {
        await _collaboration.DeleteViewAsync(id, cancellationToken);
        return NoContent();
    }

    // ---------- bulk, versions ----------

    [HttpPost("contracts/bulk")]
    public async Task<ActionResult<BulkResultDto>> Bulk([FromBody] BulkInput input, CancellationToken cancellationToken) =>
        await _collaboration.BulkAsync(input, cancellationToken);

    [HttpGet("contracts/{id:guid}/versions")]
    public async Task<ActionResult<List<ContractVersionDto>>> Versions(Guid id, CancellationToken cancellationToken) =>
        await _versions.GetVersionsAsync(id, cancellationToken);

    [HttpGet("contracts/{id:guid}/compare")]
    public async Task<ActionResult<ContractComparisonDto>> Compare(Guid id, [FromQuery] string? from, [FromQuery] string? to, CancellationToken cancellationToken) =>
        await _versions.CompareAsync(id, from, to, cancellationToken);

    // ---------- data room ----------

    [HttpPost("data-room/exports")]
    public async Task<IActionResult> CreateExport([FromBody] DataRoomInput input, CancellationToken cancellationToken)
    {
        var export = await _dataRoom.CreateAsync(input, cancellationToken);
        return StatusCode(StatusCodes.Status202Accepted, export);
    }

    [HttpGet("data-room/exports")]
    public async Task<ActionResult<List<DataRoomExportDto>>> Exports(CancellationToken cancellationToken) =>
        await _dataRoom.ListAsync(cancellationToken);

    [HttpGet("data-room/exports/{id:guid}")]
    public async Task<ActionResult<DataRoomExportDto>> Export(Guid id, CancellationToken cancellationToken) =>
        await _dataRoom.GetAsync(id, cancellationToken);

    [HttpGet("data-room/exports/{id:guid}/content")]
    public async Task<IActionResult> ExportContent(Guid id, CancellationToken cancellationToken)
    {
        var content = await _dataRoom.OpenAsync(id, cancellationToken);
        return File(content.Stream, content.ContentType, content.FileName);
    }
}

/// <summary>Release 3 administration (CONTRACT_V3 sections 8-12). Role Admin only.</summary>
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public class AdminRelease3Controller : ApiControllerBase
{
    private readonly CounterpartyService _counterparties;
    private readonly ImportService _imports;
    private readonly SyncService _sync;
    private readonly CustomFieldService _customFields;
    private readonly AdminDirectoryService _directory;
    private readonly CollaborationService _collaboration;
    private readonly SettingsService _settings;
    private readonly JobRunner _jobs;

    public AdminRelease3Controller(
        CounterpartyService counterparties, ImportService imports, SyncService sync, CustomFieldService customFields,
        AdminDirectoryService directory, CollaborationService collaboration, SettingsService settings, JobRunner jobs)
    {
        _counterparties = counterparties;
        _imports = imports;
        _sync = sync;
        _customFields = customFields;
        _directory = directory;
        _collaboration = collaboration;
        _settings = settings;
        _jobs = jobs;
    }

    // ---------- counterparties ----------

    [HttpGet("counterparties")]
    public async Task<ActionResult<PagedDto<CounterpartyDto>>> Counterparties(
        [FromQuery] string? q, [FromQuery] bool? active, [FromQuery] string? kind, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken) =>
        await _counterparties.ListAsync(q, active, kind, page, pageSize, cancellationToken);

    [HttpPost("counterparties")]
    public async Task<ActionResult<CounterpartyDto>> CreateCounterparty([FromBody] CounterpartyInput input, CancellationToken cancellationToken) =>
        await _counterparties.CreateAsync(input, cancellationToken);

    [HttpPut("counterparties/{id:guid}")]
    public async Task<ActionResult<CounterpartyDto>> UpdateCounterparty(Guid id, [FromBody] CounterpartyInput input, CancellationToken cancellationToken) =>
        await _counterparties.UpdateAsync(id, input, cancellationToken);

    [HttpGet("counterparties/duplicates")]
    public async Task<ActionResult<List<DuplicateGroupDto>>> Duplicates(CancellationToken cancellationToken) =>
        await _counterparties.DuplicatesAsync(cancellationToken);

    [HttpPost("counterparties/merge")]
    public async Task<ActionResult<MergeResultDto>> Merge([FromBody] MergeInput input, CancellationToken cancellationToken) =>
        await _counterparties.MergeAsync(input, cancellationToken);

    // ---------- imports ----------

    [HttpGet("imports/{kind}/fields")]
    public async Task<ActionResult<List<ImportFieldDefDto>>> ImportFields(string kind, CancellationToken cancellationToken) =>
        await _imports.FieldsAsync(ImportService.ParseKind(kind), cancellationToken);

    [HttpGet("imports/{kind}/template")]
    public async Task<IActionResult> ImportTemplate(string kind, CancellationToken cancellationToken)
    {
        var parsed = ImportService.ParseKind(kind);
        var bytes = await _imports.TemplateAsync(parsed, cancellationToken);
        return File(bytes, "text/csv; charset=utf-8", "import-" + ImportService.KindName(parsed) + ".csv");
    }

    [HttpPost("imports/{kind}/upload")]
    [RequestSizeLimit(12L * 1024 * 1024)]
    public async Task<ActionResult<ImportSessionDto>> ImportUpload(string kind, IFormFile? file, CancellationToken cancellationToken)
    {
        var parsed = ImportService.ParseKind(kind);
        if (file is null || file.Length == 0)
        {
            throw new InputValidationException("file", "Choose a .csv or .xlsx file.");
        }
        if (file.Length > ImportService.MaxFileBytes)
        {
            throw new InputValidationException("file", "The file is larger than 10 MB.");
        }
        var bytes = await ReadAllAsync(file, cancellationToken);
        return await _imports.UploadAsync(parsed, file.FileName, bytes, cancellationToken);
    }

    [HttpPost("imports/sessions/{id:guid}/preview")]
    public async Task<ActionResult<ImportPreviewDto>> ImportPreview(Guid id, [FromBody] ImportRunInput input, CancellationToken cancellationToken) =>
        await _imports.PreviewAsync(id, input, cancellationToken);

    [HttpPost("imports/sessions/{id:guid}/run")]
    public async Task<ActionResult<ImportResultDto>> ImportRun(Guid id, [FromBody] ImportRunInput input, CancellationToken cancellationToken) =>
        await _imports.RunAsync(id, input, cancellationToken);

    [HttpGet("imports/history")]
    public async Task<ActionResult<List<ImportResultDto>>> ImportHistory([FromQuery] string? kind, CancellationToken cancellationToken) =>
        await _imports.HistoryAsync(kind, cancellationToken);

    // ---------- sync sources ----------

    [HttpGet("sync-connections")]
    public ActionResult<List<SyncConnectionDto>> SyncConnections() => _sync.GetConnections();

    [HttpGet("sync-sources")]
    public async Task<ActionResult<List<SyncSourceDto>>> SyncSources(CancellationToken cancellationToken) =>
        await _sync.ListAsync(cancellationToken);

    [HttpPost("sync-sources")]
    public async Task<ActionResult<SyncSourceDto>> CreateSyncSource([FromBody] SyncSourceInput input, CancellationToken cancellationToken) =>
        await _sync.CreateAsync(input, cancellationToken);

    [HttpPut("sync-sources/{id:guid}")]
    public async Task<ActionResult<SyncSourceDto>> UpdateSyncSource(Guid id, [FromBody] SyncSourceInput input, CancellationToken cancellationToken) =>
        await _sync.UpdateAsync(id, input, cancellationToken);

    [HttpDelete("sync-sources/{id:guid}")]
    public async Task<IActionResult> DeleteSyncSource(Guid id, CancellationToken cancellationToken)
    {
        await _sync.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPost("sync-sources/test")]
    public async Task<ActionResult<SyncTestResultDto>> TestSyncSource([FromBody] SyncSourceInput input, CancellationToken cancellationToken) =>
        await _sync.TestAsync(input, cancellationToken);

    [HttpPost("sync-sources/{id:guid}/run")]
    public async Task<ActionResult<SyncRunDto>> RunSyncSource(Guid id, CancellationToken cancellationToken) =>
        await _sync.RunNowAsync(id, cancellationToken);

    [HttpGet("sync-sources/{id:guid}/runs")]
    public async Task<ActionResult<List<SyncRunDto>>> SyncRuns(Guid id, CancellationToken cancellationToken) =>
        await _sync.RunsAsync(id, cancellationToken);

    // ---------- custom fields, field rules ----------

    [HttpGet("custom-fields")]
    public async Task<ActionResult<List<CustomFieldDefDto>>> AllCustomFields(CancellationToken cancellationToken) =>
        await _customFields.GetAllAsync(cancellationToken);

    [HttpPost("custom-fields")]
    public async Task<ActionResult<CustomFieldDefDto>> CreateCustomField([FromBody] CustomFieldInput input, CancellationToken cancellationToken) =>
        await _customFields.CreateAsync(input, cancellationToken);

    [HttpPut("custom-fields/order")]
    public async Task<ActionResult<List<CustomFieldDefDto>>> ReorderCustomFields([FromBody] IdsInput input, CancellationToken cancellationToken) =>
        await _customFields.ReorderAsync(input, cancellationToken);

    [HttpPut("custom-fields/{id:guid}")]
    public async Task<ActionResult<CustomFieldDefDto>> UpdateCustomField(Guid id, [FromBody] CustomFieldInput input, CancellationToken cancellationToken) =>
        await _customFields.UpdateAsync(id, input, cancellationToken);

    [HttpDelete("custom-fields/{id:guid}")]
    public async Task<IActionResult> DeleteCustomField(Guid id, CancellationToken cancellationToken)
    {
        await _customFields.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpGet("field-rules")]
    public async Task<ActionResult<FieldRulesDto>> GetFieldRules(CancellationToken cancellationToken) =>
        await _customFields.GetFieldRulesAsync(cancellationToken);

    [HttpPut("field-rules")]
    public async Task<ActionResult<FieldRulesDto>> PutFieldRules([FromBody] FieldRulesDto input, CancellationToken cancellationToken) =>
        await _customFields.ReplaceFieldRulesAsync(input, cancellationToken);

    // ---------- entities, currencies, tags ----------

    [HttpGet("entities")]
    public async Task<ActionResult<List<LegalEntityDto>>> Entities(CancellationToken cancellationToken) =>
        await _directory.GetEntitiesAsync(cancellationToken);

    [HttpPost("entities")]
    public async Task<ActionResult<LegalEntityDto>> CreateEntity([FromBody] LegalEntityInput input, CancellationToken cancellationToken) =>
        await _directory.CreateEntityAsync(input, cancellationToken);

    [HttpPut("entities/{id:guid}")]
    public async Task<ActionResult<LegalEntityDto>> UpdateEntity(Guid id, [FromBody] LegalEntityInput input, CancellationToken cancellationToken) =>
        await _directory.UpdateEntityAsync(id, input, cancellationToken);

    [HttpGet("currencies")]
    public async Task<ActionResult<List<CurrencyDto>>> Currencies(CancellationToken cancellationToken) =>
        await _directory.GetCurrenciesAsync(cancellationToken);

    [HttpPut("currencies")]
    public async Task<ActionResult<List<CurrencyDto>>> PutCurrencies([FromBody] List<CurrencyDto> input, CancellationToken cancellationToken) =>
        await _directory.ReplaceCurrenciesAsync(input, cancellationToken);

    [HttpGet("tags")]
    public async Task<ActionResult<List<TagDto>>> AdminTags(CancellationToken cancellationToken) =>
        await _collaboration.GetAdminTagsAsync(cancellationToken);

    [HttpPost("tags")]
    public async Task<ActionResult<TagDto>> CreateTag([FromBody] TagInput input, CancellationToken cancellationToken) =>
        await _collaboration.CreateTagAsync(input, cancellationToken);

    [HttpPut("tags/{id:guid}")]
    public async Task<ActionResult<TagDto>> UpdateTag(Guid id, [FromBody] TagInput input, CancellationToken cancellationToken) =>
        await _collaboration.UpdateTagAsync(id, input, cancellationToken);

    [HttpDelete("tags/{id:guid}")]
    public async Task<IActionResult> DeleteTag(Guid id, CancellationToken cancellationToken)
    {
        await _collaboration.DeleteTagAsync(id, cancellationToken);
        return NoContent();
    }

    // ---------- users and access ----------

    [HttpGet("users")]
    public async Task<ActionResult<List<AdminUserDto>>> Users([FromQuery] string? q, [FromQuery] bool? active, CancellationToken cancellationToken) =>
        await _directory.GetUsersAsync(q, active, cancellationToken);

    [HttpPost("users")]
    public async Task<ActionResult<AdminUserDto>> CreateUser([FromBody] AdminUserInput input, CancellationToken cancellationToken) =>
        await _directory.CreateUserAsync(input, cancellationToken);

    [HttpPut("users/{id:guid}")]
    public async Task<ActionResult<AdminUserDto>> UpdateUser(Guid id, [FromBody] AdminUserInput input, CancellationToken cancellationToken) =>
        await _directory.UpdateUserAsync(id, input, cancellationToken);

    [HttpGet("users/{id:guid}/effective-access")]
    public async Task<ActionResult<EffectiveAccessDto>> EffectiveAccess(
        Guid id, [FromQuery] string? q, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken) =>
        await _directory.EffectiveAccessAsync(id, q, page, pageSize, cancellationToken);

    [HttpPost("reassign/preview")]
    public async Task<ActionResult<ReassignPreviewDto>> ReassignPreview([FromBody] ReassignInput input, CancellationToken cancellationToken) =>
        await _directory.PreviewReassignAsync(input, cancellationToken);

    [HttpPost("reassign")]
    public async Task<ActionResult<ReassignResultDto>> Reassign([FromBody] ReassignInput input, CancellationToken cancellationToken) =>
        await _directory.ReassignAsync(input, cancellationToken);

    // ---------- notifications, jobs, general ----------

    [HttpGet("notification-settings")]
    public async Task<ActionResult<NotificationSettingsDto>> NotificationSettings(CancellationToken cancellationToken) =>
        await _settings.GetNotificationSettingsAsync(cancellationToken);

    [HttpPut("notification-settings")]
    public async Task<ActionResult<NotificationSettingsDto>> PutNotificationSettings([FromBody] NotificationSettingsDto input, CancellationToken cancellationToken) =>
        await _settings.UpdateNotificationSettingsAsync(input, cancellationToken);

    [HttpPost("notification-settings/preview")]
    public ActionResult<TemplatePreviewDto> PreviewTemplate([FromBody] TemplatePreviewInput input) =>
        _settings.Preview(input);

    [HttpGet("jobs")]
    public async Task<ActionResult<List<JobInfoDto>>> Jobs(CancellationToken cancellationToken) =>
        await _settings.GetJobsAsync(cancellationToken);

    [HttpPut("jobs/{key}")]
    public async Task<ActionResult<JobInfoDto>> UpdateJob(string key, [FromBody] JobScheduleInput input, CancellationToken cancellationToken) =>
        await _settings.UpdateJobAsync(key, input, cancellationToken);

    [HttpPost("jobs/{key}/run")]
    public async Task<ActionResult<JobInfoDto>> RunJob(string key, CancellationToken cancellationToken) =>
        await _jobs.RunNowAsync(key, cancellationToken);

    [HttpGet("app-settings")]
    public async Task<ActionResult<AppSettingsDto>> AppSettings(CancellationToken cancellationToken) =>
        await _settings.GetAppSettingsAsync(cancellationToken);

    [HttpPut("app-settings")]
    public async Task<ActionResult<AppSettingsDto>> PutAppSettings([FromBody] AppSettingsDto input, CancellationToken cancellationToken) =>
        await _settings.UpdateAppSettingsAsync(input, cancellationToken);
}
