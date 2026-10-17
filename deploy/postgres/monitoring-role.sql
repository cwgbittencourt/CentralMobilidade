-- Run as the database owner using psql; set the login password interactively
-- with \password telemetry_monitor afterwards (never store it in this file).
\set ON_ERROR_STOP on
BEGIN;
SELECT 'CREATE ROLE telemetry_monitor LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'telemetry_monitor')
\gexec
ALTER ROLE telemetry_monitor SET search_path = pg_catalog;
GRANT CONNECT ON DATABASE telemetry_control TO telemetry_monitor;
GRANT pg_monitor TO telemetry_monitor;
COMMIT;
