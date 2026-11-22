using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Api.Auth;

public static class AuthModes
{
    public const string Dev = "Dev";
    public const string Entra = "Entra";

    /// <summary>Name of the OpenID Connect scheme registered in Entra mode (OpenIdConnectDefaults.AuthenticationScheme).</summary>
    public const string EntraChallengeScheme = "OpenIdConnect";

    /// <summary>
    /// Fails closed (H3): only the exact words Dev and Entra (any casing) are accepted. A missing,
    /// empty or misspelled value stops the app at startup instead of silently becoming Dev mode.
    /// </summary>
    public static string Parse(string? value)
    {
        var text = (value ?? "").Trim();
        if (string.Equals(text, Entra, StringComparison.OrdinalIgnoreCase))
        {
            return Entra;
        }
        if (string.Equals(text, Dev, StringComparison.OrdinalIgnoreCase))
        {
            return Dev;
        }
        throw new InvalidOperationException(
            "Auth:Mode must be set to 'Dev' or 'Entra' (found '" + text + "'). The application does not start without a valid sign-in mode.");
    }
}

/// <summary>Resolved once at startup from Auth:Mode and Auth:Dev:AccessCode.</summary>
public sealed class AuthSettings
{
    public AuthSettings(string mode, string? devAccessCode)
    {
        Mode = mode;
        DevAccessCode = string.IsNullOrWhiteSpace(devAccessCode) ? null : devAccessCode.Trim();
    }

    public string Mode { get; }
    public string? DevAccessCode { get; }
    public bool IsDev => Mode == AuthModes.Dev;
    public bool AccessCodeRequired => IsDev && DevAccessCode is not null;

    public const int MinAccessCodeLength = 12;

    /// <summary>
    /// Startup guard (H3). Dev sign-in lets a visitor act as any demo user, so outside the
    /// Development environment it is refused unless a shared access code of at least
    /// <see cref="MinAccessCodeLength"/> characters is configured. Throws when the rule is broken.
    /// </summary>
    public void EnsureSafeFor(bool isDevelopmentEnvironment)
    {
        if (!IsDev || isDevelopmentEnvironment)
        {
            return;
        }
        if (DevAccessCode is null || DevAccessCode.Length < MinAccessCodeLength)
        {
            throw new InvalidOperationException(
                "Auth:Mode=Dev outside the Development environment requires Auth:Dev:AccessCode with at least "
                + MinAccessCodeLength + " characters. Set the access code, or use Auth:Mode=Entra.");
        }
    }

    /// <summary>
    /// A short fingerprint of the configured access code, stored in the dev session cookie. A session
    /// issued under another code (or before a code was set) stops working when the code changes.
    /// </summary>
    public string AccessCodeStamp()
    {
        if (DevAccessCode is null)
        {
            return "";
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("dafa-contracts-dev-code:" + DevAccessCode));
        return Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>Constant-time comparison of a supplied access code with the configured one.</summary>
    public bool AccessCodeMatches(string? supplied)
    {
        if (DevAccessCode is null)
        {
            return true;
        }
        if (string.IsNullOrEmpty(supplied))
        {
            return false;
        }
        // Hash first so the comparison time does not depend on the length of either value.
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(DevAccessCode));
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(supplied.Trim()));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}

public static class AppClaims
{
    /// <summary>Set by dev login: the id of the chosen demo user.</summary>
    public const string DevUserId = "dafa:dev_uid";
    /// <summary>Set by dev login: fingerprint of the access code the session was issued under.</summary>
    public const string DevCodeStamp = "dafa:dev_code";
    /// <summary>Added per request by the claims transformation: the AppUser id.</summary>
    public const string AppUserId = "dafa:uid";
    public const string AppUserName = "dafa:name";
    public const string AppIdentityType = "DafaApp";
}

/// <summary>
/// Maps the authenticated principal (dev cookie or Entra sign-in) to an AppUser on every request
/// and adds its id, name and roles as a second identity. Roles therefore always come from the
/// database, never from the cookie.
/// </summary>
public sealed class AppUserClaimsTransformation : IClaimsTransformation
{
    private readonly IAppDbContext _db;
    private readonly AuthSettings _settings;
    private readonly IConfiguration _configuration;

