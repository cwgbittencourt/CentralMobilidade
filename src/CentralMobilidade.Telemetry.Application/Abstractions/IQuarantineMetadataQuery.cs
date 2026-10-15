using System;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
namespace CentralMobilidade.Telemetry.Application.Abstractions;

public interface IQuarantineMetadataQuery
{
    ValueTask<QuarantinedContentMetadata?> FindAsync(BatchId batchId, Guid quarantineId, CancellationToken cancellationToken);
}
public sealed record QuarantinedContentMetadata(QuarantineMetadata Metadata, DateTimeOffset? DeletedAt);
