using Microsoft.AspNetCore.Authentication.Cookies;

namespace Dafa.Contracts.Api.Auth;

public static class DevAuthExtensions
{
    /// <summary>
    /// TEST MODE (Auth:Mode=Dev). Cookie session only; the user is picked from the seeded demo users
    /// through /api/dev/users and /api/dev/login. Optionally gated by Auth:Dev:AccessCode.
    /// </summary>
    public static IServiceCollection AddDevAuth(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                CookieSetup.Configure(options, configuration, environment);
            });
        return services;
    }
}
