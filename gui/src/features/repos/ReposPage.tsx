import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "react-router-dom";
import { ChevronRight, Loader2, RefreshCw } from "lucide-react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
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
      toast.success("Sync requested");
      void queryClient.invalidateQueries({ queryKey: ["repo-sources"] });
      void queryClient.invalidateQueries({ queryKey: ["repos"] });
    },
    onError: (error) =>
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
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
        <Tooltip>
          <TooltipTrigger asChild>
            <span>
              <Badge variant="outline">manual</Badge>
            </span>
          </TooltipTrigger>
          <TooltipContent>
            Synced outside the control plane (CLI db sync); no tracked git source.
          </TooltipContent>
        </Tooltip>
      );
    }

    return source.enabled ? (
      <Badge variant="outline" className="border-success/50 text-success">
        every {source.syncIntervalSeconds}s
      </Badge>
    ) : (
      <Badge variant="outline">paused</Badge>
    );
  };

  const renderHealth = (source: RepoSource | undefined) => {
    if (source === undefined) {
      return <span className="text-muted-foreground">-</span>;
    }

    return source.lastError !== null ? (
      <Tooltip>
        <TooltipTrigger asChild>
          <span>
            <Badge variant="destructive" data-testid="source-error">error</Badge>
          </span>
        </TooltipTrigger>
        <TooltipContent className="max-w-lg break-words">{source.lastError}</TooltipContent>
      </Tooltip>
    ) : (
      <Badge variant="outline" className="border-success/50 text-success">ok</Badge>
    );
  };

  const columns: Column<MergedRow>[] = [
    {
      id: "name",
      header: "Name",
      render: (row) => (
        <span className="inline-flex items-center gap-2">
          <span className="font-medium">{row.name}</span>
          {row.source !== undefined && row.repo === undefined && (
            <Badge variant="outline" className="border-warning/50 text-warning">pending first sync</Badge>
          )}
        </span>
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
        <span className="inline-flex items-center justify-end gap-1">
          {row.source !== undefined && (
            <Button
              variant="ghost"
              size="xs"
              disabled={!row.source.enabled || syncNow.isPending}
              onClick={(e) => {
                e.stopPropagation();
                if (row.source !== undefined) {
                  syncNow.mutate(row.source.id);
                }
              }}
              data-testid="source-sync-now"
            >
              {syncNow.isPending && syncNow.variables === row.source.id
                ? <Loader2 className="animate-spin" />
                : <RefreshCw />}
              Sync now
            </Button>
          )}
          {row.repo !== undefined && <ChevronRight className="size-4 text-muted-foreground" />}
        </span>
      ),
    },
  ];

  return (
    <Page data-testid="page-repos">
      <PageHeader
        title="Repos"
        actions={(
          <Button size="sm" onClick={() => setRegisterOpen(true)} data-testid="open-register-source">
            Register source
          </Button>
        )}
      />

      {error !== null && (
        isApiError(error)
          ? <CorrelationError error={error} />
          : <p className="text-[13px] text-destructive">{String(error)}</p>
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
        <p className="text-xs text-muted-foreground">
          Showing the first {FETCH_CAP} repos and sources.
        </p>
      )}

      {registerOpen && <RegisterSourceDialog onClose={() => setRegisterOpen(false)} />}
    </Page>
  );
}
