import { useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Loader2, Network, Play, RefreshCw } from "lucide-react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { isApiError } from "../../api/client";
import { repoApi, repoSourceApi } from "../../api/endpoints";
import { CorrelationError } from "../../components/CorrelationError";
import { DetailHeaderCard } from "../../components/DetailHeaderCard";
import { DetailPair } from "../../components/DetailPair";
import { EmptyState } from "../../components/EmptyState";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { RelativeTime } from "../../components/RelativeTime";
import { SearchInput } from "../../components/SearchInput";
import { TruncatedText } from "../../components/TruncatedText";
import { useTabTitle } from "../../layout/workbench/TabsContext";
import { fetchAllPipelines } from "../pipelines/fetchAllPipelines";
import { groupByProject, pipelineMatches, ProjectGroup } from "../pipelines/ProjectGroup";
import { TriggerRunDialog } from "../runs/TriggerRunDialog";
import { useSyncTracePanel } from "./useSyncTracePanel";

/** The most repos/sources a single control plane realistically holds; one page covers the by-name lookup. */
const SOURCE_LOOKUP_CAP = 200;

/** The repo's pipelines grouped by project (root folder): each project is one source's set of pipelines. A project
 * that contains a batch flow (flowType: batch) can be run as a unit: the batch executes its members in lineage
 * wave order, so "Run project" triggers that ordered run. */
