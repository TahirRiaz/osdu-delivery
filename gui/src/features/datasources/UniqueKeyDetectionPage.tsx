import { useCallback, useEffect, useMemo, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import Alert from "@mui/material/Alert";
import Autocomplete from "@mui/material/Autocomplete";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import CircularProgress from "@mui/material/CircularProgress";
import Dialog from "@mui/material/Dialog";
import DialogActions from "@mui/material/DialogActions";
import DialogContent from "@mui/material/DialogContent";
import DialogTitle from "@mui/material/DialogTitle";
import FormControlLabel from "@mui/material/FormControlLabel";
import Paper from "@mui/material/Paper";
import Radio from "@mui/material/Radio";
import RadioGroup from "@mui/material/RadioGroup";
import Stack from "@mui/material/Stack";
import Switch from "@mui/material/Switch";
import TextField from "@mui/material/TextField";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";
import CancelIcon from "@mui/icons-material/Cancel";
import KeyIcon from "@mui/icons-material/VpnKey";
import LockPersonIcon from "@mui/icons-material/LockPerson";
import { datasourceApi } from "../../api/endpoints";
import type {
  ComputeTask, ComputeTaskSummary, DatasourceDatabase, DatasourceObject, DatasourceObjectPage,
  DatasourceSchema, UniqueKeyReport,
} from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { EmptyState } from "../../components/EmptyState";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { RunStatusBadge } from "../../components/StatusBadge";
import { parseUtc } from "../../lib/time";
import { UniqueKeyReportView } from "./UniqueKeyReportView";
import { useCompute } from "./useCompute";

/** Only these provider kinds can be profiled (detection is T-SQL); null means "unknown, assume SQL Server". */
function detectableKind(kind: string | null): boolean {
  return kind === null || kind === "MSSQL" || kind === "AZDB";
}

/** "1m 23s" from two UTC instants; a dash while either end is missing. */
function durationLabel(startUtc: string | null, endUtc: string | null): string {
  if (startUtc === null || endUtc === null) {
    return "-";
  }

  const totalSeconds = Math.max(0, Math.round((parseUtc(endUtc).getTime() - parseUtc(startUtc).getTime()) / 1000));
  const minutes = Math.floor(totalSeconds / 60);
  return minutes > 0 ? `${minutes}m ${totalSeconds % 60}s` : `${totalSeconds}s`;
}

/** "0:42" elapsed-time ticker text. */
function elapsedLabel(sinceMs: number, nowMs: number): string {
  const totalSeconds = Math.max(0, Math.floor((nowMs - sinceMs) / 1000));
  const minutes = Math.floor(totalSeconds / 60);
  return `${minutes}:${String(totalSeconds % 60).padStart(2, "0")}`;
}

/**
 * A past detection task opened from the history list: the stored report for a succeeded task, the error for a
 * failed one, live progress otherwise (the dialog polls until the task is terminal, so an operator can follow
 * a detection another session started).
 */
function HistoryTaskDialog({ taskId, onClose }: { taskId: string; onClose: () => void }) {
  const task = useQuery({
    queryKey: ["compute-task", taskId],
    queryFn: () => datasourceApi.task(taskId),
    refetchInterval: (query) => {
      const status = (query.state.data as ComputeTask | undefined)?.status;
      return status === "queued" || status === "running" ? 2000 : false;
    },
  });

  const data = task.data;
  const report = data?.status === "succeeded" && data.result !== null ? (data.result as UniqueKeyReport) : null;

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="md" data-testid="history-task-dialog">
      <DialogTitle>
        <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap" useFlexGap>
          <span>Detection report</span>
          {data?.target != null && <Mono sx={{ fontSize: "inherit" }}>{data.target}</Mono>}
          {data !== undefined && <RunStatusBadge status={data.status} />}
        </Stack>
      </DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          {task.isPending && (
            <Stack direction="row" spacing={1.5} alignItems="center">
              <CircularProgress size={18} />
              <Typography variant="body2" color="text.secondary">Loading the task...</Typography>
            </Stack>
          )}
          {task.isError && <Alert severity="error">{String(task.error)}</Alert>}
          {data !== undefined && (
            <Typography variant="body2" color="text.secondary">
              <Mono>{data.sourceRef}</Mono>, enqueued <RelativeTime value={data.enqueuedUtc} />
              {data.endUtc !== null ? <>, ran {durationLabel(data.startUtc, data.endUtc)}</> : null}
              {data.requestedBy !== null ? <>, by {data.requestedBy}</> : null}
            </Typography>
          )}
          {data !== undefined && (data.status === "queued" || data.status === "running") && (
            <Stack direction="row" spacing={1.5} alignItems="center">
              <CircularProgress size={18} />
              <Typography variant="body2" color="text.secondary">
                The task is {data.status}; this dialog follows it live.
              </Typography>
            </Stack>
          )}
          {data?.error != null && <Alert severity="error" data-testid="history-task-error">{data.error}</Alert>}
          {report !== null && <UniqueKeyReportView report={report} data-testid="history-task-report" />}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} data-testid="history-task-close">Close</Button>
      </DialogActions>
    </Dialog>
  );
}

