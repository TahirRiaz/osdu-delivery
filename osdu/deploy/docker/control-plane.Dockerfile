# The OSDU Delivery control plane as a container: the warm, always-on API the GUI, the CLI and any external
# scheduler call. A trigger is a sub-second authenticated call; nothing is provisioned per run, so this image runs
# as a long-lived service (Azure Container Apps, App Service, Kubernetes, or plain Docker), not booted per execution.
#
# The image publishes osdu/hosts/SqlFlow.Delivery.ControlPlane.Host: SQLFlow's control plane composed with the OSDU
# Delivery module (its endpoints, background services and branding).
#
# Build from the REPOSITORY ROOT. The context must span the whole repository: the host references both osdu/src and
# sqlflow/src, and central package management lives in osdu/ and sqlflow/.
#
#   docker build -f osdu/deploy/docker/control-plane.Dockerfile -t osdu-delivery-control-plane:latest .

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
# Framework-dependent publish (run via `dotnet <dll>`, no native apphost). Building inside the linux SDK image
# restores the linux-x64 native assets the engine carries (LibGit2Sharp, for SHA-pinned git materialization), so
# they ship in the publish output under runtimes/linux-x64.
RUN dotnet publish osdu/hosts/SqlFlow.Delivery.ControlPlane.Host/SqlFlow.Delivery.ControlPlane.Host.csproj \
    -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
# LibGit2Sharp's bundled native git needs OpenSSL present on the Debian runtime image for HTTPS remotes.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libssl3 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# Listen on 8080 (the unprivileged port the .NET container images default to) and run as the image's non-root user.
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "SqlFlow.Delivery.ControlPlane.Host.dll"]
