import { useState, type ReactNode } from "react";
import { Link as RouterLink, Navigate, useParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useSnackbar } from "notistack";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Chip from "@mui/material/Chip";
import Dialog from "@mui/material/Dialog";
import DialogContent from "@mui/material/DialogContent";
import DialogTitle from "@mui/material/DialogTitle";
import Link from "@mui/material/Link";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import Tab from "@mui/material/Tab";
import Tabs from "@mui/material/Tabs";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";
import type {
  RunAssertion, RunFile, RunHealthCheckMetric, RunStatement, RunSurrogateKey,
} from "../../api/types";
import { isApiError } from "../../api/client";
import { runApi } from "../../api/endpoints";
import { CodeView } from "../../components/CodeView";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { RunStatusBadge } from "../../components/StatusBadge";
import { pollingInterval } from "../../hooks/usePolling";
import { formatBytes, formatDurationSeconds } from "../../lib/time";

function YesNo({ value }: { value: boolean }) {
  return value
    ? <Chip size="small" label="yes" color="success" variant="outlined" />
    : <Chip size="small" label="no" color="default" variant="outlined" />;
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <Box sx={{ minWidth: 180 }}>
      <Typography variant="caption" color="text.secondary" display="block">{label}</Typography>
      <Typography variant="body2" component="div">{children}</Typography>
    </Box>
  );
}

const statementColumns: Column<RunStatement>[] = [
  { id: "ordinal", header: "Ordinal", width: 90, render: (row) => row.ordinal },
  { id: "step", header: "Step", width: 200, render: (row) => row.step },
  {
    id: "sql",
    header: "SQL",
    render: (row) => (
      <span style={{ fontFamily: "monospace" }}>
        {row.sql.length > 120 ? `${row.sql.slice(0, 120)}…` : row.sql}
      </span>
    ),
  },
];

const assertionColumns: Column<RunAssertion>[] = [
  { id: "name", header: "Name", render: (row) => row.name },
  { id: "result", header: "Result", render: (row) => row.result },
  { id: "assertedValue", header: "Asserted value", render: (row) => row.assertedValue },
  { id: "evaluated", header: "Evaluated", render: (row) => <YesNo value={row.evaluated} /> },
  { id: "error", header: "Error", render: (row) => row.error ?? "-" },
];

const fileColumns: Column<RunFile>[] = [
  { id: "name", header: "Name", render: (row) => row.name },
  { id: "path", header: "Path", render: (row) => row.path ?? "-" },
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
  { id: "error", header: "Error", render: (row) => row.error ?? "-" },
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
  { id: "error", header: "Error", render: (row) => row.error ?? "-" },
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
      <Box data-testid="page-run-detail">
        {isApiError(query.error)
          ? <CorrelationError error={query.error} />
          : <Typography color="error">{String(query.error)}</Typography>}
      </Box>
    );
  }

  const run = query.data;
  if (run === undefined) {
    return (
      <Box data-testid="page-run-detail">
        <Skeleton variant="rounded" height={240} sx={{ mb: 2 }} />
        <Skeleton variant="rounded" height={320} />
      </Box>
    );
  }

  return (
    <Box data-testid="page-run-detail">
      <Card variant="outlined" sx={{ mb: 2 }}>
        <CardContent>
          <Stack direction="row" alignItems="center" spacing={2} useFlexGap flexWrap="wrap" sx={{ mb: 2 }}>
            <RunStatusBadge status={run.status} />
            <Typography variant="h5">{run.flowName}</Typography>
            <Chip size="small" label={run.flowKind} variant="outlined" data-testid="run-kind" />
            <Box sx={{ flexGrow: 1 }} />
            {run.status === "queued" && (
              <Button color="error" variant="outlined" onClick={() => setConfirmOpen(true)} data-testid="cancel-run">
                Cancel run
              </Button>
            )}
          </Stack>

          {run.status === "failed" && run.error !== null && (
            <Alert severity="error" sx={{ mb: 2 }} data-testid="run-error">{run.error}</Alert>
          )}

          <Stack direction="row" spacing={3} useFlexGap flexWrap="wrap">
            <Field label="Run id"><span style={{ fontFamily: "monospace" }}>{run.runId}</span></Field>
            <Field label="Repo">
              {run.repoId
                ? <Link component={RouterLink} to={`/repos/${run.repoId}`} data-testid="run-repo-link">{run.repoId}</Link>
                : "-"}
            </Field>
            <Field label="Pipeline">
              <Link component={RouterLink} to={`/pipelines/${run.pipelineId}`} data-testid="run-pipeline-link">
                {run.pipelineId}
              </Link>
            </Field>
            <Field label="Enqueued"><RelativeTime value={run.enqueuedUtc} /></Field>
            <Field label="Started"><RelativeTime value={run.startUtc} /></Field>
            <Field label="Ended"><RelativeTime value={run.endUtc} /></Field>
            <Field label="Duration">
              {run.durationSeconds != null ? formatDurationSeconds(run.durationSeconds) : "-"}
            </Field>
            <Field label="Claimed by node">{run.claimedByNode ?? "-"}</Field>
            <Field label="Host">{run.host ?? "-"}</Field>
            <Field label="Target pool">{run.targetPool ?? "-"}</Field>
            <Field label="Commit">
              {run.commitSha ? (
                <Tooltip title={run.commitSha}>
                  <span style={{ fontFamily: "monospace" }}>{run.commitSha.slice(0, 12)}</span>
                </Tooltip>
              ) : "-"}
            </Field>
            <Field label="Rows loaded">{run.rowsLoaded ?? "-"}</Field>
            <Field label="Rows inserted">{run.rowsInserted ?? "-"}</Field>
            <Field label="Rows updated">{run.rowsUpdated ?? "-"}</Field>
            <Field label="Rows deleted">{run.rowsDeleted ?? "-"}</Field>
          </Stack>
        </CardContent>
      </Card>

      <Tabs
        value={tab}
        onChange={(_, next: number) => setTab(next)}
        variant="scrollable"
        allowScrollButtonsMobile
        sx={{ mb: 2 }}
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
          emptyMessage="No statements were recorded for this run."
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
          {statement && <CodeView value={statement.sql} language="sql" data-testid="statement-sql" />}
        </DialogContent>
      </Dialog>

      <ConfirmDialog
        open={confirmOpen}
        title="Cancel run"
        message={`Cancel queued run ${run.runId}? A queued run is dequeued before any node claims it.`}
        confirmLabel="Cancel run"
        danger
        busy={cancel.isPending}
        onConfirm={() => cancel.mutate()}
        onClose={() => setConfirmOpen(false)}
      />
    </Box>
  );
}
