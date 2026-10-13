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
        b.Property(e => e.Destination).HasConversion<string>().HasMaxLength(32);
        b.Property(e => e.StorageLocation).HasConversion<string>().HasMaxLength(32);
        b.HasIndex(e => e.GuardExpiresAt);
        b.HasIndex(e => e.LeaseUntil);
        b.Property(e => e.FencingVersion).IsConcurrencyToken();
        b.ToTable("event_identity_guards", t => t.HasCheckConstraint("ck_guard_owner", "\"OwnerLineNumber\" > 0 AND \"FencingVersion\" > 0"));
    }
}

