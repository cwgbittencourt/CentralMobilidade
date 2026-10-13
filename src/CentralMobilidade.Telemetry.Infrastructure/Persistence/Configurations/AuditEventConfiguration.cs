using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CentralMobilidade.Telemetry.Infrastructure.Persistence.Configurations;

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.ToTable("audit_events");
        b.HasKey(e => e.EventId);
        b.Property(e => e.ActorId).HasMaxLength(256);
        b.Property(e => e.GrafanaOrganizationId).HasMaxLength(256);
        b.Property(e => e.OpaqueResourceId).HasMaxLength(256);
        b.Property(e => e.TraceId).HasMaxLength(128);
        b.Property(e => e.ActorKind).HasConversion<string>().HasMaxLength(32);
        b.Property(e => e.Action).HasConversion<string>().HasMaxLength(32);
        b.Property(e => e.ResourceCategory).HasConversion<string>().HasMaxLength(32);
        b.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(32);
        b.HasIndex(e => e.OccurredAt);
        b.Property(e => e.CommittedAt).HasDefaultValueSql("clock_timestamp()").ValueGeneratedOnAdd();
    }
}

