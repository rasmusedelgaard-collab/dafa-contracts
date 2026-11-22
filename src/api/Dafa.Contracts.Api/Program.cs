using System.Text.Json.Serialization;
using Dafa.Contracts.Api.Auth;
using Dafa.Contracts.Api.Hosting;
using Dafa.Contracts.Core;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Infrastructure;
using Dafa.Contracts.Infrastructure.Seed;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var environment = builder.Environment;

// Sign-in mode: Dev (demo users) or Entra (Microsoft.Identity.Web). Fails closed (H3): an unknown or
// missing Auth:Mode stops the app here, and Dev mode outside the Development environment is
// refused unless a shared access code of at least 12 characters is configured.
var authSettings = new AuthSettings(AuthModes.Parse(configuration["Auth:Mode"]), configuration["Auth:Dev:AccessCode"]);
authSettings.EnsureSafeFor(environment.IsDevelopment());
builder.Services.AddSingleton(authSettings);
builder.Services.AddSingleton(new AccessCodeThrottle());

builder.Services.AddHttpContextAccessor();
builder.Services.AddDafaCore();
builder.Services.AddDafaInfrastructure(configuration, environment.ContentRootPath);
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddScoped<IClaimsTransformation, AppUserClaimsTransformation>();

var dataProtectionWarning = builder.Services.AddDafaDataProtection(configuration, environment);

if (authSettings.Mode == AuthModes.Entra)
{
    builder.Services.AddEntraAuth(configuration, environment);
}
else
{
    builder.Services.AddDevAuth(configuration, environment);
}
builder.Services.AddAuthorization(options =>
{
    // M10: one rule for every endpoint that asks for authorization. A cookie alone is not enough;
    // the principal must map to a known, active AppUser (the claim is added per request by
    // AppUserClaimsTransformation). Deactivated or unprovisioned users get 401/403 everywhere.
    options.DefaultPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireClaim(AppClaims.AppUserId)
        .Build();
});

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        // camelCase is the default; enums go out and come in as strings.
        // Numbers are not accepted for enums: {"decision": 7} is a 400, not an approval.
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(null, false));
    });
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 30L * 1024 * 1024;
});
builder.Services.AddHostedService<JobsHostedService>();

var app = builder.Build();

if (dataProtectionWarning is not null)
{
    app.Logger.LogWarning("{Warning}", dataProtectionWarning);
}

// Schema + seed data. Fails fast when the database cannot be reached.
using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await initializer.InitializeAsync();
}

app.UseMiddleware<ApiExceptionMiddleware>();
app.UseStatusCodePages();
app.Use(async (context, next) =>
{
    // Browsers must not guess a content type for downloaded contract files.
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next(context);
});
app.UseMiddleware<RequestedWithMiddleware>();

// The SPA build is copied to wwwroot at publish.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthentication();
app.UseMiddleware<AppUserGateMiddleware>();
app.UseAuthorization();

app.MapHealthChecks("/healthz").AllowAnonymous();
app.MapControllers();
// Unknown API routes are 404, everything else falls back to the SPA.
app.MapFallback("/api/{**path}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found")).AllowAnonymous();
app.MapFallbackToFile("index.html").AllowAnonymous();

await app.RunAsync();

/// <summary>Exposed for WebApplicationFactory in the test project.</summary>
public partial class Program
{
}
