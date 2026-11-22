using Dafa.Contracts.Api.Auth;
using Dafa.Contracts.Api.Hosting;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Dafa.Contracts.Core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Dafa.Contracts.Api.Controllers;

[Route("api")]
public class GeneralController : ApiControllerBase
{
    private readonly ContractQueryService _queries;
    private readonly AuthSettings _auth;
    private readonly SearchService _search;

    public GeneralController(ContractQueryService queries, AuthSettings auth, SearchService search)
    {
        _queries = queries;
        _auth = auth;
        _search = search;
    }

    [HttpGet("me")]
    public async Task<ActionResult<MeDto>> Me(CancellationToken cancellationToken)
    {
        return await _queries.MeAsync(_auth.Mode, cancellationToken);
    }

    [HttpGet("lookups")]
    public async Task<ActionResult<LookupsDto>> Lookups(CancellationToken cancellationToken)
    {
        return await _queries.LookupsAsync(cancellationToken);
    }

    [HttpGet("reminder-settings")]
    public async Task<ActionResult<ReminderSettingsDto>> GetReminderSettings(CancellationToken cancellationToken)
    {
        return await _queries.ReminderSettingsAsync(cancellationToken);
    }

    [HttpGet("suppliers")]
    public async Task<ActionResult<List<SupplierDto>>> Suppliers([FromQuery] string? q, CancellationToken cancellationToken)
    {
        return await _queries.SuppliersAsync(q, cancellationToken);
    }

    /// <summary>Release 4: ?entityId= (repeatable) is the entity scope.</summary>
    [HttpGet("home")]
    public async Task<ActionResult<HomeDto>> Home(CancellationToken cancellationToken)
    {
        return await _queries.HomeAsync(EntityScope.From(Request), cancellationToken);
    }

    [HttpGet("search")]
    public async Task<ActionResult<List<SearchResultDto>>> Search([FromQuery] string? q, CancellationToken cancellationToken)
    {
        // Release 3: contracts, documents (name and text), people, then suppliers; 8 at most.
        // Release 4: ?entityId= (repeatable) scopes contract and document hits.
        return await _search.PaletteAsync(q, EntityScope.From(Request), cancellationToken);
    }

    [HttpGet("renewals")]
    public async Task<ActionResult<List<RenewalDto>>> Renewals([FromQuery] int? months, CancellationToken cancellationToken)
    {
        return await _queries.RenewalsAsync(months, EntityScope.From(Request), cancellationToken);
    }

    [HttpGet("overview")]
    public async Task<ActionResult<OverviewDto>> Overview(CancellationToken cancellationToken)
    {
        return await _queries.OverviewAsync(EntityScope.From(Request), cancellationToken);
    }
}

[Route("api/auth")]
public class AuthController : ApiControllerBase
{
    private readonly AuthSettings _auth;

    public AuthController(AuthSettings auth)
    {
        _auth = auth;
    }

    /// <summary>Entra mode only: starts the interactive sign-in and returns to the SPA.</summary>
    [HttpGet("login")]
    [AllowAnonymous]
    public IActionResult Login([FromQuery] string? returnUrl)
    {
        if (_auth.IsDev)
        {
            return NotFound();
        }
        var target = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
        return Challenge(new AuthenticationProperties { RedirectUri = target }, AuthModes.EntraChallengeScheme);
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }
}

/// <summary>Dev mode only: pick a seeded demo user. Returns 404 in Entra mode (except config).</summary>
[ApiController]
[Route("api/dev")]
[AllowAnonymous]
public class DevController : ControllerBase
{
    public const string AccessCodeHeader = "X-Dev-Access-Code";

    private readonly IAppDbContext _db;
    private readonly AuthSettings _auth;
    private readonly AccessCodeThrottle _throttle;

    public DevController(IAppDbContext db, AuthSettings auth, AccessCodeThrottle throttle)
    {
        _db = db;
        _auth = auth;
        _throttle = throttle;
    }

    /// <summary>Release 4: also carries the logo for the sign-in screen (public, like the logo itself).</summary>
    [HttpGet("config")]
    public async Task<ActionResult<DevConfigDto>> Config(CancellationToken cancellationToken)
    {
        return new DevConfigDto
        {
            AuthMode = _auth.Mode,
            AccessCodeRequired = _auth.AccessCodeRequired,
            Branding = await BrandingService.LoadAsync(_db, cancellationToken)
        };
    }

