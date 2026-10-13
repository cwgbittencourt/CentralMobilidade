using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CentralMobilidade.Telemetry.Infrastructure.Persistence.Configurations;

public sealed class LineProcessingResultConfiguration : IEntityTypeConfiguration<LineProcessingResult>
{
    public void Configure(EntityTypeBuilder<LineProcessingResult> b)
    {
        b.ToTable("line_processing_results");
        b.HasKey(e => new { e.BatchId, e.LineNumber });
        b.Property(e => e.ErrorCode).HasMaxLength(64);
        b.Property(e => e.CompanyId).HasMaxLength(256);
        b.Property(e => e.VehicleId).HasMaxLength(256);
        b.Property(e => e.SafeDigest).HasMaxLength(128);
        b.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(32);

        b.HasOne<IngestionBatch>().WithMany().HasForeignKey(e => e.BatchId).OnDelete(DeleteBehavior.Cascade);
        b.ToTable("line_processing_results", t => t.HasCheckConstraint("ck_line_number", "\"LineNumber\" > 0"));
    }
}

