using System;
using CentralMobilidade.Telemetry.Domain.ValueObjects;

namespace CentralMobilidade.Telemetry.Domain.Processing;

public sealed record QuarantinedRejection
{
    public QuarantinedRejection(Guid quarantineId, BatchId batchId, int lineNumber, LineRejectionCode errorCode,
        ProtectedContentReference encryptedContentLocation, string contentDigest, DateTimeOffset createdAt, DateTimeOffset deleteAfter)
        : this(quarantineId, batchId, lineNumber, errorCode, encryptedContentLocation, contentDigest, createdAt, deleteAfter, null) { }

    private QuarantinedRejection(Guid quarantineId, BatchId batchId, int lineNumber, LineRejectionCode errorCode,
        ProtectedContentReference location, string contentDigest, DateTimeOffset createdAt, DateTimeOffset deleteAfter, DateTimeOffset? deletedAt)
    {
        ArgumentNullException.ThrowIfNull(batchId);
        ArgumentNullException.ThrowIfNull(location);
        if (quarantineId == Guid.Empty)
            throw new ArgumentException("Quarantine ID must be nonempty.", nameof(quarantineId));
        if (lineNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be one-based.");
        if (!Enum.IsDefined(errorCode))
            throw new ArgumentOutOfRangeException(nameof(errorCode), "Unknown rejection code.");
        var created = createdAt.ToUniversalTime();
        var deadline = deleteAfter.ToUniversalTime();
        if (deadline < created || deadline - created > TimeSpan.FromDays(30))
            throw new ArgumentOutOfRangeException(nameof(deleteAfter), "Deletion deadline must fall within 30 days of creation.");
        QuarantineId = quarantineId;
        BatchId = batchId;
        LineNumber = lineNumber;
        ErrorCode = errorCode;
        EncryptedContentLocation = location;
        ContentDigest = ProcessingValidation.Sha256(contentDigest, nameof(contentDigest));
        CreatedAt = created;
        DeleteAfter = deadline;
        DeletedAt = deletedAt;
    }

    public Guid QuarantineId { get; }
    public BatchId BatchId { get; }
    public int LineNumber { get; }
    public LineRejectionCode ErrorCode { get; }
    public ProtectedContentReference EncryptedContentLocation { get; }
    public string ContentDigest { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset DeleteAfter { get; }
    public DateTimeOffset? DeletedAt { get; }

    /// <summary>Call only after the protected store confirms deletion; this entity performs no IO.</summary>
    public QuarantinedRejection MarkExpiredContentDeleted(DateTimeOffset deletedAt)
    {
        var utc = deletedAt.ToUniversalTime();
        if (utc < DeleteAfter)
            throw new ArgumentOutOfRangeException(nameof(deletedAt), "Expired-content deletion cannot precede its deadline.");
        return DeletedAt is not null ? this : new(QuarantineId, BatchId, LineNumber, ErrorCode,
            EncryptedContentLocation, ContentDigest, CreatedAt, DeleteAfter, utc);
    }
    public override string ToString() => "[QuarantinedRejection]";
}
