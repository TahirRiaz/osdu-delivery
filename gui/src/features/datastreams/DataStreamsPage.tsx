import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Activity, CircleSlash } from "lucide-react";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { isApiError } from "../../api/client";
import { dataStreamApi } from "../../api/endpoints";
import type { DataStream } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { EmptyState } from "../../components/EmptyState";
import { KpiCard } from "../../components/KpiCard";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { useLocalStorageState } from "../../hooks/useLocalStorageState";
import { pollingInterval } from "../../hooks/usePolling";
import { StreamDetailSheet } from "./StreamDetailSheet";
import { StreamStatusBadge } from "./StreamStatusBadge";
import { categoryLabels, formatDays, formatRows } from "./streamPresentation";

const kpiGridClass = "grid grid-cols-[repeat(auto-fill,minmax(180px,1fr))] gap-4";

const windowChoices = [
  { days: 30, label: "30d" },
  { days: 60, label: "60d" },
  { days: 90, label: "90d" },
];

const statusFilters = [
  { value: "", label: "All" },
  { value: "stalled", label: "Stopped" },
  { value: "degraded", label: "Degraded" },
  { value: "watch", label: "Watch" },
  { value: "healthy", label: "Healthy" },
];

/**
 * DataStream anomaly detection: which tables have stopped receiving data, and which are loading abnormally.
 *
 * The board answers three questions about every table the platform writes, ranked because they are not equally
 * urgent: zero data (an outage), less data than normal (a degradation), and more data than normal
 * (information). It needs no per-table configuration, because the run history already records rows inserted,
 * updated, and deleted for every run of every flow.
 *
 * Two controls change what the numbers mean rather than just what is shown, which is why they sit on the page
 * rather than in a menu. "Include backfills" puts operator-driven reprocessing back into the baseline, and a
 * history replay will then make every ordinary day after it look like a collapse. "Scheduled only" restricts
 * to streams that join an enabled schedule, so every verdict is measured against a declared cron cadence
 * instead of one inferred from the stream's own recent behaviour.
 */
