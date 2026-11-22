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

    public static string Parse(string? value) =>
        string.Equals((value ?? "").Trim(), Entra, StringComparison.OrdinalIgnoreCase) ? Entra : Dev;
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

    private async Task<AppUser?> FindOrCreateEntraUserAsync(ClaimsPrincipal principal)
    {
        var objectId = principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
            ?? principal.FindFirst("oid")?.Value;
        var email = principal.FindFirst("preferred_username")?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value
            ?? principal.FindFirst("email")?.Value
            ?? principal.FindFirst(ClaimTypes.Upn)?.Value;
        var name = principal.FindFirst("name")?.Value ?? principal.FindFirst(ClaimTypes.Name)?.Value ?? email;

        AppUser? user = null;
        if (!string.IsNullOrEmpty(objectId))
        {
            user = await _db.Users.FirstOrDefaultAsync(u => u.ExternalId == objectId);
        }
        if (user is null && !string.IsNullOrEmpty(email))
        {
            var candidates = await _db.Users.Where(u => u.Email == email).ToListAsync();
            user = candidates.FirstOrDefault();
            if (user is not null && string.IsNullOrEmpty(user.ExternalId) && !string.IsNullOrEmpty(objectId))
            {
                user.ExternalId = objectId;
                await _db.SaveChangesAsync();
            }
        }
        if (user is not null || string.IsNullOrEmpty(email) || string.IsNullOrEmpty(name))
        {
            return user;
        }
        if (!_configuration.GetValue("Auth:Entra:AutoProvision", true))
        {
            return null;
        }

        // First sign-in of someone in the tenant: create them as a reader without any memberships.
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
        await _db.SaveChangesAsync();
        return user;
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
