#!/bin/sh
# Composes the `sqlflow worker` invocation from the container's environment. The catalog connection itself is the
# CLI default (${env:SQLFLOW_CATALOG_DB}), so it never appears on the command line or in `ps` output.
set -e

set -- worker
if [ -n "${SQLFLOW_WORKER_POOL:-}" ]; then
  set -- "$@" --pool "$SQLFLOW_WORKER_POOL"
fi
if [ -n "${SQLFLOW_WORKER_POLL_SECONDS:-}" ]; then
  set -- "$@" --poll-seconds "$SQLFLOW_WORKER_POLL_SECONDS"
fi

exec dotnet /app/SqlFlow.Cli.dll "$@"
