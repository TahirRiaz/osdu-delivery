import { useCallback, useEffect, useRef, useState } from "react";
import { Link as RouterLink, Navigate, useNavigate, useParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useSnackbar } from "notistack";
import Alert from "@mui/material/Alert";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import Dialog from "@mui/material/Dialog";
import DialogContent from "@mui/material/DialogContent";
import DialogTitle from "@mui/material/DialogTitle";
import Link from "@mui/material/Link";
import Paper from "@mui/material/Paper";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import { alpha, useTheme } from "@mui/material/styles";
import Tab from "@mui/material/Tab";
import Tabs from "@mui/material/Tabs";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";
import ContentCopyIcon from "@mui/icons-material/ContentCopy";
import ErrorOutlineIcon from "@mui/icons-material/ErrorOutline";
import MonitorHeartIcon from "@mui/icons-material/MonitorHeart";
import ReplayIcon from "@mui/icons-material/Replay";
import RuleIcon from "@mui/icons-material/Rule";
import type {
  RunAssertion, RunFile, RunHealthCheckMetric, RunStatement, RunSurrogateKey, RunTraceEntry,
} from "../../api/types";
import { isApiError } from "../../api/client";
import { pipelineApi, runApi } from "../../api/endpoints";
import { CodeView } from "../../components/CodeView";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable } from "../../components/DataTable";
import { DetailHeaderCard } from "../../components/DetailHeaderCard";
import { DetailPair } from "../../components/DetailPair";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { RunStatusBadge } from "../../components/StatusBadge";
import { TruncatedText } from "../../components/TruncatedText";
import { pollingInterval } from "../../hooks/usePolling";
import { embeddedHealthCheckName } from "../../lib/definition";
import { formatBytes, formatDurationSeconds, parseUtc } from "../../lib/time";
import { TriggerRunDialog } from "./TriggerRunDialog";
import { useRunTraceStream } from "./useRunTraceStream";

/** A compact UTC stamp for a backfill window bound (the API sends UTC timestamps). */
function fmtBound(value: string): string {
  return parseUtc(value).toISOString().replace("T", " ").replace(/:\d\d\.\d+Z$/, "");
}

/** The chip color for an incremental mode: an incremental read is the notable case (primary), a full read is
 * neutral, and the operator-driven backfill / init-load modes echo the backfill banner's warning tone. */
function incrementalModeColor(mode: string): "primary" | "default" | "warning" {
  if (mode === "incremental") {
    return "primary";
  }

  if (mode === "backfill" || mode === "init-load") {
    return "warning";
  }

  return "default";
}

function YesNo({ value }: { value: boolean }) {
  return value
    ? <Chip size="small" label="yes" color="success" variant="outlined" />
    : <Chip size="small" label="no" color="default" variant="outlined" />;
}

/** The chip color for a trace level: problems stand out, info is the normal case, engine detail is muted. */
function traceLevelColor(level: RunTraceEntry["level"]): "default" | "info" | "warning" | "error" {
  if (level === "error") {
    return "error";
  }

  if (level === "warning") {
    return "warning";
  }

  return level === "info" ? "info" : "default";
}

/** A compact UTC clock stamp (HH:mm:ss.fff) for a trace entry; legacy statements without one show "-". */
function fmtEventTime(value: string | null): string {
  return value ? parseUtc(value).toISOString().slice(11, 23) : "-";
}

const traceColumns: Column<RunTraceEntry>[] = [
  { id: "time", header: "Time", width: 120, render: (row) => <Mono>{fmtEventTime(row.timestampUtc)}</Mono> },
  {
    id: "level",
    header: "Level",
    width: 100,
    // A statement entry is labelled "sql" (its level is always trace); event entries show their own level.
    render: (row) => (
      <Chip
        size="small"
        variant="outlined"
        label={row.kind === "statement" ? "sql" : row.level}
        color={row.kind === "statement" ? "default" : traceLevelColor(row.level)}
      />
    ),
  },
  {
    id: "step",
    header: "Step",
    width: 220,
    render: (row) => (row.error
      ? (
        <Stack direction="row" spacing={0.75} alignItems="center">
          <ErrorOutlineIcon fontSize="small" color="error" />
          <Typography component="span" variant="body2" color="error" fontWeight={600}>{row.step ?? "-"}</Typography>
        </Stack>
      )
      : row.step ?? "-"),
  },
  {
    id: "detail",
    header: "Event",
    render: (row) => (row.kind === "statement"
      ? <TruncatedText text={row.sql} mono maxWidth={640} />
      : <TruncatedText text={row.message} maxWidth={640} />),
  },
];

