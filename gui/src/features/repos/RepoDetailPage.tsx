import { useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useSnackbar } from "notistack";
import Accordion from "@mui/material/Accordion";
import AccordionDetails from "@mui/material/AccordionDetails";
import AccordionSummary from "@mui/material/AccordionSummary";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Chip from "@mui/material/Chip";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableRow from "@mui/material/TableRow";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";
import AccountTreeIcon from "@mui/icons-material/AccountTree";
import ExpandMoreIcon from "@mui/icons-material/ExpandMore";
import FolderIcon from "@mui/icons-material/Folder";
import PlayArrowIcon from "@mui/icons-material/PlayArrow";
import SyncIcon from "@mui/icons-material/Sync";
import { isApiError } from "../../api/client";
import { pipelineApi, repoApi, repoSourceApi } from "../../api/endpoints";
import type { PipelineSummary } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { DetailHeaderCard } from "../../components/DetailHeaderCard";
import { DetailPair } from "../../components/DetailPair";
import { EmptyState } from "../../components/EmptyState";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { RelativeTime } from "../../components/RelativeTime";
import { ActiveBadge } from "../../components/StatusBadge";
import { TriggerRunDialog } from "../runs/TriggerRunDialog";
import { projectOf } from "./project";

/** The most pipelines a single repo realistically holds; one page covers grouping them by project. */
const REPO_PIPELINE_CAP = 500;

/** The most repos/sources a single control plane realistically holds; one page covers the by-name lookup. */
const SOURCE_LOOKUP_CAP = 200;

/** The repo's pipelines grouped by project (root folder): each project is one source's set of pipelines. A project
 * that contains a batch flow (flowType: batch) can be run as a unit: the batch executes its members in lineage
 * wave order, so "Run project" triggers that ordered run. */
