import { useQuery } from "@tanstack/react-query";
import Box from "@mui/material/Box";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import CardHeader from "@mui/material/CardHeader";
import Skeleton from "@mui/material/Skeleton";
import Typography from "@mui/material/Typography";
import { useTheme } from "@mui/material/styles";
import { Bar, BarChart, Cell, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { isApiError } from "../../api/client";
import { summaryApi } from "../../api/endpoints";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { KpiCard } from "../../components/KpiCard";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { RelativeTime } from "../../components/RelativeTime";
import { pollingInterval } from "../../hooks/usePolling";

// One even grid for the KPI cards (and their loading skeletons), so the headline numbers read as a designed
// row instead of a ragged flex wrap.
const kpiGridSx = {
  display: "grid",
  gap: 2,
  gridTemplateColumns: "repeat(auto-fill, minmax(180px, 1fr))",
} as const;

/** The operator's landing page: headline counts linking into each area plus the run-state distribution. */
export default function DashboardPage() {
  const theme = useTheme();
  const query = useQuery({
    queryKey: ["summary"],
    queryFn: summaryApi.get,
    refetchInterval: pollingInterval(10000),
  });

  if (query.isError) {
    return (
      <Page data-testid="page-dashboard">
        <PageHeader title="Dashboard" />
        {isApiError(query.error)
          ? <CorrelationError error={query.error} />
          : <Typography color="error">{String(query.error)}</Typography>}
      </Page>
    );
  }

  const dashboard = query.data;
  if (dashboard === undefined) {
    return (
      <Page data-testid="page-dashboard">
        <PageHeader title="Dashboard" />
        <Box sx={kpiGridSx}>
          {Array.from({ length: 6 }, (_, i) => (
            <Skeleton key={`kpi-skeleton-${i}`} variant="rounded" height={104} />
          ))}
        </Box>
        <Skeleton variant="rounded" height={372} />
      </Page>
    );
  }

  const allNodesOnline = dashboard.nodesTotal > 0 && dashboard.nodesOnline === dashboard.nodesTotal;
  const runStates = [
    { state: "queued", count: dashboard.runs.queued, color: theme.palette.info.main },
    { state: "running", count: dashboard.runs.running, color: theme.palette.primary.main },
    { state: "succeeded", count: dashboard.runs.succeeded, color: theme.palette.success.main },
    { state: "failed", count: dashboard.runs.failed, color: theme.palette.error.main },
    { state: "cancelled", count: dashboard.runs.cancelled, color: theme.palette.warning.main },
  ];
  const noRuns = runStates.every((entry) => entry.count === 0);

  return (
    <Page data-testid="page-dashboard">
      <PageHeader
        title="Dashboard"
        subtitle={(
          <Box component="span" data-testid="dashboard-as-of">
            As of <RelativeTime value={dashboard.asOfUtc} />
          </Box>
        )}
      />

      <Box sx={kpiGridSx}>
        <KpiCard label="Repos" value={dashboard.repos} linkTo="/repos" testId="kpi-repos" />
        <KpiCard
          label="Pipelines"
          value={`${dashboard.activePipelines}/${dashboard.pipelines}`}
          caption="active/total"
          linkTo="/pipelines"
          testId="kpi-pipelines"
        />
        <KpiCard
          label="Nodes"
          value={`${dashboard.nodesOnline}/${dashboard.nodesTotal}`}
          caption="online/total"
          linkTo="/nodes"
          color={allNodesOnline ? "success" : undefined}
          testId="kpi-nodes"
        />
        <KpiCard
          label="Schedules"
          value={dashboard.schedulesEnabled}
          caption={`enabled (${dashboard.schedulesPaused} paused)`}
          linkTo="/schedules"
          testId="kpi-schedules"
        />
        <KpiCard
          label="Repo sources"
          value={dashboard.repoSources}
          caption={`${dashboard.repoSourcesWithErrors} with errors`}
          linkTo="/repos"
          color={dashboard.repoSourcesWithErrors > 0 ? "error" : undefined}
          testId="kpi-repo-sources"
        />
        <KpiCard label="Runs last 24h" value={dashboard.runs.last24h} linkTo="/runs" testId="kpi-runs-24h" />
      </Box>

      <Card variant="outlined" data-testid="runs-by-state-card">
        <CardHeader title="Runs by state" titleTypographyProps={{ variant: "h6" }} />
        <CardContent sx={{ height: 320, pt: 0 }}>
          {noRuns ? (
            <EmptyState title="No runs recorded yet" description="Trigger a run and its state distribution appears here." />
          ) : (
            <ResponsiveContainer width="100%" height="100%">
              <BarChart data={runStates} margin={{ top: 8, right: 16, bottom: 0, left: 0 }}>
                <XAxis dataKey="state" stroke={theme.palette.text.secondary} tickLine={false} />
                <YAxis allowDecimals={false} stroke={theme.palette.text.secondary} tickLine={false} />
                <Tooltip
                  cursor={{ fill: theme.palette.action.hover }}
                  contentStyle={{
                    backgroundColor: theme.palette.background.paper,
                    borderColor: theme.palette.divider,
                  }}
                  labelStyle={{ color: theme.palette.text.primary }}
                  itemStyle={{ color: theme.palette.text.primary }}
                />
                <Bar dataKey="count" name="Runs" radius={[4, 4, 0, 0]}>
                  {runStates.map((entry) => (
                    <Cell key={entry.state} fill={entry.color} />
                  ))}
                </Bar>
              </BarChart>
            </ResponsiveContainer>
          )}
        </CardContent>
      </Card>
    </Page>
  );
}
