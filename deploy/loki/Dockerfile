# Loki's minimal image has no HTTP client for a Compose readiness probe.
FROM busybox:1.37.0-musl AS probe
FROM grafana/loki:3.7.0
COPY --from=probe /bin/busybox /bin/busybox
