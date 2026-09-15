# An OSDU Delivery compute node as a container: the pull-based drain loop over the durable run queue, with the OSDU
# Delivery module installed so it can execute delivery, retrieval and cache flows.
#
# The image publishes osdu/hosts/SqlFlow.Delivery.Worker.Host. It needs OUTBOUND SQL to the catalog and to the OSDU
# module database, outbound git to the repository remotes (for SHA-pinned runs), and outbound HTTPS to the OSDU
# endpoint; it exposes nothing, so it never sits behind an ingress. Scale it on queue depth (osdu/deploy/k8s for the
# KEDA setup), not on traffic.
#
# Configuration is environment-only, matching the node's "credentials live on the node" model:
#   SQLFLOW_CATALOG_DB            the catalog connection string (required)
#   SQLFLOW_WORKER_POOL           comma-separated pools this node serves (optional; empty = untargeted runs only)
#   SQLFLOW_WORKER_POLL_SECONDS   queue poll cadence (optional)
#   SQLFLOW_WORKER_DRAIN_SECONDS  how long a stopping node finishes what it holds (optional)
#   SQLFLOW_GIT_TOKEN             token for private git remotes (optional)
#   plus every ${env:...} reference the flows themselves use (the ingestion database connection, the OSDU
#   credentials) and the OSDU module database connection the ledger is read and written through.
#
# Build from the REPOSITORY ROOT (the context must span osdu/ and sqlflow/):
#
#   docker build -f osdu/deploy/docker/worker.Dockerfile -t osdu-delivery-worker:latest .

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
# Framework-dependent publish; building inside the linux SDK image restores the linux-x64 native assets the engine
# carries (LibGit2Sharp for materialization), so they ship under runtimes/linux-x64.
RUN dotnet publish osdu/hosts/SqlFlow.Delivery.Worker.Host/SqlFlow.Delivery.Worker.Host.csproj \
    -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
# LibGit2Sharp's bundled native git needs OpenSSL present on the Debian runtime image for HTTPS remotes.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libssl3 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
COPY osdu/deploy/docker/worker-entrypoint.sh /worker-entrypoint.sh
# The CR strip keeps the shebang valid when the build context comes from a Windows checkout (CRLF would make the
# kernel look for /bin/sh\r and fail the container with "no such file or directory").
RUN sed -i 's/\r$//' /worker-entrypoint.sh && chmod +x /worker-entrypoint.sh
ENV DOTNET_RUNNING_IN_CONTAINER=true
USER $APP_UID
ENTRYPOINT ["/worker-entrypoint.sh"]