const statementColumns: Column<RunStatement>[] = [
  { id: "ordinal", header: "Ordinal", width: 90, render: (row) => row.ordinal },
  {
    id: "step",
    header: "Step",
    width: 220,
    render: (row) => (row.error
      ? (
        <Stack direction="row" spacing={0.75} alignItems="center">
          <ErrorOutlineIcon fontSize="small" color="error" />
          <Typography component="span" variant="body2" color="error" fontWeight={600}>{row.step}</Typography>
        </Stack>
      )
      : row.step),
  },
  {
    id: "sql",
    header: "SQL",
    render: (row) => <TruncatedText text={row.sql} mono maxWidth={640} />,
  },
];

const assertionColumns: Column<RunAssertion>[] = [
  { id: "name", header: "Name", render: (row) => row.name },
  { id: "result", header: "Result", render: (row) => row.result },
  { id: "assertedValue", header: "Asserted value", render: (row) => <TruncatedText text={row.assertedValue} maxWidth={280} /> },
  { id: "evaluated", header: "Evaluated", render: (row) => <YesNo value={row.evaluated} /> },
  { id: "error", header: "Error", render: (row) => <TruncatedText text={row.error} maxWidth={360} /> },
];

const fileColumns: Column<RunFile>[] = [
  { id: "name", header: "Name", render: (row) => row.name },
  { id: "path", header: "Path", render: (row) => <TruncatedText text={row.path} mono maxWidth={520} /> },
  { id: "rows", header: "Rows", align: "right", render: (row) => row.rows },
  { id: "columns", header: "Columns", align: "right", render: (row) => row.columns },
  { id: "size", header: "Size", align: "right", render: (row) => formatBytes(row.sizeBytes) },
  {
    id: "hash",
    header: "Hash",
    render: (row) => (row.hash ? <TruncatedText text={row.hash} mono maxWidth={140} /> : "-"),
  },
];

const surrogateKeyColumns: Column<RunSurrogateKey>[] = [
  { id: "surrogateTable", header: "Table", render: (row) => row.surrogateTable },
  { id: "surrogateColumn", header: "Column", render: (row) => row.surrogateColumn },
  { id: "keysGenerated", header: "Keys generated", align: "right", render: (row) => row.keysGenerated },
  { id: "rowsStamped", header: "Rows stamped", align: "right", render: (row) => row.rowsStamped },
  { id: "isRemote", header: "Remote", render: (row) => <YesNo value={row.isRemote} /> },
  { id: "executed", header: "Executed", render: (row) => <YesNo value={row.executed} /> },
  { id: "error", header: "Error", render: (row) => <TruncatedText text={row.error} maxWidth={360} /> },
];

const healthMetricColumns: Column<RunHealthCheckMetric>[] = [
  { id: "name", header: "Name", render: (row) => row.name },
  { id: "seriesPoints", header: "Series points", align: "right", render: (row) => row.seriesPoints },
  { id: "imputedPoints", header: "Imputed", align: "right", render: (row) => row.imputedPoints },
  { id: "immaturePoints", header: "Immature", align: "right", render: (row) => row.immaturePoints },
  { id: "anomalies", header: "Anomalies", align: "right", render: (row) => row.anomalies },
  { id: "levelShifts", header: "Level shifts", align: "right", render: (row) => row.levelShifts },
  { id: "modelTrained", header: "Model trained", render: (row) => <YesNo value={row.modelTrained} /> },
  { id: "modelTrainer", header: "Trainer", render: (row) => row.modelTrainer ?? "-" },
  { id: "error", header: "Error", render: (row) => <TruncatedText text={row.error} maxWidth={360} /> },
];

