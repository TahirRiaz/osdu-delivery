import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import Button from "@mui/material/Button";
import FormControlLabel from "@mui/material/FormControlLabel";
import MenuItem from "@mui/material/MenuItem";
import Stack from "@mui/material/Stack";
import Switch from "@mui/material/Switch";
import TextField from "@mui/material/TextField";
import ToggleButton from "@mui/material/ToggleButton";
import ToggleButtonGroup from "@mui/material/ToggleButtonGroup";
import Typography from "@mui/material/Typography";
import type { RunStatus, RunSummary } from "../../api/types";
import { runApi } from "../../api/endpoints";
import { FilterBar } from "../../components/FilterBar";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { PagedTable, type Column, type TableGrouping } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { RunStatusBadge } from "../../components/StatusBadge";
import { formatDurationSeconds } from "../../lib/time";
import { TriggerRunDialog } from "./TriggerRunDialog";

const statuses: RunStatus[] = ["queued", "running", "succeeded", "failed", "cancelled", "skipped"];
const kinds = ["all", "file", "ing", "exp", "sp", "inv", "hc", "scm", "batch"];

const baseColumns: Column<RunSummary>[] = [
  { id: "status", header: "Status", render: (row) => <RunStatusBadge status={row.status} /> },
  {
    id: "flow",
    header: "Flow",
    render: (row) => <Typography variant="body2" sx={{ fontWeight: 600 }}>{row.flowName}</Typography>,
  },
  { id: "kind", header: "Kind", render: (row) => row.flowKind },
  {
    id: "enqueued",
    header: "Enqueued",
    render: (row) => <RelativeTime value={row.enqueuedUtc ?? row.writtenUtc} />,
  },
  {
    id: "duration",
    header: "Duration",
    render: (row) => (row.durationSeconds != null ? formatDurationSeconds(row.durationSeconds) : "-"),
  },
  { id: "rowsLoaded", header: "Rows loaded", align: "right", render: (row) => row.rowsLoaded ?? "-" },
  { id: "pool", header: "Pool", render: (row) => row.targetPool ?? "-" },
  {
    id: "commit",
    header: "Commit",
    render: (row) => <Mono>{row.commitSha?.slice(0, 10) ?? "-"}</Mono>,
  },
];

// In the flat (ungrouped) view batch and step become ordinary columns; grouped, they live in the header rows.
const flatColumns: Column<RunSummary>[] = [
  baseColumns[0],
  baseColumns[1],
  baseColumns[2],
  { id: "batch", header: "Batch", render: (row) => row.batch },
  { id: "step", header: "Step", align: "right", render: (row) => (row.wave >= 0 ? row.wave : "-") },
  ...baseColumns.slice(3),
];

/** Aggregates one group's rows for its header line: when it started, total duration, and how many failed. */
function groupStats(rows: RunSummary[]) {
  let durationTotal = 0;
  let hasDuration = false;
  let earliest: string | null = null;
  let failed = 0;
  for (const row of rows) {
    if (row.durationSeconds != null) {
      durationTotal += row.durationSeconds;
      hasDuration = true;
    }

    const at = row.enqueuedUtc ?? row.writtenUtc;
    if (earliest === null || new Date(at).getTime() < new Date(earliest).getTime()) {
      earliest = at;
    }

    if (row.status === "failed") {
      failed += 1;
    }
  }

  return { durationTotal: hasDuration ? durationTotal : null, earliest, failed };
}

function GroupStatsInline({ rows }: { rows: RunSummary[] }) {
  const stats = groupStats(rows);
  return (
    <>
      <Typography variant="body2" color="text.secondary" component="span">
        started <RelativeTime value={stats.earliest} />
      </Typography>
      {stats.durationTotal != null && (
        <Typography variant="body2" color="text.secondary" component="span">
          duration {formatDurationSeconds(stats.durationTotal)}
        </Typography>
      )}
      <Typography variant="body2" color="text.secondary" component="span">({rows.length})</Typography>
      {stats.failed > 0 && (
        <Typography variant="body2" color="error.main" component="span">{stats.failed} failed</Typography>
      )}
    </>
  );
}

// The batch-report layout carried over from classic SQLFlow: each pipeline's LAST run clusters under its batch,
// then under the lineage step, so the grouped view answers "what failed, what needs fixing" at a glance. Full
// run history lives in the flat view and on the pipeline detail page. The "Run batch" action launches the whole
// data source (every flow in the batch, in dependency order) as one run group.
function makeBatchGrouping(onRunBatch: (repoId: string | null, batch: string) => void): TableGrouping<RunSummary> {
  return {
  groupKey: (row) => row.batch,
  renderGroupHeader: (rows) => (
    <Stack direction="row" spacing={1.5} alignItems="baseline" useFlexGap flexWrap="wrap" data-testid="batch-group-header">
      <Typography variant="body2" sx={{ fontWeight: 700 }}>Batch: {rows[0].batch}</Typography>
      <GroupStatsInline rows={rows} />
      <Button
        size="small"
        variant="outlined"
        sx={{ ml: "auto" }}
        onClick={(e) => {
          e.stopPropagation();
          onRunBatch(rows[0].repoId, rows[0].batch);
        }}
        data-testid="run-batch"
      >
        Run batch
      </Button>
    </Stack>
  ),
  subKey: (row) => row.wave,
  renderSubHeader: (rows) => (
    <Stack direction="row" spacing={1.5} alignItems="baseline" useFlexGap flexWrap="wrap" data-testid="step-group-header">
      <Typography variant="body2" sx={{ fontWeight: 600 }}>
        Step: {rows[0].wave >= 0 ? rows[0].wave : "?"}
      </Typography>
      <GroupStatsInline rows={rows} />
    </Stack>
  ),
  };
}

