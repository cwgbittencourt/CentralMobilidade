using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CentralMobilidade.Telemetry.Infrastructure.Persistence.Configurations;

public sealed class IngestionBatchConfiguration : IEntityTypeConfiguration<IngestionBatch>
{
    public void Configure(EntityTypeBuilder<IngestionBatch> b)
    {
        b.ToTable("ingestion_batches");
        b.HasKey(e => e.BatchId);
        b.Property(e => e.TraceId).HasMaxLength(128);
        b.Property(e => e.ProducerId).HasMaxLength(256);
        b.Property(e => e.ContractVersion).HasMaxLength(16);
        b.Property(e => e.TerminalErrorCode).HasMaxLength(64);
        b.Property(e => e.State).HasConversion<string>().HasMaxLength(32);
        b.HasIndex(e => e.ExpiresAt);
        b.HasIndex(e => new { e.ProducerId, e.ReceivedAt });
        b.Property(e => e.RowVersion).IsConcurrencyToken();
        b.ToTable("ingestion_batches", t =>
        {
            t.HasCheckConstraint("ck_batch_counts", "\"CompressedBytes\" >= 0 AND \"Accepted\" >= 0 AND \"Rejected\" >= 0 AND \"Replayed\" >= 0 AND \"RetryCount\" >= 0 AND (\"DecompressedBytes\" IS NULL OR \"DecompressedBytes\" >= 0) AND (\"TotalLines\" IS NULL OR \"TotalLines\" >= 0)");
            t.HasCheckConstraint("ck_batch_retention", "isfinite(\"ReceivedAt\") AND isfinite(\"ExpiresAt\") AND \"ExpiresAt\" > \"ReceivedAt\" AND \"ExpiresAt\" <= \"ReceivedAt\" + interval '720 hours'");
            t.HasCheckConstraint("ck_batch_version", "\"RowVersion\" > 0");
            t.HasCheckConstraint("ck_batch_state", "\"State\" IN ('Receiving','Publishing','Accepted','Processing','Retrying','Completed','CompletedWithRejections','Rejected','PublishFailed','Failed','Expired')");
        });
    }
}

