#!/bin/sh
set -eu
# Isolated CLI double: no real token, catalog, network or volume is used.
mkdir -p /tmp/init-test-bin /run/secrets
export PATH="/tmp/init-test-bin:$PATH"
export INFLUXDB3_HOST_URL=http://influxdb:8181 INFLUXDB3_DATABASE_NAME=telemetry
cat > /tmp/init-test-bin/influxdb3 <<'CLI'
#!/bin/sh
set -eu
test "$INFLUXDB3_AUTH_TOKEN" = synthetic-private-token
printf '%s\n' "$*" >> /tmp/init-calls
case "$*" in
  'show databases --format csv')
    test "$MODE" != list-failure || exit 7
    printf 'iox::database\n'
    test "$MODE" != existing || printf 'telemetry\n'
    ;;
  'create database --retention-period 60d telemetry')
    test "$MODE" != create-failure || exit 8
    ;;
  *) exit 9 ;;
esac
CLI
chmod +x /tmp/init-test-bin/influxdb3
printf '{"token":"synthetic-private-token"}\n' > /run/secrets/influxdb_admin_token
for MODE in absent existing list-failure create-failure; do
    export MODE
    : > /tmp/init-calls
    result=0
    sh /init-database.sh > /tmp/init-output 2>&1 || result=$?
    case "$MODE" in
      absent) test "$result" = 0; test "$(wc -l < /tmp/init-calls)" = 2 ;;
      existing) test "$result" = 0; test "$(wc -l < /tmp/init-calls)" = 1 ;;
      list-failure) test "$result" = 7; test "$(wc -l < /tmp/init-calls)" = 1 ;;
      create-failure) test "$result" = 8; test "$(wc -l < /tmp/init-calls)" = 2 ;;
    esac
    ! grep -q synthetic-private-token /tmp/init-calls /tmp/init-output
    printf 'PASS: %s\n' "$MODE"
done
: > /tmp/init-calls
if INFLUXDB3_DATABASE_NAME=telemetry_pending sh /init-database.sh >/tmp/init-output 2>&1; then exit 10; fi
test ! -s /tmp/init-calls
if INFLUXDB3_HOST_URL= sh /init-database.sh >/tmp/init-output 2>&1; then exit 11; fi
test ! -s /tmp/init-calls
printf '{}\n' > /run/secrets/influxdb_admin_token
if sh /init-database.sh >/tmp/init-output 2>&1; then exit 12; fi
test ! -s /tmp/init-calls
printf 'PASS: wrong database, missing host and missing token fail before CLI\n'
