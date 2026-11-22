using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Infrastructure.Data;
using Dafa.Contracts.Infrastructure.Providers;
using Dafa.Contracts.Infrastructure.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dafa.Contracts.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the database and one implementation per external concern, chosen by configuration.
    /// Defaults are the test-mode providers, so the app runs with no Microsoft tenant:
    ///   Database:Provider       Sqlite (default) | SqlServer      connection string: ConnectionStrings:Default
    ///   Documents:Provider      Local (default) | SharePoint
    ///   Analyzer:Provider       Heuristic (default) | AzureOpenAI
    ///   Suppliers:Provider      Seed (default) | PowerBi
    ///   Notifications:Provider  Outbox (default) | Graph
    ///   Directory:Provider      Seeded (default) | Graph
    ///   Signature:Provider      None (default) | DocuSign
    /// </summary>
    public static IServiceCollection AddDafaInfrastructure(this IServiceCollection services, IConfiguration configuration, string contentRootPath)
    {
        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            var config = serviceProvider.GetRequiredService<IConfiguration>();
            var connectionString = config.GetConnectionString("Default");
            if (IsSqlServer(config))
            {
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    throw new InvalidOperationException("ConnectionStrings:Default must be set when Database:Provider is SqlServer.");
                }
                options.UseSqlServer(connectionString);
            }
            else
            {
                options.UseSqlite(string.IsNullOrWhiteSpace(connectionString)
                    ? "Data Source=" + DefaultSqlitePath(contentRootPath)
                    : connectionString);
            }
        });
        services.AddScoped<IAppDbContext>(serviceProvider => serviceProvider.GetRequiredService<AppDbContext>());

        services.AddHttpClient();
        services.AddSingleton<IAccessTokenProvider, ClientCredentialsTokenProvider>();
        services.AddSingleton<ITemplateEngine, OpenXmlTemplateEngine>();

        if (Is(configuration, "Documents:Provider", "SharePoint"))
        {
            services.AddScoped<IDocumentStore, SharePointDocumentStore>();
        }
        else
        {
            services.AddSingleton<IDocumentStore>(serviceProvider =>
            {
                var config = serviceProvider.GetRequiredService<IConfiguration>();
                var path = config["Documents:LocalPath"];
                if (string.IsNullOrWhiteSpace(path))
                {
                    path = Path.Combine(contentRootPath, "App_Data", "files");
                }
                else if (!Path.IsPathRooted(path))
                {
                    path = Path.Combine(contentRootPath, path);
                }
                return new LocalDocumentStore(path);
            });
        }

        if (Is(configuration, "Analyzer:Provider", "AzureOpenAI"))
        {
            services.AddScoped<IContractAnalyzer, AzureOpenAiContractAnalyzer>();
        }
        else
        {
            services.AddScoped<IContractAnalyzer, HeuristicContractAnalyzer>();
        }

        if (Is(configuration, "Suppliers:Provider", "PowerBi"))
        {
            services.AddScoped<ISupplierSource, PowerBiSupplierSource>();
        }
        else
        {
            services.AddScoped<ISupplierSource, SeedSupplierSource>();
        }

        if (Is(configuration, "Notifications:Provider", "Graph"))
        {
            services.AddScoped<INotifier, GraphMailNotifier>();
        }
        else
        {
            services.AddScoped<INotifier, OutboxNotifier>();
        }

        if (Is(configuration, "Directory:Provider", "Graph"))
        {
            services.AddScoped<IDirectoryService, GraphDirectoryService>();
        }
        else
        {
            services.AddScoped<IDirectoryService, SeededDirectoryService>();
        }

        if (Is(configuration, "Signature:Provider", "DocuSign"))
        {
            services.AddScoped<ISignatureProvider, DocuSignSignatureProvider>();
        }
        else
        {
            services.AddScoped<ISignatureProvider, NoSignatureProvider>();
        }

        services.AddScoped<DatabaseInitializer>();
        return services;
    }

    public static bool IsSqlServer(IConfiguration configuration) => Is(configuration, "Database:Provider", "SqlServer");

    public static string DefaultSqlitePath(string contentRootPath) => Path.Combine(contentRootPath, "App_Data", "dafa-contracts.db");

    private static bool Is(IConfiguration configuration, string key, string value) =>
        string.Equals((configuration[key] ?? "").Trim(), value, StringComparison.OrdinalIgnoreCase);
}