function PipelinesByProject({
  repoId, onOpen, onRunBatch,
}: {
  repoId: string;
  onOpen: (pipelineId: string) => void;
  onRunBatch: (flowName: string) => void;
}) {
  const query = useQuery({
    queryKey: ["pipelines", "by-repo-grouped", repoId],
    queryFn: () => pipelineApi.list({ repoId, page: 1, pageSize: REPO_PIPELINE_CAP }),
  });

  if (query.isError) {
    return isApiError(query.error)
      ? <CorrelationError error={query.error} />
      : <Typography color="error">{String(query.error)}</Typography>;
  }

  const pipelines = query.data?.items;
  if (pipelines === undefined) {
    return <Skeleton variant="rounded" height={120} />;
  }

  if (pipelines.length === 0) {
    return (
      <EmptyState
        title="No pipelines yet"
        description="They appear here after a sync imports the selected flows from git."
        data-testid="repo-no-pipelines"
      />
    );
  }

  const byProject = new Map<string, PipelineSummary[]>();
  for (const p of [...pipelines].sort((a, b) => a.name.localeCompare(b.name))) {
    const key = projectOf(p.relativePath);
    const group = byProject.get(key);
    if (group) {
      group.push(p);
    } else {
      byProject.set(key, [p]);
    }
  }
  const projects = [...byProject.entries()].sort((a, b) => a[0].localeCompare(b[0]));

  return (
    <Stack spacing={1} data-testid="repo-projects">
      {projects.map(([project, rows]) => {
        const inactive = rows.filter((r) => !r.active).length;
        // The project's batch flow (if any): the wave-ordered "run the whole project" entry point.
        const batch = rows.find((r) => r.kind === "batch" && r.active);
        return (
          <Accordion key={project} defaultExpanded disableGutters data-testid="repo-project">
            <AccordionSummary expandIcon={<ExpandMoreIcon />}>
              <Stack direction="row" spacing={1} alignItems="center" sx={{ flexGrow: 1, minWidth: 0 }}>
                <FolderIcon fontSize="small" color="action" />
                <Typography variant="body2" fontWeight={700} noWrap>{project}</Typography>
                <Chip size="small" variant="outlined" label={`${rows.length} pipeline${rows.length === 1 ? "" : "s"}`} />
                {inactive > 0 && (
                  <Chip size="small" variant="outlined" color="warning" label={`${inactive} inactive`} />
                )}
                <Box sx={{ flexGrow: 1 }} />
                {batch !== undefined && (
                  <Tooltip title={`Run this project's batch flow '${batch.name}' (members in wave order)`}>
                    <Button
                      size="small"
                      variant="outlined"
                      startIcon={<PlayArrowIcon fontSize="small" />}
                      onClick={(e) => {
                        e.stopPropagation();
                        onRunBatch(batch.name);
                      }}
                      data-testid="run-project"
                    >
                      Run project
                    </Button>
                  </Tooltip>
                )}
              </Stack>
            </AccordionSummary>
            <AccordionDetails sx={{ p: 0 }}>
              <Table size="small">
                <TableBody>
                  {rows.map((p) => (
                    <TableRow
                      key={p.id}
                      hover
                      sx={{ cursor: "pointer" }}
                      onClick={() => onOpen(p.id)}
                      data-testid="table-row"
                    >
                      <TableCell><Typography variant="body2" fontWeight={600}>{p.name}</Typography></TableCell>
                      <TableCell><Chip size="small" variant="outlined" label={p.kind} /></TableCell>
                      <TableCell>{p.wave === -1 ? "-" : `wave ${p.wave}`}</TableCell>
                      <TableCell><ActiveBadge active={p.active} /></TableCell>
                      <TableCell>
                        <Typography variant="caption" color="text.secondary">{p.relativePath}</Typography>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </AccordionDetails>
          </Accordion>
        );
      })}
    </Stack>
  );
}

/** One repo: its sync facts, quick actions (lineage graph, trigger run), and its pipelines. */
export default function RepoDetailPage() {
  const { repoId = "" } = useParams();
  const navigate = useNavigate();
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const [triggerOpen, setTriggerOpen] = useState(false);
  const [runBatchFlow, setRunBatchFlow] = useState<string | null>(null);

  const repoQuery = useQuery({
    queryKey: ["repos", "detail", repoId],
    queryFn: () => repoApi.getById(repoId),
    enabled: repoId !== "",
  });

  const repo = repoQuery.data;

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
      enqueueSnackbar("Sync requested", { variant: "success" });
      void queryClient.invalidateQueries({ queryKey: ["repo-sources"] });
      void queryClient.invalidateQueries({ queryKey: ["repos"] });
    },
    onError: (error) =>
      enqueueSnackbar(error instanceof Error ? error.message : String(error), { variant: "error" }),
  });

  // A CLI/local-path repo has no git source for the background sync to poll, so it is re-synced inline from its
  // recorded root path (connected: the derived lineage tier reads the live database). On success the pipeline and
  // lineage views are refreshed so the new waves/edges show without a manual reload.
  const syncLocal = useMutation({
    mutationFn: (id: string) => repoApi.syncLocal(id),
    onSuccess: (result) => {
      enqueueSnackbar(
        `Synced: ${result.pipelinesAdded} added, ${result.pipelinesUpdated} updated, `
        + `${result.objects} objects, ${result.edges} edges, ${result.waves} waves`
        + `${result.connected ? " (connected)" : ""}`
        + `${result.warnings.length > 0 ? `; ${result.warnings.length} warning(s)` : ""}`,
        { variant: result.connected ? "success" : "warning" },
      );
      void queryClient.invalidateQueries({ queryKey: ["repos"] });
      void queryClient.invalidateQueries({ queryKey: ["pipelines"] });
      void queryClient.invalidateQueries({ queryKey: ["lineage-waves"] });
      void queryClient.invalidateQueries({ queryKey: ["lineage-object-edges"] });
    },
    onError: (error) =>
      enqueueSnackbar(error instanceof Error ? error.message : String(error), { variant: "error" }),
  });

  if (repoQuery.isError) {
    return isApiError(repoQuery.error)
      ? <CorrelationError error={repoQuery.error} />
      : <Typography color="error">{String(repoQuery.error)}</Typography>;
  }

  return (
    <Page data-testid="page-repo-detail">
      {repo === undefined ? (
        <Card variant="outlined">
          <CardContent>
            <Stack spacing={1}>
              <Skeleton width={280} height={36} />
              <Skeleton width="60%" />
              <Skeleton width="40%" />
            </Stack>
          </CardContent>
        </Card>
      ) : (
        <DetailHeaderCard
          title={repo.name}
          badges={source !== undefined && (
            source.lastError !== null ? (
              <Tooltip title={source.lastError}>
                <Chip size="small" color="error" label="sync error" data-testid="repo-source-error" />
              </Tooltip>
            ) : (
              <Chip size="small" color="success" variant="outlined" label="synced" />
            )
          )}
          actions={(
            <>
              {source !== undefined ? (
                <Button
                  variant="outlined"
                  startIcon={<SyncIcon />}
                  disabled={!source.enabled || syncNow.isPending}
                  onClick={() => syncNow.mutate(source.id)}
                  data-testid="repo-sync-now"
                >
                  Sync now
                </Button>
              ) : repo.rootPath ? (
                <Tooltip title={`Re-sync from ${repo.rootPath} (connected: reads the live database for object lineage)`}>
                  <span>
                    <Button
                      variant="outlined"
                      startIcon={<SyncIcon />}
                      disabled={syncLocal.isPending}
                      onClick={() => syncLocal.mutate(repoId)}
                      data-testid="repo-sync-local"
                    >
                      {syncLocal.isPending ? "Syncing…" : "Sync now"}
                    </Button>
                  </span>
                </Tooltip>
              ) : null}
              <Button
                variant="outlined"
                startIcon={<AccountTreeIcon />}
                onClick={() => navigate(`/lineage?repoId=${repoId}`)}
                data-testid="repo-lineage-graph"
              >
                Lineage graph
              </Button>
              <Button
                variant="contained"
                startIcon={<PlayArrowIcon />}
                onClick={() => setTriggerOpen(true)}
                data-testid="open-trigger-run"
              >
                Trigger run
              </Button>
            </>
          )}
        >
          <DetailPair label="Remote URL">{repo.remoteUrl ?? "-"}</DetailPair>
          <DetailPair label="Root path">{repo.rootPath ?? "-"}</DetailPair>
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
          <DetailPair label="First seen"><RelativeTime value={repo.firstSeenUtc} /></DetailPair>
          <DetailPair label="Last sync"><RelativeTime value={repo.lastSyncUtc} /></DetailPair>
        </DetailHeaderCard>
      )}

      <Box>
        <Typography variant="h6" gutterBottom>Projects</Typography>
        <PipelinesByProject
          repoId={repoId}
          onOpen={(id) => navigate(`/pipelines/${id}`)}
          onRunBatch={(flowName) => setRunBatchFlow(flowName)}
        />
      </Box>

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
