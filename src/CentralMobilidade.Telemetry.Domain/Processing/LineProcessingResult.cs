using System;
using CentralMobilidade.Telemetry.Domain.ValueObjects;

namespace CentralMobilidade.Telemetry.Domain.Processing;

public sealed record LineProcessingResult
{
    public LineProcessingResult(BatchId batchId, int lineNumber, ProcessingOutcome outcome,
        SafeLineDigest safeDigest, DateTimeOffset occurredAt, LineRejectionCode? errorCode = null,
        CompanyId? companyId = null, VehicleId? vehicleId = null, Guid? quarantineId = null)
    {
        ArgumentNullException.ThrowIfNull(batchId);
        ArgumentNullException.ThrowIfNull(safeDigest);
        if (lineNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be one-based.");
        if (!Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome), "Unknown processing outcome.");
        if (errorCode is not null && !Enum.IsDefined(errorCode.Value))
            throw new ArgumentOutOfRangeException(nameof(errorCode), "Unknown rejection code.");
        if (quarantineId == Guid.Empty)
            throw new ArgumentException("Quarantine ID must be nonempty when present.", nameof(quarantineId));
        if (outcome == ProcessingOutcome.Rejected)
        {
            if (errorCode is null)
                throw new ArgumentException("Rejected lines require a stable rejection code.", nameof(errorCode));
        }
        else
        {
            if (errorCode is not null || quarantineId is not null)
                throw new ArgumentException("Successful lines cannot carry rejection metadata.");
            if (companyId is null || vehicleId is null)
                throw new ArgumentException("Successful lines require safely parsed event identity.");
        }
        BatchId = batchId;
        LineNumber = lineNumber;
        Outcome = outcome;
        ErrorCode = errorCode;
        CompanyId = companyId;
        VehicleId = vehicleId;
        SafeDigest = safeDigest;
        QuarantineId = quarantineId;
        OccurredAt = occurredAt.ToUniversalTime();
    }
    public BatchId BatchId { get; }
    public int LineNumber { get; }
    public ProcessingOutcome Outcome { get; }
    public LineRejectionCode? ErrorCode { get; }
    public CompanyId? CompanyId { get; }
    public VehicleId? VehicleId { get; }
    public SafeLineDigest SafeDigest { get; }
    public Guid? QuarantineId { get; }
    public DateTimeOffset OccurredAt { get; }
    public override string ToString() => "[LineProcessingResult]";
}
