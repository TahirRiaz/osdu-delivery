import { useEffect, useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { Play } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
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
    render: (row) => <span className="font-mono text-[12px] font-medium">{row.flowName}</span>,
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
  {
    id: "rowsLoaded",
    header: "Rows loaded",
    align: "right",
    render: (row) => <span className="font-mono tabular-nums">{row.rowsLoaded ?? "-"}</span>,
  },
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
  {
    id: "step",
    header: "Step",
    align: "right",
    render: (row) => <span className="font-mono tabular-nums">{row.wave >= 0 ? row.wave : "-"}</span>,
  },
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
      <span className="text-[13px] text-muted-foreground">
        started <RelativeTime value={stats.earliest} />
      </span>
      {stats.durationTotal != null && (
        <span className="text-[13px] text-muted-foreground">
          duration {formatDurationSeconds(stats.durationTotal)}
        </span>
      )}
      <span className="text-[13px] text-muted-foreground">({rows.length})</span>
      {stats.failed > 0 && (
        <span className="text-[13px] font-medium text-destructive">{stats.failed} failed</span>
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
      // grow makes this row fill the header (it is a content-sized flex item inside the node row's cell), so
      // the button's ml-auto really pushes it to the right edge instead of leaving it mid-row where it would
      // swallow clicks meant to collapse the group.
      <div className="flex grow flex-wrap items-baseline gap-x-3 gap-y-1" data-testid="batch-group-header">
        <span className="text-[13px] font-semibold">Batch: {rows[0].batch}</span>
        <GroupStatsInline rows={rows} />
        <Button
          variant="outline"
          size="xs"
          className="ml-auto"
          onClick={(e) => {
            e.stopPropagation();
            onRunBatch(rows[0].repoId, rows[0].batch);
          }}
          data-testid="run-batch"
        >
          <Play />
          Run batch
        </Button>
      </div>
    ),
    subKey: (row) => row.wave,
    renderSubHeader: (rows) => (
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1" data-testid="step-group-header">
        <span className="text-[13px] font-medium">Step: {rows[0].wave >= 0 ? rows[0].wave : "?"}</span>
        <GroupStatsInline rows={rows} />
      </div>
    ),
  };
}

/** The run inbox: live-polled list with status/flow/kind/batch filters and the entry point for triggering runs.
 * Grouped by batch (the default) it is a status board: each pipeline's latest run under its batch and lineage
 * step, like the classic batch report. Toggled flat it is the full run history. */
export default function RunsPage() {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const [triggerOpen, setTriggerOpen] = useState(false);
  const [batchRun, setBatchRun] = useState<{ repoId: string | null; batch: string } | null>(null);
  // The status bar's workload segments deep-link here as /runs?status=running|queued.
  const [status, setStatus] = useState<RunStatus | null>(() => {
    const fromUrl = searchParams.get("status");
    return statuses.includes(fromUrl as RunStatus) ? (fromUrl as RunStatus) : null;
  });
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
          <Button size="sm" onClick={() => setTriggerOpen(true)} data-testid="open-trigger-run">
            <Play />
            Trigger run
          </Button>
        )}
      />

      <FilterBar>
        <ToggleGroup
          type="single"
          variant="outline"
          size="sm"
          value={status ?? ""}
          onValueChange={(value) => setStatus(value === "" ? null : (value as RunStatus))}
          aria-label="Filter by status"
        >
          {statuses.map((s) => (
            <ToggleGroupItem key={s} value={s} data-testid={`filter-status-${s}`} className="h-8 px-2.5 text-xs">
              {s}
            </ToggleGroupItem>
          ))}
        </ToggleGroup>
        <Input
          value={flowNameInput}
          onChange={(e) => setFlowNameInput(e.target.value)}
          placeholder="Flow name"
          aria-label="Flow name"
          data-testid="filter-flow-name"
          className="h-8 w-44"
        />
        <Input
          value={batchInput}
          onChange={(e) => setBatchInput(e.target.value)}
          placeholder="Batch"
          aria-label="Batch"
          data-testid="filter-batch"
          className="h-8 w-36"
        />
        <Select value={kind} onValueChange={setKind}>
          <SelectTrigger size="sm" className="h-8 w-28" aria-label="Kind" data-testid="filter-kind">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {kinds.map((k) => (
              <SelectItem key={k} value={k}>{k}</SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Label className="flex items-center gap-2 text-[13px] font-normal">
          <Switch
            checked={groupByBatch}
            onCheckedChange={setGroupByBatch}
            data-testid="group-by-batch"
          />
          Group by batch
        </Label>
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
