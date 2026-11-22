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
    ///   Signing:*               e-signature: the provider is chosen in Settings (None | Simulated | DocuSign | Penneo);
    ///                           credentials are Signing:DocuSign:* / Signing:Penneo:*, Signing:AllowSimulated,
    ///                           Signing:Webhook:Secret (Signature:Provider is no longer read)
    ///   Graph:*                 the shared Microsoft Graph client: Graph:Auth = ClientSecret | Certificate | ManagedIdentity,
    ///                           Graph:MaxAttempts, Graph:MetadataTimeoutSeconds, Graph:ContentTimeoutSeconds
    ///   Sync:Provider           Demo (default) | Live           release 3 sync sources
    ///   DataRoom:LocalPath      App_Data/dataroom (default)     release 3 data room zips
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
        AddGraphClient(services, configuration);
        services.AddSingleton<IAccessTokenProvider, ClientCredentialsTokenProvider>();
        // Release 4: how this installation runs (test mode or SharePoint / Entra), for Core.
        services.AddSingleton(new RuntimeModes
        {
            StorageMode = Is(configuration, "Documents:Provider", "SharePoint") ? "SharePoint" : "Local",
            DirectoryMode = Is(configuration, "Directory:Provider", "Graph") ? "Graph" : "Seeded"
        });
        if (Is(configuration, "Documents:Provider", "SharePoint") || Is(configuration, "Directory:Provider", "Graph"))
        {
            services.AddScoped<IOrgLinkProbe, GraphOrgLinkProbe>();
        }
        else
        {
            services.AddScoped<IOrgLinkProbe, NoOrgLinkProbe>();
        }
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

        // The mail channel; INotifier itself is Core's NotificationDispatcher (settings, preferences, in-app).
        if (Is(configuration, "Notifications:Provider", "Graph"))
        {
            services.AddScoped<IMailChannel, GraphMailNotifier>();
        }
        else
        {
            services.AddScoped<IMailChannel, OutboxNotifier>();
        }

        // Release 3 providers.
        services.AddSingleton<ITabularFileReader, TabularFileReader>();
        services.AddSingleton<IDocumentTextExtractor, DocumentTextExtractor>();
        if (Is(configuration, "Sync:Provider", "Live"))
        {
            services.AddScoped<ISyncSourceReader, LiveSyncSourceReader>();
        }
        else
        {
            services.AddScoped<ISyncSourceReader, DemoSyncSourceReader>();
        }
        services.AddSingleton<IExportStorage>(serviceProvider =>
        {
            var config = serviceProvider.GetRequiredService<IConfiguration>();
            var path = config["DataRoom:LocalPath"];
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(contentRootPath, "App_Data", "dataroom");
            }
            else if (!Path.IsPathRooted(path))
            {
                path = Path.Combine(contentRootPath, path);
            }
            return new LocalExportStorage(path);
        });

        if (Is(configuration, "Directory:Provider", "Graph"))
        {
            services.AddScoped<IDirectoryService, GraphDirectoryService>();
        }
        else
        {
            services.AddScoped<IDirectoryService, SeededDirectoryService>();
        }

        // Release 4: every e-signature provider is registered; the one in use is chosen in the
        // e-sign settings, and a provider without server configuration cannot be selected.
        services.AddHttpClient("esign", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });
        services.AddScoped<ISignatureProvider, SimulatedSignatureProvider>();
        services.AddScoped<ISignatureProvider, DocuSignSignatureProvider>();
        services.AddScoped<ISignatureProvider, PenneoSignatureProvider>();

        services.AddScoped<DatabaseInitializer>();
        return services;
    }

    /// <summary>
    /// The shared Microsoft Graph client (SharePoint hardening): one named client for the document
    /// store, the link probe and the directory, with a descriptive User-Agent, a connect timeout,
    /// and the resilience handler (retry honouring Retry-After, per-try timeouts, circuit breaker).
    /// The client's own timeout is only an outer limit; the handler times each try.
    /// </summary>
    public static void AddGraphClient(IServiceCollection services, IConfiguration configuration)
    {
        var options = new GraphResilienceOptions();
        options.MaxAttempts = Math.Clamp(configuration.GetValue("Graph:MaxAttempts", options.MaxAttempts), 1, 8);
        options.MetadataTimeout = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Graph:MetadataTimeoutSeconds", 30), 5, 120));
        options.ContentTimeout = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Graph:ContentTimeoutSeconds", 180), 30, 900));
        services.AddSingleton(options);
        services.AddSingleton<StorageHealthState>();
        services.AddTransient<GraphResilienceHandler>(serviceProvider =>
            new GraphResilienceHandler(serviceProvider.GetRequiredService<StorageHealthState>(), serviceProvider.GetRequiredService<GraphResilienceOptions>()));
        services.AddHttpClient(GraphCalls.ClientName, client =>
            {
                client.Timeout = TimeSpan.FromMinutes(20);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(GraphCalls.UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                AutomaticDecompression = System.Net.DecompressionMethods.All
            })
            .AddHttpMessageHandler<GraphResilienceHandler>();
        services.AddHttpClient("token", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(GraphCalls.UserAgent);
        });
    }

    public static bool IsSqlServer(IConfiguration configuration) => Is(configuration, "Database:Provider", "SqlServer");

    public static string DefaultSqlitePath(string contentRootPath) => Path.Combine(contentRootPath, "App_Data", "dafa-contracts.db");

    private static bool Is(IConfiguration configuration, string key, string value) =>
        string.Equals((configuration[key] ?? "").Trim(), value, StringComparison.OrdinalIgnoreCase);
}
