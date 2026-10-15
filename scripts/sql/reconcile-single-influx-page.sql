-- OFFLINE UPGRADE ONLY. Pause all writers and verify source/destination points first.
-- Same psql/Npgsql session must provide pg_temp.single_influx_verified_page (runbook).
-- A fingerprint is evidence supplied by the operator, not a substitute for reading Influx.
BEGIN;
SET LOCAL lock_timeout = '10s';
SET LOCAL statement_timeout = '2min';
LOCK TABLE public.event_identity_guards IN SHARE ROW EXCLUSIVE MODE;
LOCK TABLE public.event_identity_evidence IN SHARE MODE;
DO $reconcile$
DECLARE proof record; guard_row record; evidence_row record;
        owner_id uuid := gen_random_uuid(); page_count integer;
BEGIN
    IF to_regclass('pg_temp.single_influx_verified_page') IS NULL THEN
        RAISE EXCEPTION 'Verified source/destination manifest is required' USING ERRCODE='23514';
    END IF;
    SELECT count(*) INTO page_count FROM pg_temp.single_influx_verified_page;
    IF page_count < 1 OR page_count > 500 THEN
        RAISE EXCEPTION 'Reconciliation pages require 1..500 verified identities' USING ERRCODE='23514';
    END IF;
    FOR proof IN SELECT * FROM pg_temp.single_influx_verified_page ORDER BY company_id, vehicle_id, event_time LOOP
        IF proof.source_fingerprint !~ '^[0-9a-f]{64}$' OR proof.destination_fingerprint IS DISTINCT FROM proof.source_fingerprint THEN
            RAISE EXCEPTION 'Source/destination fingerprint mismatch' USING ERRCODE='23514';
        END IF;
        SELECT * INTO guard_row FROM public.event_identity_guards
          WHERE "CompanyId"=proof.company_id AND "VehicleId"=proof.vehicle_id AND "EventTimestamp"=proof.event_time FOR UPDATE;
        IF NOT FOUND OR guard_row."State" <> 'Committed' THEN
            RAISE EXCEPTION 'Missing or uncommitted legacy identity; reconcile ownership first' USING ERRCODE='23514';
        END IF;
        SELECT * INTO evidence_row FROM public.event_identity_evidence
          WHERE "CompanyId"=proof.company_id AND "VehicleId"=proof.vehicle_id AND "EventTimestamp"=proof.event_time;
        IF NOT FOUND OR evidence_row."EvidenceExpiresAt" <= clock_timestamp()
            OR evidence_row."CanonicalFingerprint" IS DISTINCT FROM decode(proof.source_fingerprint,'hex') THEN
            RAISE EXCEPTION 'Missing, expired or conflicting canonical evidence' USING ERRCODE='23514';
        END IF;
        IF guard_row."Destination"='Queryable' AND guard_row."StorageLocation"='QueryableCommitted'
            AND guard_row."PromotionOwner" IS NULL AND guard_row."PromotionLeaseUntil" IS NULL THEN
            CONTINUE; -- Verified repeat: leave fence, provenance and retention unchanged.
        END IF;
        IF guard_row."Destination" <> 'Pending' OR guard_row."StorageLocation" NOT IN ('PendingCommitted','Promoting')
            OR guard_row."PromotionLeaseUntil" > clock_timestamp() THEN
            RAISE EXCEPTION 'Unreconciled routing state or active promotion lease' USING ERRCODE='23514';
        END IF;
        UPDATE public.event_identity_guards SET "StorageLocation"='Promoting', "PromotionOwner"=owner_id,
            "PromotionLeaseUntil"=clock_timestamp()+interval '5 minutes', "FencingVersion"="FencingVersion"+1
          WHERE "CompanyId"=proof.company_id AND "VehicleId"=proof.vehicle_id AND "EventTimestamp"=proof.event_time;
        -- Destination was independently read and reconciled before importing this manifest.
        UPDATE public.event_identity_guards SET "Destination"='Queryable', "StorageLocation"='QueryableCommitted',
            "PromotionOwner"=NULL, "PromotionLeaseUntil"=NULL
          WHERE "CompanyId"=proof.company_id AND "VehicleId"=proof.vehicle_id AND "EventTimestamp"=proof.event_time
            AND "PromotionOwner"=owner_id AND "PromotionLeaseUntil">clock_timestamp();
        IF NOT FOUND THEN
            RAISE EXCEPTION 'Promotion fence expired during reconciliation' USING ERRCODE='23514';
        END IF;
    END LOOP;
END $reconcile$;
COMMIT;
