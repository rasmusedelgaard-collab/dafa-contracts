using System.Text.Json.Serialization;
using Dafa.Contracts.Api.Auth;
using Dafa.Contracts.Api.Hosting;
using Dafa.Contracts.Core;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Infrastructure;
using Dafa.Contracts.Infrastructure.Seed;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var environment = builder.Environment;

// Sign-in mode: Dev (default, demo users) or Entra (Microsoft.Identity.Web).
var authSettings = new AuthSettings(AuthModes.Parse(configuration["Auth:Mode"]), configuration["Auth:Dev:AccessCode"]);
builder.Services.AddSingleton(authSettings);

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
builder.Services.AddAuthorization();

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        // camelCase is the default; enums go out and come in as strings.
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 60L * 1024 * 1024;
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

// The SPA build is copied to wwwroot at publish.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthentication();
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
