using System.Globalization;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

public static class NotificationRouting
{
    public static List<string> SplitList(string? text) =>
        (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();

    public static List<Guid> SplitIds(string? text)
    {
        var result = new List<Guid>();
        foreach (var part in SplitList(text))
        {
            if (Guid.TryParse(part, out var id) && !result.Contains(id))
            {
                result.Add(id);
            }
        }
        return result;
    }

    /// <summary>App path for a notification about a contract or event.</summary>
    public static string? LinkFor(string eventName, Guid? contractId)
    {
        switch (eventName)
        {
            case NotificationCatalog.ApprovalRequested:
            case NotificationCatalog.ApprovalDelegated:
                return "/approvals";
            case NotificationCatalog.ImportFinished:
            case NotificationCatalog.SyncFailed:
                return "/settings/imports";
            case NotificationCatalog.Mention:
                return contractId is null ? null : "/contracts/" + contractId.Value + "#comments";
            default:
                return contractId is null ? null : "/contracts/" + contractId.Value;
        }
    }
}

/// <summary>A user's per-event channel switches (UserPreference.EventsJson).</summary>
public sealed class PreferenceSwitch
{
    public string Event { get; set; } = "";
    public bool Email { get; set; } = true;
    public bool Teams { get; set; } = true;
    public bool InApp { get; set; } = true;
}

public static class PreferenceReader
{
    public static List<PreferenceSwitch> Switches(UserPreference? preference) =>
        preference is null ? new List<PreferenceSwitch>() : JsonDefaults.ReadList<PreferenceSwitch>(preference.EventsJson);

    /// <summary>Whether the user allows a channel for an event (true when they never said otherwise).</summary>
    public static bool Allows(UserPreference? preference, string eventName, string channel)
    {
        var item = Switches(preference).FirstOrDefault(s => s.Event == eventName);
        if (item is null)
        {
            return true;
        }
        return channel switch
        {
            "Email" => item.Email,
            "Teams" => item.Teams,
            "InApp" => item.InApp,
            _ => true
        };
    }
}

/// <summary>
/// The INotifier of the application (release 3). For every notification it applies the admin
/// notification settings (enabled, channels, recipient rules, extra recipients, subject and body
/// templates with {{placeholders}}) and each recipient's preferences, then delivers: Email through
/// the mail channel (Outbox or Graph), Teams as an outbox row (stub), InApp as an in-app
/// notification. Contract notifications only go to people who can see the contract (administrators
/// always can). Like every notifier it only adds rows; the caller saves.
/// Without a settings row for an event, the text written by the code is sent unchanged, so
/// release 2 behaviour (and its outbox texts) stays the same.
/// </summary>
public class NotificationDispatcher : INotifier
{
    private readonly IAppDbContext _db;
    private readonly IMailChannel _mail;
    private readonly IClock _clock;
    private readonly ICurrentUser _currentUser;
    private readonly AppLinks _links;
    private Dictionary<string, NotificationSetting?>? _settings;
    private readonly Dictionary<Guid, UserPreference?> _preferences = new();
    private AppSettingsDto? _appSettings;

    public NotificationDispatcher(IAppDbContext db, IMailChannel mail, IClock clock, ICurrentUser currentUser, AppLinks? links = null)
    {
        _db = db;
        _mail = mail;
        _clock = clock;
        _currentUser = currentUser;
        _links = links ?? new AppLinks();
    }

    private async Task<NotificationSetting?> SettingAsync(string eventName, CancellationToken cancellationToken)
    {
        if (_settings is null)
        {
            var rows = await _db.NotificationSettings.AsNoTracking().ToListAsync(cancellationToken);
            _settings = new Dictionary<string, NotificationSetting?>();
            foreach (var row in rows)
            {
                _settings[row.EventType] = row;
            }
        }
        return _settings.TryGetValue(eventName, out var setting) ? setting : null;
    }

