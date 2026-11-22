using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Dafa.Contracts.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the application services. The host must also register IAppDbContext, ICurrentUser
    /// and the providers (IDocumentStore, IContractAnalyzer, ISupplierSource, INotifier,
    /// IDirectoryService, ISignatureProvider, ITemplateEngine).
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
        return services;
    }
}
