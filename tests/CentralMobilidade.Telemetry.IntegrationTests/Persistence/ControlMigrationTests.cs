using System.Linq;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Persistence;

public sealed partial class ControlMigrationTests
{
    [Fact]
    public void Initial_migration_exists_and_snapshot_matches_the_model()
    {
        using var db = new TelemetryControlDbContext(new DbContextOptionsBuilder<TelemetryControlDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=model_only").Options);
        db.Database.GetMigrations().Count().ShouldBe(4);
        db.Database.HasPendingModelChanges().ShouldBeFalse();
    }
}
