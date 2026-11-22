using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dafa.Contracts.Infrastructure.Providers;

/// <summary>TEST MODE (Analyzer:Provider=Heuristic). Text extraction plus regex/keyword rules; no external calls.</summary>
public sealed class HeuristicContractAnalyzer : IContractAnalyzer
{
    public async Task<ContractAnalysis> AnalyzeAsync(string fileName, byte[] content, CancellationToken cancellationToken = default)
    {
        var extracted = await TextExtractor.ExtractAsync(fileName, content, cancellationToken);
        return TextExtractor.ToAnalysis(extracted, fileName);
    }
}

/// <summary>
/// PRODUCTION MODE (Analyzer:Provider=AzureOpenAI). NOT exercised in the test environment.
/// Sends the extracted text to an Azure OpenAI chat deployment and expects a JSON answer.
/// Falls back to the heuristic analyzer on any failure. Settings: AzureOpenAI:Endpoint,
/// AzureOpenAI:Deployment, AzureOpenAI:ApiKey, AzureOpenAI:ApiVersion.
/// </summary>
public sealed class AzureOpenAiContractAnalyzer : IContractAnalyzer
{
    private const string SystemPrompt =
        "You extract facts from commercial contracts for DAFA Group, a Danish manufacturer. "
        + "Answer with one JSON object and nothing else: "
        + "{\"suggestions\":[{\"field\":string,\"value\":string,\"confidence\":number between 0 and 1}],"
        + "\"summary\":string (at most 3 sentences),"
        + "\"deviations\":[{\"severity\":\"High\"|\"Medium\"|\"Low\"|\"None\",\"text\":string,\"standard\":string}]}. "
        + "Allowed fields: title, counterpartyName, startDate, endDate (dates as yyyy-MM-dd), noticePeriodDays, "
        + "autoRenew (true/false), renewalMonths, governingLaw, liabilityCap, changeOfControl (true/false), "
        + "paymentTerms, priceAdjustment, currency, annualValue, totalValue, exclusivity. Only include fields found in the text. "
        + "DAFA standard for deviations: liability cap of at least 12 months' fees; automatic renewal for at most 12 months "
        + "at a time; price changes only by a published index; governing law stated (Danish law preferred).";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AzureOpenAiContractAnalyzer> _logger;

    public AzureOpenAiContractAnalyzer(
        IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<AzureOpenAiContractAnalyzer> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ContractAnalysis> AnalyzeAsync(string fileName, byte[] content, CancellationToken cancellationToken = default)
    {
        var extracted = await TextExtractor.ExtractAsync(fileName, content, cancellationToken);
        var fallback = TextExtractor.ToAnalysis(extracted, fileName);
        if (string.IsNullOrWhiteSpace(extracted.Text))
        {
            return fallback;
        }

        try
        {
            var endpoint = (_configuration["AzureOpenAI:Endpoint"] ?? "").TrimEnd('/');
            var deployment = _configuration["AzureOpenAI:Deployment"];
            var apiKey = _configuration["AzureOpenAI:ApiKey"];
            var apiVersion = _configuration["AzureOpenAI:ApiVersion"] ?? "2024-06-01";
            if (endpoint.Length == 0 || string.IsNullOrWhiteSpace(deployment) || string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogWarning("Azure OpenAI is not configured; using heuristic extraction");
                return fallback;
            }

            var text = extracted.Text.Length > 60000 ? extracted.Text.Substring(0, 60000) : extracted.Text;
            var payload = new Dictionary<string, object>
            {
                ["messages"] = new object[]
                {
                    new Dictionary<string, string> { ["role"] = "system", ["content"] = SystemPrompt },
                    new Dictionary<string, string> { ["role"] = "user", ["content"] = "File name: " + fileName + "\n\n" + text }
                },
                ["temperature"] = 0,
                ["response_format"] = new Dictionary<string, string> { ["type"] = "json_object" }
            };

            var client = _httpClientFactory.CreateClient("azure-openai");
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                endpoint + "/openai/deployments/" + Uri.EscapeDataString(deployment) + "/chat/completions?api-version=" + Uri.EscapeDataString(apiVersion));
            request.Headers.Add("api-key", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var answerText = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(answerText))
            {
                return fallback;
            }
            var answer = JsonSerializer.Deserialize<AzureOpenAiAnswer>(answerText, JsonDefaults.Options);
            if (answer is null)
            {
                return fallback;
            }

            return new ContractAnalysis
            {
                Pages = extracted.Pages,
                Suggestions = (answer.Suggestions ?? new List<FieldSuggestion>())
                    .Where(s => !string.IsNullOrWhiteSpace(s.Field) && !string.IsNullOrWhiteSpace(s.Value))
                    .ToList(),
                Summary = string.IsNullOrWhiteSpace(answer.Summary) ? fallback.Summary : answer.Summary!,
                Deviations = answer.Deviations ?? fallback.Deviations,
                Note = fallback.Note
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure OpenAI analysis failed; using heuristic extraction");
            return fallback;
        }
    }
}

/// <summary>The JSON object the model is asked to return.</summary>
public sealed class AzureOpenAiAnswer
{
    public List<FieldSuggestion>? Suggestions { get; set; }
    public string? Summary { get; set; }
    public List<Deviation>? Deviations { get; set; }
}