    private string ClientKey() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// Null when the caller may use the dev endpoints: no access code is configured, the caller has a
    /// session for a known, active user (the code was checked at login; a deactivated or unknown
    /// user's cookie does not count, M10), or the supplied code matches. Otherwise the 401 or 429
    /// answer. Wrong codes are counted per client address (L6).
    /// </summary>
    private ObjectResult? CheckAccess(string? suppliedCode, string missingDetail)
    {
        if (!_auth.AccessCodeRequired)
        {
            return null;
        }
        if (User.FindFirst(AppClaims.AppUserId) is not null)
        {
            return null;
        }
        var client = ClientKey();
        if (_throttle.IsBlocked(client))
        {
            return Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many attempts",
                detail: "Too many wrong access codes. Wait 10 minutes and try again.");
        }
        if (_auth.AccessCodeMatches(suppliedCode))
        {
            return null;
        }
        if (!string.IsNullOrEmpty(suppliedCode))
        {
            _throttle.RecordFailure(client);
        }
        return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Access code required", detail: missingDetail);
    }

    [HttpGet("users")]
    public async Task<ActionResult<List<DevUserDto>>> Users(CancellationToken cancellationToken)
    {
        if (!_auth.IsDev)
        {
            return NotFound();
        }
        var header = Request.Headers[AccessCodeHeader].ToString();
        var denied = CheckAccess(header, "Enter the access code for this test site.");
        if (denied is not null)
        {
            return denied;
        }

        var users = await _db.Users.AsNoTracking().Where(u => u.IsActive).ToListAsync(cancellationToken);
        var departments = (await _db.Departments.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(d => d.Id, d => d.Name);
        var entities = (await _db.LegalEntities.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(e => e.Id, e => e.Name);
        var userDepartments = await _db.UserDepartments.AsNoTracking().ToListAsync(cancellationToken);
        var managed = await _db.UserEntityManagements.AsNoTracking().ToListAsync(cancellationToken);

        var result = new List<DevUserDto>();
        foreach (var user in users.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase))
        {
            var access = new List<string>();
            if ((user.Roles & AppRole.Admin) == AppRole.Admin) access.Add("Admin");
            if (user.IsGroupManagement) access.Add("Group management");
            foreach (var membership in userDepartments.Where(d => d.UserId == user.Id))
            {
                if (departments.TryGetValue(membership.DepartmentId, out var departmentName))
                {
                    access.Add(departmentName + (membership.IsHead ? " (head)" : ""));
                }
            }
            foreach (var management in managed.Where(m => m.UserId == user.Id))
            {
                if (entities.TryGetValue(management.EntityId, out var entityName))
                {
                    access.Add("Manages " + entityName);
                }
            }
            if (access.Count == 0)
            {
                access.Add("No department");
            }
            result.Add(new DevUserDto
            {
                Id = user.Id,
                Name = user.Name,
                Email = user.Email,
                Initials = user.Initials,
                Title = string.IsNullOrWhiteSpace(user.JobTitle) ? string.Join(", ", access) : user.JobTitle!,
                Roles = user.Roles.ToNames(),
                Access = string.Join(", ", access)
            });
        }
        return result;
    }

    /// <summary>Signs in as the chosen demo user and returns the same shape as GET me.</summary>
    [HttpPost("login")]
    public async Task<ActionResult<MeDto>> Login([FromBody] DevLoginInput input, CancellationToken cancellationToken)
    {
        if (!_auth.IsDev)
        {
            return NotFound();
        }
        var supplied = string.IsNullOrEmpty(input.AccessCode) ? Request.Headers[AccessCodeHeader].ToString() : input.AccessCode;
        var denied = CheckAccess(supplied, "The access code is missing or wrong.");
        if (denied is not null)
        {
            return denied;
        }
        if (input.UserId is null)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Validation failed", detail: "userId is required.");
        }
        var userId = input.UserId.Value;
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, cancellationToken);
        if (user is null)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: "Unknown user.");
        }

        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(AppClaims.DevUserId, user.Id.ToString()),
                new Claim(AppClaims.DevCodeStamp, _auth.AccessCodeStamp()),
                new Claim(ClaimTypes.Name, user.Name)
            },
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = true,
                // Release 3: the session timeout from General settings (default 12 hours).
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes((await AppSettingsStore.LoadAsync(_db, cancellationToken)).SessionTimeoutMinutes)
            });

        // Release 3: last sign-in for the admin user list.
        var tracked = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (tracked is not null)
        {
            tracked.LastSignInAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        var profile = await AccessProfileLoader.LoadAsync(_db, user.Id, cancellationToken);
        if (profile is null)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: "Unknown user.");
        }
        return ContractQueryService.ToMe(profile, _auth.Mode);
    }
}
