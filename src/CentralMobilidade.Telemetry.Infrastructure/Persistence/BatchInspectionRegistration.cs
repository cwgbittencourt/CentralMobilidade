using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Batches;
using CentralMobilidade.Telemetry.Infrastructure.Quarantine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace CentralMobilidade.Telemetry.Infrastructure.Persistence;

public static class BatchInspectionRegistration
{
    public static IServiceCollection AddTelemetryBatchInspection(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddScoped<BatchVisibility>();
        services.TryAddScoped<GetBatchStatusHandler>();
        services.TryAddScoped<ListBatchRejectionsHandler>();
        services.TryAddScoped<GetQuarantinedRejectionContentHandler>();
        services.TryAddScoped<IQuarantineMetadataQuery, PostgresRejectionQuery>();
        services.TryAddSingleton<IQuarantineStore, FileSystemQuarantineStore>();
        return services;
    }
}
