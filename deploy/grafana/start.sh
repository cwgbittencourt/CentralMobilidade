#!/bin/sh
set -eu
if [ -n "${INFLUXDB_GRAFANA_READ_TOKEN_FILE:-}" ]; then
  if [ ! -s "$INFLUXDB_GRAFANA_READ_TOKEN_FILE" ]; then
    echo 'Grafana read-token secret is unavailable.' >&2
    exit 1
  fi
  INFLUXDB_GRAFANA_READ_TOKEN=$(cat "$INFLUXDB_GRAFANA_READ_TOKEN_FILE")
  export INFLUXDB_GRAFANA_READ_TOKEN
fi
exec /run.sh "$@"
