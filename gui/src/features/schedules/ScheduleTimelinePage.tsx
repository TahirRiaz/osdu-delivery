import { useMemo, useRef, useState } from "react";
import { Link as RouterLink } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import ToggleButton from "@mui/material/ToggleButton";
import ToggleButtonGroup from "@mui/material/ToggleButtonGroup";
import Typography from "@mui/material/Typography";
import { alpha, useTheme } from "@mui/material/styles";
import TableRowsIcon from "@mui/icons-material/TableRows";
import { isApiError } from "../../api/client";
import { runApi, scheduleApi } from "../../api/endpoints";
import type { RunStatus, RunSummary } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { pollingInterval } from "../../hooks/usePolling";
import { formatDurationSeconds } from "../../lib/time";
import { ScheduleTimelineChart, type ScheduleTimelineHandle } from "./ScheduleTimelineChart";
import { buildRows, computeStats, DEFAULT_RANGE_KEY, RANGE_PRESETS, rangeByKey } from "./timeline";

const DAY_MS = 86_400_000;

/** Pages the windowed runs list until the whole window is in hand (bounded so a huge estate can't run away). */
async function fetchRunsInWindow(fromIso: string, toIso: string): Promise<RunSummary[]> {
  const pageSize = 500;
  const out: RunSummary[] = [];
  for (let page = 1; page <= 40; page += 1) {
    const result = await runApi.list({ from: fromIso, to: toIso, page, pageSize });
    out.push(...result.items);
    if (out.length >= result.total || result.items.length < pageSize) {
      break;
    }
  }

  return out;
}

function InsightCell({ label, value, valueColor }: { label: string; value: string; valueColor?: string }) {
  return (
    <Card variant="outlined">
      <CardContent sx={{ py: 1.5, "&:last-child": { pb: 1.5 } }}>
        <Typography variant="h6" sx={{ color: valueColor, lineHeight: 1.2 }} data-testid="timeline-insight-value">
          {value}
        </Typography>
        <Typography variant="caption" color="text.secondary" sx={{ textTransform: "uppercase", letterSpacing: "0.05em" }}>
          {label}
        </Typography>
      </CardContent>
    </Card>
  );
}

const LEGEND: { status: RunStatus; label: string }[] = [
  { status: "succeeded", label: "succeeded" },
  { status: "failed", label: "failed" },
  { status: "running", label: "running" },
  { status: "queued", label: "queued" },
  { status: "cancelled", label: "cancelled" },
  { status: "skipped", label: "skipped" },
];

/** The Schedules timeline: a day-by-day Gantt of when each schedule's flow actually ran, with the estate's
 * cadence rolled up into an insight strip (busiest hour, quietest free window, success rate). */
