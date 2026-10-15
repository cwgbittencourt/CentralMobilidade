using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CentralMobilidade.Telemetry.Infrastructure.Persistence;

public sealed class TelemetryControlDbContext(DbContextOptions<TelemetryControlDbContext> options) : DbContext(options)
{
    public DbSet<IngestionBatch> Batches => Set<IngestionBatch>();
    public DbSet<LineProcessingResult> LineResults => Set<LineProcessingResult>();
    public DbSet<QuarantinedRejection> QuarantinedRejections => Set<QuarantinedRejection>();
    public DbSet<EventIdentityGuard> IdentityGuards => Set<EventIdentityGuard>();
    public DbSet<EventIdentityEvidence> IdentityEvidence => Set<EventIdentityEvidence>();
    public DbSet<OperatorAccessProfile> OperatorProfiles => Set<OperatorAccessProfile>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TelemetryControlDbContext).Assembly);

    private void PrepareChanges()
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is AuditEvent && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Audit events are append-only.");
            var version = entry.Metadata.FindProperty("RowVersion");
            if (version is not null && entry.State is EntityState.Added or EntityState.Modified)
                entry.Property("RowVersion").CurrentValue = entry.State == EntityState.Added ? 1L :
                    checked((long)entry.Property("RowVersion").OriginalValue! + 1);
            foreach (var property in entry.Properties.Where(p => p.IsModified || entry.State == EntityState.Added))
                if (property.CurrentValue is DateTimeOffset timestamp && timestamp.Offset != TimeSpan.Zero)
                    throw new InvalidOperationException("Persistence timestamps must be UTC.");
        }
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PrepareChanges();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
