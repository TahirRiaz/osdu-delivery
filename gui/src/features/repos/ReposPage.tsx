import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "react-router-dom";
import { useSnackbar } from "notistack";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import Stack from "@mui/material/Stack";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";
import ChevronRightIcon from "@mui/icons-material/ChevronRight";
import SyncIcon from "@mui/icons-material/Sync";
import { isApiError } from "../../api/client";
import { repoApi, repoSourceApi } from "../../api/endpoints";
import type { Repo, RepoSource } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { RelativeTime } from "../../components/RelativeTime";
import { TruncatedText } from "../../components/TruncatedText";
import { RegisterSourceDialog } from "./RegisterSourceDialog";

// Repos and their git sources are two facets of one thing, joined by name: a source is the git registration that
// drives auto-sync; a repo is what a sync produced (its pipelines and lineage). One list shows both so a source
// registered but not yet synced, a repo synced by the CLI with no source, and the common managed case all read as
// a single row. These collections are small (a handful per control plane); one generous page covers them.
const FETCH_CAP = 200;

interface MergedRow {
  key: string;
  name: string;
  repo?: Repo;
  source?: RepoSource;
}

function mergeByName(repos: Repo[], sources: RepoSource[]): MergedRow[] {
  const byName = new Map<string, MergedRow>();
  const row = (name: string): MergedRow => {
    const existing = byName.get(name);
    if (existing !== undefined) {
      return existing;
    }

    const created: MergedRow = { key: name, name };
    byName.set(name, created);
    return created;
  };

  for (const source of sources) {
    row(source.name).source = source;
  }

  for (const repo of repos) {
    row(repo.name).repo = repo;
  }

  return [...byName.values()].sort((a, b) => a.name.localeCompare(b.name));
}

/** The catalog's repos, each shown with its git source: register a source, watch it sync, and drill into a
 * synced repo's pipelines and lineage. */
export default function ReposPage() {
  const navigate = useNavigate();
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const [registerOpen, setRegisterOpen] = useState(false);

  const reposQuery = useQuery({
    queryKey: ["repos", "list", FETCH_CAP],
    queryFn: () => repoApi.list({ page: 1, pageSize: FETCH_CAP }),
    refetchInterval: 8000,
  });
  const sourcesQuery = useQuery({
    queryKey: ["repo-sources", "list", FETCH_CAP],
    queryFn: () => repoSourceApi.list({ page: 1, pageSize: FETCH_CAP }),
    refetchInterval: 8000,
  });

  const syncNow = useMutation({
    mutationFn: (id: string) => repoSourceApi.syncNow(id),
    onSuccess: () => {
      enqueueSnackbar("Sync requested", { variant: "success" });
      void queryClient.invalidateQueries({ queryKey: ["repo-sources"] });
      void queryClient.invalidateQueries({ queryKey: ["repos"] });
    },
    onError: (error) =>
      enqueueSnackbar(error instanceof Error ? error.message : String(error), { variant: "error" }),
  });

  const rows = useMemo(
    () => mergeByName(reposQuery.data?.items ?? [], sourcesQuery.data?.items ?? []),
    [reposQuery.data, sourcesQuery.data],
  );

  const error = reposQuery.error ?? sourcesQuery.error;
  const loading = reposQuery.data === undefined || sourcesQuery.data === undefined;
  const truncated = (reposQuery.data?.total ?? 0) > FETCH_CAP || (sourcesQuery.data?.total ?? 0) > FETCH_CAP;

  const renderSync = (source: RepoSource | undefined) => {
    if (source === undefined) {
      return (
        <Tooltip title="Synced outside the control plane (CLI db sync); no tracked git source.">
          <Chip size="small" variant="outlined" label="manual" />
        </Tooltip>
      );
    }

    return (
      <Chip
        size="small"
        color={source.enabled ? "success" : "default"}
        variant="outlined"
        label={source.enabled ? `every ${source.syncIntervalSeconds}s` : "paused"}
      />
    );
  };

  const renderHealth = (source: RepoSource | undefined) => {
    if (source === undefined) {
      return <Typography variant="body2" color="text.secondary">-</Typography>;
    }

    return source.lastError !== null ? (
      <Tooltip title={source.lastError}>
        <Chip size="small" color="error" label="error" data-testid="source-error" />
      </Tooltip>
    ) : (
      <Chip size="small" color="success" variant="outlined" label="ok" />
    );
  };

  const columns: Column<MergedRow>[] = [
    {
      id: "name",
      header: "Name",
      render: (row) => (
        <Stack direction="row" spacing={1} alignItems="center">
          <Typography variant="body2" fontWeight={600}>{row.name}</Typography>
          {row.source !== undefined && row.repo === undefined && (
            <Chip size="small" variant="outlined" color="warning" label="pending first sync" />
          )}
        </Stack>
      ),
    },
    {
      id: "remoteUrl",
      header: "Remote URL",
      render: (row) => <TruncatedText text={row.source?.remoteUrl ?? row.repo?.remoteUrl} mono maxWidth={360} />,
    },
    { id: "branch", header: "Branch", render: (row) => row.source?.branch ?? "-" },
    { id: "sync", header: "Sync", render: (row) => renderSync(row.source) },
    {
      id: "lastSync",
      header: "Last sync",
      render: (row) => <RelativeTime value={row.source?.lastSyncUtc ?? row.repo?.lastSyncUtc ?? null} />,
    },
    { id: "sha", header: "Synced commit", render: (row) => <Mono>{row.source?.lastSyncedSha?.slice(0, 10) ?? "-"}</Mono> },
    { id: "health", header: "Health", render: (row) => renderHealth(row.source) },
    {
      id: "actions",
      header: "",
      align: "right",
      render: (row) => (
        <Stack direction="row" spacing={0.5} justifyContent="flex-end" alignItems="center">
          {row.source !== undefined && (
            <Button
              size="small"
              startIcon={<SyncIcon fontSize="small" />}
              disabled={!row.source.enabled || syncNow.isPending}
              onClick={(e) => {
                e.stopPropagation();
                if (row.source !== undefined) {
                  syncNow.mutate(row.source.id);
                }
              }}
              data-testid="source-sync-now"
            >
              Sync now
            </Button>
          )}
          {row.repo !== undefined && <ChevronRightIcon fontSize="small" color="action" />}
        </Stack>
      ),
    },
  ];

  return (
    <Page data-testid="page-repos">
      <PageHeader
        title="Repos"
        actions={(
          <Button variant="contained" onClick={() => setRegisterOpen(true)} data-testid="open-register-source">
            Register source
          </Button>
        )}
      />

      {error !== null && (
        isApiError(error)
          ? <CorrelationError error={error} />
          : <Typography color="error">{String(error)}</Typography>
      )}

      <DataTable
        columns={columns}
        rows={loading ? undefined : rows}
        rowKey={(row) => row.key}
        onRowClick={(row) => {
          if (row.repo !== undefined) {
            navigate(`/repos/${row.repo.id}`);
          }
        }}
        rowClickable={(row) => row.repo !== undefined}
        emptyMessage="No repos yet. Register a git source to sync one in."
        skeletonRows={4}
        data-testid="repos-table"
      />

      {truncated && (
        <Typography variant="caption" color="text.secondary">
          Showing the first {FETCH_CAP} repos and sources.
        </Typography>
      )}

      {registerOpen && <RegisterSourceDialog onClose={() => setRegisterOpen(false)} />}
    </Page>
  );
}