function PipelinesByProject({
  repoId, filter, onOpen, onRunBatch,
}: {
  repoId: string;
  filter: string;
  onOpen: (pipelineId: string) => void;
  onRunBatch: (repoId: string, flowName: string) => void;
}) {
  // Every page of the repo's flows, not just the first: the API clamps a page at 200 rows, so a single request drops
  // whole project folders off the end of the alphabet with nothing on screen to say so.
  const query = useQuery({
    queryKey: ["pipelines", "by-repo-grouped", repoId],
    queryFn: () => fetchAllPipelines({ repoId }),
  });

  if (query.isError) {
    return isApiError(query.error)
      ? <CorrelationError error={query.error} />
      : <p className="text-[13px] text-destructive">{String(query.error)}</p>;
  }

  const result = query.data;
  if (result === undefined) {
    return <Skeleton className="h-28 w-full rounded-lg" />;
  }

  const pipelines = result.items;
  if (pipelines.length === 0) {
    return (
      <EmptyState
        title="No pipelines yet"
        description="They appear here after a sync imports the selected flows from git."
        data-testid="repo-no-pipelines"
      />
    );
  }

  const needle = filter.trim().toLowerCase();
  const matches = needle === "" ? pipelines : pipelines.filter((p) => pipelineMatches(p, needle));

  // Only reachable on an estate past the sweep's cap; says so rather than quietly showing a partial tree.
  const cappedNote = result.capped && (
    <p className="text-xs text-warning" data-testid="repo-pipelines-capped">
      Showing the first {pipelines.length.toLocaleString()} of {result.total.toLocaleString()} pipelines. Narrow with
      the search above to see the rest.
    </p>
  );

  if (matches.length === 0) {
    return (
      <div className="flex flex-col gap-2">
        {cappedNote}
        <EmptyState
          title="No matches"
          description={`No pipeline matches "${filter.trim()}". Search by name, path, kind, or project.`}
          data-testid="repo-no-matches"
        />
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-2" data-testid="repo-projects">
      {cappedNote}
      {groupByProject(matches).map(([project, rows]) => (
        <ProjectGroup
          key={project}
          project={project}
          rows={rows}
          repoId={repoId}
          filtered={needle !== ""}
          defaultOpen={needle !== ""}
          onOpen={onOpen}
          onRunBatch={onRunBatch}
        />
      ))}
    </div>
  );
}

/** One repo: its sync facts, quick actions (lineage graph, trigger run), and its pipelines. */
export default function RepoDetailPage() {
  const { repoId = "" } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const openSyncTrace = useSyncTracePanel();
  const [triggerOpen, setTriggerOpen] = useState(false);
  const [runBatchFlow, setRunBatchFlow] = useState<string | null>(null);
  const [pipelineFilter, setPipelineFilter] = useState("");

  const repoQuery = useQuery({
    queryKey: ["repos", "detail", repoId],
    queryFn: () => repoApi.getById(repoId),
    enabled: repoId !== "",
  });

  const repo = repoQuery.data;

  // The workbench tab reads the repo's name once it is known, instead of the generic route title.
  useTabTitle(repo?.name);

  // The git source that drives this repo's auto-sync, matched by name (a repo synced by the CLI has none). Small
  // collection, so one page is fetched and the lookup happens client-side.
  const sourcesQuery = useQuery({
    queryKey: ["repo-sources", "list", SOURCE_LOOKUP_CAP],
    queryFn: () => repoSourceApi.list({ page: 1, pageSize: SOURCE_LOOKUP_CAP }),
    refetchInterval: 8000,
  });
  const source = repo === undefined
    ? undefined
    : sourcesQuery.data?.items.find((s) => s.name === repo.name);

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

  // A CLI/local-path repo has no git source for the background sync to poll, so it is re-synced inline from its
  // recorded root path (connected: the derived lineage tier reads the live database). On success the pipeline and
  // lineage views are refreshed so the new waves/edges show without a manual reload.
  const syncLocal = useMutation({
    mutationFn: (id: string) => repoApi.syncLocal(id),
    onSuccess: (result) => {
      const message = `Synced: ${result.pipelinesAdded} added, ${result.pipelinesUpdated} updated, `
        + `${result.objects} objects, ${result.edges} edges, ${result.waves} waves`
        + `${result.connected ? " (connected)" : ""}`
        + `${result.warnings.length > 0 ? `; ${result.warnings.length} warning(s)` : ""}`;
      if (result.connected) {
        toast.success(message);
      } else {
        toast.warning(message);
      }
      void queryClient.invalidateQueries({ queryKey: ["repos"] });
      void queryClient.invalidateQueries({ queryKey: ["pipelines"] });
      void queryClient.invalidateQueries({ queryKey: ["lineage-waves"] });
      void queryClient.invalidateQueries({ queryKey: ["lineage-object-edges"] });
    },
    onError: (error) =>
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });

  if (repoQuery.isError) {
    return isApiError(repoQuery.error)
      ? <CorrelationError error={repoQuery.error} />
      : <p className="text-[13px] text-destructive">{String(repoQuery.error)}</p>;
  }

  return (
    <Page data-testid="page-repo-detail">
      {repo === undefined ? (
        <Card className="gap-0 rounded-lg p-4">
          <div className="flex flex-col gap-2">
            <Skeleton className="h-7 w-72" />
            <Skeleton className="h-4 w-[60%]" />
            <Skeleton className="h-4 w-[40%]" />
          </div>
        </Card>
      ) : (
        <DetailHeaderCard
          title={repo.name}
          badges={source !== undefined && (
            source.lastError !== null ? (
              <Tooltip>
                <TooltipTrigger asChild>
                  <button
                    type="button"
                    onClick={() => openSyncTrace(source.id, repo.name)}
                    className="cursor-pointer"
                    data-testid="repo-source-error"
                  >
                    <Badge variant="destructive">sync error</Badge>
                  </button>
                </TooltipTrigger>
                <TooltipContent className="max-w-lg break-words">
                  {source.lastError}
                  <span className="mt-1 block text-muted-foreground">Click to open the sync trace.</span>
                </TooltipContent>
              </Tooltip>
            ) : (
              <Badge variant="outline" className="border-success/50 text-success">synced</Badge>
            )
          )}
          actions={(
            <>
              {source !== undefined ? (
                <Button
                  variant="outline"
                  size="sm"
                  disabled={!source.enabled || syncNow.isPending}
                  onClick={() => {
                    syncNow.mutate(source.id);
                    openSyncTrace(source.id, repo.name);
                  }}
                  data-testid="repo-sync-now"
                >
                  {syncNow.isPending ? <Loader2 className="animate-spin" /> : <RefreshCw />}
                  Sync now
                </Button>
              ) : repo.rootPath ? (
                <Tooltip>
                  <TooltipTrigger asChild>
                    <span>
                      <Button
                        variant="outline"
                        size="sm"
                        disabled={syncLocal.isPending}
                        onClick={() => syncLocal.mutate(repoId)}
                        data-testid="repo-sync-local"
                      >
                        {syncLocal.isPending ? <Loader2 className="animate-spin" /> : <RefreshCw />}
                        {syncLocal.isPending ? "Syncing..." : "Sync now"}
                      </Button>
                    </span>
                  </TooltipTrigger>
                  <TooltipContent className="max-w-sm">
                    {`Re-sync from ${repo.rootPath} (connected: reads the live database for object lineage)`}
                  </TooltipContent>
                </Tooltip>
              ) : null}
              <Button
                variant="outline"
                size="sm"
                onClick={() => navigate(`/lineage?repoId=${repoId}`)}
                data-testid="repo-lineage-graph"
              >
                <Network />
                Lineage graph
              </Button>
              <Button size="sm" onClick={() => setTriggerOpen(true)} data-testid="open-trigger-run">
                <Play />
                Trigger run
              </Button>
            </>
          )}
        >
          {/* URL and root path are the two values long enough to wrap over several lines and push the rest of the
              grid down, so they clip to one line and carry the full string in a tooltip and on the clipboard. */}
          <DetailPair label="Remote URL">
            <TruncatedText text={repo.remoteUrl} mono copy copyTestId="copy-remote-url" />
          </DetailPair>
          <DetailPair label="Root path">
            <TruncatedText text={repo.rootPath} mono copy copyTestId="copy-root-path" />
          </DetailPair>
          <DetailPair label="Branch">{source?.branch ?? "-"}</DetailPair>
          <DetailPair label="Sync">
            {source === undefined
              ? "manual (CLI)"
              : source.enabled ? `every ${source.syncIntervalSeconds}s` : "paused"}
          </DetailPair>
          <DetailPair label="Synced commit">
            <Mono>{source?.lastSyncedSha?.slice(0, 10) ?? "-"}</Mono>
          </DetailPair>
          <DetailPair label="Credential">
            {source?.credentialReference == null
              ? "-"
              : `${source.credentialReference}${source.credentialUsername ? ` (${source.credentialUsername})` : ""}`}
          </DetailPair>
          <DetailPair label="First seen"><RelativeTime value={repo.firstSeenUtc} absolute /></DetailPair>
          <DetailPair label="Last sync"><RelativeTime value={repo.lastSyncUtc} absolute /></DetailPair>
        </DetailHeaderCard>
      )}

      <div className="flex flex-col gap-3">
        {/* The search follows the heading rather than being pushed to the far right (DESIGN.md 7.1), so it starts
            where the filter row starts on every other page. */}
        <div className="flex flex-wrap items-center gap-3">
          <h2 className="text-base font-medium">Projects</h2>
          <SearchInput
            value={pipelineFilter}
            onChange={setPipelineFilter}
            placeholder="Search by name, path, kind, project"
            label="Search pipelines"
            testId="repo-pipeline-search"
          />
        </div>
        <PipelinesByProject
          repoId={repoId}
          filter={pipelineFilter}
          onOpen={(id) => navigate(`/pipelines/${id}`)}
          onRunBatch={(_repoId, flowName) => setRunBatchFlow(flowName)}
        />
      </div>

      {triggerOpen && <TriggerRunDialog open onClose={() => setTriggerOpen(false)} repoId={repoId} />}
      {runBatchFlow !== null && (
        <TriggerRunDialog
          open
          onClose={() => setRunBatchFlow(null)}
          repoId={repoId}
          flowName={runBatchFlow}
        />
      )}
    </Page>
  );
}
