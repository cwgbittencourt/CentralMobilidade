using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CentralMobilidade.Telemetry.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TelemetryControlDbContext))]
[Migration("20261014190000_PartitionIdentityGuard")]
public sealed class PartitionIdentityGuard : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            LOCK TABLE public.event_identity_guards IN ACCESS EXCLUSIVE MODE;
            ALTER TABLE public.event_identity_guards RENAME TO event_identity_guards_previous;
            CREATE TABLE public.event_identity_guards (LIKE public.event_identity_guards_previous INCLUDING DEFAULTS INCLUDING CONSTRAINTS)
                PARTITION BY HASH ("VehicleId");
            """);
        for (var i = 0; i < 16; i++)
            migrationBuilder.Sql($"CREATE TABLE public.event_identity_guards_p{i} PARTITION OF public.event_identity_guards FOR VALUES WITH (MODULUS 16, REMAINDER {i});");
        CopyGrants(migrationBuilder, "event_identity_guards_previous", "event_identity_guards");
        migrationBuilder.Sql("""
            INSERT INTO public.event_identity_guards SELECT * FROM public.event_identity_guards_previous;
            DROP TABLE public.event_identity_guards_previous;
            ALTER TABLE public.event_identity_guards ADD CONSTRAINT "PK_event_identity_guards" PRIMARY KEY ("CompanyId","VehicleId","EventTimestamp");
            CREATE INDEX "IX_event_identity_guards_GuardExpiresAt" ON public.event_identity_guards ("GuardExpiresAt");
            CREATE INDEX "IX_event_identity_guards_LeaseUntil" ON public.event_identity_guards ("LeaseUntil");
            CREATE TRIGGER event_identity_guards_invariants BEFORE UPDATE OR DELETE ON public.event_identity_guards
                FOR EACH ROW EXECUTE FUNCTION public.telemetry_protect_identity_guard();
            CREATE TRIGGER event_identity_guards_no_truncate BEFORE TRUNCATE ON public.event_identity_guards
                FOR EACH STATEMENT EXECUTE FUNCTION public.telemetry_reject_guard_truncate();
            """);
        // A direct TRUNCATE of any leaf must be protected too.
        for (var i = 0; i < 16; i++)
            migrationBuilder.Sql($"CREATE TRIGGER event_identity_guards_no_truncate BEFORE TRUNCATE ON public.event_identity_guards_p{i} FOR EACH STATEMENT EXECUTE FUNCTION public.telemetry_reject_guard_truncate();");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            LOCK TABLE public.event_identity_guards IN ACCESS EXCLUSIVE MODE;
            CREATE TABLE public.event_identity_guards_restore (LIKE public.event_identity_guards INCLUDING DEFAULTS INCLUDING CONSTRAINTS);
            """);
        CopyGrants(migrationBuilder, "event_identity_guards", "event_identity_guards_restore");
        migrationBuilder.Sql("""
            INSERT INTO public.event_identity_guards_restore SELECT * FROM public.event_identity_guards;
            DROP TABLE public.event_identity_guards;
            ALTER TABLE public.event_identity_guards_restore RENAME TO event_identity_guards;
            ALTER TABLE public.event_identity_guards ADD CONSTRAINT "PK_event_identity_guards" PRIMARY KEY ("CompanyId","VehicleId","EventTimestamp");
            CREATE INDEX "IX_event_identity_guards_GuardExpiresAt" ON public.event_identity_guards ("GuardExpiresAt");
            CREATE INDEX "IX_event_identity_guards_LeaseUntil" ON public.event_identity_guards ("LeaseUntil");
            CREATE TRIGGER event_identity_guards_invariants BEFORE UPDATE OR DELETE ON public.event_identity_guards
                FOR EACH ROW EXECUTE FUNCTION public.telemetry_protect_identity_guard();
            CREATE TRIGGER event_identity_guards_no_truncate BEFORE TRUNCATE ON public.event_identity_guards
                FOR EACH STATEMENT EXECUTE FUNCTION public.telemetry_reject_guard_truncate();
            """);
    }

    private static void CopyGrants(MigrationBuilder migrationBuilder, string source, string target)
    {
        migrationBuilder.Sql($"""
            DO $grants$
            DECLARE permission record;
            BEGIN
                FOR permission IN
                    SELECT acl.grantee, acl.privilege_type, acl.is_grantable
                    FROM pg_class relation
                    CROSS JOIN LATERAL aclexplode(COALESCE(relation.relacl, acldefault('r', relation.relowner))) acl
                    WHERE relation.oid = 'public.{source}'::regclass
                LOOP
                    EXECUTE format('GRANT %s ON TABLE public.{target} TO %s%s',
                        permission.privilege_type,
                        CASE WHEN permission.grantee = 0 THEN 'PUBLIC' ELSE quote_ident(pg_get_userbyid(permission.grantee)) END,
                        CASE WHEN permission.is_grantable THEN ' WITH GRANT OPTION' ELSE '' END);
                END LOOP;
            END $grants$;
            """);
    }
}
