using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dafa.Contracts.Infrastructure.Providers;

/// <summary>TEST MODE (Notifications:Provider=Outbox). Notifications become rows shown in admin; nothing is sent.</summary>
public class OutboxNotifier : INotifier
{
    protected readonly IAppDbContext Db;
    protected readonly IClock Clock;

    public OutboxNotifier(IAppDbContext db, IClock clock)
    {
        Db = db;
        Clock = clock;
    }

    protected async Task<OutboxMessage> AddToOutboxAsync(Notification notification, CancellationToken cancellationToken)
    {
        var recipientId = notification.RecipientId;
        var email = await Db.Users.AsNoTracking()
            .Where(u => u.Id == recipientId)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(cancellationToken);
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            CreatedAt = Clock.UtcNow,
            RecipientId = notification.RecipientId,
            RecipientEmail = email ?? "",
            Kind = notification.Kind,
            Subject = notification.Subject.Length > 400 ? notification.Subject.Substring(0, 400) : notification.Subject,
            Body = notification.Body,
            ContractId = notification.ContractId
        };
        Db.OutboxMessages.Add(message);
        return message;
    }

    public virtual async Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        await AddToOutboxAsync(notification, cancellationToken);
        return true;
    }
}

/// <summary>
/// PRODUCTION MODE (Notifications:Provider=Graph). NOT exercised in the test environment.
/// Sends mail through Microsoft Graph from the mailbox in Notifications:Sender and keeps the
/// outbox row as a log (DeliveredAt is set on success). Teams messages are not implemented yet.
/// </summary>
public sealed class GraphMailNotifier : OutboxNotifier
{
    private const string GraphScope = "https://graph.microsoft.com/.default";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAccessTokenProvider _tokens;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GraphMailNotifier> _logger;

    public GraphMailNotifier(
        IAppDbContext db, IClock clock, IHttpClientFactory httpClientFactory, IAccessTokenProvider tokens,
        IConfiguration configuration, ILogger<GraphMailNotifier> logger) : base(db, clock)
    {
        _httpClientFactory = httpClientFactory;
        _tokens = tokens;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Returns false when the mail could not be sent, so the reminder job tries again later.</summary>
    public override async Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        var message = await AddToOutboxAsync(notification, cancellationToken);
        if (string.IsNullOrWhiteSpace(message.RecipientEmail))
        {
            // Nobody to send to; retrying would not help.
            return true;
        }

        try
        {
            var sender = _configuration["Notifications:Sender"];
            if (string.IsNullOrWhiteSpace(sender))
            {
                throw new InvalidOperationException("Notifications:Sender must be configured.");
            }
            var appUrl = (_configuration["App:BaseUrl"] ?? "").TrimEnd('/');
            var body = notification.Body;
            if (appUrl.Length > 0 && notification.ContractId is not null)
            {
                body += "\n\n" + appUrl + "/contracts/" + notification.ContractId.Value;
            }

            var payload = new Dictionary<string, object>
            {
                ["message"] = new Dictionary<string, object>
                {
                    ["subject"] = notification.Subject,
                    ["body"] = new Dictionary<string, string> { ["contentType"] = "Text", ["content"] = body },
                    ["toRecipients"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["emailAddress"] = new Dictionary<string, string> { ["address"] = message.RecipientEmail }
                        }
                    }
                },
                ["saveToSentItems"] = false
            };

            var client = _httpClientFactory.CreateClient("graph");
            var token = await _tokens.GetTokenAsync(GraphScope, cancellationToken);
            using var request = new HttpRequestMessage(
                HttpMethod.Post, "https://graph.microsoft.com/v1.0/users/" + Uri.EscapeDataString(sender) + "/sendMail");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            message.DeliveredAt = Clock.UtcNow;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The outbox row stays without DeliveredAt so the failure is visible in admin.
            _logger.LogError(ex, "Sending mail to {Recipient} failed", message.RecipientEmail);
            return false;
        }
    }
}
