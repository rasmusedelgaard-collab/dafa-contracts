using System.Security.Cryptography;
using System.Text;
using Dafa.Contracts.Api.Hosting;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Dtos;
using Dafa.Contracts.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dafa.Contracts.Api.Controllers;

/// <summary>The entity scope of a request: the repeated query parameter entityId (CONTRACT_V4 section 1).</summary>
public static class EntityScope
{
    /// <summary>Empty or absent = every entity the user can see; a malformed id is 400 on entityId.</summary>
    public static List<Guid> From(HttpRequest request)
    {
        return ContractFilterParser.IdsFromQuery(
            request.Query.Select(pair => new KeyValuePair<string, string[]>(pair.Key, pair.Value.Select(v => v ?? "").ToArray())),
            "entityId");
    }
}

/// <summary>Release 4 endpoints for every signed-in user (CONTRACT_V4 sections 2, 3, 5 and 8). All contract data is access scoped.</summary>
[Route("api")]
public class Release4Controller : ApiControllerBase
{
    private readonly DistinctValuesService _distinct;
    private readonly PickerService _pickers;
    private readonly TemplateCatalogService _templates;
    private readonly SignatureService _signatures;

    public Release4Controller(DistinctValuesService distinct, PickerService pickers, TemplateCatalogService templates, SignatureService signatures)
    {
        _distinct = distinct;
        _pickers = pickers;
        _templates = templates;
        _signatures = signatures;
    }

    // ---------- register header filters ----------

    /// <summary>The values of one register column under the current filter (the column's own filter left out), with counts.</summary>
    [HttpGet("contracts/distinct")]
    public async Task<ActionResult<DistinctValuesDto>> Distinct([FromQuery] string? column, [FromQuery] string? q, [FromQuery] string? limit, CancellationToken cancellationToken)
    {
        // limit is read as text so that an empty value is "the default" and never a model-binding 400.
        int? take = null;
        if (!string.IsNullOrWhiteSpace(limit))
        {
            if (!int.TryParse(limit.Trim(), out var parsed))
            {
                throw new InputValidationException("limit", "Use a whole number.");
            }
            take = parsed;
        }
        var filter = QueryFilter.From(Request);
        // "q" is the search inside the value list here, not the register's free-text search.
        filter.Q = null;
        return await _distinct.GetAsync(column, filter, q, take, cancellationToken);
    }

    // ---------- pickers ----------

    [HttpGet("people")]
    public async Task<ActionResult<List<PersonOptionDto>>> People(
        [FromQuery] string? q, [FromQuery] string? limit, [FromQuery] string? role, [FromQuery] string? includeInactive, CancellationToken cancellationToken)
    {
        int? take = null;
        if (!string.IsNullOrWhiteSpace(limit))
        {
            if (!int.TryParse(limit.Trim(), out var parsed))
            {
                throw new InputValidationException("limit", "Use a whole number.");
            }
            take = parsed;
        }
        var inactive = false;
        if (!string.IsNullOrWhiteSpace(includeInactive) && !bool.TryParse(includeInactive.Trim(), out inactive))
        {
            throw new InputValidationException("includeInactive", "Use true or false.");
        }
        return await _pickers.SearchPeopleAsync(q, take, role, inactive, cancellationToken);
    }

    /// <summary>Create a counterparty from the contract form, for the users the general setting counterpartyCreation allows.</summary>
    [HttpPost("counterparties")]
    public async Task<ActionResult<CounterpartyDto>> CreateCounterparty([FromBody] CounterpartyInput input, CancellationToken cancellationToken) =>
        await _pickers.CreateCounterpartyAsync(input, cancellationToken);

    // ---------- templates ----------

    [HttpGet("template-categories")]
    public async Task<ActionResult<List<TemplateCategoryDto>>> TemplateCategories(CancellationToken cancellationToken) =>
        await _templates.GetActiveCategoriesAsync(cancellationToken);

    [HttpGet("templates/{id:guid}/preview")]
    public async Task<ActionResult<TemplatePreviewInfoDto>> TemplatePreview(Guid id, CancellationToken cancellationToken) =>
        await _templates.PreviewAsync(id, cancellationToken);

    // ---------- e-signature ----------

    [HttpGet("contracts/{id:guid}/signature-permissions")]
    public async Task<ActionResult<SignaturePermissionsDto>> SignaturePermissions(Guid id, CancellationToken cancellationToken) =>
        await _signatures.GetPermissionsAsync(id, cancellationToken);

