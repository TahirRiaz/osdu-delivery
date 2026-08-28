import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Activity } from "lucide-react";
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
import {
  confidenceLabel, formatDays, formatRows, howOftenItLoads, whatIsWrong,
} from "./streamPresentation";

const kpiGridClass = "grid grid-cols-[repeat(auto-fill,minmax(148px,1fr))] gap-3";

const windowChoices = [
  { days: 30, label: "30d" },
  { days: 60, label: "60d" },
  { days: 90, label: "90d" },
];

/** The filter chips carry the same words as the verdicts themselves, so a reader never has to map one
 * vocabulary onto another. */
const statusFilters = [
  { value: "", label: "All" },
  { value: "stalled", label: "Stopped" },
  { value: "degraded", label: "Missing data" },
  { value: "watch", label: "Worth a look" },
  { value: "healthy", label: "OK" },
];

/**
 * DataStream anomaly detection: which tables have stopped receiving data.
 *
 * Every column answers a question in the words an operator would use, because the algorithm's vocabulary
 * ("degraded", "gap-days", "2 of 6 detectors") is precise and useless at a glance. What is wrong, how often
 * this table normally loads, when data last arrived, how many days it missed against how many it usually
 * misses, and how much to trust the finding. The statistics behind each of those live one click away in the
 * detail sheet, where there is room to show the reasoning rather than just the verdict.
 *
 * "Include backfills" is on the page rather than in a menu because it changes what the numbers MEAN, not just
 * what is shown: it puts operator-driven reprocessing back into the baseline, and a history replay will then
 * make every ordinary day after it look like a collapse.
 */
export default function DataStreamsPage() {
  const [days, setDays] = useLocalStorageState<number>("datastreams.windowDays", 60);
  const [status, setStatus] = useLocalStorageState<string>("datastreams.status", "");
  const [includeBackfills, setIncludeBackfills] = useLocalStorageState<boolean>("datastreams.includeBackfills", false);
  const [drill, setDrill] = useState<{ pipelineId: string; flowName: string } | null>(null);

  const query = useQuery({
    queryKey: ["datastreams", days, status, includeBackfills],
    queryFn: () => dataStreamApi.list({
      days,
      status: status === "" ? undefined : status,
      includeBackfills,
      limit: 300,
    }),
    refetchInterval: pollingInterval(120_000),
  });

  const columns = useMemo<Column<DataStream>[]>(() => [
    {
      id: "table",
      header: "Table",
      width: 300,
      render: (s) => (
        <div className="flex min-w-0 flex-col">
          <span className="truncate font-mono text-xs" title={s.flowName}>{s.flowName}</span>
          <span className="truncate text-[11px] text-muted-foreground" title={s.targetObject ?? undefined}>
            {s.targetObject ?? s.batch ?? "unknown target"}
          </span>
        </div>
      ),
    },
    {
      id: "wrong",
      header: "What's wrong",
      width: 190,
      render: (s) => (
        <div className="flex min-w-0 flex-col gap-0.5">
          <StreamStatusBadge status={s.status} severity={s.severity} />
          <span className="truncate text-[11px] text-muted-foreground" title={s.summary}>{whatIsWrong(s)}</span>
        </div>
      ),
    },
    {
      id: "cadence",
      header: "Normally loads",
      width: 165,
      render: (s) => (
        <div className="flex min-w-0 flex-col">
          <span className="truncate text-[13px]">
            {howOftenItLoads(s.profile.pattern, s.profile.expectedGapDays)}
          </span>
          <span className="truncate font-mono text-[11px] tabular-nums text-muted-foreground">
            {s.profile.pattern.typicalRows > 0
              ? `~${formatRows(s.profile.pattern.typicalRows, true)} rows each time`
              : "no typical size yet"}
          </span>
        </div>
      ),
    },
    {
      id: "lastLoad",
      header: "Data last arrived",
      width: 130,
      align: "right",
      render: (s) => (
        <span className="font-mono text-xs tabular-nums">{formatDays(s.profile.daysSinceLastLoad)}</span>
      ),
    },
    {
      id: "missed",
      header: "Days it missed",
      width: 130,
      align: "right",
      render: (s) => {
        const missed = s.profile.unexpectedNullDays;
        const usual = s.profile.predictedNullDays;
        return (
          <div className="flex flex-col items-end">
            <span className={`font-mono text-xs tabular-nums ${missed > usual ? "text-destructive" : ""}`}>
              {missed}
            </span>
            <span className="text-[11px] text-muted-foreground">
              {usual < 0.5 ? "usually none" : `usually ${usual.toFixed(0)}`}
            </span>
          </div>
        );
      },
    },
    {
      id: "confidence",
      header: "Confidence",
      width: 110,
      align: "right",
      render: (s) => {
        const confidence = confidenceLabel(s);
        return confidence === null
          ? <span className="text-xs text-muted-foreground">-</span>
          : <span className="text-xs" title={confidence.title}>{confidence.label}</span>;
      },
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
          {Array.from({ length: 5 }, (_, i) => <Skeleton key={`kpi-${i}`} className="h-[88px] rounded-lg" />)}
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
          (board.includeBackfills ? " (backfills counted as normal traffic)" : "")
        }
        actions={controls}
      />

      <div className={kpiGridClass}>
        <KpiCard
          label="Stopped"
          value={board.stalledCount}
          caption="no data at all"
          color={board.stalledCount > 0 ? "error" : "success"}
          testId="kpi-streams-stalled"
        />
        <KpiCard
          label="Missing data"
          value={board.degradedCount}
          caption="skipping loads"
          color={board.degradedCount > 0 ? "warning" : undefined}
          testId="kpi-streams-degraded"
        />
        <KpiCard label="Worth a look" value={board.watchCount} caption="one weak signal" testId="kpi-streams-watch" />
        <KpiCard label="OK" value={board.healthyCount} caption="loading on pattern" testId="kpi-streams-healthy" />
        <KpiCard
          label="Backfills"
          value={board.excludedBackfillRuns}
          caption={board.includeBackfills ? "counted" : "left out"}
          testId="kpi-streams-backfills"
        />
      </div>

      <Card>
        <CardHeader className="pb-2">
          <CardTitle className="text-sm">
            {board.analyzedStreams} table{board.analyzedStreams === 1 ? "" : "s"} checked
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
              icon={<Activity />}
              title="Nothing to show"
              description={
                status === ""
                  ? "No flow has run inside this window, so there is nothing to check."
                  : "Every table checked is in a different state. Clear the filter to see them."
              }
            />
          ) : (
            <DataTable
              columns={columns}
              rows={board.streams}
              rowKey={(s) => s.pipelineId}
              onRowClick={(s) => setDrill({ pipelineId: s.pipelineId, flowName: s.flowName })}
              emptyMessage="No tables to show."
              // Below this the columns would compress and clip their content instead of the card scrolling.
              minWidth={1000}
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