    private async Task<UserPreference?> PreferenceAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!_preferences.TryGetValue(userId, out var preference))
        {
            preference = await _db.UserPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
            _preferences[userId] = preference;
        }
        return preference;
    }

    public Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        var eventName = NotificationCatalog.EventFor(notification.Kind);
        return DispatchAsync(eventName, notification.Kind, notification.ContractId, new[] { notification.RecipientId },
            notification.Subject, notification.Body, notification.Data, cancellationToken);
    }

    /// <summary>
    /// Sends one event to its natural recipients (the people the code chose: reminder recipient,
    /// approver, mentioned users, ...) and to whoever the event's recipient rules add. Returns the
    /// e-mail delivery result for the natural recipients (true when no e-mail was due).
    /// </summary>
    public async Task<bool> DispatchAsync(
        string eventName, string kind, Guid? contractId, IReadOnlyCollection<Guid> naturalRecipients,
        string defaultSubject, string defaultBody, IReadOnlyDictionary<string, string>? data,
        CancellationToken cancellationToken = default)
    {
        var info = NotificationCatalog.Find(eventName);
        if (info is null)
        {
            // Not a configurable event: straight to the mail channel, as before.
            var allOk = true;
            foreach (var recipient in naturalRecipients)
            {
                allOk &= await _mail.SendAsync(new Notification(recipient, kind, defaultSubject, defaultBody, contractId, data), cancellationToken);
            }
            return allOk;
        }
        var setting = await SettingAsync(eventName, cancellationToken);
        if (setting is not null && !setting.Enabled)
        {
            return true;
        }
        var channels = setting is null ? info.Channels.ToList() : NotificationRouting.SplitList(setting.Channels);
        var rules = setting is null ? info.Recipients.ToList() : NotificationRouting.SplitList(setting.Recipients);

        Contract? contract = null;
        if (contractId is not null)
        {
            var id = contractId.Value;
            contract = _db.Contracts.Local.FirstOrDefault(c => c.Id == id)
                ?? await _db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        }

        // Recipients: the natural ones when the event's own rule is selected, plus the other rules.
        var recipients = new List<Guid>();
        void Add(Guid? userId)
        {
            if (userId is not null && !recipients.Contains(userId.Value))
            {
                recipients.Add(userId.Value);
            }
        }
        if (rules.Contains(info.NaturalRecipient))
        {
            foreach (var natural in naturalRecipients)
            {
                Add(natural);
            }
        }
        if (contract is not null)
        {
            foreach (var rule in rules)
            {
                switch (rule)
                {
                    case "ReminderRecipient": Add(ContractPeople.ReminderRecipientId(contract)); break;
                    case "Owner": Add(contract.OwnerId); break;
                    case "Delegate": Add(contract.DelegateId); break;
                    case "Responsible":
                        if (info.NaturalRecipient != "Responsible") Add(contract.ResponsibleId);
                        break;
                    case "Watchers":
                        if (info.NaturalRecipient != "Watchers")
                        {
                            var contractKey = contract.Id;
                            foreach (var watcher in await _db.ContractWatchers.AsNoTracking().Where(w => w.ContractId == contractKey).Select(w => w.UserId).ToListAsync(cancellationToken))
                            {
                                Add(watcher);
                            }
                        }
                        break;
                }
            }
        }
        if (rules.Contains("Admins") && info.NaturalRecipient != "Admins")
        {
            var activeUsers = await _db.Users.AsNoTracking().Where(u => u.IsActive).ToListAsync(cancellationToken);
            foreach (var admin in activeUsers.Where(u => (u.Roles & AppRole.Admin) == AppRole.Admin))
            {
                Add(admin.Id);
            }
        }
        if (setting is not null)
        {
            foreach (var extra in NotificationRouting.SplitIds(setting.ExtraRecipientIds))
            {
                Add(extra);
            }
        }

        var actorId = _currentUser.UserId;
        var values = await BuildValuesAsync(contract, data, cancellationToken);
        var naturalOk = true;
        foreach (var recipientId in recipients)
        {
            var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == recipientId, cancellationToken);
            if (user is null || !user.IsActive)
            {
                continue;
            }
            // People added by rules must be able to see the contract; natural recipients were chosen by the code.
            var isNatural = naturalRecipients.Contains(recipientId);
            if (contract is not null && !isNatural && !await ContractAccessService.CanUserReadAsync(_db, recipientId, contract, cancellationToken))
            {
                continue;
            }
            var preference = await PreferenceAsync(recipientId, cancellationToken);
            values["recipient.name"] = user.Name;
            var subject = setting?.SubjectTemplate is null ? defaultSubject : NotificationCatalog.Render(setting.SubjectTemplate, values);
            var body = setting?.BodyTemplate is null ? defaultBody : NotificationCatalog.Render(setting.BodyTemplate, values);

            if (channels.Contains("Email") && PreferenceReader.Allows(preference, eventName, "Email"))
            {
                var ok = await _mail.SendAsync(new Notification(recipientId, kind, subject, body, contractId, data), cancellationToken);
                if (isNatural)
                {
                    naturalOk &= ok;
                }
            }
            if (channels.Contains("Teams") && PreferenceReader.Allows(preference, eventName, "Teams"))
            {
                _db.OutboxMessages.Add(new OutboxMessage
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = _clock.UtcNow,
                    RecipientId = recipientId,
                    RecipientEmail = user.Email,
                    Kind = kind,
                    Channel = "Teams",
                    Subject = subject.Length > 400 ? subject.Substring(0, 400) : subject,
                    Body = body,
                    ContractId = contractId
                });
            }
            if (channels.Contains("InApp") && PreferenceReader.Allows(preference, eventName, "InApp") && recipientId != actorId)
            {
                AddInApp(recipientId, eventName, subject, body, contractId, NotificationRouting.LinkFor(eventName, contractId));
            }
        }
        return naturalOk;
    }

    public InAppNotification AddInApp(Guid userId, string eventName, string title, string body, Guid? contractId, string? link)
    {
        var row = new InAppNotification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Event = eventName,
            Title = title.Length > 400 ? title.Substring(0, 400) : title,
            Body = body,
            ContractId = contractId,
            Link = link,
            ActorName = string.IsNullOrWhiteSpace(_currentUser.UserName) ? null : _currentUser.UserName,
            CreatedAt = _clock.UtcNow
        };
        _db.Notifications.Add(row);
        return row;
    }

    private async Task<Dictionary<string, string>> BuildValuesAsync(Contract? contract, IReadOnlyDictionary<string, string>? data, CancellationToken cancellationToken)
    {
        _appSettings ??= await AppSettingsStore.LoadAsync(_db, cancellationToken);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app.name"] = _appSettings.AppName,
            ["actor.name"] = string.IsNullOrWhiteSpace(_currentUser.UserName) ? "System" : _currentUser.UserName!,
            ["recipient.name"] = ""
        };
        if (contract is not null)
        {
            var entityId = contract.EntityId;
            var entityName = await _db.LegalEntities.AsNoTracking().Where(e => e.Id == entityId).Select(e => e.Name).FirstOrDefaultAsync(cancellationToken);
            values["contract.title"] = contract.Title;
            values["contract.no"] = contract.ContractNo;
            values["contract.counterparty"] = contract.CounterpartyName;
            values["contract.entity"] = entityName ?? "";
            values["contract.url"] = _links.BaseUrl.TrimEnd('/') + "/contracts/" + contract.Id;
            var deadline = ContractCalculations.NoticeDeadline(contract);
            if (deadline is not null)
            {
                values["deadline"] = deadline.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
                values["days"] = (deadline.Value.DayNumber - _clock.Today.DayNumber).ToString(CultureInfo.InvariantCulture);
            }
        }
        if (data is not null)
        {
            foreach (var pair in data)
            {
                values[pair.Key] = pair.Value;
            }
        }
        return values;
    }
}