    [HttpGet("contracts/{id:guid}/signature-requests")]
    public async Task<ActionResult<List<SignatureRequestDto>>> SignatureRequests(Guid id, CancellationToken cancellationToken) =>
        await _signatures.ListAsync(id, cancellationToken);

    [HttpPost("contracts/{id:guid}/signature-requests")]
    public async Task<ActionResult<SignatureRequestDto>> CreateSignatureRequest(Guid id, [FromBody] SignatureRequestInput input, CancellationToken cancellationToken) =>
        await _signatures.CreateAndSendAsync(id, input, cancellationToken);

    [HttpGet("signature-requests/{id:guid}")]
    public async Task<ActionResult<SignatureRequestDto>> SignatureRequest(Guid id, CancellationToken cancellationToken) =>
        await _signatures.GetAsync(id, cancellationToken);

    [HttpPost("signature-requests/{id:guid}/resend")]
    public async Task<ActionResult<SignatureRequestDto>> ResendSignatureRequest(Guid id, CancellationToken cancellationToken) =>
        await _signatures.ResendAsync(id, cancellationToken);

    /// <summary>The body is optional: { "reason": "..." }.</summary>
    [HttpPost("signature-requests/{id:guid}/cancel")]
    public async Task<ActionResult<SignatureRequestDto>> CancelSignatureRequest(Guid id, CancellationToken cancellationToken)
    {
        string? reason = null;
        if (Request.ContentLength is > 0 && Request.HasJsonContentType())
        {
            var body = await Request.ReadFromJsonAsync<CancelSignatureInput>(cancellationToken);
            reason = body?.Reason;
        }
        return await _signatures.CancelAsync(id, reason, cancellationToken);
    }

    /// <summary>Only while the Simulated provider is selected (400 feature-off otherwise).</summary>
    [HttpPost("signature-requests/{id:guid}/simulate")]
    public async Task<ActionResult<SignatureRequestDto>> SimulateSignature(Guid id, [FromBody] SimulateSignatureInput input, CancellationToken cancellationToken) =>
        await _signatures.SimulateAsync(id, input, cancellationToken);
}

/// <summary>
/// The organisation's logo. Anonymous on purpose: it is shown on the sign-in screen. Only GET, no
/// contract data, and every file is served as an image with a policy that forbids scripts.
/// </summary>
[ApiController]
[Route("api")]
[AllowAnonymous]
public class BrandingController : ControllerBase
{
    /// <summary>An SVG served with this policy cannot run scripts or load anything, even when opened directly.</summary>
    public const string LogoContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'";

    private readonly BrandingService _branding;

    public BrandingController(BrandingService branding)
    {
        _branding = branding;
    }

    [HttpGet("branding")]
    public async Task<ActionResult<BrandingDto>> Get(CancellationToken cancellationToken)
    {
        Response.Headers["Cache-Control"] = "no-cache";
        return await _branding.GetAsync(cancellationToken);
    }

    /// <summary>
    /// variant = full (default), compact or dark. With the current version in v the answer may be
    /// cached for a year (the address changes with every upload); otherwise it is revalidated with the ETag.
    /// </summary>
    [HttpGet("branding/logo")]
    public async Task<IActionResult> Logo([FromQuery] string? variant, [FromQuery] string? v, CancellationToken cancellationToken)
    {
        var file = await _branding.GetFileAsync(variant, cancellationToken);
        if (file is null)
        {
            Response.Headers["Cache-Control"] = "no-cache";
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: "No logo has been uploaded.");
        }
        var etag = "\"" + file.ETag + "\"";
        Response.Headers["ETag"] = etag;
        Response.Headers["Cache-Control"] = string.Equals(v, file.ETag, StringComparison.Ordinal)
            ? "public, max-age=31536000, immutable"
            : "public, no-cache";
        Response.Headers["Content-Security-Policy"] = LogoContentSecurityPolicy;
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Disposition"] = "inline";
        if (Request.Headers["If-None-Match"].ToString().Split(',').Any(tag => tag.Trim() == etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }
        return File(file.Content, file.ContentType);
    }
}

