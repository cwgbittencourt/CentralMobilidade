using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CentralMobilidade.Telemetry.Infrastructure.Persistence.Configurations;

public sealed class QuarantinedRejectionConfiguration : IEntityTypeConfiguration<QuarantinedRejection>
{
    public void Configure(EntityTypeBuilder<QuarantinedRejection> b)
    {
        b.ToTable("quarantined_rejections");
        b.HasKey(e => e.QuarantineId);
        b.Property(e => e.ErrorCode).HasMaxLength(64);
        b.Property(e => e.EncryptedContentLocation).HasMaxLength(2048);
        b.Property(e => e.ContentDigest).HasMaxLength(128);

        b.HasIndex(e => e.DeleteAfter);
        b.HasIndex(e => new { e.BatchId, e.LineNumber }).IsUnique();
        b.ToTable("quarantined_rejections", t => t.HasCheckConstraint("ck_quarantine_retention", "\"LineNumber\" > 0 AND \"DeleteAfter\" >= \"CreatedAt\" AND \"DeleteAfter\" <= \"CreatedAt\" + interval '30 days'"));
    }
}