    public AppUserClaimsTransformation(IAppDbContext db, AuthSettings settings, IConfiguration configuration)
    {
        _db = db;
        _settings = settings;
        _configuration = configuration;
    }

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return principal;
        }
        if (principal.Identities.Any(i => i.AuthenticationType == AppClaims.AppIdentityType))
        {
            return principal;
        }

        AppUser? user;
        if (_settings.IsDev)
        {
            var raw = principal.FindFirst(AppClaims.DevUserId)?.Value;
            if (!Guid.TryParse(raw, out var id))
            {
                return principal;
            }
            var stamp = principal.FindFirst(AppClaims.DevCodeStamp)?.Value ?? "";
            if (!string.Equals(stamp, _settings.AccessCodeStamp(), StringComparison.Ordinal))
            {
                // The access code was set or changed after this session was issued.
                return principal;
            }
            user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
        }
        else
        {
            user = await FindOrCreateEntraUserAsync(principal);
        }

        if (user is null || !user.IsActive)
        {
            return principal;
        }

        var claims = new List<Claim>
        {
            new(AppClaims.AppUserId, user.Id.ToString()),
            new(AppClaims.AppUserName, user.Name)
        };
        foreach (var role in user.Roles.ToNames())
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        principal.AddIdentity(new ClaimsIdentity(claims, AppClaims.AppIdentityType, AppClaims.AppUserName, ClaimTypes.Role));
        return principal;
    }

    /// <summary>E-mail addresses / UPNs in Auth:BootstrapAdmins (array, or one string separated by commas or semicolons).</summary>
    public static HashSet<string> ReadBootstrapAdmins(IConfiguration configuration)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var section = configuration.GetSection("Auth:BootstrapAdmins");
        var values = new List<string?> { section.Value };
        values.AddRange(section.GetChildren().Select(child => child.Value));
        foreach (var value in values)
        {
            foreach (var part in (value ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                result.Add(part);
            }
        }
        return result;
    }

    /// <summary>
    /// Entra mode (M9). Finds the AppUser for the signed-in directory account, creating or linking it
    /// when allowed:
    /// - the directory object id is the identity; a sign-in without one maps to nobody;
    /// - a row is linked by e-mail only while it has no object id yet, so a reused address never
    ///   inherits another person's roles;
    /// - the unique index on the object id makes parallel first requests safe: the loser of the race
    ///   discards its insert and reads the winner's row;
    /// - accounts listed in Auth:BootstrapAdmins are created (even when AutoProvision is off) and
    ///   given the Admin role, which is how the first administrators come into being.
    /// </summary>
    private async Task<AppUser?> FindOrCreateEntraUserAsync(ClaimsPrincipal principal)
    {
        var objectId = principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
            ?? principal.FindFirst("oid")?.Value;
        var upn = principal.FindFirst("preferred_username")?.Value ?? principal.FindFirst(ClaimTypes.Upn)?.Value;
        var email = upn
            ?? principal.FindFirst(ClaimTypes.Email)?.Value
            ?? principal.FindFirst("email")?.Value;
        var name = principal.FindFirst("name")?.Value ?? principal.FindFirst(ClaimTypes.Name)?.Value ?? email;
        if (string.IsNullOrWhiteSpace(objectId))
        {
            return null;
        }
        objectId = objectId.Trim();

        var bootstrapAdmins = ReadBootstrapAdmins(_configuration);
        var isBootstrapAdmin = !string.IsNullOrWhiteSpace(email) && bootstrapAdmins.Contains(email.Trim());

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.ExternalId == objectId);
            var changed = false;
            if (user is null && !string.IsNullOrEmpty(email))
            {
                var candidates = await _db.Users.Where(u => u.Email == email).ToListAsync();
                user = candidates
                    .Where(u => string.IsNullOrEmpty(u.ExternalId))
                    .OrderBy(u => u.Id)
                    .FirstOrDefault();
                if (user is not null)
                {
                    user.ExternalId = objectId;
                    changed = true;
                }
            }
            if (user is null)
            {
                if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(name))
                {
                    return null;
                }
                if (!isBootstrapAdmin && !_configuration.GetValue("Auth:Entra:AutoProvision", true))
                {
                    return null;
                }
                // First sign-in of someone in the tenant: a reader without any memberships.
                user = new AppUser
                {
                    Id = Guid.NewGuid(),
                    ExternalId = objectId,
                    Name = name,
                    Email = email,
                    Initials = MakeInitials(name),
                    Roles = AppRole.Reader,
                    IsActive = true
                };
                _db.Users.Add(user);
                changed = true;
            }
            if (isBootstrapAdmin && user.IsActive && (user.Roles & AppRole.Admin) != AppRole.Admin)
            {
                user.Roles = user.Roles | AppRole.Reader | AppRole.Admin;
                changed = true;
            }
            if (!changed)
            {
                return user;
            }
            try
            {
                await _db.SaveChangesAsync();
                return user;
            }
            catch (DbUpdateException)
            {
                // Another request for the same person saved first (unique index on the object id).
                // Forget our copy and read theirs.
                _db.DiscardChanges();
            }
        }
        return await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.ExternalId == objectId);
    }

    private static string MakeInitials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "?";
        }
        if (parts.Length == 1)
        {
            return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        }
        return (parts[0].Substring(0, 1) + parts[parts.Length - 1].Substring(0, 1)).ToUpperInvariant();
    }
}

/// <summary>The signed-in user of the current HTTP request. Outside a request there is no user.</summary>
public sealed class HttpCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;
    private readonly IAppDbContext _db;
    private AccessProfile? _profile;

    public HttpCurrentUser(IHttpContextAccessor accessor, IAppDbContext db)
    {
        _accessor = accessor;
        _db = db;
    }

    public Guid? UserId
    {
        get
        {
            var raw = _accessor.HttpContext?.User.FindFirst(AppClaims.AppUserId)?.Value;
            return Guid.TryParse(raw, out var id) ? id : (Guid?)null;
        }
    }

    public string? UserName => _accessor.HttpContext?.User.FindFirst(AppClaims.AppUserName)?.Value;

    public async Task<AccessProfile> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        if (_profile is not null)
        {
            return _profile;
        }
        var userId = UserId;
        if (userId is null)
        {
            throw new UnauthorizedException("Sign in to continue.");
        }
        var profile = await AccessProfileLoader.LoadAsync(_db, userId.Value, cancellationToken);
        if (profile is null)
        {
            throw new UnauthorizedException("Your user is unknown or inactive.");
        }
        _profile = profile;
        return profile;
    }
}

public static class CookieSetup
{
    public const string CookieName = "dafa.contracts.auth";

    /// <summary>Shared session cookie settings: HttpOnly, SameSite=Lax, Secure, and 401/403 instead of redirects.</summary>
    public static void Configure(CookieAuthenticationOptions options, IConfiguration configuration, IHostEnvironment environment)
    {
        var allowInsecure = environment.IsDevelopment() || configuration.GetValue("Auth:Cookie:AllowInsecure", false);
        options.Cookie.Name = CookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = allowInsecure ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    }
}