/**
 * The dedicated unique-key detection surface: pick any table or view reachable through a datasource
 * reference, tune the search (sampling, key width, whether declared keys answer from metadata), run the
 * detection as a durable compute task on a worker node, and read the full report: ranked candidates with
 * selectivity, per-column statistics, excluded columns, notes. Past detections stay on the durable task queue
 * and reopen from the history list. The scope lives in the URL, so a detection setup is shareable and the
 * inspector drawer deep-links here.
 */
export default function UniqueKeyDetectionPage() {
  const [params, setParams] = useSearchParams();
  const { hasScope } = useAuth();
  const canOperate = hasScope("operate");

  const reference = params.get("ref") ?? "";
  const kind = params.get("kind");
  const database = params.get("db");
  const schema = params.get("schema");
  const objectName = params.get("object") ?? "";

  /** Rewrites the URL scope in place; empty values drop their parameter so links stay minimal. */
  const setScope = useCallback((patch: Record<string, string | null>) => {
    setParams((current) => {
      const next = new URLSearchParams(current);
      for (const [key, value] of Object.entries(patch)) {
        if (value === null || value === "") {
          next.delete(key);
        } else {
          next.set(key, value);
        }
      }

      return next;
    }, { replace: true });
  }, [setParams]);

  // ---- Scope pickers (each level is a live compute task, like the datasource browser) ----------------------
  const datasources = useQuery({ queryKey: ["datasources", "list"], queryFn: () => datasourceApi.list() });
  const databases = useCompute<{ databases: DatasourceDatabase[] }>();
  const schemas = useCompute<{ schemas: DatasourceSchema[] }>();
  const objects = useCompute<DatasourceObjectPage>();
  const { run: runDatabases } = databases;
  const { run: runSchemas } = schemas;
  const { run: runObjects } = objects;

  const [objectInput, setObjectInput] = useState("");
  const usable = reference !== "" && canOperate;

  useEffect(() => {
    if (usable) {
      void runDatabases({ reference, kind, operation: "listDatabases" });
    }
  }, [usable, runDatabases, reference, kind]);

  useEffect(() => {
    if (usable) {
      void runSchemas({ reference, kind, database, operation: "listSchemas" });
    }
  }, [usable, runSchemas, reference, kind, database]);

  // The object picker searches as you type (debounced), scoped to the chosen database/schema.
  useEffect(() => {
    if (!usable) {
      return;
    }

    const handle = setTimeout(() => {
      void runObjects({
        reference, kind, database, schema,
        nameLike: objectInput.trim() === "" ? null : objectInput.trim(),
        limit: 200,
        operation: "listObjects",
      });
    }, 350);
    return () => clearTimeout(handle);
  }, [usable, runObjects, reference, kind, database, schema, objectInput]);

  const selectableSources = useMemo(
    () => (datasources.data ?? []).filter((d) => d.resolvable && detectableKind(d.kind)),
    [datasources.data],
  );
  const selectedSource = selectableSources.find((d) => d.reference === reference)
    ?? (reference !== "" ? { reference, kind, resolvable: true, sourcePipelines: 0, targetPipelines: 0 } : null);
  const selectedObject: DatasourceObject | null = objectName !== "" && schema !== null
    ? { schema, name: objectName, type: "Table", approxRows: 0 }
    : null;

  // ---- Options ----------------------------------------------------------------------------------------------
  const [sampleMode, setSampleMode] = useState<"auto" | "full" | "custom">("auto");
  const [sampleSize, setSampleSize] = useState(500_000);
  const [maxKeyColumns, setMaxKeyColumns] = useState(4);
  const [maxCandidates, setMaxCandidates] = useState(5);
  const [verify, setVerify] = useState(true);
  const [trustDeclaredKeys, setTrustDeclaredKeys] = useState(true);

  const sampleInvalid = sampleMode === "custom" && (!Number.isFinite(sampleSize) || sampleSize < 1);
  const widthInvalid = !Number.isFinite(maxKeyColumns) || maxKeyColumns < 1 || maxKeyColumns > 16;
  const candidatesInvalid = !Number.isFinite(maxCandidates) || maxCandidates < 1 || maxCandidates > 50;

  // ---- Run --------------------------------------------------------------------------------------------------
  const detect = useCompute<UniqueKeyReport>();
  const [startedAtMs, setStartedAtMs] = useState<number | null>(null);
  const [nowMs, setNowMs] = useState(0);
  const [cancelled, setCancelled] = useState(false);
  const [historyVersion, setHistoryVersion] = useState(0);
  const [openTaskId, setOpenTaskId] = useState<string | null>(null);

  useEffect(() => {
    if (!detect.running) {
      return;
    }

    const timer = setInterval(() => setNowMs(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [detect.running]);

  const canRun = usable && schema !== null && objectName !== ""
    && !detect.running && !sampleInvalid && !widthInvalid && !candidatesInvalid;

  const runDetection = () => {
    setCancelled(false);
    setStartedAtMs(Date.now());
    setNowMs(Date.now());
    void detect
      .run({
        reference,
        kind,
        database,
        schema,
        objectName,
        operation: "detectUniqueKey",
        sampleSize: sampleMode === "auto" ? null : sampleMode === "full" ? 0 : sampleSize,
        maxKeyColumns,
        maxCandidates,
        verifyCandidates: verify,
        trustDeclaredKeys,
      })
      .finally(() => setHistoryVersion((v) => v + 1));
  };

  const cancelDetection = () => {
    detect.reset(); // aborts the long-poll and best-effort cancels the queued/running task server-side
    setCancelled(true);
    setHistoryVersion((v) => v + 1);
  };

  // ---- History ----------------------------------------------------------------------------------------------
  const historyColumns: Column<ComputeTaskSummary>[] = [
    {
      id: "target",
      header: "Object",
      render: (row) => (row.target !== null
        ? <Mono sx={{ fontWeight: 600 }}>{row.target}</Mono>
        : <Typography variant="body2" color="text.secondary">-</Typography>),
    },
    { id: "reference", header: "Datasource", render: (row) => <Mono>{row.sourceRef}</Mono> },
    { id: "status", header: "Status", render: (row) => <RunStatusBadge status={row.status} /> },
    { id: "enqueued", header: "Enqueued", render: (row) => <RelativeTime value={row.enqueuedUtc} /> },
    { id: "duration", header: "Duration", align: "right", render: (row) => durationLabel(row.startUtc, row.endUtc) },
    {
      id: "requestedBy",
      header: "By",
      render: (row) => row.requestedBy ?? <Typography variant="body2" color="text.secondary">-</Typography>,
    },
  ];

  if (!canOperate) {
    return (
      <Page data-testid="page-key-detection">
        <PageHeader title="Unique key detection" />
        <EmptyState
          icon={<LockPersonIcon />}
          title="Operate scope required"
          description="Detection profiles the live source through a worker node, so it needs the operate scope. Ask an administrator for the operator role."
        />
      </Page>
    );
  }

  return (
    <Page data-testid="page-key-detection">
      <PageHeader
        title="Unique key detection"
        subtitle="Find the minimal column set(s) that uniquely identify a table's rows. A key the database already declares answers instantly from metadata; otherwise the rows are profiled on a worker node, on a random sample for large tables, and every reported key is verified against the whole table."
      />

      <Paper variant="outlined" sx={{ p: 2 }}>
        <Stack spacing={2}>
          <Typography variant="subtitle2">Target</Typography>
          <Stack direction={{ xs: "column", md: "row" }} spacing={2} flexWrap="wrap" useFlexGap>
            <Autocomplete
              sx={{ minWidth: 280 }}
              size="small"
              options={selectableSources}
              getOptionLabel={(option) => option.reference}
              isOptionEqualToValue={(option, value) => option.reference === value.reference}
              value={selectedSource}
              onChange={(_, value) => setScope({
                ref: value?.reference ?? null, kind: value?.kind ?? null, db: null, schema: null, object: null,
              })}
              loading={datasources.isPending}
              renderOption={(props, option) => (
                <li {...props} key={option.reference}>
                  <Stack direction="row" spacing={1} alignItems="center">
                    <Mono>{option.reference}</Mono>
                    {option.kind !== null && <Chip size="small" variant="outlined" label={option.kind} />}
                  </Stack>
                </li>
              )}
              renderInput={(inputParams) => (
                <TextField
                  {...inputParams}
                  label="Datasource"
                  placeholder="pick a connection reference"
                  inputProps={{ ...inputParams.inputProps, "data-testid": "kd-datasource" }}
                />
              )}
            />
            <Autocomplete
              sx={{ minWidth: 220 }}
              size="small"
              disabled={!usable}
              options={databases.data?.databases.map((d) => d.name) ?? []}
              value={database}
              onChange={(_, value) => setScope({ db: value, schema: null, object: null })}
              loading={databases.running}
              renderInput={(inputParams) => (
                <TextField
                  {...inputParams}
                  label="Database"
                  placeholder="connection default"
                  inputProps={{ ...inputParams.inputProps, "data-testid": "kd-database" }}
                />
              )}
            />
            <Autocomplete
              sx={{ minWidth: 200 }}
              size="small"
              disabled={!usable}
              options={schemas.data?.schemas.map((s) => s.name) ?? []}
              value={schema}
              onChange={(_, value) => setScope({ schema: value, object: null })}
              loading={schemas.running}
              renderInput={(inputParams) => (
                <TextField
                  {...inputParams}
                  label="Schema"
                  placeholder="all schemas"
                  inputProps={{ ...inputParams.inputProps, "data-testid": "kd-schema" }}
                />
              )}
            />
            <Autocomplete
              sx={{ minWidth: 300, flexGrow: 1 }}
              size="small"
              disabled={!usable}
              options={objects.data?.items ?? []}
              getOptionLabel={(option) => `${option.schema}.${option.name}`}
              isOptionEqualToValue={(option, value) => option.schema === value.schema && option.name === value.name}
              value={selectedObject}
              onChange={(_, value) => setScope({ schema: value?.schema ?? null, object: value?.name ?? null })}
              onInputChange={(_, value, reason) => {
                if (reason === "input") {
                  setObjectInput(value);
                }
              }}
              loading={objects.running}
              renderOption={(props, option) => (
                <li {...props} key={`${option.schema}.${option.name}`}>
                  <Stack direction="row" spacing={1} alignItems="center">
                    <Mono>{option.schema}.{option.name}</Mono>
                    <Chip size="small" variant="outlined" label={option.type} />
                  </Stack>
                </li>
              )}
              renderInput={(inputParams) => (
                <TextField
                  {...inputParams}
                  label="Table or view"
                  placeholder="type to search"
                  inputProps={{ ...inputParams.inputProps, "data-testid": "kd-object" }}
                />
              )}
            />
          </Stack>
          {databases.error !== null && <Alert severity="error" data-testid="kd-databases-error">{databases.error}</Alert>}
          {schemas.error !== null && databases.error === null && (
            <Alert severity="error" data-testid="kd-schemas-error">{schemas.error}</Alert>
          )}
          {objects.error !== null && <Alert severity="error" data-testid="kd-objects-error">{objects.error}</Alert>}

          <Typography variant="subtitle2">Options</Typography>
          <Stack direction={{ xs: "column", lg: "row" }} spacing={2} alignItems={{ lg: "center" }} flexWrap="wrap" useFlexGap>
            <RadioGroup
              row
              value={sampleMode}
              onChange={(e) => setSampleMode(e.target.value as typeof sampleMode)}
              data-testid="kd-sample-mode"
            >
              <Tooltip title="Tables over 2 million rows profile a 500,000-row random sample; smaller tables get a full scan.">
                <FormControlLabel value="auto" control={<Radio size="small" />} label="Auto sample" />
              </Tooltip>
              <Tooltip title="Profile every row. Exact, but expensive on a large table.">
                <FormControlLabel value="full" control={<Radio size="small" />} label="Full scan" />
              </Tooltip>
              <Tooltip title="Profile a random sample of this many rows; reported keys are still verified against the whole table.">
                <FormControlLabel value="custom" control={<Radio size="small" />} label="Sample" />
              </Tooltip>
            </RadioGroup>
            {sampleMode === "custom" && (
              <TextField
                size="small"
                sx={{ width: 160 }}
                type="number"
                label="Sample rows"
                value={sampleSize}
                onChange={(e) => setSampleSize(Number.parseInt(e.target.value, 10))}
                error={sampleInvalid}
                helperText={sampleInvalid ? "At least 1 row." : undefined}
                inputProps={{ min: 1, "data-testid": "kd-sample-size" }}
              />
            )}
            <TextField
              size="small"
              sx={{ width: 150 }}
              type="number"
              label="Max key width"
              value={maxKeyColumns}
              onChange={(e) => setMaxKeyColumns(Number.parseInt(e.target.value, 10))}
              error={widthInvalid}
              helperText={widthInvalid ? "1 to 16 columns." : undefined}
              inputProps={{ min: 1, max: 16, "data-testid": "kd-max-columns" }}
            />
            <TextField
              size="small"
              sx={{ width: 150 }}
              type="number"
              label="Max candidates"
              value={maxCandidates}
              onChange={(e) => setMaxCandidates(Number.parseInt(e.target.value, 10))}
              error={candidatesInvalid}
              helperText={candidatesInvalid ? "1 to 50 candidates." : undefined}
              inputProps={{ min: 1, max: 50, "data-testid": "kd-max-candidates" }}
            />
            <Tooltip title="Confirm every sampled candidate against the whole table before reporting it as unique.">
              <FormControlLabel
                control={<Switch checked={verify} onChange={(e) => setVerify(e.target.checked)} data-testid="kd-verify" />}
                label="Verify on full table"
              />
            </Tooltip>
            <Tooltip title="When the database already enforces a unique index or constraint, answer from that metadata without reading a row. Turn off to profile the data regardless.">
              <FormControlLabel
                control={(
                  <Switch
                    checked={trustDeclaredKeys}
                    onChange={(e) => setTrustDeclaredKeys(e.target.checked)}
                    data-testid="kd-trust-declared"
                  />
                )}
                label="Use declared keys"
              />
            </Tooltip>
          </Stack>

          <Stack direction="row" spacing={1.5} alignItems="center" flexWrap="wrap" useFlexGap>
            <Button
              variant="contained"
              startIcon={detect.running ? <CircularProgress size={16} color="inherit" /> : <KeyIcon />}
              disabled={!canRun}
              onClick={runDetection}
              data-testid="kd-run"
            >
              {detect.running ? "Profiling..." : "Detect unique key"}
            </Button>
            {detect.running && (
              <>
                <Button
                  color="inherit"
                  startIcon={<CancelIcon />}
                  onClick={cancelDetection}
                  data-testid="kd-cancel"
                >
                  Cancel
                </Button>
                {startedAtMs !== null && (
                  <Typography variant="body2" color="text.secondary" data-testid="kd-elapsed">
                    {elapsedLabel(startedAtMs, nowMs)} elapsed; a large table can take a while. Leaving this page
                    cancels the detection.
                  </Typography>
                )}
              </>
            )}
          </Stack>
        </Stack>
      </Paper>

      {detect.error !== null && <Alert severity="error" data-testid="kd-error">{detect.error}</Alert>}
      {cancelled && detect.data === null && detect.error === null && (
        <Alert severity="info" data-testid="kd-cancelled">The detection was cancelled.</Alert>
      )}

      {detect.data !== null && (
        <Paper variant="outlined" sx={{ p: 2 }}>
          <Stack spacing={1.5}>
            <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap" useFlexGap>
              <Typography variant="subtitle2">Result</Typography>
              {detect.data.objectName !== null && <Mono sx={{ fontWeight: 600 }}>{detect.data.objectName}</Mono>}
            </Stack>
            <UniqueKeyReportView report={detect.data} data-testid="kd-result" />
          </Stack>
        </Paper>
      )}

      <Stack spacing={1}>
        <Typography variant="subtitle2">
          Detection history{reference !== "" ? <> for <Mono>{reference}</Mono></> : null}
        </Typography>
        <PagedTable
          queryKey={["compute-tasks", "detectUniqueKey", reference, historyVersion]}
          fetchPage={(page, pageSize) => datasourceApi.tasks({
            operation: "detectUniqueKey",
            reference: reference === "" ? undefined : reference,
            page,
            pageSize,
          })}
          columns={historyColumns}
          rowKey={(row) => row.taskId}
          onRowClick={(row) => setOpenTaskId(row.taskId)}
          pollMs={10_000}
          emptyMessage="No detections have run yet. Pick a table above and run one."
          data-testid="kd-history"
        />
      </Stack>

      {openTaskId !== null && <HistoryTaskDialog taskId={openTaskId} onClose={() => setOpenTaskId(null)} />}
    </Page>
  );
}
