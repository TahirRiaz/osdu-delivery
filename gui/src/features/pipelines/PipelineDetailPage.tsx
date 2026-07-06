import { useState } from "react";
import { Link as RouterLink, useNavigate, useParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import Button from "@mui/material/Button";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Chip from "@mui/material/Chip";
import Link from "@mui/material/Link";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import Tab from "@mui/material/Tab";
import Tabs from "@mui/material/Tabs";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";
import PlayArrowIcon from "@mui/icons-material/PlayArrow";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import { isApiError } from "../../api/client";
import { pipelineApi, runApi, scheduleApi } from "../../api/endpoints";
import type { RunSummary, Schedule } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";
import { DetailHeaderCard } from "../../components/DetailHeaderCard";
import { DetailPair } from "../../components/DetailPair";
import { EmptyState } from "../../components/EmptyState";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { ActiveBadge, RunStatusBadge, ScheduleStateBadge } from "../../components/StatusBadge";
import { formatDurationSeconds } from "../../lib/time";
import { projectOf } from "../repos/project";
import { TriggerRunDialog } from "../runs/TriggerRunDialog";

/** Definition JSON arrives as one compact string; pretty-print it, falling back to the raw text if malformed. */
function prettyJson(raw: string): string {
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}

function scheduleTrigger(row: Schedule): string {
  if (row.cron !== null) {
    return row.cron;
  }

  return row.intervalSeconds !== null ? `every ${row.intervalSeconds}s` : "-";
}

const runColumns = (): Column<RunSummary>[] => [
  { id: "status", header: "Status", render: (row) => <RunStatusBadge status={row.status} /> },
  {
    id: "enqueued",
    header: "Enqueued",
    render: (row) => <RelativeTime value={row.enqueuedUtc ?? row.writtenUtc} />,
  },
  {
    id: "duration",
    header: "Duration",
    render: (row) => (row.durationSeconds !== null ? formatDurationSeconds(row.durationSeconds) : "-"),
  },
  { id: "rowsLoaded", header: "Rows loaded", align: "right", render: (row) => row.rowsLoaded ?? "-" },
  {
    id: "commit",
    header: "Commit",
    render: (row) => <Mono>{row.commitSha?.slice(0, 10) ?? "-"}</Mono>,
  },
];

/** The pre-ingestion transform columns of a pipeline: the declared (YAML) and detected (latest run) view
 * projection, fetched whole (a view's column count is bounded by the table it projects). */
function TransformsTab({ pipelineId }: { pipelineId: string }) {
  const columnsQuery = useQuery({
    queryKey: ["pipelines", "columns", pipelineId],
    queryFn: () => pipelineApi.columns(pipelineId),
  });

  if (columnsQuery.isError) {
    return isApiError(columnsQuery.error)
      ? <CorrelationError error={columnsQuery.error} />
      : <Typography color="error">{String(columnsQuery.error)}</Typography>;
  }

  const rows = columnsQuery.data;
  if (rows === undefined) {
    return <Skeleton variant="rounded" height={240} data-testid="pipeline-transforms-loading" />;
  }

  if (rows.length === 0) {
    return (
      <Card variant="outlined">
        <EmptyState
          title="No transformations yet"
          description="Nothing is declared in this pipeline's YAML, and no run has detected any yet."
          data-testid="pipeline-transforms-empty"
        />
      </Card>
    );
  }

  return (
    <Card variant="outlined">
      <Table size="small" data-testid="pipeline-transforms">
        <TableHead>
          <TableRow>
            <TableCell>Kind</TableCell>
            <TableCell align="right">#</TableCell>
            <TableCell>Column</TableCell>
            <TableCell>Source column</TableCell>
            <TableCell>Data type</TableCell>
            <TableCell>Expression</TableCell>
            <TableCell>Flags</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {rows.map((row) => (
            <TableRow key={`${row.kind}-${row.ordinal}`} hover>
              <TableCell>
                <Chip
                  size="small"
                  label={row.kind}
                  color={row.kind === "declared" ? "primary" : "default"}
                  variant="outlined"
                />
              </TableCell>
              <TableCell align="right">{row.ordinal}</TableCell>
              <TableCell>
                <Typography variant="body2" component="span" sx={{ fontFamily: "monospace" }}>
                  {row.columnName}
                </Typography>
              </TableCell>
              <TableCell>
                <Typography variant="body2" component="span" sx={{ fontFamily: "monospace" }}>
                  {row.sourceColumn ?? "-"}
                </Typography>
              </TableCell>
              <TableCell>
                <Typography variant="body2" component="span" sx={{ fontFamily: "monospace" }}>
                  {row.dataType ?? "-"}
                </Typography>
              </TableCell>
              <TableCell sx={{ maxWidth: 420 }}>
                {row.expression === null ? (
                  "-"
                ) : (
                  <Tooltip title={row.expression}>
                    <Typography variant="body2" component="span" noWrap sx={{ fontFamily: "monospace", display: "block" }}>
                      {row.expression}
                    </Typography>
                  </Tooltip>
                )}
              </TableCell>
              <TableCell>
                <Stack direction="row" spacing={0.5} flexWrap="wrap" useFlexGap>
                  {row.converted && <Chip size="small" label="typed" variant="outlined" color="success" />}
                  {row.isVirtual && <Chip size="small" label="virtual" variant="outlined" />}
                  {row.excludeFromView && <Chip size="small" label="excluded" variant="outlined" color="warning" />}
                </Stack>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </Card>
  );
}

const scheduleColumns: Column<Schedule>[] = [
  { id: "trigger", header: "Trigger", render: (row) => scheduleTrigger(row) },
  { id: "timezone", header: "Timezone", render: (row) => row.timezone },
  {
    id: "state",
    header: "State",
    render: (row) => (
      <Stack direction="row" spacing={0.5} alignItems="center">
        <ScheduleStateBadge enabled={row.enabled} paused={row.paused} />
        {row.catchup && <Chip size="small" variant="outlined" label="catchup" />}
      </Stack>
    ),
  },
  { id: "source", header: "Source", render: (row) => <Chip size="small" label={row.source} variant="outlined" /> },
  { id: "nextFire", header: "Next fire", render: (row) => <RelativeTime value={row.nextFireUtc} /> },
  { id: "lastFire", header: "Last fire", render: (row) => <RelativeTime value={row.lastFireUtc} /> },
];

/** One pipeline: its definition facts, the YAML and parsed definition, its run history, and its schedules. */
export default function PipelineDetailPage() {
  const { pipelineId = "" } = useParams();
  const navigate = useNavigate();
  const [tab, setTab] = useState(0);
  const [triggerOpen, setTriggerOpen] = useState(false);

  const detailQuery = useQuery({
    queryKey: ["pipelines", "detail", pipelineId],
    queryFn: () => pipelineApi.getById(pipelineId),
    enabled: pipelineId !== "",
  });

  if (detailQuery.isError) {
    return isApiError(detailQuery.error)
      ? <CorrelationError error={detailQuery.error} />
      : <Typography color="error">{String(detailQuery.error)}</Typography>;
  }

  const detail = detailQuery.data;
  if (detail === undefined) {
    return (
      <Page data-testid="page-pipeline-detail">
        <Card variant="outlined">
          <CardContent>
            <Stack spacing={1}>
              <Skeleton width={320} height={36} />
              <Skeleton width="70%" />
              <Skeleton width="50%" />
            </Stack>
          </CardContent>
        </Card>
        <Skeleton variant="rounded" height={320} />
      </Page>
    );
  }

  return (
    <Page data-testid="page-pipeline-detail">
      <DetailHeaderCard
        title={detail.name}
        badges={(
          <>
            <Chip size="small" label={detail.kind} variant="outlined" />
            <ActiveBadge active={detail.active} />
          </>
        )}
        actions={(
          <Button
            variant="contained"
            startIcon={<PlayArrowIcon />}
            onClick={() => setTriggerOpen(true)}
            data-testid="open-trigger-run"
          >
            Trigger run
          </Button>
        )}
      >
        <DetailPair label="Repo">
          <Link component={RouterLink} to={`/repos/${detail.repoId}`} data-testid="pipeline-repo-link">
            {detail.repoId}
          </Link>
        </DetailPair>
        <DetailPair label="Project">
          <Link component={RouterLink} to={`/repos/${detail.repoId}`} data-testid="pipeline-project-link">
            {projectOf(detail.relativePath)}
          </Link>
        </DetailPair>
        <DetailPair label="Batch">{detail.batch ?? "-"}</DetailPair>
        <DetailPair label="Wave">{detail.wave === -1 ? "-" : String(detail.wave)}</DetailPair>
        <DetailPair label="Source server">{detail.sourceServer ?? "-"}</DetailPair>
        <DetailPair label="Target server">{detail.targetServer ?? "-"}</DetailPair>
        <DetailPair label="Path"><Mono>{detail.relativePath}</Mono></DetailPair>
        <DetailPair label="Content hash">
          <Tooltip title={detail.contentHash}>
            <Mono>{detail.contentHash.slice(0, 12)}</Mono>
          </Tooltip>
        </DetailPair>
        <DetailPair label="First seen"><RelativeTime value={detail.firstSeenUtc} /></DetailPair>
        <DetailPair label="Last seen"><RelativeTime value={detail.lastSeenUtc} /></DetailPair>
      </DetailHeaderCard>

      <Tabs value={tab} onChange={(_, next) => setTab(next as number)} data-testid="pipeline-tabs">
        <Tab label="YAML" data-testid="pipeline-tab-yaml" />
        <Tab label="Definition" data-testid="pipeline-tab-definition" />
        <Tab label="Transforms" data-testid="pipeline-tab-transforms" />
        <Tab label="Runs" data-testid="pipeline-tab-runs" />
        <Tab label="Schedules" data-testid="pipeline-tab-schedules" />
      </Tabs>

      {tab === 0 && <CodeView value={detail.yaml} language="yaml" height={560} data-testid="pipeline-yaml" />}
      {tab === 1 && (
        <CodeView value={prettyJson(detail.definitionJson)} language="json" height={560} data-testid="pipeline-definition" />
      )}
      {tab === 2 && <TransformsTab pipelineId={pipelineId} />}
      {tab === 3 && (
        <PagedTable
          queryKey={["runs", "by-pipeline", pipelineId]}
          fetchPage={(page, pageSize) => runApi.list({ pipelineId, page, pageSize })}
          columns={runColumns()}
          rowKey={(row) => row.runId}
          onRowClick={(row) => navigate(`/runs/${row.runId}`)}
          pollMs={5000}
          emptyMessage="This pipeline has not run yet."
        />
      )}
      {tab === 4 && (
        <PagedTable
          queryKey={["schedules", "by-pipeline", pipelineId]}
          fetchPage={(page, pageSize) => scheduleApi.list({ pipelineId, page, pageSize })}
          columns={scheduleColumns}
          rowKey={(row) => row.id}
          emptyMessage="This pipeline has no schedules."
        />
      )}

      {triggerOpen && (
        <TriggerRunDialog
          open
          onClose={() => setTriggerOpen(false)}
          repoId={detail.repoId}
          flowName={detail.name}
        />
      )}
    </Page>
  );
}
