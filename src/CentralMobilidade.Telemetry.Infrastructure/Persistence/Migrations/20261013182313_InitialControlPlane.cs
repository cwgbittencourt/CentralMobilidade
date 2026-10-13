using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CentralMobilidade.Telemetry.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialControlPlane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ActorKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    GrafanaOrganizationId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResourceCategory = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OpaqueResourceId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CompanyScope = table.Column<string[]>(type: "text[]", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TraceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CommittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "event_identity_evidence",
                columns: table => new
                {
                    CompanyId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    VehicleId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    EventTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CanonicalFingerprint = table.Column<byte[]>(type: "bytea", nullable: false),
                    FirstBatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EvidenceExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_identity_evidence", x => new { x.CompanyId, x.VehicleId, x.EventTimestamp });
                    table.CheckConstraint("ck_evidence", "octet_length(\"CanonicalFingerprint\") = 32 AND isfinite(\"FirstSeenAt\") AND \"EvidenceExpiresAt\" = \"FirstSeenAt\" + interval '720 hours' AND date_trunc('milliseconds', \"EventTimestamp\") = \"EventTimestamp\"");
                });

            migrationBuilder.CreateTable(
                name: "event_identity_guards",
                columns: table => new
                {
                    CompanyId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    VehicleId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    EventTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OwnerBatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerLineNumber = table.Column<long>(type: "bigint", nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FencingVersion = table.Column<long>(type: "bigint", nullable: false),
                    Destination = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StorageLocation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    GuardExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TelemetryExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PromotionOwner = table.Column<Guid>(type: "uuid", nullable: true),
                    PromotionLeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_identity_guards", x => new { x.CompanyId, x.VehicleId, x.EventTimestamp });
                    table.CheckConstraint("ck_guard_identity", "octet_length(\"CompanyId\") BETWEEN 1 AND 256 AND octet_length(\"VehicleId\") BETWEEN 1 AND 256 AND isfinite(\"EventTimestamp\") AND date_trunc('milliseconds', \"EventTimestamp\") = \"EventTimestamp\"");
                    table.CheckConstraint("ck_guard_location", "\"Destination\" IN ('Queryable','Pending') AND ((\"StorageLocation\" IN ('PendingCommitted','QueryableCommitted') AND \"PromotionOwner\" IS NULL AND \"PromotionLeaseUntil\" IS NULL) OR (\"State\" = 'Committed' AND \"StorageLocation\" = 'Promoting' AND \"PromotionOwner\" IS NOT NULL AND \"PromotionLeaseUntil\" IS NOT NULL AND isfinite(\"PromotionLeaseUntil\")))");
                    table.CheckConstraint("ck_guard_owner", "\"OwnerLineNumber\" > 0 AND \"FencingVersion\" > 0 AND \"OwnerBatchId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_guard_retention", "isfinite(\"TelemetryExpiresAt\") AND isfinite(\"GuardExpiresAt\") AND \"TelemetryExpiresAt\" > \"EventTimestamp\" AND \"GuardExpiresAt\" >= \"TelemetryExpiresAt\"");
                    table.CheckConstraint("ck_guard_state", "(\"State\" IN ('Reserved','Writing') AND \"LeaseUntil\" IS NOT NULL AND isfinite(\"LeaseUntil\")) OR (\"State\" = 'Committed' AND \"LeaseUntil\" IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "ingestion_batches",
                columns: table => new
                {
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    TraceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProducerId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ContractVersion = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DurableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProcessingStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompressedBytes = table.Column<long>(type: "bigint", nullable: false),
                    DecompressedBytes = table.Column<long>(type: "bigint", nullable: true),
                    TotalLines = table.Column<long>(type: "bigint", nullable: true),
                    Accepted = table.Column<long>(type: "bigint", nullable: false),
                    Rejected = table.Column<long>(type: "bigint", nullable: false),
                    Replayed = table.Column<long>(type: "bigint", nullable: false),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    TerminalErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CompanyScope = table.Column<string[]>(type: "text[]", nullable: false),
                    CompanyScopeComplete = table.Column<bool>(type: "boolean", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingestion_batches", x => x.BatchId);
                    table.CheckConstraint("ck_batch_counts", "\"CompressedBytes\" >= 0 AND \"Accepted\" >= 0 AND \"Rejected\" >= 0 AND \"Replayed\" >= 0 AND \"RetryCount\" >= 0 AND (\"DecompressedBytes\" IS NULL OR \"DecompressedBytes\" >= 0) AND (\"TotalLines\" IS NULL OR \"TotalLines\" >= 0)");
                    table.CheckConstraint("ck_batch_retention", "isfinite(\"ReceivedAt\") AND isfinite(\"ExpiresAt\") AND \"ExpiresAt\" > \"ReceivedAt\" AND \"ExpiresAt\" <= \"ReceivedAt\" + interval '720 hours'");
                    table.CheckConstraint("ck_batch_state", "\"State\" IN ('Receiving','Publishing','Accepted','Processing','Retrying','Completed','CompletedWithRejections','Rejected','PublishFailed','Failed','Expired')");
                    table.CheckConstraint("ck_batch_version", "\"RowVersion\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "operator_access_profiles",
                columns: table => new
                {
                    ActorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CredentialIds = table.Column<string[]>(type: "text[]", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Permissions = table.Column<string[]>(type: "text[]", nullable: false),
                    CompanyIds = table.Column<string[]>(type: "text[]", nullable: false),
                    GrafanaOrganizationId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operator_access_profiles", x => x.ActorId);
                    table.CheckConstraint("ck_operator_kind", "(\"Kind\" = 'Human' AND \"GrafanaOrganizationId\" IS NULL) OR (\"Kind\" = 'ViewerPresentationService' AND \"GrafanaOrganizationId\" IS NOT NULL AND length(btrim(\"GrafanaOrganizationId\")) > 0 AND cardinality(\"CompanyIds\") > 0 AND \"Permissions\" <@ ARRAY['telemetry.read','driver.read']::text[])");
                    table.CheckConstraint("ck_operator_permissions", "\"Permissions\" <@ ARRAY['batch.read','telemetry.read','driver.read','quarantine.raw.read','cross_company.read']::text[] AND array_position(\"Permissions\", NULL) IS NULL");
                    table.CheckConstraint("ck_operator_scope", "array_position(\"CompanyIds\", NULL) IS NULL AND array_position(\"CompanyIds\", '') IS NULL AND array_position(\"CredentialIds\", NULL) IS NULL AND array_position(\"CredentialIds\", '') IS NULL AND length(btrim(\"ActorId\")) > 0 AND \"RowVersion\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "pending_telemetry_promotions",
                columns: table => new
                {
                    PromotionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SourceBatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    RangeStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RangeEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    ExpectedPointCount = table.Column<long>(type: "bigint", nullable: false),
                    ConfirmedPointCount = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_telemetry_promotions", x => x.PromotionId);
                    table.CheckConstraint("ck_promotion_counts", "\"RangeEnd\" >= \"RangeStart\" AND \"ExpectedPointCount\" >= 0 AND \"Attempts\" >= 0 AND (\"ConfirmedPointCount\" IS NULL OR \"ConfirmedPointCount\" >= 0)");
                    table.CheckConstraint("ck_promotion_state", "\"RowVersion\" > 0 AND \"State\" IN ('Discovered','Copying','Copied','SourceDeleted','Completed','Retrying')");
                });

            migrationBuilder.CreateTable(
                name: "quarantined_rejections",
                columns: table => new
                {
                    QuarantineId = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNumber = table.Column<long>(type: "bigint", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EncryptedContentLocation = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ContentDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeleteAfter = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quarantined_rejections", x => x.QuarantineId);
                    table.CheckConstraint("ck_quarantine_retention", "\"LineNumber\" > 0 AND \"DeleteAfter\" >= \"CreatedAt\" AND \"DeleteAfter\" <= \"CreatedAt\" + interval '30 days'");
                });

            migrationBuilder.CreateTable(
                name: "line_processing_results",
                columns: table => new
                {
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNumber = table.Column<long>(type: "bigint", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CompanyId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    VehicleId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SafeDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    QuarantineId = table.Column<Guid>(type: "uuid", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_line_processing_results", x => new { x.BatchId, x.LineNumber });
                    table.CheckConstraint("ck_line_number", "\"LineNumber\" > 0");
                    table.ForeignKey(
                        name: "FK_line_processing_results_ingestion_batches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "ingestion_batches",
                        principalColumn: "BatchId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_OccurredAt",
                table: "audit_events",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_event_identity_evidence_EvidenceExpiresAt",
                table: "event_identity_evidence",
                column: "EvidenceExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_event_identity_guards_GuardExpiresAt",
                table: "event_identity_guards",
                column: "GuardExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_event_identity_guards_LeaseUntil",
                table: "event_identity_guards",
                column: "LeaseUntil");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_batches_ExpiresAt",
                table: "ingestion_batches",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_batches_ProducerId_ReceivedAt",
                table: "ingestion_batches",
                columns: new[] { "ProducerId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_operator_access_profiles_GrafanaOrganizationId",
                table: "operator_access_profiles",
                column: "GrafanaOrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_pending_telemetry_promotions_CompanyId_State_UpdatedAt",
                table: "pending_telemetry_promotions",
                columns: new[] { "CompanyId", "State", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_pending_telemetry_promotions_State",
                table: "pending_telemetry_promotions",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_quarantined_rejections_BatchId_LineNumber",
                table: "quarantined_rejections",
                columns: new[] { "BatchId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_quarantined_rejections_DeleteAfter",
                table: "quarantined_rejections",
                column: "DeleteAfter");
            migrationBuilder.Sql(InitialControlPlaneGuards.UpSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(InitialControlPlaneGuards.DownSql);
            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "event_identity_evidence");

            migrationBuilder.DropTable(
                name: "event_identity_guards");

            migrationBuilder.DropTable(
                name: "line_processing_results");

            migrationBuilder.DropTable(
                name: "operator_access_profiles");

            migrationBuilder.DropTable(
                name: "pending_telemetry_promotions");

            migrationBuilder.DropTable(
                name: "quarantined_rejections");

            migrationBuilder.DropTable(
                name: "ingestion_batches");
        }
    }
}