export default function ScheduleTimelinePage() {
  const theme = useTheme();
  const [rangeKey, setRangeKey] = useState<string>(DEFAULT_RANGE_KEY);
  const [zoomed, setZoomed] = useState(false);
  const chartRef = useRef<ScheduleTimelineHandle>(null);
  const range = rangeByKey(rangeKey);

  const statusColor = (status: RunStatus): string => {
    switch (status) {
      case "succeeded":
        return theme.palette.success.main;
      case "failed":
        return theme.palette.error.main;
      case "running":
        return theme.palette.primary.main;
      case "queued":
        return theme.palette.info.main;
      case "cancelled":
        return theme.palette.warning.main;
      default:
        return theme.palette.text.disabled;
    }
  };

  const schedulesQuery = useQuery({
    queryKey: ["schedule-timeline", "schedules"],
    queryFn: () => scheduleApi.list({ page: 1, pageSize: 200 }),
    refetchInterval: pollingInterval(30000),
  });

  const runsQuery = useQuery({
    queryKey: ["schedule-timeline", "runs", rangeKey],
    queryFn: async () => {
      const toMs = Date.now();
      const fromMs = toMs - range.days * DAY_MS;
      const runs = await fetchRunsInWindow(new Date(fromMs).toISOString(), new Date(toMs).toISOString());
      return { runs, fromMs, toMs };
    },
    refetchInterval: pollingInterval(30000),
  });

  const schedules = schedulesQuery.data?.items;
  const rows = useMemo(
    () => (schedules !== undefined && runsQuery.data !== undefined ? buildRows(schedules, runsQuery.data.runs) : []),
    [schedules, runsQuery.data],
  );
  const stats = useMemo(
    () => (schedules !== undefined ? computeStats(schedules, rows) : null),
    [schedules, rows],
  );

  const successColor = stats?.successRate == null
    ? undefined
    : stats.successRate >= 90
      ? theme.palette.success.main
      : stats.successRate >= 50
        ? theme.palette.warning.main
        : theme.palette.error.main;

  const rangeControl = (
    <Stack direction="row" spacing={1} alignItems="center">
      {zoomed && (
        <Button size="small" variant="outlined" onClick={() => chartRef.current?.reset()} data-testid="timeline-reset-zoom">
          Reset zoom
        </Button>
      )}
      <ToggleButtonGroup
        exclusive
        size="small"
        value={rangeKey}
        onChange={(_, next) => {
          if (next !== null) {
            setRangeKey(next);
          }
        }}
        data-testid="timeline-range"
      >
        {RANGE_PRESETS.map((preset) => (
          <ToggleButton key={preset.key} value={preset.key} sx={{ textTransform: "none" }}>
            {preset.key}
          </ToggleButton>
        ))}
      </ToggleButtonGroup>
      <Button
        component={RouterLink}
        to="/schedules"
        size="small"
        variant="text"
        startIcon={<TableRowsIcon />}
        data-testid="timeline-to-table"
      >
        Table
      </Button>
    </Stack>
  );

  const renderBody = () => {
    if (schedulesQuery.isError) {
      return isApiError(schedulesQuery.error)
        ? <CorrelationError error={schedulesQuery.error} />
        : <Typography color="error">{String(schedulesQuery.error)}</Typography>;
    }

    if (schedules === undefined) {
      return <Skeleton variant="rounded" height={420} />;
    }

    if (schedules.length === 0) {
      return (
        <EmptyState
          title="No schedules yet"
          description="Create a schedule and its runs appear here, day by day."
          action={<Button component={RouterLink} to="/schedules" variant="contained">Go to schedules</Button>}
          data-testid="timeline-empty-schedules"
        />
      );
    }

    if (runsQuery.isError) {
      return isApiError(runsQuery.error)
        ? <CorrelationError error={runsQuery.error} />
        : <Typography color="error">{String(runsQuery.error)}</Typography>;
    }

    if (runsQuery.data === undefined) {
      return <Skeleton variant="rounded" height={420} />;
    }

    if (stats !== null && stats.runCount === 0) {
      return (
        <EmptyState
          title={`No runs in the ${range.label.toLowerCase()}`}
          description="Nothing fired in this window. Widen the range, or trigger a run to see it land on the timeline."
          action={rangeKey !== "30d" ? (
            <Button variant="outlined" onClick={() => setRangeKey("30d")}>Show last 30 days</Button>
          ) : undefined}
          data-testid="timeline-empty-runs"
        />
      );
    }

    return (
      <ScheduleTimelineChart
        ref={chartRef}
        rows={rows}
        windowStartMs={runsQuery.data.fromMs}
        windowEndMs={runsQuery.data.toMs}
        nowMs={runsQuery.data.toMs}
        onZoomChange={setZoomed}
      />
    );
  };

  return (
    <Page data-testid="page-schedule-timeline">
      <PageHeader
        title="Schedule timeline"
        subtitle="When each schedule's flow actually ran, across the selected window."
        actions={rangeControl}
      />

      {stats !== null && runsQuery.data !== undefined && (
        <Box
          sx={{
            display: "grid",
            gap: 2,
            gridTemplateColumns: "repeat(auto-fill, minmax(150px, 1fr))",
          }}
        >
          <InsightCell label="Schedules" value={`${stats.activeScheduleCount}/${stats.scheduleCount}`} />
          <InsightCell label="Runs" value={String(stats.runCount)} />
          <InsightCell
            label="Success"
            value={stats.successRate == null ? "-" : `${stats.successRate}%`}
            valueColor={successColor}
          />
          <InsightCell
            label="Avg duration"
            value={stats.avgDurationSeconds == null ? "-" : formatDurationSeconds(stats.avgDurationSeconds)}
          />
          <InsightCell label="Busiest hour" value={stats.busiestHour ?? "-"} />
          <InsightCell label="Best window" value={stats.bestWindow ?? "-"} />
        </Box>
      )}

      <Card variant="outlined">
        <CardContent>{renderBody()}</CardContent>
      </Card>

      {schedules !== undefined && schedules.length > 0 && stats !== null && stats.runCount > 0 && (
        <Stack
          direction="row"
          spacing={2}
          alignItems="center"
          flexWrap="wrap"
          useFlexGap
          sx={{ px: 0.5, color: "text.secondary" }}
        >
          {LEGEND.map((entry) => (
            <Stack key={entry.status} direction="row" spacing={0.75} alignItems="center">
              <Box sx={{ width: 12, height: 12, borderRadius: 0.5, bgcolor: statusColor(entry.status) }} />
              <Typography variant="caption">{entry.label}</Typography>
            </Stack>
          ))}
          <Stack direction="row" spacing={0.75} alignItems="center">
            <Box
              sx={{
                width: 12,
                height: 12,
                borderRadius: "50%",
                border: `1.5px dashed ${theme.palette.primary.main}`,
                bgcolor: alpha(theme.palette.primary.main, 0.08),
              }}
            />
            <Typography variant="caption">next fire</Typography>
          </Stack>
          <Typography variant="caption" sx={{ ml: "auto" }}>
            Click a day to drill in · double-click the chart to zoom · scroll to zoom · drag to pan
          </Typography>
        </Stack>
      )}
    </Page>
  );
}