/** The run inbox: live-polled list with status/flow/kind/batch filters and the entry point for triggering runs.
 * Grouped by batch (the default) it is a status board: each pipeline's latest run under its batch and lineage
 * step, like the classic batch report. Toggled flat it is the full run history. */
export default function RunsPage() {
  const navigate = useNavigate();
  const [triggerOpen, setTriggerOpen] = useState(false);
  const [batchRun, setBatchRun] = useState<{ repoId: string | null; batch: string } | null>(null);
  const [status, setStatus] = useState<RunStatus | null>(null);
  const [kind, setKind] = useState("all");
  const [flowNameInput, setFlowNameInput] = useState("");
  const [flowName, setFlowName] = useState("");
  const [batchInput, setBatchInput] = useState("");
  const [batch, setBatch] = useState("");
  const [groupByBatch, setGroupByBatch] = useState(true);

  useEffect(() => {
    const handle = window.setTimeout(() => setFlowName(flowNameInput.trim()), 400);
    return () => window.clearTimeout(handle);
  }, [flowNameInput]);

  useEffect(() => {
    const handle = window.setTimeout(() => setBatch(batchInput.trim()), 400);
    return () => window.clearTimeout(handle);
  }, [batchInput]);

  return (
    <Page data-testid="page-runs">
      <PageHeader
        title="Runs"
        actions={(
          <Button variant="contained" onClick={() => setTriggerOpen(true)} data-testid="open-trigger-run">
            Trigger run
          </Button>
        )}
      />

      <FilterBar>
        <ToggleButtonGroup
          size="small"
          exclusive
          value={status}
          onChange={(_, value: RunStatus | null) => setStatus(value)}
          aria-label="Filter by status"
        >
          {statuses.map((s) => (
            <ToggleButton key={s} value={s} data-testid={`filter-status-${s}`}>{s}</ToggleButton>
          ))}
        </ToggleButtonGroup>
        <TextField
          size="small"
          label="Flow name"
          value={flowNameInput}
          onChange={(e) => setFlowNameInput(e.target.value)}
          inputProps={{ "data-testid": "filter-flow-name" }}
        />
        <TextField
          size="small"
          label="Batch"
          value={batchInput}
          onChange={(e) => setBatchInput(e.target.value)}
          inputProps={{ "data-testid": "filter-batch" }}
        />
        <TextField
          select
          size="small"
          label="Kind"
          value={kind}
          onChange={(e) => setKind(e.target.value)}
          inputProps={{ "data-testid": "filter-kind" }}
          sx={{ minWidth: 120 }}
        >
          {kinds.map((k) => (
            <MenuItem key={k} value={k}>{k}</MenuItem>
          ))}
        </TextField>
        <FormControlLabel
          control={(
            <Switch
              size="small"
              checked={groupByBatch}
              onChange={(e) => setGroupByBatch(e.target.checked)}
              data-testid="group-by-batch"
            />
          )}
          label="Group by batch"
        />
      </FilterBar>

      <PagedTable
        queryKey={["runs", "list", status, flowName, kind, batch, groupByBatch]}
        fetchPage={(page, pageSize) =>
          runApi.list({
            status: status ?? undefined,
            flowName: flowName === "" ? undefined : flowName,
            flowKind: kind === "all" ? undefined : kind,
            batch: batch === "" ? undefined : batch,
            latest: groupByBatch || undefined,
            page,
            pageSize,
          })}
        columns={groupByBatch ? baseColumns : flatColumns}
        rowKey={(row) => row.runId}
        onRowClick={(row) => navigate(`/runs/${row.runId}`)}
        pollMs={5000}
        emptyMessage="No runs match the current filters."
        grouping={groupByBatch
          ? makeBatchGrouping((repoId, batch) => setBatchRun({ repoId, batch }))
          : undefined}
        data-testid="runs-table"
      />

      <TriggerRunDialog open={triggerOpen} onClose={() => setTriggerOpen(false)} />
      <TriggerRunDialog
        open={batchRun !== null}
        onClose={() => setBatchRun(null)}
        repoId={batchRun?.repoId ?? undefined}
        batch={batchRun?.batch}
        scope="batch"
      />
    </Page>
  );
}
