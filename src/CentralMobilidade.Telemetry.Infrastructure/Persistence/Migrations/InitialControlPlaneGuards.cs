namespace CentralMobilidade.Telemetry.Infrastructure.Persistence.Migrations;

// Database invariants only; the application reservation algorithm remains gated by T041/T047.
internal static class InitialControlPlaneGuards
{
    internal const string UpSql = """
        CREATE FUNCTION public.telemetry_reject_audit_mutation() RETURNS trigger
        LANGUAGE plpgsql SET search_path = pg_catalog AS $body$
        BEGIN
            RAISE EXCEPTION 'Audit events are append-only' USING ERRCODE = '42501';
        END;
        $body$;
        CREATE TRIGGER audit_events_no_mutation BEFORE UPDATE OR DELETE OR TRUNCATE
            ON public.audit_events FOR EACH STATEMENT EXECUTE FUNCTION public.telemetry_reject_audit_mutation();
        ALTER TABLE public.audit_events ENABLE ALWAYS TRIGGER audit_events_no_mutation;

        CREATE FUNCTION public.telemetry_require_next_version() RETURNS trigger
        LANGUAGE plpgsql SET search_path = pg_catalog AS $body$
        BEGIN
            IF NEW."RowVersion" <> OLD."RowVersion" + 1 THEN
                RAISE EXCEPTION 'Control row version must advance by one' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $body$;
        CREATE TRIGGER ingestion_batches_version BEFORE UPDATE ON public.ingestion_batches
            FOR EACH ROW EXECUTE FUNCTION public.telemetry_require_next_version();
        CREATE TRIGGER operator_access_profiles_version BEFORE UPDATE ON public.operator_access_profiles
            FOR EACH ROW EXECUTE FUNCTION public.telemetry_require_next_version();
        CREATE TRIGGER pending_telemetry_promotions_version BEFORE UPDATE ON public.pending_telemetry_promotions
            FOR EACH ROW EXECUTE FUNCTION public.telemetry_require_next_version();

        CREATE FUNCTION public.telemetry_protect_identity_guard() RETURNS trigger
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
        CREATE TRIGGER event_identity_guards_invariants BEFORE UPDATE OR DELETE ON public.event_identity_guards
            FOR EACH ROW EXECUTE FUNCTION public.telemetry_protect_identity_guard();
        CREATE FUNCTION public.telemetry_reject_guard_truncate() RETURNS trigger
        LANGUAGE plpgsql SET search_path = pg_catalog AS $body$
        BEGIN
            RAISE EXCEPTION 'Identity guards require expiry-aware deletion' USING ERRCODE = '42501';
        END;
        $body$;
        CREATE TRIGGER event_identity_guards_no_truncate BEFORE TRUNCATE ON public.event_identity_guards
            FOR EACH STATEMENT EXECUTE FUNCTION public.telemetry_reject_guard_truncate();
        REVOKE ALL ON public.audit_events FROM PUBLIC;
        REVOKE EXECUTE ON FUNCTION public.telemetry_reject_audit_mutation(),
            public.telemetry_require_next_version(), public.telemetry_protect_identity_guard(),
            public.telemetry_reject_guard_truncate() FROM PUBLIC;
        """;

    internal const string DownSql = """
        DROP TRIGGER audit_events_no_mutation ON public.audit_events;
        DROP TRIGGER ingestion_batches_version ON public.ingestion_batches;
        DROP TRIGGER operator_access_profiles_version ON public.operator_access_profiles;
        DROP TRIGGER pending_telemetry_promotions_version ON public.pending_telemetry_promotions;
        DROP TRIGGER event_identity_guards_invariants ON public.event_identity_guards;
        DROP TRIGGER event_identity_guards_no_truncate ON public.event_identity_guards;
        DROP FUNCTION public.telemetry_reject_audit_mutation();
        DROP FUNCTION public.telemetry_require_next_version();
        DROP FUNCTION public.telemetry_protect_identity_guard();
        DROP FUNCTION public.telemetry_reject_guard_truncate();
        """;
}
