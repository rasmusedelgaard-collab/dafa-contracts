using System.Security.Claims;
using Dafa.Contracts.Api.Auth;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Api.Hosting;

/// <summary>Turns application exceptions into RFC 7807 problem responses.</summary>
public sealed class ApiExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiExceptionMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger, IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (InputValidationException ex)
        {
            var problem = new ValidationProblemDetails(ex.Errors)
            {
                Status = ex.StatusCode,
                Title = ex.Title,
                Detail = ex.Message,
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1"
            };
            await WriteAsync(context, problem);
        }
        catch (AppException ex)
        {
            var problem = new ProblemDetails
            {
                Status = ex.StatusCode,
                Title = ex.Title,
                Detail = ex.Message
            };
            if (ex.ProblemType is not null)
            {
                problem.Type = ex.ProblemType;
            }
            await WriteAsync(context, problem);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Optimistic concurrency (M1): the row was changed by someone else after it was read.
            _logger.LogInformation(ex, "Concurrency conflict for {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteAsync(context, new ProblemDetails
            {
                Status = 409,
                Title = "Conflict",
                Type = ProblemTypes.Stale,
                Detail = "This was changed by someone else at the same time. Reload and try again."
            });
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning(ex, "A stored file is missing");
            await WriteAsync(context, new ProblemDetails { Status = 404, Title = "Not found", Detail = "The file is missing from the document store." });
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away; nothing to answer.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error for {Method} {Path}", context.Request.Method, context.Request.Path);
            var problem = new ProblemDetails
            {
                Status = 500,
                Title = "Unexpected error",
                Detail = _environment.IsDevelopment() ? ex.ToString() : "Something went wrong. Try again, and contact IT if it keeps happening."
            };
            await WriteAsync(context, problem);
        }
    }

    public static async Task WriteAsync(HttpContext context, ProblemDetails problem)
    {
        if (context.Response.HasStarted)
        {
            return;
        }
        context.Response.Clear();
        context.Response.StatusCode = problem.Status ?? 500;
        await context.Response.WriteAsJsonAsync(problem, problem.GetType(), options: null, contentType: "application/problem+json");
    }
}

/// <summary>
/// Release 3: checks every Jobs:TickMinutes (default 5) which jobs are due on their own schedules
/// (admin/jobs) and runs them through JobRunner, while the app is running.
/// </summary>
public sealed class JobsHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<JobsHostedService> _logger;

    public JobsHostedService(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<JobsHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Jobs:Enabled", true))
        {
            _logger.LogInformation("Background jobs are disabled (Jobs:Enabled=false)");
            return;
        }
        var interval = TimeSpan.FromMinutes(Math.Max(1, _configuration.GetValue("Jobs:TickMinutes", 5)));
        var startupDelay = TimeSpan.FromSeconds(Math.Max(0, _configuration.GetValue("Jobs:StartupDelaySeconds", 30)));

        try
        {
            await Task.Delay(startupDelay, stoppingToken);
            using var timer = new PeriodicTimer(interval);
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<JobRunner>();
            var ran = await runner.RunDueAsync(stoppingToken);
            if (ran > 0)
            {
                _logger.LogInformation("Scheduler ran {Count} due jobs", ran);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The background job run failed");
        }
    }
}

public static class DataProtectionSetup
{
    /// <summary>
    /// Persists the Data Protection keys (which sign the session cookie) to DataProtection:KeysPath
    /// (default App_Data/keys under the content root) so a redeploy or restart does not sign users out.
    /// Returns a warning text when the folder cannot be used; keys are then kept in memory only.
    /// </summary>
    public static string? AddDafaDataProtection(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var builder = services.AddDataProtection().SetApplicationName("Dafa.Contracts");

        var path = configuration["DataProtection:KeysPath"];
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine("App_Data", "keys");
        }
        if (!Path.IsPathRooted(path))
        {
            path = Path.GetFullPath(Path.Combine(environment.ContentRootPath, path));
        }

        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            builder.PersistKeysToFileSystem(new DirectoryInfo(path));
            return null;
        }
        catch (Exception ex)
        {
            return "Data Protection keys cannot be stored in '" + path + "' (" + ex.Message
                + "). Keys are kept in memory, so users are signed out when the app restarts.";
        }
    }
}

/// <summary>
/// CSRF protection for the cookie session: every state-changing API request must carry the header
/// X-Requested-With: dafa-contracts. A browser cannot add a custom header to a cross-site form post
/// or a no-preflight request, so another site cannot ride on the user's cookie. Applies to every
/// /api request that is not GET or HEAD, with no exceptions (login and logout included).
/// </summary>
public sealed class RequestedWithMiddleware
{
    public const string HeaderName = "X-Requested-With";
    public const string HeaderValue = "dafa-contracts";

    private readonly RequestDelegate _next;

    public RequestedWithMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public static bool IsExempt(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method);

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api") && !IsExempt(context.Request.Method))
        {
            var value = context.Request.Headers[HeaderName].ToString();
            if (!string.Equals(value, HeaderValue, StringComparison.Ordinal))
            {
                await ApiExceptionMiddleware.WriteAsync(context, new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Missing request header",
                    Detail = "Requests that change data must send the header " + HeaderName + ": " + HeaderValue + "."
                });
                return;
            }
        }
        await _next(context);
    }
}

/// <summary>
/// M10, Dev mode: a session cookie that no longer maps to a known, active user (deactivated, deleted,
/// or issued under another access code) is treated as no session at all. The API then answers 401,
/// the user picker is shown again, and such a cookie can never stand in for the access code.
/// In Entra mode the principal is kept and the default authorization policy answers 403.
/// </summary>
public sealed class AppUserGateMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AuthSettings _settings;

    public AppUserGateMiddleware(RequestDelegate next, AuthSettings settings)
    {
        _next = next;
        _settings = settings;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_settings.IsDev
            && context.User.Identity?.IsAuthenticated == true
            && context.User.FindFirst(AppClaims.AppUserId) is null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
        }
        await _next(context);
    }
}

/// <summary>
/// In-memory limit on wrong dev access codes (L6): after <see cref="MaxFailures"/> wrong codes from one
/// client address within <see cref="Window"/>, further attempts from it are refused until the window
/// has passed. Per app instance; a restart clears it.
/// </summary>
public sealed class AccessCodeThrottle
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private sealed class Entry
    {
        public DateTime WindowStart;
        public int Failures;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _utcNow;

    public AccessCodeThrottle() : this(() => DateTime.UtcNow)
    {
    }

    public AccessCodeThrottle(Func<DateTime> utcNow)
    {
        _utcNow = utcNow;
    }

    public bool IsBlocked(string client)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(client, out var entry)
                && _utcNow() - entry.WindowStart < Window
                && entry.Failures >= MaxFailures;
        }
    }

    public void RecordFailure(string client)
    {
        lock (_lock)
        {
            var now = _utcNow();
            if (_entries.Count > 10000)
            {
                // Forget expired entries so the table cannot grow without bound.
                foreach (var key in _entries.Where(pair => now - pair.Value.WindowStart >= Window).Select(pair => pair.Key).ToList())
                {
                    _entries.Remove(key);
                }
            }
            if (!_entries.TryGetValue(client, out var entry) || now - entry.WindowStart >= Window)
            {
                entry = new Entry { WindowStart = now, Failures = 0 };
                _entries[client] = entry;
            }
            entry.Failures++;
        }
    }
}