/// <summary>
/// Webhooks of the e-signature providers. Anonymous (the provider has no session) and the ONLY
/// route exempt from the X-Requested-With rule; instead every call must prove the shared secret
/// Signing:Webhook:Secret. Without a configured secret the route does not exist (404).
/// The answer never says whether an envelope reference exists.
/// </summary>
[ApiController]
[Route("api/esign/webhooks")]
[AllowAnonymous]
public class SignatureWebhookController : ControllerBase
{
    public const string RoutePrefix = "/api/esign/webhooks";
    public const string SecretHeader = "X-Dafa-Webhook-Secret";
    public const string HexSignatureHeader = "X-Dafa-Signature";
    public const string DocuSignSignatureHeader = "X-DocuSign-Signature-1";
    public const int MinSecretLength = 24;
    public const int MaxBodyBytes = 1024 * 1024;

    private readonly SignatureService _signatures;
    private readonly IConfiguration _configuration;
    private readonly AccessCodeThrottle _throttle;

    public SignatureWebhookController(SignatureService signatures, IConfiguration configuration, AccessCodeThrottle throttle)
    {
        _signatures = signatures;
        _configuration = configuration;
        _throttle = throttle;
    }

    private static bool FixedEquals(byte[] a, byte[] b) => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);

    /// <summary>
    /// True when the caller proves the secret in one of three ways: the secret itself in
    /// X-Dafa-Webhook-Secret; an HMAC-SHA256 of the raw body, hex in X-Dafa-Signature; or the same
    /// HMAC in Base64 in X-DocuSign-Signature-1 (DocuSign Connect). Comparisons are constant-time.
    /// </summary>
    public static bool IsAuthentic(string secret, string body, string? secretHeader, string? hexSignature, string? base64Signature)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        if (!string.IsNullOrEmpty(secretHeader))
        {
            // Hash both sides so that the comparison time does not depend on their lengths.
            return FixedEquals(SHA256.HashData(key), SHA256.HashData(Encoding.UTF8.GetBytes(secretHeader.Trim())));
        }
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(body));
        if (!string.IsNullOrEmpty(hexSignature))
        {
            try
            {
                return FixedEquals(mac, Convert.FromHexString(hexSignature.Trim()));
            }
            catch (FormatException)
            {
                return false;
            }
        }
        if (!string.IsNullOrEmpty(base64Signature))
        {
            try
            {
                return FixedEquals(mac, Convert.FromBase64String(base64Signature.Trim()));
            }
            catch (FormatException)
            {
                return false;
            }
        }
        return false;
    }

    [HttpPost("{provider}")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Receive(string provider, CancellationToken cancellationToken)
    {
        var secret = (_configuration["Signing:Webhook:Secret"] ?? "").Trim();
        if (secret.Length < MinSecretLength)
        {
            // Not configured (or too short to be a secret): the route is closed.
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }
        var client = "webhook:" + (HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        if (_throttle.IsBlocked(client))
        {
            return Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many attempts");
        }
        string body;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
        {
            var buffer = new char[MaxBodyBytes + 1];
            var read = await reader.ReadBlockAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read > MaxBodyBytes)
            {
                return Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Payload too large");
            }
            body = new string(buffer, 0, read);
        }
        var authentic = IsAuthentic(
            secret, body,
            Request.Headers[SecretHeader].ToString(),
            Request.Headers[HexSignatureHeader].ToString(),
            Request.Headers[DocuSignSignatureHeader].ToString());
        if (!authentic)
        {
            _throttle.RecordFailure(client);
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized", detail: "The webhook secret is missing or wrong.");
        }
        await _signatures.HandleWebhookAsync(provider, body, cancellationToken);
        return NoContent();
    }
}

