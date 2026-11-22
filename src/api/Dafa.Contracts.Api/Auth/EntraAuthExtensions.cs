using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;

namespace Dafa.Contracts.Api.Auth;

public static class EntraAuthExtensions
{
    /// <summary>
    /// PRODUCTION MODE (Auth:Mode=Entra). NOT exercised in the test environment.
    /// Microsoft.Identity.Web OpenID Connect sign-in with a cookie session, configured from the
    /// AzureAd section (Instance, TenantId, ClientId, ClientSecret, CallbackPath).
    /// API calls without a session get 401; the SPA then sends the browser to /api/auth/login.
    /// This is the only file that references Microsoft.Identity.Web.
    /// </summary>
    public static IServiceCollection AddEntraAuth(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddMicrosoftIdentityWebApp(configuration.GetSection("AzureAd"));

        services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
        {
            CookieSetup.Configure(options, configuration, environment);
        });

        services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            var previous = options.Events.OnRedirectToIdentityProvider;
            options.Events.OnRedirectToIdentityProvider = async context =>
            {
                var path = context.Request.Path;
                if (path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/auth/login"))
                {
                    // An API call must not be redirected to the sign-in page.
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.HandleResponse();
                    return;
                }
                if (previous is not null)
                {
                    await previous(context);
                }
            };
        });

        return services;
    }
}
