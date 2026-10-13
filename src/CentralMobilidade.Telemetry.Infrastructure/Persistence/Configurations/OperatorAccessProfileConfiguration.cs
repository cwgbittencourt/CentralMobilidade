using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CentralMobilidade.Telemetry.Infrastructure.Persistence.Configurations;

public sealed class OperatorAccessProfileConfiguration : IEntityTypeConfiguration<OperatorAccessProfile>
{
    public void Configure(EntityTypeBuilder<OperatorAccessProfile> b)
    {
        b.ToTable("operator_access_profiles");
        b.HasKey(e => e.ActorId);
        b.Property(e => e.ActorId).HasMaxLength(256);
        b.Property(e => e.GrafanaOrganizationId).HasMaxLength(256);
        b.Property(e => e.Kind).HasConversion<string>().HasMaxLength(32);

        b.Property(e => e.RowVersion).IsConcurrencyToken();
        b.ToTable("operator_access_profiles", t => t.HasCheckConstraint("ck_operator_kind", "(\"Kind\" = 'Human' AND \"GrafanaOrganizationId\" IS NULL) OR (\"Kind\" = 'ViewerPresentationService' AND \"GrafanaOrganizationId\" IS NOT NULL AND \"Permissions\" <@ ARRAY['telemetry.read','driver.read']::text[])"));
    }
}

