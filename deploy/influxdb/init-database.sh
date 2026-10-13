#!/bin/sh
set -eu

# Read the offline bootstrap token without putting it in command arguments or logs.
INFLUXDB3_AUTH_TOKEN=$(sed -n 's/.*"token": *"\([^"]*\)".*/\1/p' /run/secrets/influxdb_admin_token)
test -n "$INFLUXDB3_AUTH_TOKEN"
export INFLUXDB3_AUTH_TOKEN

databases=$(influxdb3 show databases --format csv)
if ! printf '%s\n' "$databases" | grep -Fxq "$INFLUXDB3_DATABASE_NAME"; then
    influxdb3 create database --retention-period 60d "$INFLUXDB3_DATABASE_NAME"
fi