/// <summary>Release 4 administration (CONTRACT_V4 sections 5 to 9a). Role Admin only; every change is audited.</summary>
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public class AdminRelease4Controller : ApiControllerBase
{
    private readonly TemplateCatalogService _templates;
    private readonly BrandingService _branding;
    private readonly OrgLinkService _links;
    private readonly SignatureService _signatures;
    private readonly StorageService _storage;

    public AdminRelease4Controller(
        TemplateCatalogService templates, BrandingService branding, OrgLinkService links, SignatureService signatures, StorageService storage)
    {
        _templates = templates;
        _branding = branding;
        _links = links;
        _signatures = signatures;
        _storage = storage;
    }

    // ---------- template categories ----------

    [HttpGet("template-categories")]
    public async Task<ActionResult<List<TemplateCategoryDto>>> TemplateCategories(CancellationToken cancellationToken) =>
        await _templates.GetAllCategoriesAsync(cancellationToken);

    [HttpPost("template-categories")]
    public async Task<ActionResult<TemplateCategoryDto>> CreateTemplateCategory([FromBody] TemplateCategoryInput input, CancellationToken cancellationToken) =>
        await _templates.CreateCategoryAsync(input, cancellationToken);

    [HttpPut("template-categories/order")]
    public async Task<ActionResult<List<TemplateCategoryDto>>> OrderTemplateCategories([FromBody] IdsInput input, CancellationToken cancellationToken) =>
        await _templates.ReorderCategoriesAsync(input, cancellationToken);

    [HttpPut("template-categories/{id:guid}")]
    public async Task<ActionResult<TemplateCategoryDto>> UpdateTemplateCategory(Guid id, [FromBody] TemplateCategoryInput input, CancellationToken cancellationToken) =>
        await _templates.UpdateCategoryAsync(id, input, cancellationToken);

    [HttpDelete("template-categories/{id:guid}")]
    public async Task<IActionResult> DeleteTemplateCategory(Guid id, CancellationToken cancellationToken)
    {
        await _templates.DeleteCategoryAsync(id, cancellationToken);
        return NoContent();
    }

    // ---------- branding ----------

    /// <summary>Multipart file; variant = full (default), compact or dark. PNG, JPEG, WebP or SVG, at most 1 MB.</summary>
    [HttpPost("branding/logo")]
    [RequestSizeLimit(2L * 1024 * 1024)]
    public async Task<ActionResult<BrandingDto>> UploadLogo(IFormFile? file, [FromQuery] string? variant, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            throw new InputValidationException("file", "Choose a PNG, JPEG, WebP or SVG file.");
        }
        if (file.Length > LogoRules.MaxBytes)
        {
            throw new InputValidationException("file", "The logo is larger than 1 MB.");
        }
        var bytes = await ReadAllAsync(file, cancellationToken);
        return await _branding.UploadAsync(variant, file.FileName, bytes, cancellationToken);
    }

    [HttpDelete("branding/logo")]
    public async Task<ActionResult<BrandingDto>> DeleteLogo([FromQuery] string? variant, CancellationToken cancellationToken) =>
        await _branding.DeleteAsync(variant, cancellationToken);

    // ---------- SharePoint / Entra link ----------

    [HttpPost("entities/{id:guid}/test-link")]
    public async Task<ActionResult<LinkTestResultDto>> TestEntityLink(Guid id, CancellationToken cancellationToken) =>
        await _links.TestEntityAsync(id, cancellationToken);

    [HttpPost("departments/{id:guid}/test-link")]
    public async Task<ActionResult<LinkTestResultDto>> TestDepartmentLink(Guid id, CancellationToken cancellationToken) =>
        await _links.TestDepartmentAsync(id, cancellationToken);

    [HttpGet("access-mapping")]
    public async Task<ActionResult<AccessMappingDto>> AccessMapping(CancellationToken cancellationToken) =>
        await _links.GetAccessMappingAsync(cancellationToken);

    // ---------- e-signature settings ----------

    [HttpGet("esign-settings")]
    public async Task<ActionResult<ESignSettingsDto>> ESignSettings(CancellationToken cancellationToken) =>
        await _signatures.GetSettingsAsync(cancellationToken);

    [HttpPut("esign-settings")]
    public async Task<ActionResult<ESignSettingsDto>> PutESignSettings([FromBody] ESignSettingsDto input, CancellationToken cancellationToken) =>
        await _signatures.UpdateSettingsAsync(input, cancellationToken);

    // ---------- storage ----------

    [HttpGet("storage/health")]
    public async Task<ActionResult<StorageHealthDto>> StorageHealth(CancellationToken cancellationToken) =>
        await _storage.GetHealthAsync(cancellationToken);

    /// <summary>The latest report, or JSON null when the reconciliation has never run.</summary>
    [HttpGet("storage/reconciliation")]
    public async Task<IActionResult> Reconciliation(CancellationToken cancellationToken)
    {
        var report = await _storage.GetLatestAsync(cancellationToken);
        // A null object result would be 204 No Content; the contract asks for the JSON value null.
        return report is null ? Content("null", "application/json") : Ok(report);
    }

    [HttpPost("storage/reconciliation/run")]
    public async Task<ActionResult<ReconciliationReportDto>> RunReconciliation(CancellationToken cancellationToken) =>
        await _storage.RunAsync(cancellationToken);
}
