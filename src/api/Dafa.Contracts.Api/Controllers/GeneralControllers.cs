using Dafa.Contracts.Api.Auth;
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

    public GeneralController(ContractQueryService queries, AuthSettings auth)
    {
        _queries = queries;
        _auth = auth;
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

    [HttpGet("home")]
    public async Task<ActionResult<HomeDto>> Home(CancellationToken cancellationToken)
    {
        return await _queries.HomeAsync(cancellationToken);
    }

    [HttpGet("search")]
    public async Task<ActionResult<List<SearchResultDto>>> Search([FromQuery] string? q, CancellationToken cancellationToken)
    {
        return await _queries.SearchAsync(q, cancellationToken);
    }

    [HttpGet("renewals")]
    public async Task<ActionResult<List<RenewalDto>>> Renewals([FromQuery] int? months, CancellationToken cancellationToken)
    {
        return await _queries.RenewalsAsync(months, cancellationToken);
    }

    [HttpGet("overview")]
    public async Task<ActionResult<OverviewDto>> Overview(CancellationToken cancellationToken)
    {
        return await _queries.OverviewAsync(cancellationToken);
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

    public DevController(IAppDbContext db, AuthSettings auth)
    {
        _db = db;
        _auth = auth;
    }

    [HttpGet("config")]
    public ActionResult<DevConfigDto> Config()
    {
        return new DevConfigDto { AuthMode = _auth.Mode, AccessCodeRequired = _auth.AccessCodeRequired };
    }

    /// <summary>
    /// True when no access code is configured, when the caller already has a session (the code was
    /// checked at login), or when the supplied code matches.
    /// </summary>
    private bool HasAccess(string? suppliedCode)
    {
        if (!_auth.AccessCodeRequired)
        {
            return true;
        }
        if (User.Identity?.IsAuthenticated == true)
        {
            return true;
        }
        return _auth.AccessCodeMatches(suppliedCode);
    }

    [HttpGet("users")]
    public async Task<ActionResult<List<DevUserDto>>> Users(CancellationToken cancellationToken)
    {
        if (!_auth.IsDev)
        {
            return NotFound();
        }
        var header = Request.Headers[AccessCodeHeader].ToString();
        if (!HasAccess(header))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Access code required",
                detail: "Enter the access code for this test site.");
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
        if (!HasAccess(supplied))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Access code required",
                detail: "The access code is missing or wrong.");
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
                new Claim(ClaimTypes.Name, user.Name)
            },
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12) });

        var profile = await AccessProfileLoader.LoadAsync(_db, user.Id, cancellationToken);
        if (profile is null)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: "Unknown user.");
        }
        return new MeDto
        {
            Id = profile.UserId,
            Name = profile.Name,
            Email = profile.Email,
            Initials = profile.Initials,
            Roles = profile.Roles.ToNames(),
            DepartmentIds = profile.DepartmentIds.ToList(),
            ManagedEntityIds = profile.ManagedEntityIds.ToList(),
            IsGroupManagement = profile.IsGroupManagement,
            AuthMode = _auth.Mode
        };
    }
}
