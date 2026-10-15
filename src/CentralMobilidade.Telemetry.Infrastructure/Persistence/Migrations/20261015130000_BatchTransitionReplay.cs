using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace CentralMobilidade.Telemetry.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TelemetryControlDbContext))]
[Migration("20261015130000_BatchTransitionReplay")]
public sealed class BatchTransitionReplay : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<DateTimeOffset>(
        name: "LastTransitionAt", table: "ingestion_batches", type: "timestamp with time zone", nullable: true);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(
        name: "LastTransitionAt", table: "ingestion_batches");
}
