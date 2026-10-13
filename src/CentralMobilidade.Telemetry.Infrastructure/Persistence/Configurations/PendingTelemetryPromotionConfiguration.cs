using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CentralMobilidade.Telemetry.Infrastructure.Persistence.Configurations;

public sealed class PendingTelemetryPromotionConfiguration : IEntityTypeConfiguration<PendingTelemetryPromotion>
{
    public void Configure(EntityTypeBuilder<PendingTelemetryPromotion> b)
    {
        b.ToTable("pending_telemetry_promotions");
        b.HasKey(e => e.PromotionId);
        b.Property(e => e.CompanyId).HasMaxLength(256);
        b.Property(e => e.LastErrorCode).HasMaxLength(64);
        b.Property(e => e.State).HasConversion<string>().HasMaxLength(32);
        b.HasIndex(e => e.State);
        b.Property(e => e.RowVersion).IsConcurrencyToken();
        b.ToTable("pending_telemetry_promotions", t => t.HasCheckConstraint("ck_promotion_counts", "\"RangeEnd\" >= \"RangeStart\" AND \"ExpectedPointCount\" >= 0 AND \"Attempts\" >= 0 AND (\"ConfirmedPointCount\" IS NULL OR \"ConfirmedPointCount\" >= 0)"));
    }
}

