-- Run with psql as the migration owner AFTER migration, passing -v runtime_role=<existing role>.
-- Role creation, login secrets and rotation belong to deployment secret management.
\set ON_ERROR_STOP on
BEGIN;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM :"runtime_role";
GRANT USAGE ON SCHEMA public TO :"runtime_role";
GRANT SELECT, INSERT, UPDATE, DELETE ON public.ingestion_batches,
    public.line_processing_results, public.quarantined_rejections,
    public.event_identity_guards, public.event_identity_evidence TO :"runtime_role";
GRANT SELECT ON public.operator_access_profiles TO :"runtime_role";
GRANT SELECT, INSERT ON public.audit_events TO :"runtime_role";
COMMIT;
