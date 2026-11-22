using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Services;
using Microsoft.Extensions.Configuration;

namespace Dafa.Contracts.Infrastructure.Providers;

/// <summary>
/// TEST MODE e-signature provider, fully working without any outside service. Nothing is sent to
/// anyone: signers are moved along with POST signature-requests/{id}/simulate (or the provider-neutral
/// webhook body), and the "signed document" is a generated PDF signature page that says so.
///
/// Because a simulated signature is not a signature, the provider can only be selected when
/// Signing:AllowSimulated is true. The default is true with Auth:Mode=Dev (test sites) and false
/// otherwise, so a production installation cannot be switched to it by accident.
/// </summary>
public sealed class SimulatedSignatureProvider : ISignatureProvider
{
    private readonly IConfiguration _configuration;

    public SimulatedSignatureProvider(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string Name => SignatureProviderNames.Simulated;

    public bool IsConfigured
    {
        get
        {
            var devMode = string.Equals((_configuration["Auth:Mode"] ?? "").Trim(), "Dev", StringComparison.OrdinalIgnoreCase);
            return _configuration.GetValue("Signing:AllowSimulated", devMode);
        }
    }

    public string? Account => "Simulated (no documents leave the app)";

    public Task<string> SendAsync(SignatureEnvelope envelope, CancellationToken cancellationToken = default) =>
        Task.FromResult("SIM-" + envelope.RequestId.ToString("N"));

    public Task<IReadOnlyList<SignatureProviderEvent>> GetEventsAsync(string reference, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SignatureProviderEvent>>(Array.Empty<SignatureProviderEvent>());

    /// <summary>A one-page PDF: what was signed (name and SHA-256 of each document) and who signed when.</summary>
    public Task<SignedFile> GetSignedDocumentAsync(SignatureEnvelope envelope, string reference, CancellationToken cancellationToken = default)
    {
        var culture = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            "SIMULATED SIGNATURE - test mode. This page is not a legally binding signature.",
            "",
            "Contract:   " + envelope.ContractNo + " - " + envelope.Title,
            "Reference:  " + reference,
            "Order:      " + (envelope.Sequential ? "Sequential" : "Parallel"),
            "",
            "Documents"
        };
        foreach (var document in envelope.Documents)
        {
            lines.Add("  " + document.Name + " (" + document.Content.Length.ToString(culture) + " bytes)");
            lines.Add("    SHA-256 " + SignatureService.Sha256Hex(document.Content));
        }
        lines.Add("");
        lines.Add("Signed by");
        foreach (var signer in envelope.Signers.OrderBy(s => s.Order))
        {
            var at = signer.SignedAt is null ? "-" : signer.SignedAt.Value.ToString("yyyy-MM-dd HH:mm 'UTC'", culture);
            lines.Add("  " + signer.Order.ToString(culture) + ". " + signer.Name + " <" + signer.Email + ">   " + at);
        }
        var name = envelope.Documents.Count > 0 ? envelope.Documents[0].Name : envelope.ContractNo;
        var file = new SignedFile(SignatureService.SignedFileName(name), SimplePdf.FromLines("Signature page - " + envelope.ContractNo, lines));
        return Task.FromResult(file);
    }

    public Task RemindAsync(string reference, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task CancelAsync(string reference, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public IReadOnlyList<SignatureProviderEvent> ParseWebhook(string body) => SignatureWebhook.ParseGeneric(body);
}

/// <summary>
/// DocuSign eSignature REST API v2.1. COMPILES, NEVER RUN: no call in this class has been made
/// against DocuSign. Before it is used: create the integration key, grant consent for JWT
/// ("signature impersonation"), run every method against the DocuSign demo environment and compare
/// with their current documentation.
///
/// Server configuration (never in the database or the API):
///   Signing:DocuSign:BaseUrl         e.g. https://eu.docusign.net/restapi (demo: https://demo.docusign.net/restapi)
///   Signing:DocuSign:AuthServer      account.docusign.com (demo: account-d.docusign.com)
///   Signing:DocuSign:AccountId       the API account id (GUID)
///   Signing:DocuSign:IntegrationKey  the app's client id
///   Signing:DocuSign:UserId          the API user to act as (GUID)
///   Signing:DocuSign:PrivateKeyPath  PEM file with the RSA private key of the integration key
/// Webhooks: DocuSign Connect (JSON, "SIM" event model) to POST /api/esign/webhooks/DocuSign with
/// HMAC enabled; the HMAC key is Signing:Webhook:Secret.
/// </summary>
public sealed class DocuSignSignatureProvider : ISignatureProvider
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private string? _token;
    private DateTime _tokenExpiresUtc;

    public DocuSignSignatureProvider(IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    public string Name => SignatureProviderNames.DocuSign;

    private string? Setting(string key) =>
        string.IsNullOrWhiteSpace(_configuration["Signing:DocuSign:" + key]) ? null : _configuration["Signing:DocuSign:" + key]!.Trim();

    public bool IsConfigured =>
        Setting("BaseUrl") is not null && Setting("AuthServer") is not null && Setting("AccountId") is not null
        && Setting("IntegrationKey") is not null && Setting("UserId") is not null && Setting("PrivateKeyPath") is not null;

    public string? Account => Setting("AccountId");

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        if (_token is not null && _tokenExpiresUtc > DateTime.UtcNow.AddMinutes(2))
        {
            return _token;
        }
        var authServer = Setting("AuthServer")!;
        using var rsa = RSA.Create();
        rsa.ImportFromPem(await File.ReadAllTextAsync(Setting("PrivateKeyPath")!, cancellationToken));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = JsonSerializer.Serialize(new Dictionary<string, object> { ["alg"] = "RS256", ["typ"] = "JWT" });
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = Setting("IntegrationKey")!,
            ["sub"] = Setting("UserId")!,
            ["aud"] = authServer,
            ["iat"] = now,
            ["exp"] = now + 3000,
            ["scope"] = "signature impersonation"
        });
        var unsigned = Base64Url(Encoding.UTF8.GetBytes(header)) + "." + Base64Url(Encoding.UTF8.GetBytes(payload));
        var assertion = unsigned + "." + Base64Url(rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        var client = _httpClientFactory.CreateClient("esign");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = assertion
        });
        using var response = await client.PostAsync("https://" + authServer + "/oauth/token", form, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        _token = json.RootElement.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("DocuSign returned no access token.");
        var expiresIn = json.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds) ? seconds : 3000;
        _tokenExpiresUtc = DateTime.UtcNow.AddSeconds(expiresIn);
        return _token;
    }

    private string Url(string path) => Setting("BaseUrl")!.TrimEnd('/') + "/v2.1/accounts/" + Setting("AccountId") + path;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("esign");
        using var request = new HttpRequestMessage(method, Url(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(cancellationToken));
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }
        var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            response.Dispose();
            throw new InvalidOperationException("DocuSign answered HTTP " + (int)response.StatusCode + ": " + (text.Length > 300 ? text.Substring(0, 300) : text));
        }
        return response;
    }

    public async Task<string> SendAsync(SignatureEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var documents = new List<object>();
        var number = 0;
        foreach (var document in envelope.Documents)
        {
            number++;
            documents.Add(new Dictionary<string, object>
            {
                ["documentBase64"] = Convert.ToBase64String(document.Content),
                ["name"] = document.Name,
                ["fileExtension"] = Path.GetExtension(document.Name).TrimStart('.').ToLowerInvariant(),
                ["documentId"] = number.ToString(CultureInfo.InvariantCulture)
            });
        }
        var signers = new List<object>();
        var recipient = 0;
        foreach (var signer in envelope.Signers.OrderBy(s => s.Order))
        {
            recipient++;
            signers.Add(new Dictionary<string, object>
            {
                ["email"] = signer.Email,
                ["name"] = signer.Name,
                ["recipientId"] = recipient.ToString(CultureInfo.InvariantCulture),
                // Parallel: everyone has routing order 1. Sequential: the signer's own order.
                ["routingOrder"] = (envelope.Sequential ? signer.Order : 1).ToString(CultureInfo.InvariantCulture)
            });
        }
        var body = new Dictionary<string, object>
        {
            ["emailSubject"] = "Please sign: " + envelope.ContractNo + " " + envelope.Title,
            ["emailBlurb"] = envelope.Message,
            ["status"] = "sent",
            ["documents"] = documents,
            ["recipients"] = new Dictionary<string, object> { ["signers"] = signers }
        };
        using var response = await SendAsync(HttpMethod.Post, "/envelopes", body, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.GetProperty("envelopeId").GetString() ?? throw new InvalidOperationException("DocuSign returned no envelope id.");
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTime? Time(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value)
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : null;

    /// <summary>Events from an envelope with its recipients (the answer of GET envelopes/{id}?include=recipients, or a Connect envelopeSummary).</summary>
    private static List<SignatureProviderEvent> EventsFrom(string reference, JsonElement envelope)
    {
        var events = new List<SignatureProviderEvent>();
        if (envelope.TryGetProperty("recipients", out var recipients) && recipients.TryGetProperty("signers", out var signers) && signers.ValueKind == JsonValueKind.Array)
        {
            foreach (var signer in signers.EnumerateArray())
            {
                var email = Text(signer, "email");
                switch ((Text(signer, "status") ?? "").ToLowerInvariant())
                {
                    case "delivered":
                        events.Add(new SignatureProviderEvent { Reference = reference, Kind = SignatureProviderEvent.Viewed, SignerEmail = email, At = Time(Text(signer, "deliveredDateTime")) });
                        break;
                    case "completed":
                    case "signed":
                        events.Add(new SignatureProviderEvent { Reference = reference, Kind = SignatureProviderEvent.Signed, SignerEmail = email, At = Time(Text(signer, "signedDateTime")) });
                        break;
                    case "declined":
                        events.Add(new SignatureProviderEvent
                        {
                            Reference = reference, Kind = SignatureProviderEvent.Declined, SignerEmail = email,
                            At = Time(Text(signer, "declinedDateTime")), Reason = Text(signer, "declinedReason")
                        });
                        break;
                }
            }
        }
        switch ((Text(envelope, "status") ?? "").ToLowerInvariant())
        {
            case "completed":
                events.Add(new SignatureProviderEvent { Reference = reference, Kind = SignatureProviderEvent.Completed, At = Time(Text(envelope, "completedDateTime")) });
                break;
            case "voided":
                events.Add(new SignatureProviderEvent { Reference = reference, Kind = SignatureProviderEvent.Cancelled, Reason = Text(envelope, "voidedReason") });
                break;
            case "declined":
                if (!events.Any(e => e.Kind == SignatureProviderEvent.Declined))
                {
                    events.Add(new SignatureProviderEvent { Reference = reference, Kind = SignatureProviderEvent.Declined });
                }
                break;
        }
        return events;
    }

    public async Task<IReadOnlyList<SignatureProviderEvent>> GetEventsAsync(string reference, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/envelopes/" + Uri.EscapeDataString(reference) + "?include=recipients", null, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return EventsFrom(reference, json.RootElement);
    }

    public async Task<SignedFile> GetSignedDocumentAsync(SignatureEnvelope envelope, string reference, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/envelopes/" + Uri.EscapeDataString(reference) + "/documents/combined", null, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var name = envelope.Documents.Count > 0 ? envelope.Documents[0].Name : envelope.ContractNo;
        return new SignedFile(SignatureService.SignedFileName(name), bytes);
    }

    public async Task RemindAsync(string reference, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Put, "/envelopes/" + Uri.EscapeDataString(reference) + "?resend_envelope=true", new Dictionary<string, object>(), cancellationToken);
    }

    public async Task CancelAsync(string reference, string reason, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object> { ["status"] = "voided", ["voidedReason"] = string.IsNullOrWhiteSpace(reason) ? "Cancelled" : reason };
        using var response = await SendAsync(HttpMethod.Put, "/envelopes/" + Uri.EscapeDataString(reference), body, cancellationToken);
    }

    /// <summary>DocuSign Connect, JSON: { "event": "envelope-completed", "data": { "envelopeId": "...", "envelopeSummary": { ... } } }.</summary>
    public IReadOnlyList<SignatureProviderEvent> ParseWebhook(string body)
    {
        var events = new List<SignatureProviderEvent>();
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data))
            {
                return events;
            }
            var reference = Text(data, "envelopeId");
            if (string.IsNullOrWhiteSpace(reference))
            {
                return events;
            }
            if (data.TryGetProperty("envelopeSummary", out var summary) && summary.ValueKind == JsonValueKind.Object)
            {
                events.AddRange(EventsFrom(reference!, summary));
            }
            if (events.Count == 0)
            {
                switch ((Text(root, "event") ?? "").ToLowerInvariant())
                {
                    case "envelope-completed":
                        events.Add(new SignatureProviderEvent { Reference = reference!, Kind = SignatureProviderEvent.Completed });
                        break;
                    case "envelope-declined":
                        events.Add(new SignatureProviderEvent { Reference = reference!, Kind = SignatureProviderEvent.Declined });
                        break;
                    case "envelope-voided":
                        events.Add(new SignatureProviderEvent { Reference = reference!, Kind = SignatureProviderEvent.Cancelled });
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // Not a Connect message: no events.
        }
        return events;
    }
}

