using System;
using System.Linq;
using System.Text;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Domain.Processing;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.UnitTests.Domain;

public sealed class ProcessingEntityTests
{
    private static readonly BatchId Batch = new(Guid.NewGuid());
    private static readonly DateTimeOffset Created = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Key = Enumerable.Repeat((byte)0xa5, 32).ToArray();
    private static readonly SafeLineDigest Digest = SafeLineDigest.Compute(Encoding.UTF8.GetBytes("PRIVATE_LINE"), Key);
    private const string ContentDigest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(ProcessingOutcome.Accepted)]
    [InlineData(ProcessingOutcome.IdenticalReplay)]
    public void Successful_results_have_identity_and_no_rejection_metadata(ProcessingOutcome outcome)
    {
        var result = Result(outcome);
        result.BatchId.ShouldBe(Batch);
        result.LineNumber.ShouldBe(17);
        result.Outcome.ShouldBe(outcome);
        result.ErrorCode.ShouldBeNull();
        result.QuarantineId.ShouldBeNull();
        result.CompanyId!.Value.ShouldBe("company");
        result.VehicleId!.Value.ShouldBe("vehicle");
        result.OccurredAt.ShouldBe(Created);
        result.SafeDigest.ShouldBe(Digest);
    }

    [Fact]
    public void Rejection_can_preserve_only_safely_parsed_identifiers_and_opaque_quarantine_id()
    {
        var id = Guid.NewGuid();
        var result = new LineProcessingResult(Batch, 2, ProcessingOutcome.Rejected, Digest, Created,
            LineRejectionCode.LP_MISSING_REQUIRED_TAG, vehicleId: new("vehicle"), quarantineId: id);
        result.CompanyId.ShouldBeNull();
        result.VehicleId!.Value.ShouldBe("vehicle");
        result.QuarantineId.ShouldBe(id);
        result.ErrorCode.ShouldBe(LineRejectionCode.LP_MISSING_REQUIRED_TAG);
    }

    [Theory]
    [InlineData(ProcessingOutcome.Accepted)]
    [InlineData(ProcessingOutcome.IdenticalReplay)]
    public void Successful_results_reject_error_codes_or_quarantine_references(ProcessingOutcome outcome)
    {
        Should.Throw<ArgumentException>(() => Result(outcome, LineRejectionCode.LP_INVALID_SYNTAX));
        Should.Throw<ArgumentException>(() => Result(outcome, quarantineId: Guid.NewGuid()));
        Should.Throw<ArgumentException>(() => new LineProcessingResult(Batch, 1, outcome, Digest, Created));
    }

    [Fact]
    public void Rejections_require_known_code_and_valid_optional_quarantine_id()
    {
        Should.Throw<ArgumentException>(() => Result(ProcessingOutcome.Rejected));
        Should.Throw<ArgumentException>(() => Result(ProcessingOutcome.Rejected, (LineRejectionCode)999));
        Should.Throw<ArgumentException>(() => Result(ProcessingOutcome.Rejected, LineRejectionCode.LP_INVALID_SYNTAX, Guid.Empty));
        Should.Throw<ArgumentException>(() => Result((ProcessingOutcome)999));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Both_entities_require_one_based_line_numbers(int number)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new LineProcessingResult(Batch, number, ProcessingOutcome.Rejected, Digest, Created, LineRejectionCode.LP_INVALID_SYNTAX));
        Should.Throw<ArgumentOutOfRangeException>(() => Rejection(line: number));
    }

    [Fact]
    public void Required_references_and_ids_cannot_be_empty()
    {
        Should.Throw<ArgumentNullException>(() => new LineProcessingResult(null!, 1, ProcessingOutcome.Rejected, Digest, Created, LineRejectionCode.LP_INVALID_SYNTAX));
        Should.Throw<ArgumentNullException>(() => new LineProcessingResult(Batch, 1, ProcessingOutcome.Rejected, null!, Created, LineRejectionCode.LP_INVALID_SYNTAX));
        Should.Throw<ArgumentException>(() => Rejection(id: Guid.Empty));
        Should.Throw<ArgumentNullException>(() => new QuarantinedRejection(Guid.NewGuid(), null!, 1, LineRejectionCode.LP_INVALID_SYNTAX,
            new("private-reference"), ContentDigest, Created, Created.AddDays(30)));
    }

    [Fact]
    public void Domain_rejection_enum_matches_the_stable_contract_catalog_exactly()
    {
        Enum.GetNames<LineRejectionCode>().OrderBy(value => value).ShouldBe(TelemetryErrorCodes.RejectionCodes.OrderBy(value => value));
    }

    [Fact]
    public void Safe_digest_is_keyed_deterministic_and_not_a_raw_line_hash()
    {
        Digest.ShouldBe(SafeLineDigest.Compute(Encoding.UTF8.GetBytes("PRIVATE_LINE"), Key));
        Digest.ShouldNotBe(SafeLineDigest.Compute(Encoding.UTF8.GetBytes("PRIVATE_LINE"), Enumerable.Repeat((byte)0xb5, 32).ToArray()));
        Digest.ShouldNotBe(SafeLineDigest.Compute(Encoding.UTF8.GetBytes("DIFFERENT_LINE"), Key));
        Digest.Value.ShouldStartWith("hmac-sha256:v1:");
        Digest.Value.ShouldNotContain("PRIVATE_LINE");
        SafeLineDigest.FromStoredValue(Digest.Value).ShouldBe(Digest);
        Should.Throw<ArgumentException>(() => SafeLineDigest.FromStoredValue(new string('a', 64)));
        Should.Throw<ArgumentException>(() => SafeLineDigest.Compute([], new byte[31]));
        Digest.ToString().ShouldBe("[SafeLineDigest]");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(30 * 24 * 60 * 60 + 1)]
    public void Quarantine_deadline_must_be_between_creation_and_30_days(int seconds)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Rejection(deadline: Created.AddSeconds(seconds)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public void Inclusive_retention_boundaries_are_valid(int days)
    {
        var result = Rejection(deadline: Created.AddDays(days));
        result.DeleteAfter.ShouldBe(Created.AddDays(days));
        result.DeletedAt.ShouldBeNull();
    }

    [Fact]
    public void Confirmed_expired_deletion_is_immutable_and_replay_safe()
    {
        var result = Rejection();
        Should.Throw<ArgumentOutOfRangeException>(() => result.MarkExpiredContentDeleted(result.DeleteAfter.AddTicks(-1)));
        var deleted = result.MarkExpiredContentDeleted(result.DeleteAfter);
        result.DeletedAt.ShouldBeNull();
        deleted.DeletedAt.ShouldBe(result.DeleteAfter);
        deleted.MarkExpiredContentDeleted(result.DeleteAfter.AddHours(1)).ShouldBeSameAs(deleted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\nsecret")]
    [InlineData(" ")]
    public void Protected_reference_rejects_invalid_locations_without_echoing_them(string location)
    {
        Should.Throw<ArgumentException>(() => new ProtectedContentReference(location));
    }

    [Theory]
    [InlineData("")]
    [InlineData("raw-content")]
    [InlineData("ffffffff")]
    public void Content_integrity_requires_sha256_hex(string digest)
    {
        Should.Throw<ArgumentException>(() => Rejection(digest: digest));
    }

    [Fact]
    public void Entities_normalize_timestamps_and_hide_private_references_in_diagnostics()
    {
        var offset = Created.ToOffset(TimeSpan.FromHours(-3));
        var location = new ProtectedContentReference("PRIVATE_STORE_REFERENCE");
        var result = new QuarantinedRejection(Guid.NewGuid(), Batch, 17, LineRejectionCode.LP_INVALID_SYNTAX,
            location, ContentDigest.ToUpperInvariant(), offset, offset.AddDays(30));
        result.CreatedAt.ShouldBe(Created);
        result.CreatedAt.Offset.ShouldBe(TimeSpan.Zero);
        result.ContentDigest.ShouldBe(ContentDigest);
        result.EncryptedContentLocation.Value.ShouldBe("PRIVATE_STORE_REFERENCE");
        result.ToString().ShouldBe("[QuarantinedRejection]");
        location.ToString().ShouldBe("[ProtectedContentReference]");
        Result(ProcessingOutcome.Accepted).ToString().ShouldBe("[LineProcessingResult]");
        typeof(LineProcessingResult).GetProperties().All(property => property.SetMethod is null).ShouldBeTrue();
        typeof(QuarantinedRejection).GetProperties().All(property => property.SetMethod is null).ShouldBeTrue();
    }

    private static LineProcessingResult Result(ProcessingOutcome outcome, LineRejectionCode? error = null, Guid? quarantineId = null) =>
        new(Batch, 17, outcome, Digest, Created, error, new("company"), new("vehicle"), quarantineId);
    private static QuarantinedRejection Rejection(int line = 17, Guid? id = null, DateTimeOffset? deadline = null, string digest = ContentDigest) =>
        new(id ?? Guid.NewGuid(), Batch, line, LineRejectionCode.LP_INVALID_SYNTAX, new("PRIVATE_STORE_REFERENCE"), digest, Created, deadline ?? Created.AddDays(30));
}
