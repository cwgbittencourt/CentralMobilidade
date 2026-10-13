using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CentralMobilidade.Telemetry.Infrastructure.Persistence.Configurations;

public sealed class EventIdentityEvidenceConfiguration : IEntityTypeConfiguration<EventIdentityEvidence>
{
    public void Configure(EntityTypeBuilder<EventIdentityEvidence> b)
    {
        b.ToTable("event_identity_evidence");
        b.HasKey(e => new { e.CompanyId, e.VehicleId, e.EventTimestamp });
        b.Property(e => e.CompanyId).HasMaxLength(256);
        b.Property(e => e.VehicleId).HasMaxLength(256);

        b.HasIndex(e => e.EvidenceExpiresAt);
        // No cascade to the guard or batch: retention clocks are independent.
        b.ToTable("event_identity_evidence", t => t.HasCheckConstraint("ck_evidence", "octet_length(\"CanonicalFingerprint\") = 32 AND \"EvidenceExpiresAt\" = \"FirstSeenAt\" + interval '30 days'"));
    }
}

