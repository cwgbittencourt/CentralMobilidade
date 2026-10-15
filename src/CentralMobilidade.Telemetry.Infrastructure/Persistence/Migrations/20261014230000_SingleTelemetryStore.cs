using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CentralMobilidade.Telemetry.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TelemetryControlDbContext))]
[Migration("20261014230000_SingleTelemetryStore")]
public sealed class SingleTelemetryStore : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(PreflightSql);
        migrationBuilder.Sql(SingleStoreGuardSql);
        migrationBuilder.Sql("""
            ALTER TABLE public.event_identity_guards DROP CONSTRAINT ck_guard_location,
                DROP COLUMN "Destination", DROP COLUMN "StorageLocation",
                DROP COLUMN "PromotionOwner", DROP COLUMN "PromotionLeaseUntil";
            DROP TABLE public.pending_telemetry_promotions;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '10s';
            LOCK TABLE public.event_identity_guards, public.event_identity_evidence IN ACCESS EXCLUSIVE MODE;
            DO $gate$
            BEGIN
              IF EXISTS (SELECT 1 FROM public.event_identity_guards) OR EXISTS (SELECT 1 FROM public.event_identity_evidence) THEN
                RAISE EXCEPTION 'Populated single-store downgrade requires coordinated backup restore' USING ERRCODE='23514';
              END IF;
            END $gate$;
            ALTER TABLE public.event_identity_guards ADD COLUMN "Destination" character varying(32) NOT NULL DEFAULT 'Queryable',
                ADD COLUMN "StorageLocation" character varying(32) NOT NULL DEFAULT 'QueryableCommitted',
                ADD COLUMN "PromotionOwner" uuid, ADD COLUMN "PromotionLeaseUntil" timestamp with time zone;
            ALTER TABLE public.event_identity_guards ALTER COLUMN "Destination" DROP DEFAULT,
                ALTER COLUMN "StorageLocation" DROP DEFAULT,
                ADD CONSTRAINT ck_guard_location CHECK (
                 "Destination" IN ('Queryable','Pending') AND (("StorageLocation" IN ('PendingCommitted','QueryableCommitted')
                 AND "PromotionOwner" IS NULL AND "PromotionLeaseUntil" IS NULL) OR ("State"='Committed'
                 AND "StorageLocation"='Promoting' AND "PromotionOwner" IS NOT NULL
                 AND "PromotionLeaseUntil" IS NOT NULL AND isfinite("PromotionLeaseUntil"))));
            CREATE TABLE public.pending_telemetry_promotions (
              "PromotionId" uuid NOT NULL CONSTRAINT "PK_pending_telemetry_promotions" PRIMARY KEY,
              "CompanyId" character varying(256) NOT NULL, "SourceBatchId" uuid NOT NULL,
              "RangeStart" timestamp with time zone NOT NULL, "RangeEnd" timestamp with time zone NOT NULL,
              "State" character varying(32) NOT NULL, "Attempts" integer NOT NULL,
              "ExpectedPointCount" bigint NOT NULL, "ConfirmedPointCount" bigint,
              "CreatedAt" timestamp with time zone NOT NULL, "UpdatedAt" timestamp with time zone NOT NULL,
              "CompletedAt" timestamp with time zone, "LastErrorCode" character varying(64), "RowVersion" bigint NOT NULL,
              CONSTRAINT ck_promotion_counts CHECK ("RangeEnd">="RangeStart" AND "ExpectedPointCount">=0 AND "Attempts">=0 AND ("ConfirmedPointCount" IS NULL OR "ConfirmedPointCount">=0)),
              CONSTRAINT ck_promotion_state CHECK ("RowVersion">0 AND "State" IN ('Discovered','Copying','Copied','SourceDeleted','Completed','Retrying')));
            CREATE INDEX "IX_pending_telemetry_promotions_State" ON public.pending_telemetry_promotions ("State");
            CREATE INDEX "IX_pending_telemetry_promotions_CompanyId_State_UpdatedAt" ON public.pending_telemetry_promotions ("CompanyId","State","UpdatedAt");
            CREATE TRIGGER pending_telemetry_promotions_version BEFORE UPDATE ON public.pending_telemetry_promotions
              FOR EACH ROW EXECUTE FUNCTION public.telemetry_require_next_version();
            """);
        migrationBuilder.Sql(LegacyGuardSql);
    }

    private const string PreflightSql = """
        SET LOCAL lock_timeout = '10s';
        LOCK TABLE public.event_identity_guards, public.pending_telemetry_promotions IN ACCESS EXCLUSIVE MODE;
        DO $gate$
        BEGIN
         IF EXISTS (SELECT 1 FROM public.event_identity_guards WHERE "Destination" <> 'Queryable'
             OR "StorageLocation" <> 'QueryableCommitted' OR "PromotionOwner" IS NOT NULL OR "PromotionLeaseUntil" IS NOT NULL)
            OR EXISTS (SELECT 1 FROM public.pending_telemetry_promotions) THEN
            RAISE EXCEPTION 'Single-store upgrade requires verified legacy reconciliation and promotion archival; see single-influx-upgrade runbook'
                USING ERRCODE='23514';
         END IF;
        END $gate$;
        """;

    private const string SingleStoreGuardSql = """
        CREATE OR REPLACE FUNCTION public.telemetry_protect_identity_guard() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog AS $body$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        IF OLD."State" = 'Committed' AND OLD."GuardExpiresAt" > clock_timestamp() THEN
                            RAISE EXCEPTION 'Committed identity retention has not expired' USING ERRCODE = '23514';
                        END IF;
                        RETURN OLD;
                    END IF;
                    IF (NEW."CompanyId", NEW."VehicleId", NEW."EventTimestamp") IS DISTINCT FROM
                       (OLD."CompanyId", OLD."VehicleId", OLD."EventTimestamp")
                       OR NEW."FencingVersion" < OLD."FencingVersion"
                       OR NEW."GuardExpiresAt" < OLD."GuardExpiresAt"
                       OR NEW."TelemetryExpiresAt" < OLD."TelemetryExpiresAt" THEN
                        RAISE EXCEPTION 'Identity, fence and retention cannot regress' USING ERRCODE = '23514';
                    END IF;
                    IF (NEW."OwnerBatchId", NEW."OwnerLineNumber") IS DISTINCT FROM
                       (OLD."OwnerBatchId", OLD."OwnerLineNumber") THEN
                        IF OLD."State" = 'Committed' OR OLD."LeaseUntil" > clock_timestamp()
                           OR NEW."FencingVersion" <= OLD."FencingVersion" OR NEW."State" <> 'Reserved' THEN
                            RAISE EXCEPTION 'Identity takeover requires expired lease and a new fence' USING ERRCODE = '23514';
                        END IF;
                    ELSIF NEW."State" <> OLD."State" AND NOT
                        ((OLD."State" = 'Reserved' AND NEW."State" = 'Writing') OR
                         (OLD."State" = 'Writing' AND NEW."State" = 'Committed')) THEN
                        RAISE EXCEPTION 'Invalid identity state transition' USING ERRCODE = '23514';
                    END IF;
                    IF NEW."State" <> OLD."State" AND NEW."State" IN ('Writing','Committed')
                       AND OLD."LeaseUntil" <= clock_timestamp() THEN
                        RAISE EXCEPTION 'Expired owner cannot advance an identity' USING ERRCODE = '23514';
                    END IF;
                    IF OLD."State" = 'Committed' AND NEW."State" <> 'Committed' THEN
            RAISE EXCEPTION 'Committed identities cannot reopen' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
                END;
                $body$;
        """;

    private const string LegacyGuardSql = """
        CREATE OR REPLACE FUNCTION public.telemetry_protect_identity_guard() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog AS $body$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        IF OLD."State" = 'Committed' AND OLD."GuardExpiresAt" > clock_timestamp() THEN
                            RAISE EXCEPTION 'Committed identity retention has not expired' USING ERRCODE = '23514';
                        END IF;
                        RETURN OLD;
                    END IF;
                    IF (NEW."CompanyId", NEW."VehicleId", NEW."EventTimestamp") IS DISTINCT FROM
                       (OLD."CompanyId", OLD."VehicleId", OLD."EventTimestamp")
                       OR NEW."FencingVersion" < OLD."FencingVersion"
                       OR NEW."GuardExpiresAt" < OLD."GuardExpiresAt"
                       OR NEW."TelemetryExpiresAt" < OLD."TelemetryExpiresAt" THEN
                        RAISE EXCEPTION 'Identity, fence and retention cannot regress' USING ERRCODE = '23514';
                    END IF;
                    IF (NEW."OwnerBatchId", NEW."OwnerLineNumber") IS DISTINCT FROM
                       (OLD."OwnerBatchId", OLD."OwnerLineNumber") THEN
                        IF OLD."State" = 'Committed' OR OLD."LeaseUntil" > clock_timestamp()
                           OR NEW."FencingVersion" <= OLD."FencingVersion" OR NEW."State" <> 'Reserved' THEN
                            RAISE EXCEPTION 'Identity takeover requires expired lease and a new fence' USING ERRCODE = '23514';
                        END IF;
                    ELSIF NEW."State" <> OLD."State" AND NOT
                        ((OLD."State" = 'Reserved' AND NEW."State" = 'Writing') OR
                         (OLD."State" = 'Writing' AND NEW."State" = 'Committed')) THEN
                        RAISE EXCEPTION 'Invalid identity state transition' USING ERRCODE = '23514';
                    END IF;
                    IF NEW."State" <> OLD."State" AND NEW."State" IN ('Writing','Committed')
                       AND OLD."LeaseUntil" <= clock_timestamp() THEN
                        RAISE EXCEPTION 'Expired owner cannot advance an identity' USING ERRCODE = '23514';
                    END IF;
                    IF OLD."State" = 'Committed' THEN
                        IF NEW."State" <> 'Committed' OR
                           (NEW."StorageLocation" <> OLD."StorageLocation" AND NOT
                             ((OLD."StorageLocation" = 'PendingCommitted' AND NEW."StorageLocation" = 'Promoting') OR
                              (OLD."StorageLocation" = 'Promoting' AND NEW."StorageLocation" = 'QueryableCommitted'))) THEN
                            RAISE EXCEPTION 'Committed identities cannot reopen' USING ERRCODE = '23514';
                        END IF;
                        IF NEW."PromotionOwner" IS DISTINCT FROM OLD."PromotionOwner" AND NEW."PromotionOwner" IS NOT NULL THEN
                            IF NEW."FencingVersion" <= OLD."FencingVersion" OR
                               (OLD."StorageLocation" = 'Promoting' AND OLD."PromotionLeaseUntil" > clock_timestamp()) THEN
                                RAISE EXCEPTION 'Promotion takeover requires expired lease and a new fence' USING ERRCODE = '23514';
                            END IF;
                        END IF;
                        IF OLD."StorageLocation" = 'Promoting' AND NEW."StorageLocation" = 'QueryableCommitted'
                           AND OLD."PromotionLeaseUntil" <= clock_timestamp() THEN
                            RAISE EXCEPTION 'Expired promotion cannot complete' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $body$;
        """;
}