/** Everything the API recorded about one run: live header while active, detail tables once results land. */
export default function RunDetailPage() {
  const { runId } = useParams<{ runId: string }>();
  if (!runId) {
    return <Navigate to="/runs" replace />;
  }

  return <RunDetailContent runId={runId} />;
}

function RunDetailContent({ runId }: { runId: string }) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const { enqueueSnackbar } = useSnackbar();
  const [tab, setTab] = useState(0);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [statement, setStatement] = useState<RunStatement | null>(null);
  const [traceEntry, setTraceEntry] = useState<RunTraceEntry | null>(null);
  const theme = useTheme();
  // A soft red wash marking the one statement that threw (see rowSx on the Statements table below).
  const failedRowStyle = { backgroundColor: alpha(theme.palette.error.main, 0.14) };

  const query = useQuery({
    queryKey: ["runs", runId],
    queryFn: () => runApi.getById(runId),
    refetchInterval: (q) => {
      const data = q.state.data;
      if (data && (data.status === "succeeded" || data.status === "failed" || data.status === "cancelled")) {
        return false;
      }

      return pollingInterval(3000)();
    },
  });

  // The pipeline's stored definition serves two tabs: for an ingestion run it says whether the flow embeds a
  // healthCheck: block (its derived flow name powers the Health tab's "Run health check" button, since the check
  // is a sibling pipeline whose metrics live on its own runs), and for every run it backs the Source tab's flow
  // YAML. Fetched for ing runs eagerly (the embedded-check name is needed before the Health tab opens) and for
  // any run once the Source tab is selected, so a non-ing run pays for it only when the source is actually read.
  // Cached under the same key the pipeline detail page uses.
  const pipelineQuery = useQuery({
    queryKey: ["pipelines", "detail", query.data?.pipelineId ?? ""],
    queryFn: () => pipelineApi.getById(query.data!.pipelineId),
    enabled: query.data !== undefined && (query.data.flowKind === "ing" || tab === 1),
  });
  const embeddedCheck = pipelineQuery.data ? embeddedHealthCheckName(pipelineQuery.data.definitionJson) : null;

  // The live trace: while the run is queued or running, the Trace tab is fed by the SSE stream (entries
  // arrive the moment the executing node persists them); once the run ends, the stream's end frame (or the
  // header poll observing the terminal status, whichever lands first) refetches everything under this run so
  // every tab shows the authoritative re-projected result without a manual reload.
  const status = query.data?.status;
  const live = status === "queued" || status === "running";
  const refreshRun = useCallback(
    () => void queryClient.invalidateQueries({ queryKey: ["runs", runId] }),
    [queryClient, runId],
  );
  const { entries: liveEntries, connected: streamConnected } = useRunTraceStream(runId, live, refreshRun);
  const wasLive = useRef(false);
  useEffect(() => {
    if (live) {
      wasLive.current = true;
    } else if (wasLive.current) {
      wasLive.current = false;
      refreshRun();
    }
  }, [live, refreshRun]);

  // "Copy trace" fetches the server-rendered plain-text trace (the same document the LLM-facing
  // /trace/text endpoint serves) and puts it on the clipboard, so what is pasted into a ticket or a chat is
  // always the complete, canonical rendering rather than whatever page the table happens to show.
  const copyTrace = useMutation({
    mutationFn: () => runApi.traceText(runId),
    onSuccess: async (text) => {
      try {
        await navigator.clipboard.writeText(text);
        enqueueSnackbar("Trace copied to clipboard.", { variant: "success" });
      } catch {
        enqueueSnackbar("The browser blocked clipboard access.", { variant: "error" });
      }
    },
    onError: (error) => {
      enqueueSnackbar(isApiError(error) ? error.title : String(error), { variant: "error" });
    },
  });

  // Re-run opens the trigger dialog prefilled with this run's flow and the operator parameters it carried (full
  // load, backfill window, file pattern), so the run can be repeated as-is or adjusted before launching. The dialog
  // renders exactly the parameters this flow's kind honors and navigates to the new run on submit.
  const [rerunOpen, setRerunOpen] = useState(false);

  // The on-demand execution the Assertions and Health tabs offer: a fresh single-flow run of this run's
  // pipeline against the current code. With assertionsOnly the engine evaluates the flow's declared assertions
  // (manual-mode ones included) against the current target and loads nothing; without it, it is a plain run
  // (the Health tab's "Run health check" for hc flows). The new execution is a new run; the button navigates there.
  const triggerFlow = useMutation({
    mutationFn: (parameters: { assertionsOnly?: boolean; flowName?: string }) => {
      const r = query.data;
      if (!r?.repoId) {
        throw new Error("The run has not loaded yet.");
      }

      return runApi.trigger({
        repoId: r.repoId,
        flowName: parameters.flowName ?? r.flowName,
        scope: "flow",
        pool: r.targetPool,
        assertionsOnly: parameters.assertionsOnly,
      });
    },
    onSuccess: (accepted, parameters) => {
      enqueueSnackbar(parameters.assertionsOnly ? "Assertion run enqueued." : "Health check enqueued.", { variant: "success" });
      if (accepted.runId) {
        navigate(`/runs/${accepted.runId}`);
      }
    },
    onError: (error) => {
      enqueueSnackbar(isApiError(error) ? error.title : String(error), { variant: "error" });
    },
  });

  const cancel = useMutation({
    mutationFn: () => runApi.cancel(runId),
    onSuccess: () => {
      enqueueSnackbar("Cancel requested for this run.", { variant: "success" });
      setConfirmOpen(false);
      void queryClient.invalidateQueries({ queryKey: ["runs", runId] });
    },
    onError: (error) => {
      enqueueSnackbar(isApiError(error) ? error.title : String(error), { variant: "error" });
      setConfirmOpen(false);
    },
  });

  if (query.isError) {
    return (
      <Page data-testid="page-run-detail">
        {isApiError(query.error)
          ? <CorrelationError error={query.error} />
          : <Typography color="error">{String(query.error)}</Typography>}
      </Page>
    );
  }

  const run = query.data;
  if (run === undefined) {
    return (
      <Page data-testid="page-run-detail">
        <Skeleton variant="rounded" height={240} />
        <Skeleton variant="rounded" height={320} />
      </Page>
    );
  }

  // A run can be cancelled while queued (dequeued outright) or while running (the node aborts the in-flight
  // statement). Once a running run's cancel is in flight, the button is disabled and a "cancelling" chip shows,
  // until polling reflects the terminal "cancelled" status.
  const cancellable = run.status === "queued" || run.status === "running";
  const cancelling = run.status === "running" && run.cancelRequestedUtc !== null;

  return (
    <Page data-testid="page-run-detail">
      <DetailHeaderCard
        title={run.flowName}
        badges={(
          <>
            <RunStatusBadge status={run.status} />
            {cancelling && (
              <Chip size="small" color="warning" label="cancelling" data-testid="run-cancelling" />
            )}
            <Chip size="small" label={run.flowKind} variant="outlined" data-testid="run-kind" />
            <Chip size="small" label={`batch: ${run.batch}`} variant="outlined" data-testid="run-batch" />
            {run.assertionsOnly && (
              <Chip size="small" color="info" label="assertions only" data-testid="run-assertions-only" />
            )}
          </>
        )}
        actions={cancellable
          ? (
            <Button
              color="error"
              variant="outlined"
              onClick={() => setConfirmOpen(true)}
              disabled={cancelling || cancel.isPending}
              data-testid="cancel-run"
            >
              {cancelling ? "Cancelling…" : "Cancel run"}
            </Button>
          )
          : (run.repoId !== null && (
            <Button
              variant="outlined"
              startIcon={<ReplayIcon fontSize="small" />}
              onClick={() => setRerunOpen(true)}
              data-testid="rerun-run"
            >
              Re-run
            </Button>
          ))}
      >
        <DetailPair label="Run id"><Mono>{run.runId}</Mono></DetailPair>
        <DetailPair label="Repo">
          {run.repoId
            ? <Link component={RouterLink} to={`/repos/${run.repoId}`} data-testid="run-repo-link">{run.repoId}</Link>
            : "-"}
        </DetailPair>
        <DetailPair label="Pipeline">
          <Link component={RouterLink} to={`/pipelines/${run.pipelineId}`} data-testid="run-pipeline-link">
            {run.pipelineId}
          </Link>
        </DetailPair>
        {run.groupId && (
          <DetailPair label="Run group">
            <Link component={RouterLink} to={`/runs/groups/${run.groupId}`} data-testid="run-group-link">
              {run.groupId}
            </Link>
          </DetailPair>
        )}
        <DetailPair label="Batch">{run.batch}</DetailPair>
        <DetailPair label="Step">{run.wave >= 0 ? run.wave : "-"}</DetailPair>
        <DetailPair label="Enqueued"><RelativeTime value={run.enqueuedUtc} /></DetailPair>
        <DetailPair label="Started"><RelativeTime value={run.startUtc} /></DetailPair>
        <DetailPair label="Ended"><RelativeTime value={run.endUtc} /></DetailPair>
        <DetailPair label="Duration">
          {run.durationSeconds != null ? formatDurationSeconds(run.durationSeconds) : "-"}
        </DetailPair>
        <DetailPair label="Claimed by node">{run.claimedByNode ?? "-"}</DetailPair>
        <DetailPair label="Host">{run.host ?? "-"}</DetailPair>
        <DetailPair label="Target pool">{run.targetPool ?? "-"}</DetailPair>
        <DetailPair label="Commit">
          {run.commitSha ? (
            <Tooltip title={run.commitSha}>
              <Mono>{run.commitSha.slice(0, 12)}</Mono>
            </Tooltip>
          ) : "-"}
        </DetailPair>
        <DetailPair label="Rows loaded">{run.rowsLoaded ?? "-"}</DetailPair>
        <DetailPair label="Rows inserted">{run.rowsInserted ?? "-"}</DetailPair>
        <DetailPair label="Rows updated">{run.rowsUpdated ?? "-"}</DetailPair>
        <DetailPair label="Rows deleted">{run.rowsDeleted ?? "-"}</DetailPair>
      </DetailHeaderCard>

      {run.status === "failed" && run.error !== null && (
        <Alert severity="error" data-testid="run-error">{run.error}</Alert>
      )}

      {run.status === "failed" && run.failedStatementSql !== null && (
        <Paper variant="outlined" sx={{ p: 1.5, borderColor: "error.main" }} data-testid="run-failed-statement">
          <Typography variant="subtitle2" color="error" gutterBottom>
            Failing statement {run.failedStatementOrdinal}: {run.failedStatementStep}
          </Typography>
          <CodeView value={run.failedStatementSql} language="sql" data-testid="run-error-sql" />
        </Paper>
      )}

      {run.assertionsOnly && (
        <Alert severity="info" icon={false} data-testid="run-assertions-only-banner">
          Assertions-only run: the flow's declared assertions (manual-mode ones included) were evaluated against
          the current target; no data was read or loaded.
        </Alert>
      )}

      {(run.fullLoad || run.backfillFrom || run.filePattern) && (
        <Alert severity="info" icon={false} data-testid="run-backfill">
          <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap alignItems="center">
            <Typography variant="body2" fontWeight={600}>Backfill:</Typography>
            {run.fullLoad && <Chip size="small" label="full load" color="warning" />}
            {run.backfillFrom && (
              <Chip
                size="small"
                label={run.backfillTo
                  ? `window ${fmtBound(run.backfillFrom)} .. ${fmtBound(run.backfillTo)}`
                  : `from ${fmtBound(run.backfillFrom)}`}
              />
            )}
            {run.filePattern && <Chip size="small" label={`files '${run.filePattern}'`} />}
          </Stack>
        </Alert>
      )}

      {run.incrementalMode && (
        <Paper variant="outlined" sx={{ p: 1.5 }} data-testid="run-incremental">
          <Stack direction="row" spacing={1} alignItems="center" sx={{ mb: 1 }}>
            <Typography variant="subtitle2">Incremental load</Typography>
            <Chip size="small" label={run.incrementalMode} color={incrementalModeColor(run.incrementalMode)} />
          </Stack>
          <Stack spacing={0.75}>
            {run.incrementalFilter && (
              <DetailPair label="Filter"><Mono>{run.incrementalFilter}</Mono></DetailPair>
            )}
            {run.incrementalWatermark && (
              <DetailPair label="Watermark"><Mono>{run.incrementalWatermark}</Mono></DetailPair>
            )}
            {run.incrementalWatermarkSource && (
              <DetailPair label="Watermark source"><Mono>{run.incrementalWatermarkSource}</Mono></DetailPair>
            )}
          </Stack>
        </Paper>
      )}

      {run.dataSetConvention && (
        <Paper variant="outlined" sx={{ p: 1.5 }} data-testid="run-dataset-convention">
          <Typography variant="subtitle2" sx={{ mb: 1 }}>DataSet date detection</Typography>
          <DetailPair label="Convention"><Mono>{run.dataSetConvention}</Mono></DetailPair>
        </Paper>
      )}

      <Tabs
        value={tab}
        onChange={(_, next: number) => setTab(next)}
        variant="scrollable"
        allowScrollButtonsMobile
        data-testid="run-tabs"
      >
        <Tab label="Trace" data-testid="tab-trace" />
        <Tab label="Source" data-testid="tab-source" />
        <Tab label="Files" data-testid="tab-files" />
        <Tab label="Statements" data-testid="tab-statements" />
        <Tab label="Surrogate" data-testid="tab-surrogate-keys" />
        <Tab label="Assertions" data-testid="tab-assertions" />
        <Tab label="Health" data-testid="tab-health-metrics" />
      </Tabs>

      {tab === 0 && (
        <Stack spacing={1}>
          <Stack direction="row" spacing={1} alignItems="center">
            {live && (
              <>
                <Chip
                  size="small"
                  color={streamConnected ? "success" : "warning"}
                  variant="outlined"
                  label={streamConnected ? "live" : "reconnecting"}
                  data-testid="trace-stream-state"
                />
                <Typography variant="body2" color="text.secondary">
                  {streamConnected
                    ? "Streaming the trace as the run executes."
                    : "Connection lost; resuming the stream."}
                </Typography>
              </>
            )}
            <Stack sx={{ flexGrow: 1 }} />
            <Button
              size="small"
              variant="outlined"
              startIcon={<ContentCopyIcon fontSize="small" />}
              onClick={() => copyTrace.mutate()}
              disabled={copyTrace.isPending}
              data-testid="copy-trace"
            >
              Copy trace
            </Button>
          </Stack>
          {live
            ? (
              // Live mode: the SSE stream pushes each entry the moment the executing node persists it. No paging
              // while streaming (a run's trace is bounded); the end frame swaps this for the paged view below.
              <DataTable
                columns={traceColumns}
                rows={liveEntries}
                rowKey={(row) => `${row.kind}-${row.id}`}
                onRowClick={(row) => setTraceEntry(row)}
                rowSx={(row) => (row.error ? failedRowStyle : undefined)}
                emptyMessage={run.status === "queued"
                  ? "The run is queued; the trace streams in once a node claims it."
                  : "Waiting for the first trace entry."}
                data-testid="trace-live-table"
              />
            )
            : (
              <PagedTable
                queryKey={["runs", runId, "trace"]}
                fetchPage={(page, pageSize) => runApi.trace(runId, { page, pageSize })}
                columns={traceColumns}
                rowKey={(row) => `${row.kind}-${row.id}`}
                onRowClick={(row) => setTraceEntry(row)}
                rowSx={(row) => (row.error ? failedRowStyle : undefined)}
                emptyMessage="No trace was recorded for this run."
                data-testid="trace-table"
              />
            )}
        </Stack>
      )}
      {tab === 1 && (
        <Stack spacing={1}>
          <Typography variant="body2" color="text.secondary">
            The flow's YAML definition as registered in the catalog. This is the current source for this pipeline;
            {run.commitSha
              ? ` the run executed against commit ${run.commitSha.slice(0, 12)}, so a later change to the flow file may differ from what ran.`
              : " if the flow file has changed since this run, the executed source may differ."}
          </Typography>
          {pipelineQuery.isError && (
            <Alert severity="error" data-testid="source-error">
              {isApiError(pipelineQuery.error) ? pipelineQuery.error.title : "Could not load the flow definition from the catalog."}
            </Alert>
          )}
          {pipelineQuery.data === undefined && !pipelineQuery.isError
            ? <Skeleton variant="rounded" height={560} data-testid="source-loading" />
            : pipelineQuery.data !== undefined && (
              <CodeView value={pipelineQuery.data.yaml} language="yaml" height={560} lsp data-testid="run-source-yaml" />
            )}
        </Stack>
      )}
      {tab === 2 && (
        <PagedTable
          queryKey={["runs", runId, "files"]}
          fetchPage={(page, pageSize) => runApi.files(runId, { page, pageSize })}
          columns={fileColumns}
          rowKey={(row) => row.id}
          emptyMessage="No files were recorded for this run."
          data-testid="files-table"
        />
      )}
      {tab === 3 && (
        <PagedTable
          queryKey={["runs", runId, "statements"]}
          fetchPage={(page, pageSize) => runApi.statements(runId, { page, pageSize })}
          columns={statementColumns}
          rowKey={(row) => row.id}
          onRowClick={(row) => setStatement(row)}
          // While the run is executing, the node streams each statement into the catalog as it runs, so poll to
          // show them live; polling stops once the run reaches a terminal state.
          pollMs={run.status === "running" ? 3000 : undefined}
          rowSx={(row) => (row.error ? failedRowStyle : undefined)}
          emptyMessage={run.status === "running"
            ? "Statements will appear here as the run executes."
            : "No statements were recorded for this run."}
          data-testid="statements-table"
        />
      )}
      {tab === 4 && (
        <PagedTable
          queryKey={["runs", runId, "surrogate-keys"]}
          fetchPage={(page, pageSize) => runApi.surrogateKeys(runId, { page, pageSize })}
          columns={surrogateKeyColumns}
          rowKey={(row) => row.id}
          emptyMessage="No surrogate keys were recorded for this run."
          data-testid="surrogate-keys-table"
        />
      )}
      {tab === 5 && (
        <Stack spacing={1}>
          {run.flowKind === "ing" && run.repoId !== null && (
            <Stack direction="row" spacing={1} alignItems="center">
              <Typography variant="body2" color="text.secondary">
                Run the flow's declared assertions on demand, including mode: manual ones; nothing is loaded.
              </Typography>
              <Stack sx={{ flexGrow: 1 }} />
              <Button
                size="small"
                variant="outlined"
                startIcon={<RuleIcon fontSize="small" />}
                onClick={() => triggerFlow.mutate({ assertionsOnly: true })}
                disabled={triggerFlow.isPending}
                data-testid="run-assertions"
              >
                Run assertions
              </Button>
            </Stack>
          )}
          <PagedTable
            queryKey={["runs", runId, "assertions"]}
            fetchPage={(page, pageSize) => runApi.assertions(runId, { page, pageSize })}
            columns={assertionColumns}
            rowKey={(row) => row.id}
            emptyMessage={run.flowKind === "ing"
              ? "No assertions were recorded for this run. Auto-mode assertions run with every load; manual-mode ones only in an assertions-only run."
              : "No assertions were recorded for this run."}
            data-testid="assertions-table"
          />
        </Stack>
      )}
      {tab === 6 && (
        <Stack spacing={1}>
          {run.flowKind === "hc" && run.repoId !== null && (
            <Stack direction="row" spacing={1} alignItems="center">
              <Typography variant="body2" color="text.secondary">
                Run this health check on demand; manual-mode (mode: manual) checks execute only from here or a direct trigger.
              </Typography>
              <Stack sx={{ flexGrow: 1 }} />
              <Button
                size="small"
                variant="outlined"
                startIcon={<MonitorHeartIcon fontSize="small" />}
                onClick={() => triggerFlow.mutate({})}
                disabled={triggerFlow.isPending}
                data-testid="run-health-check"
              >
                Run health check
              </Button>
            </Stack>
          )}
          {run.flowKind === "ing" && embeddedCheck !== null && run.repoId !== null && (
            <Stack direction="row" spacing={1} alignItems="center">
              <Typography variant="body2" color="text.secondary">
                This flow embeds health check '{embeddedCheck}'; it runs on demand and records its metrics on its own runs.
              </Typography>
              <Stack sx={{ flexGrow: 1 }} />
              <Button
                size="small"
                variant="outlined"
                startIcon={<MonitorHeartIcon fontSize="small" />}
                onClick={() => triggerFlow.mutate({ flowName: embeddedCheck })}
                disabled={triggerFlow.isPending}
                data-testid="run-embedded-health-check"
              >
                Run health check
              </Button>
            </Stack>
          )}
          <PagedTable
            queryKey={["runs", runId, "health-metrics"]}
            fetchPage={(page, pageSize) => runApi.healthMetrics(runId, { page, pageSize })}
            columns={healthMetricColumns}
            rowKey={(row) => row.id}
            emptyMessage={run.flowKind === "hc"
              ? "No health metrics were recorded for this run."
              : run.flowKind === "ing" && embeddedCheck !== null
                ? `Health metrics are recorded on the embedded check's own runs ('${embeddedCheck}'); use Run health check to execute it now.`
                : `Health metrics are produced by health-check (hc) flows; this is a '${run.flowKind}' run.`}
            data-testid="health-metrics-table"
          />
        </Stack>
      )}

      <Dialog
        open={traceEntry !== null}
        onClose={() => setTraceEntry(null)}
        maxWidth="lg"
        fullWidth
        data-testid="trace-entry-dialog"
      >
        <DialogTitle>
          {traceEntry
            ? (traceEntry.kind === "statement"
              ? `Statement ${traceEntry.ordinal}${traceEntry.step ? `: ${traceEntry.step}` : ""}`
              : `Event ${traceEntry.ordinal}${traceEntry.step ? `: ${traceEntry.step}` : ""}`)
            : ""}
        </DialogTitle>
        <DialogContent>
          {traceEntry?.error && (
            <Alert severity="error" sx={{ mb: 2 }} data-testid="trace-entry-error">{traceEntry.error}</Alert>
          )}
          {traceEntry?.kind === "statement" && traceEntry.sql !== null && (
            <CodeView value={traceEntry.sql} language="sql" data-testid="trace-entry-sql" />
          )}
          {traceEntry?.kind === "event" && traceEntry.message !== null && (
            <CodeView value={traceEntry.message} language="plaintext" data-testid="trace-entry-message" />
          )}
        </DialogContent>
      </Dialog>

      <Dialog
        open={statement !== null}
        onClose={() => setStatement(null)}
        maxWidth="lg"
        fullWidth
        data-testid="statement-dialog"
      >
        <DialogTitle>
          {statement ? `Statement ${statement.ordinal}: ${statement.step}` : ""}
        </DialogTitle>
        <DialogContent>
          {statement?.error && (
            <Alert severity="error" sx={{ mb: 2 }} data-testid="statement-error">{statement.error}</Alert>
          )}
          {statement && <CodeView value={statement.sql} language="sql" data-testid="statement-sql" />}
        </DialogContent>
      </Dialog>

      <ConfirmDialog
        open={confirmOpen}
        title="Cancel run"
        message={run.status === "running"
          ? `Cancel running run ${run.runId}? The node executing it aborts the in-flight statement and rolls back its transaction.`
          : `Cancel queued run ${run.runId}? A queued run is dequeued before any node claims it.`}
        confirmLabel="Cancel run"
        danger
        busy={cancel.isPending}
        onConfirm={() => cancel.mutate()}
        onClose={() => setConfirmOpen(false)}
      />
      {run.repoId !== null && (
        <TriggerRunDialog
          open={rerunOpen}
          onClose={() => setRerunOpen(false)}
          repoId={run.repoId}
          flowName={run.flowName}
          flowId={run.pipelineId}
          initialParameters={{
            fullLoad: run.fullLoad,
            backfillFrom: run.backfillFrom,
            backfillTo: run.backfillTo,
            filePattern: run.filePattern,
            assertionsOnly: run.assertionsOnly,
          }}
        />
      )}
    </Page>
  );
}
