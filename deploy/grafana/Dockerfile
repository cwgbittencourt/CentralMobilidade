FROM grafana/grafana:12.2.5

# Keep the baked plugin outside the persistent Grafana data directory.
ENV GF_PATHS_PLUGINS=/opt/grafana/plugins
USER root
RUN mkdir -p /opt/grafana/plugins && chown 472:0 /opt/grafana/plugins
USER 472
RUN grafana cli --pluginsDir /opt/grafana/plugins plugins install yesoreyeram-infinity-datasource 4.1.1 \
    && test -s /opt/grafana/plugins/yesoreyeram-infinity-datasource/MANIFEST.txt
