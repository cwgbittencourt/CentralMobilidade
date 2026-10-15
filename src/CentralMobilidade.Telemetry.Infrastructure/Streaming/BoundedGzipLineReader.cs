using System.Collections.Generic;
using System.IO;
using System.Threading;
using CentralMobilidade.Telemetry.Application.Configuration;

namespace CentralMobilidade.Telemetry.Infrastructure.Streaming;

// Compatibility facade; intake and worker share the same bounded gzip validation implementation.
public sealed class BoundedGzipLineReader
{
    public IAsyncEnumerable<System.ReadOnlyMemory<byte>> ReadAsync(Stream input, long compressedBytes,
        TelemetryOptions options, CancellationToken cancellationToken = default) =>
        new Application.Streaming.BoundedGzipLineReader().ReadAsync(input, compressedBytes, options, cancellationToken);
}
