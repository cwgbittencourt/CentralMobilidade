# Build from the repository root:
# docker build -f deploy/containers/api.Dockerfile -t centralmobilidade-api .
FROM busybox:1.37.0-musl AS probe
FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble AS build
WORKDIR /source

# Restore separately so source edits reuse the dependency layer.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/CentralMobilidade.Telemetry.Api/CentralMobilidade.Telemetry.Api.csproj src/CentralMobilidade.Telemetry.Api/
COPY src/CentralMobilidade.Telemetry.Application/CentralMobilidade.Telemetry.Application.csproj src/CentralMobilidade.Telemetry.Application/
COPY src/CentralMobilidade.Telemetry.Contracts/CentralMobilidade.Telemetry.Contracts.csproj src/CentralMobilidade.Telemetry.Contracts/
COPY src/CentralMobilidade.Telemetry.Domain/CentralMobilidade.Telemetry.Domain.csproj src/CentralMobilidade.Telemetry.Domain/
COPY src/CentralMobilidade.Telemetry.Infrastructure/CentralMobilidade.Telemetry.Infrastructure.csproj src/CentralMobilidade.Telemetry.Infrastructure/
RUN dotnet restore src/CentralMobilidade.Telemetry.Api/CentralMobilidade.Telemetry.Api.csproj

COPY src/ src/
RUN dotnet publish src/CentralMobilidade.Telemetry.Api/CentralMobilidade.Telemetry.Api.csproj \
    --configuration Release --no-restore --output /app/publish \
    --self-contained false /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish ./
COPY --from=probe /bin/busybox /bin/busybox
USER $APP_UID
ENTRYPOINT ["dotnet", "CentralMobilidade.Telemetry.Api.dll"]
