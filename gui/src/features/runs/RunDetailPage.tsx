import { useState } from "react";
import { Link as RouterLink, Navigate, useParams } from "react-router-dom";
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
import ErrorOutlineIcon from "@mui/icons-material/ErrorOutline";
import type {
  RunAssertion, RunFile, RunHealthCheckMetric, RunStatement, RunSurrogateKey,
} from "../../api/types";
import { isApiError } from "../../api/client";
import { runApi } from "../../api/endpoints";
import { CodeView } from "../../components/CodeView";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { DetailHeaderCard } from "../../components/DetailHeaderCard";
import { DetailPair } from "../../components/DetailPair";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { RunStatusBadge } from "../../components/StatusBadge";
import { TruncatedText } from "../../components/TruncatedText";
import { pollingInterval } from "../../hooks/usePolling";
import { formatBytes, formatDurationSeconds, parseUtc } from "../../lib/time";

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
  const { enqueueSnackbar } = useSnackbar();
  const [tab, setTab] = useState(0);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [statement, setStatement] = useState<RunStatement | null>(null);
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
          </>
        )}
        actions={cancellable && (
          <Button
            color="error"
            variant="outlined"
            onClick={() => setConfirmOpen(true)}
            disabled={cancelling || cancel.isPending}
            data-testid="cancel-run"
          >
            {cancelling ? "Cancelling…" : "Cancel run"}
          </Button>
        )}
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

      <Tabs
        value={tab}
        onChange={(_, next: number) => setTab(next)}
        variant="scrollable"
        allowScrollButtonsMobile
        data-testid="run-tabs"
      >
        <Tab label="Statements" data-testid="tab-statements" />
        <Tab label="Assertions" data-testid="tab-assertions" />
        <Tab label="Files" data-testid="tab-files" />
        <Tab label="Surrogate keys" data-testid="tab-surrogate-keys" />
        <Tab label="Health metrics" data-testid="tab-health-metrics" />
      </Tabs>

      {tab === 0 && (
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
      {tab === 1 && (
        <PagedTable
          queryKey={["runs", runId, "assertions"]}
          fetchPage={(page, pageSize) => runApi.assertions(runId, { page, pageSize })}
          columns={assertionColumns}
          rowKey={(row) => row.id}
          emptyMessage="No assertions were recorded for this run."
          data-testid="assertions-table"
        />
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
          queryKey={["runs", runId, "surrogate-keys"]}
          fetchPage={(page, pageSize) => runApi.surrogateKeys(runId, { page, pageSize })}
          columns={surrogateKeyColumns}
          rowKey={(row) => row.id}
          emptyMessage="No surrogate keys were recorded for this run."
          data-testid="surrogate-keys-table"
        />
      )}
      {tab === 4 && (
        <PagedTable
          queryKey={["runs", runId, "health-metrics"]}
          fetchPage={(page, pageSize) => runApi.healthMetrics(runId, { page, pageSize })}
          columns={healthMetricColumns}
          rowKey={(row) => row.id}
          emptyMessage="No health metrics were recorded for this run."
          data-testid="health-metrics-table"
        />
      )}

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
    </Page>
  );
}