export default function DataStreamsPage() {
  const [days, setDays] = useLocalStorageState<number>("datastreams.windowDays", 60);
  const [status, setStatus] = useLocalStorageState<string>("datastreams.status", "");
  const [includeBackfills, setIncludeBackfills] = useLocalStorageState<boolean>("datastreams.includeBackfills", false);
  const [scheduledOnly, setScheduledOnly] = useLocalStorageState<boolean>("datastreams.scheduledOnly", false);
  const [drill, setDrill] = useState<{ pipelineId: string; flowName: string } | null>(null);

  const query = useQuery({
    queryKey: ["datastreams", days, status, includeBackfills, scheduledOnly],
    queryFn: () => dataStreamApi.list({
      days,
      status: status === "" ? undefined : status,
      includeBackfills,
      scheduledOnly,
      limit: 300,
    }),
    refetchInterval: pollingInterval(120_000),
  });

  const columns = useMemo<Column<DataStream>[]>(() => [
    {
      id: "stream",
      header: "Stream",
      render: (s) => (
        <div className="flex flex-col">
          <span className="font-mono text-xs">{s.flowName}</span>
          <span className="text-[11px] text-muted-foreground">
            {s.targetObject ?? s.batch ?? "unknown target"}
          </span>
        </div>
      ),
    },
    {
      id: "status",
      header: "Verdict",
      render: (s) => (
        <div className="flex flex-col gap-0.5">
          <StreamStatusBadge status={s.status} severity={s.severity} />
          <span className="text-[11px] text-muted-foreground">
            {categoryLabels[s.category] ?? s.category}
          </span>
        </div>
      ),
    },
    {
      id: "pattern",
      header: "Normal pattern",
      render: (s) => (
        <div className="flex flex-col">
          <span className="text-xs">{s.profile.pattern.shape}</span>
          <span className="font-mono text-[11px] tabular-nums text-muted-foreground">
            ~{formatRows(s.profile.pattern.typicalRows, true)} rows
            {s.profile.cadenceSource === "schedule" && s.scheduleName !== null && ` - ${s.scheduleName}`}
          </span>
        </div>
      ),
    },
    {
      id: "missing",
      header: "Empty days",
      align: "right",
      render: (s) => (
        <span className="font-mono text-xs tabular-nums">
          {s.profile.unexpectedNullDays === 0
            ? <span className="text-muted-foreground">0</span>
            : <span className="text-destructive">{s.profile.unexpectedNullDays}</span>}
          <span className="text-muted-foreground"> / {s.profile.predictedNullDays.toFixed(1)} expected</span>
        </span>
      ),
    },
    {
      id: "lastLoad",
      header: "Last load",
      align: "right",
      render: (s) => (
        <span className="font-mono text-xs tabular-nums">{formatDays(s.profile.daysSinceLastLoad)}</span>
      ),
    },
    {
      id: "avg",
      header: "Avg ins / upd / del",
      align: "right",
      render: (s) => (
        <span className="font-mono text-xs tabular-nums text-muted-foreground">
          {formatRows(s.profile.avgRowsInsertedPerRun, true)} / {formatRows(s.profile.avgRowsUpdatedPerRun, true)}
          {" / "}{formatRows(s.profile.avgRowsDeletedPerRun, true)}
        </span>
      ),
    },
    {
      id: "agreement",
      header: "Detectors",
      align: "right",
      render: (s) => (
        <span className="font-mono text-xs tabular-nums text-muted-foreground">
          {s.agreeingDetectors === 0 ? "-" : `${s.agreeingDetectors} of 6`}
        </span>
      ),
    },
  ], []);

  const controls = (
    <div className="flex flex-wrap items-center gap-4">
      <div className="flex items-center gap-2">
        <Switch
          id="include-backfills"
          checked={includeBackfills}
          onCheckedChange={setIncludeBackfills}
          data-testid="datastreams-include-backfills"
        />
        <Label htmlFor="include-backfills" className="text-xs font-normal text-muted-foreground">
          Include backfills
        </Label>
      </div>
      <div className="flex items-center gap-2">
        <Switch
          id="scheduled-only"
          checked={scheduledOnly}
          onCheckedChange={setScheduledOnly}
          data-testid="datastreams-scheduled-only"
        />
        <Label htmlFor="scheduled-only" className="text-xs font-normal text-muted-foreground">
          Scheduled only
        </Label>
      </div>
      <ToggleGroup
        type="single"
        value={String(days)}
        onValueChange={(value) => { if (value !== "") { setDays(Number(value)); } }}
        variant="outline"
        size="sm"
        data-testid="datastreams-window-toggle"
      >
        {windowChoices.map((choice) => (
          <ToggleGroupItem key={choice.days} value={String(choice.days)} className="px-2.5 text-xs">
            {choice.label}
          </ToggleGroupItem>
        ))}
      </ToggleGroup>
    </div>
  );

  if (query.isError) {
    return (
      <Page data-testid="page-datastreams">
        <PageHeader title="Data streams" />
        {isApiError(query.error)
          ? <CorrelationError error={query.error} />
          : <p className="text-[13px] text-destructive">{String(query.error)}</p>}
      </Page>
    );
  }

  const board = query.data;

  if (board === undefined) {
    return (
      <Page data-testid="page-datastreams">
        <PageHeader title="Data streams" actions={controls} />
        <div className={kpiGridClass}>
          {Array.from({ length: 5 }, (_, i) => <Skeleton key={`kpi-${i}`} className="h-[104px] rounded-lg" />)}
        </div>
        <Skeleton className="h-96 rounded-lg" />
      </Page>
    );
  }

  const truncated = board.totalStreams > board.analyzedStreams;

  return (
    <Page data-testid="page-datastreams">
      <PageHeader
        title="Data streams"
        subtitle={
          `Which tables have stopped receiving data, over the last ${days} days` +
          (board.includeBackfills ? " (backfills included in the baseline)" : "")
        }
        actions={controls}
      />

      <div className={kpiGridClass}>
        <KpiCard
          label="Stopped"
          value={board.stalledCount}
          caption="no data arriving"
          color={board.stalledCount > 0 ? "error" : "success"}
          testId="kpi-streams-stalled"
        />
        <KpiCard
          label="Degraded"
          value={board.degradedCount}
          caption="two or more detectors agree"
          color={board.degradedCount > 0 ? "warning" : undefined}
          testId="kpi-streams-degraded"
        />
        <KpiCard label="Watch" value={board.watchCount} caption="a single detector's lead" testId="kpi-streams-watch" />
        <KpiCard label="Healthy" value={board.healthyCount} caption="on pattern" testId="kpi-streams-healthy" />
        <KpiCard
          label="Backfill runs"
          value={board.excludedBackfillRuns}
          caption={board.includeBackfills ? "counted as normal traffic" : "excluded from the baseline"}
          testId="kpi-streams-backfills"
        />
      </div>

      <Card>
        <CardHeader className="pb-2">
          <CardTitle className="text-sm">
            {board.analyzedStreams} stream{board.analyzedStreams === 1 ? "" : "s"} analysed
            {truncated && ` of ${board.totalStreams} (capped)`}
          </CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          <ToggleGroup
            type="single"
            value={status}
            onValueChange={setStatus}
            variant="outline"
            size="sm"
            className="self-start"
            data-testid="datastreams-status-filter"
          >
            {statusFilters.map((filter) => (
              <ToggleGroupItem key={filter.value || "all"} value={filter.value} className="px-2.5 text-xs">
                {filter.label}
              </ToggleGroupItem>
            ))}
          </ToggleGroup>

          {board.streams.length === 0 ? (
            <EmptyState
              icon={board.stalledCount === 0 ? <Activity /> : <CircleSlash />}
              title="No streams match"
              description={
                status === ""
                  ? "No flow has run inside this window, so there is nothing to analyse."
                  : "Every analysed stream is in a different state. Clear the filter to see them."
              }
            />
          ) : (
            <DataTable
              columns={columns}
              rows={board.streams}
              rowKey={(s) => s.pipelineId}
              onRowClick={(s) => setDrill({ pipelineId: s.pipelineId, flowName: s.flowName })}
              emptyMessage="No streams to show."
              data-testid="datastreams-table"
            />
          )}
        </CardContent>
      </Card>

      <StreamDetailSheet
        pipelineId={drill?.pipelineId ?? null}
        flowName={drill?.flowName ?? null}
        windowDays={days}
        includeBackfills={includeBackfills}
        onClose={() => setDrill(null)}
      />
    </Page>
  );
}
