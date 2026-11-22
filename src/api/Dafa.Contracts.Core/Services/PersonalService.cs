using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>GET app-config and GET|PUT me/preferences (CONTRACT_V3 section 7).</summary>
public class PersonalService
{
    public static readonly string[] DigestValues = { "Off", "Daily", "Weekly" };

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly AuditService _audit;

    public PersonalService(IAppDbContext db, ICurrentUser currentUser, IClock clock, AuditService audit)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
        _audit = audit;
    }

    /// <summary>Admin, group management or entity management may build data room exports.</summary>
    public static bool CanExportDataRoom(AccessProfile profile) =>
        profile.IsAdmin || profile.IsGroupManagement || profile.ManagedEntityIds.Count > 0;

    public async Task<AppConfigDto> GetAppConfigAsync(CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var settings = await AppSettingsStore.LoadAsync(_db, cancellationToken);
        var userId = profile.UserId;
        var preference = await _db.UserPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        var customFields = new CustomFieldService(_db, _audit, _currentUser);
        var tags = await _db.Tags.AsNoTracking().ToListAsync(cancellationToken);
        var currencies = await _db.Currencies.AsNoTracking().Where(c => c.IsActive).ToListAsync(cancellationToken);
        var rates = currencies.ToDictionary(c => c.Code, c => c.RateToDkk);
        if (rates.Count == 0)
        {
            foreach (var code in CurrencyRates.Currencies)
            {
                rates[code] = CurrencyRates.ToDkk(1m, code);
            }
        }
        return new AppConfigDto
        {
            AppName = settings.AppName,
            DateFormat = settings.DateFormat,
            NumberFormat = settings.NumberFormat,
            Features = settings.Features,
            SessionTimeoutMinutes = settings.SessionTimeoutMinutes,
            LandingPage = AppSettingsStore.ResolveLandingPage(settings, profile, preference?.LandingPage),
            DefaultEntityId = preference?.DefaultEntityId,
            DataQualityFields = settings.DataQualityFields,
            DdThresholdDkk = settings.DdThresholdDkk,
            CustomFields = await customFields.GetActiveAsync(cancellationToken),
            FieldRules = await customFields.GetFieldRulesAsync(cancellationToken),
            Tags = tags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(t => new TagRefDto { Id = t.Id, Name = t.Name, Color = t.Color }).ToList(),
            Rates = rates,
            CanExportDataRoom = CanExportDataRoom(profile)
        };
    }

    private async Task<UserPreferencesDto> ToDtoAsync(UserPreference? preference, CancellationToken cancellationToken)
    {
        var digestDefault = await AppSettingsStore.LoadDigestDefaultAsync(_db, cancellationToken);
        var switches = PreferenceReader.Switches(preference);
        var settings = await _db.NotificationSettings.AsNoTracking().ToListAsync(cancellationToken);
        var result = new UserPreferencesDto
        {
            Digest = preference?.Digest ?? digestDefault,
            DefaultEntityId = preference?.DefaultEntityId,
            LandingPage = preference?.LandingPage,
            AutoWatch = preference?.AutoWatch ?? false
        };
        foreach (var info in NotificationCatalog.All)
        {
            var row = settings.FirstOrDefault(s => s.EventType == info.Event);
            var adminChannels = row is null ? info.Channels.ToList() : NotificationRouting.SplitList(row.Channels);
            var item = switches.FirstOrDefault(s => s.Event == info.Event);
            result.Events.Add(new PreferenceEventDto
            {
                Event = info.Event,
                Label = info.Label,
                Email = adminChannels.Contains("Email") && (item?.Email ?? true),
                Teams = adminChannels.Contains("Teams") && (item?.Teams ?? true),
                InApp = adminChannels.Contains("InApp") && (item?.InApp ?? true)
            });
        }
        return result;
    }

    public async Task<UserPreferencesDto> GetPreferencesAsync(CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var userId = profile.UserId;
        var preference = await _db.UserPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        return await ToDtoAsync(preference, cancellationToken);
    }

    public async Task<UserPreferencesDto> UpdatePreferencesAsync(UserPreferencesDto input, CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var errors = new Dictionary<string, string[]>();
        if (input is null)
        {
            throw new InputValidationException("digest", "Send the preferences.");
        }
        if (!DigestValues.Contains(input.Digest)) ContractValidator.Add(errors, "digest", "Use Off, Daily or Weekly.");
        if (input.LandingPage is not null && !AppSettingsStore.LandingPages.Contains(input.LandingPage))
            ContractValidator.Add(errors, "landingPage", "Unknown landing page.");
        if (input.DefaultEntityId is not null)
        {
            var entityId = input.DefaultEntityId.Value;
            if (!await _db.LegalEntities.AnyAsync(e => e.Id == entityId && e.IsActive, cancellationToken))
                ContractValidator.Add(errors, "defaultEntityId", "Choose an active legal entity.");
        }
        foreach (var item in input.Events ?? new List<PreferenceEventDto>())
        {
            if (item is null || NotificationCatalog.Find(item.Event) is null)
                ContractValidator.Add(errors, "events", "Unknown event: " + item?.Event + ".");
        }
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }

        // A channel the admin has not enabled for an event is stored as false.
        var settings = await _db.NotificationSettings.AsNoTracking().ToListAsync(cancellationToken);
        var switches = new List<PreferenceSwitch>();
        foreach (var item in input.Events ?? new List<PreferenceEventDto>())
        {
            var info = NotificationCatalog.Find(item.Event)!;
            var row = settings.FirstOrDefault(s => s.EventType == info.Event);
            var adminChannels = row is null ? info.Channels.ToList() : NotificationRouting.SplitList(row.Channels);
            switches.Add(new PreferenceSwitch
            {
                Event = info.Event,
                Email = item.Email && adminChannels.Contains("Email"),
                Teams = item.Teams && adminChannels.Contains("Teams"),
                InApp = item.InApp && adminChannels.Contains("InApp")
            });
        }

        var userId = profile.UserId;
        var preference = await _db.UserPreferences.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        if (preference is null)
        {
            preference = new UserPreference { UserId = userId };
            _db.UserPreferences.Add(preference);
        }
        preference.Digest = input.Digest;
        preference.LandingPage = input.LandingPage;
        preference.DefaultEntityId = input.DefaultEntityId;
        preference.AutoWatch = input.AutoWatch;
        if (input.Events is not null)
        {
            preference.EventsJson = JsonDefaults.Write(switches);
        }
        preference.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(preference, cancellationToken);
    }
}
