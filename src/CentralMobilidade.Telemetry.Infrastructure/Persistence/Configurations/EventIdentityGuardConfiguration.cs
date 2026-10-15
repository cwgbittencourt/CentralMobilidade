using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CentralMobilidade.Telemetry.Infrastructure.Persistence.Configurations;

public sealed class EventIdentityGuardConfiguration : IEntityTypeConfiguration<EventIdentityGuard>
{
    public void Configure(EntityTypeBuilder<EventIdentityGuard> b)
    {
        b.ToTable("event_identity_guards");
        b.HasKey(e => new { e.CompanyId, e.VehicleId, e.EventTimestamp });
        b.Property(e => e.CompanyId).HasMaxLength(256);
        b.Property(e => e.VehicleId).HasMaxLength(256);
        b.Property(e => e.State).HasConversion<string>().HasMaxLength(32);
        b.HasIndex(e => e.GuardExpiresAt);
        b.HasIndex(e => e.LeaseUntil);
        b.Property(e => e.FencingVersion).IsConcurrencyToken();
        b.Property(e => e.CompanyId).UseCollation("C");
        b.Property(e => e.VehicleId).UseCollation("C");
        b.ToTable("event_identity_guards", t =>
        {
            t.HasCheckConstraint("ck_guard_owner", "\"OwnerLineNumber\" > 0 AND \"FencingVersion\" > 0 AND \"OwnerBatchId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_guard_identity", "octet_length(\"CompanyId\") BETWEEN 1 AND 256 AND octet_length(\"VehicleId\") BETWEEN 1 AND 256 AND isfinite(\"EventTimestamp\") AND date_trunc('milliseconds', \"EventTimestamp\") = \"EventTimestamp\"");
            t.HasCheckConstraint("ck_guard_retention", "isfinite(\"TelemetryExpiresAt\") AND isfinite(\"GuardExpiresAt\") AND \"TelemetryExpiresAt\" > \"EventTimestamp\" AND \"GuardExpiresAt\" >= \"TelemetryExpiresAt\"");
            t.HasCheckConstraint("ck_guard_state", "(\"State\" IN ('Reserved','Writing') AND \"LeaseUntil\" IS NOT NULL AND isfinite(\"LeaseUntil\")) OR (\"State\" = 'Committed' AND \"LeaseUntil\" IS NULL)");
        });
    }
}