/// <summary>
/// Penneo. COMPILES, NEVER RUN, and written from the public description of the Penneo API only:
/// the routes and field names below (case files, documents, signers, signing requests) must be
/// checked against Penneo's current API documentation and their sandbox before this provider is
/// selected. It is deliberately small and isolated so that it can be corrected in one place.
///
/// Server configuration (never in the database or the API):
///   Signing:Penneo:BaseUrl    e.g. https://app.penneo.com/api/v3 (sandbox: https://sandbox.penneo.com/api/v3)
///   Signing:Penneo:ApiKey     API key of the integration user
///   Signing:Penneo:ApiSecret  API secret of the integration user
/// Webhooks: a Penneo webhook subscription for case file events to POST /api/esign/webhooks/Penneo
/// with the header X-Dafa-Webhook-Secret set to Signing:Webhook:Secret.
/// </summary>
public sealed class PenneoSignatureProvider : ISignatureProvider
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;

    public PenneoSignatureProvider(IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    public string Name => SignatureProviderNames.Penneo;

    private string? Setting(string key) =>
        string.IsNullOrWhiteSpace(_configuration["Signing:Penneo:" + key]) ? null : _configuration["Signing:Penneo:" + key]!.Trim();

    public bool IsConfigured => Setting("BaseUrl") is not null && Setting("ApiKey") is not null && Setting("ApiSecret") is not null;

    public string? Account => Setting("ApiKey") is null ? null : "API key " + Setting("ApiKey")!.Substring(0, Math.Min(6, Setting("ApiKey")!.Length)) + "...";

    /// <summary>WSSE UsernameToken header: PasswordDigest = Base64(SHA1(nonce + created + secret)).</summary>
    private string Wsse()
    {
        var nonce = Guid.NewGuid().ToString("N");
        var created = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var digest = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(nonce + created + Setting("ApiSecret"))));
        return "UsernameToken Username=\"" + Setting("ApiKey") + "\", PasswordDigest=\"" + digest + "\", Nonce=\""
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(nonce)) + "\", Created=\"" + created + "\"";
    }

    private async Task<JsonDocument> CallAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("esign");
        using var request = new HttpRequestMessage(method, Setting("BaseUrl")!.TrimEnd('/') + path);
        request.Headers.TryAddWithoutValidation("Authorization", "WSSE profile=\"UsernameToken\"");
        request.Headers.TryAddWithoutValidation("X-WSSE", Wsse());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }
        using var response = await client.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Penneo answered HTTP " + (int)response.StatusCode + ": " + (text.Length > 300 ? text.Substring(0, 300) : text));
        }
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    private static string IdOf(JsonDocument json)
    {
        var root = json.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var id))
        {
            return id.ValueKind == JsonValueKind.Number ? id.GetRawText() : (id.GetString() ?? "");
        }
        throw new InvalidOperationException("Penneo returned no id.");
    }

    public async Task<string> SendAsync(SignatureEnvelope envelope, CancellationToken cancellationToken = default)
    {
        string caseFileId;
        using (var caseFile = await CallAsync(HttpMethod.Post, "/casefiles", new Dictionary<string, object>
        {
            ["title"] = envelope.ContractNo + " " + envelope.Title,
            ["metaData"] = envelope.RequestId.ToString()
        }, cancellationToken))
        {
            caseFileId = IdOf(caseFile);
        }
        var documentIds = new List<string>();
        foreach (var document in envelope.Documents)
        {
            using var created = await CallAsync(HttpMethod.Post, "/documents", new Dictionary<string, object>
            {
                ["caseFileId"] = caseFileId,
                ["title"] = document.Name,
                ["pdfFile"] = Convert.ToBase64String(document.Content),
                ["type"] = "signable"
            }, cancellationToken);
            documentIds.Add(IdOf(created));
        }
        foreach (var signer in envelope.Signers.OrderBy(s => s.Order))
        {
            string signerId;
            using (var created = await CallAsync(HttpMethod.Post, "/casefiles/" + caseFileId + "/signers", new Dictionary<string, object> { ["name"] = signer.Name }, cancellationToken))
            {
                signerId = IdOf(created);
            }
            foreach (var documentId in documentIds)
            {
                using var line = await CallAsync(HttpMethod.Post, "/documents/" + documentId + "/signaturelines", new Dictionary<string, object>
                {
                    ["role"] = "Signer",
                    ["signOrder"] = envelope.Sequential ? signer.Order : 0,
                    ["signerId"] = signerId
                }, cancellationToken);
            }
            using var signingRequest = await CallAsync(HttpMethod.Patch, "/casefiles/" + caseFileId + "/signers/" + signerId + "/signingrequests", new Dictionary<string, object>
            {
                ["email"] = signer.Email,
                ["emailSubject"] = "Please sign: " + envelope.ContractNo + " " + envelope.Title,
                ["emailText"] = envelope.Message
            }, cancellationToken);
        }
        using var sent = await CallAsync(HttpMethod.Patch, "/casefiles/" + caseFileId + "/send", null, cancellationToken);
        return caseFileId;
    }

    public async Task<IReadOnlyList<SignatureProviderEvent>> GetEventsAsync(string reference, CancellationToken cancellationToken = default)
    {
        var events = new List<SignatureProviderEvent>();
        using var json = await CallAsync(HttpMethod.Get, "/casefiles/" + Uri.EscapeDataString(reference), null, cancellationToken);
        // Penneo case file status: 0 new, 1 pending, 2 rejected, 3 deleted, 4 signed, 5 completed.
        if (json.RootElement.TryGetProperty("status", out var status) && status.TryGetInt32(out var code))
        {
            if (code == 5 || code == 4) events.Add(new SignatureProviderEvent { Reference = reference, Kind = SignatureProviderEvent.Completed });
            else if (code == 2) events.Add(new SignatureProviderEvent { Reference = reference, Kind = SignatureProviderEvent.Declined });
            else if (code == 3) events.Add(new SignatureProviderEvent { Reference = reference, Kind = SignatureProviderEvent.Cancelled });
        }
        return events;
    }

    public async Task<SignedFile> GetSignedDocumentAsync(SignatureEnvelope envelope, string reference, CancellationToken cancellationToken = default)
    {
        using var documents = await CallAsync(HttpMethod.Get, "/casefiles/" + Uri.EscapeDataString(reference) + "/documents", null, cancellationToken);
        if (documents.RootElement.ValueKind != JsonValueKind.Array || documents.RootElement.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Penneo returned no documents for the case file.");
        }
        var first = documents.RootElement[0];
        var documentId = first.TryGetProperty("id", out var id) ? (id.ValueKind == JsonValueKind.Number ? id.GetRawText() : id.GetString()) : null;
        using var content = await CallAsync(HttpMethod.Get, "/documents/" + documentId + "/content", null, cancellationToken);
        var base64 = content.RootElement.ValueKind == JsonValueKind.Object && content.RootElement.TryGetProperty("pdfFile", out var pdf) ? pdf.GetString() : null;
        if (string.IsNullOrEmpty(base64))
        {
            throw new InvalidOperationException("Penneo returned no signed PDF.");
        }
        var name = envelope.Documents.Count > 0 ? envelope.Documents[0].Name : envelope.ContractNo;
        return new SignedFile(SignatureService.SignedFileName(name), Convert.FromBase64String(base64));
    }

    public async Task RemindAsync(string reference, CancellationToken cancellationToken = default)
    {
        using var json = await CallAsync(HttpMethod.Patch, "/casefiles/" + Uri.EscapeDataString(reference) + "/remind", null, cancellationToken);
    }

    public async Task CancelAsync(string reference, string reason, CancellationToken cancellationToken = default)
    {
        using var json = await CallAsync(HttpMethod.Delete, "/casefiles/" + Uri.EscapeDataString(reference), null, cancellationToken);
    }

    /// <summary>{ "topic": "casefile", "eventType": "completed" | "rejected" | "expired" | "deleted", "payload": { "id": 123 } }.</summary>
    public IReadOnlyList<SignatureProviderEvent> ParseWebhook(string body)
    {
        var events = new List<SignatureProviderEvent>();
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("payload", out var payload) || !payload.TryGetProperty("id", out var id))
            {
                return events;
            }
            var reference = id.ValueKind == JsonValueKind.Number ? id.GetRawText() : (id.GetString() ?? "");
            var type = root.TryGetProperty("eventType", out var eventType) && eventType.ValueKind == JsonValueKind.String ? eventType.GetString() : "";
            string? kind = (type ?? "").ToLowerInvariant() switch
            {
                "completed" => SignatureProviderEvent.Completed,
                "signed" => SignatureProviderEvent.Completed,
                "rejected" => SignatureProviderEvent.Declined,
                "expired" => SignatureProviderEvent.Expired,
                "deleted" => SignatureProviderEvent.Cancelled,
                _ => null
            };
            if (kind is not null && reference.Length > 0)
            {
                events.Add(new SignatureProviderEvent { Reference = reference, Kind = kind });
            }
        }
        catch (JsonException)
        {
            // Not a Penneo message: no events.
        }
        return events;
    }
}
