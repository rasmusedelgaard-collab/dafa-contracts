using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

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
            await WriteAsync(context, problem);
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

    private static async Task WriteAsync(HttpContext context, ProblemDetails problem)
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

/// <summary>Runs the background jobs every hour (Jobs:IntervalMinutes) while the app is running.</summary>
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
        var interval = TimeSpan.FromMinutes(Math.Max(1, _configuration.GetValue("Jobs:IntervalMinutes", 60)));
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
            var jobs = scope.ServiceProvider.GetRequiredService<JobService>();
            var result = await jobs.RunAllAsync(stoppingToken);
            _logger.LogInformation(
                "Jobs ran: {Notice} notice reminders, {Escalations} escalations, {Obligations} obligation reminders, {Renewed} renewed, {Ended} ended, {Errors} errors",
                result.NoticeReminders, result.Escalations, result.ObligationReminders, result.ContractsRenewed, result.ContractsEnded, result.Errors.Count);
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
