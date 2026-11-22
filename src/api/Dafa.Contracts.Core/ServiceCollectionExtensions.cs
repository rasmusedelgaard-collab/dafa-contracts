using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Dafa.Contracts.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the application services. The host must also register IAppDbContext, ICurrentUser
    /// and the providers (IDocumentStore, IContractAnalyzer, ISupplierSource, IMailChannel,
    /// IDirectoryService, ISignatureProvider, ITemplateEngine, and from release 3 ITabularFileReader,
    /// ISyncSourceReader, IDocumentTextExtractor, IExportStorage). INotifier is the
    /// NotificationDispatcher, which delivers e-mail through IMailChannel.
    /// </summary>
    public static IServiceCollection AddDafaCore(this IServiceCollection services)
    {
        services.AddSingleton<IClock, UtcClock>();
        services.AddScoped<IContractAccessService, ContractAccessService>();
        services.AddScoped<AuditService>();
        services.AddScoped<DocumentWriter>();
        services.AddScoped<ApprovalService>();
        services.AddScoped<ContractQueryService>();
        services.AddScoped<ContractCommandService>();
        services.AddScoped<ObligationService>();
        services.AddScoped<LinkService>();
        services.AddScoped<DocumentService>();
        services.AddScoped<IntakeService>();
        services.AddScoped<JobService>();
        services.AddScoped<AdminService>();
        // Release 3
        services.AddScoped<SettingsService>();
        services.AddScoped<CustomFieldService>();
        services.AddScoped<CollaborationService>();
        services.AddScoped<PersonalService>();
        services.AddScoped<SearchService>();
        services.AddScoped<AnalyticsService>();
        services.AddScoped<VersionService>();
        services.AddScoped<DataRoomService>();
        services.AddScoped<AdminDirectoryService>();
        services.AddScoped<CounterpartyService>();
        services.AddScoped<ImportService>();
        services.AddScoped<SyncService>();
        services.AddScoped<DocumentTextIndexer>();
        services.AddScoped<DigestService>();
        services.AddScoped<JobRunner>();
        services.AddScoped<NotificationDispatcher>();
        services.AddScoped<INotifier>(serviceProvider => serviceProvider.GetRequiredService<NotificationDispatcher>());
        return services;
    }
}
