using System;
using System.Buffers.Binary;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
namespace CentralMobilidade.Telemetry.Infrastructure.Persistence;

// Trusted data port. Public callers must pass through application authorization.
public sealed class PostgresRejectionQuery(IDbContextFactory<TelemetryControlDbContext> factory, IConfiguration? configuration = null, IClock? clock = null) : IQuarantineMetadataQuery
{
    private DateTimeOffset Now => clock?.UtcNow ?? DateTimeOffset.UtcNow;
    public async ValueTask<QuarantinedContentMetadata?> FindAsync(BatchId batchId, Guid quarantineId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var row = await db.QuarantinedRejections.AsNoTracking().Where(x => x.BatchId == batchId.Value && x.QuarantineId == quarantineId)
            .Select(x => new { x.QuarantineId, x.BatchId, x.LineNumber, x.ErrorCode, x.CreatedAt, x.DeleteAfter, x.ContentDigest, x.DeletedAt }).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : new(new(row.QuarantineId, new(row.BatchId), checked((int)row.LineNumber), row.ErrorCode, row.CreatedAt, row.DeleteAfter, row.ContentDigest), row.DeletedAt);
    }
    public async ValueTask<RejectionResultPage> GetRejectionsAsync(BatchId batchId, string? cursor, int limit, CancellationToken token)
    {
        if (limit is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(limit));
        byte[] key;
        try
        {
            key = Convert.FromBase64String(configuration?["BatchInspection:CursorKey"] ?? "");
        }
        catch (FormatException) { throw new InvalidOperationException("A persistent batch cursor key is required."); }
        if (key.Length != 32)
            throw new InvalidOperationException("A persistent 32-byte batch cursor key is required.");
        using var aes = new AesGcm(key, 16);
        await using var db = await factory.CreateDbContextAsync(token);
        var batch = await db.Batches.AsNoTracking().SingleOrDefaultAsync(x => x.BatchId == batchId.Value, token);
        if (batch is null || batch.ExpiresAt <= Now || batch.State == BatchLifecycle.Expired)
            throw new ProviderOperationException(TelemetryErrorCodes.ResourceNotFound, ProviderFailureKind.Permanent);
        var query = db.LineResults.AsNoTracking().Where(x => x.BatchId == batchId.Value && x.Outcome == LineOutcome.Rejected);
        long after = 0;
        long upper;
        var expiry = Math.Min(batch.ExpiresAt.ToUnixTimeMilliseconds(), Now.AddMinutes(15).ToUnixTimeMilliseconds());
        if (cursor is null)
            upper = await query.MaxAsync(x => (long?)x.LineNumber, token) ?? 0;
        else
        {
            try
            {
                if (cursor.Length != 71 || cursor.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
                    throw new FormatException();
                var bytes = Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/') + "=");
                if (bytes.Length != 53 || bytes[0] != 1 || Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') != cursor)
                    throw new FormatException();
                var plain = new byte[24];
                aes.Decrypt(bytes.AsSpan(1, 12), bytes.AsSpan(13, 24), bytes.AsSpan(37, 16), plain, batchId.Value.ToByteArray());
                after = BinaryPrimitives.ReadInt64LittleEndian(plain);
                upper = BinaryPrimitives.ReadInt64LittleEndian(plain.AsSpan(8));
                expiry = BinaryPrimitives.ReadInt64LittleEndian(plain.AsSpan(16));
                if (after < 1 || upper < after || expiry <= Now.ToUnixTimeMilliseconds() || expiry > batch.ExpiresAt.ToUnixTimeMilliseconds())
                    throw new FormatException();
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                throw new ProviderOperationException(TelemetryErrorCodes.RequestInvalid, ProviderFailureKind.Permanent);
            }
        }
        var rows = await query.Where(x => x.LineNumber > after && x.LineNumber <= upper).OrderBy(x => x.LineNumber).Take(limit + 1).ToArrayAsync(token);
        var items = rows.Take(limit).Select(x => new LineProcessingOutcome(checked((int)x.LineNumber), x.Outcome, x.ErrorCode,
            x.CompanyId is null ? null : new(x.CompanyId), x.VehicleId is null ? null : new(x.VehicleId), x.SafeDigest, x.OccurredAt, x.QuarantineId)).ToArray();
        string? next = null;
        if (rows.Length > limit)
        {
            var bytes = new byte[53];
            bytes[0] = 1;
            RandomNumberGenerator.Fill(bytes.AsSpan(1, 12));
            var plain = new byte[24];
            BinaryPrimitives.WriteInt64LittleEndian(plain, items[^1].LineNumber);
            BinaryPrimitives.WriteInt64LittleEndian(plain.AsSpan(8), upper);
            BinaryPrimitives.WriteInt64LittleEndian(plain.AsSpan(16), expiry);
            aes.Encrypt(bytes.AsSpan(1, 12), plain, bytes.AsSpan(13, 24), bytes.AsSpan(37, 16), batchId.Value.ToByteArray());
            next = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        return new(items, next);
    }
}

