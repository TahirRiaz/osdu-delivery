import { useState } from "react";
import { Link as RouterLink, useNavigate, useParams, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Play } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { isApiError } from "../../api/client";
import { pipelineApi, runApi, scheduleApi } from "../../api/endpoints";
import type { RunSummary, Schedule } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { ConnectionRef } from "../../components/ConnectionRef";
import { CorrelationError } from "../../components/CorrelationError";
import { DetailHeaderCard } from "../../components/DetailHeaderCard";
import { IdChip } from "../../components/IdChip";
import { DetailPair } from "../../components/DetailPair";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { ActiveBadge, RunStatusBadge, ScheduleStateBadge } from "../../components/StatusBadge";
import { TruncatedText } from "../../components/TruncatedText";
import { useTabTitle } from "../../layout/workbench/TabsContext";
import { formatDurationSeconds } from "../../lib/time";
import { projectOf } from "../repos/project";
import { TriggerRunDialog } from "../runs/TriggerRunDialog";
import { DeliveryFlowPanel } from "../delivery/DeliveryFlowPanel";

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

const numeric = (value: number | null | undefined) => (
  <span className="font-mono tabular-nums">{value ?? "-"}</span>
);

const runColumns: Column<RunSummary>[] = [
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
  { id: "operation", header: "Operation", render: (row) => `${row.operation}${row.force ? " (forced)" : ""}` },
  { id: "delivered", header: "Delivered", align: "right", render: (row) => numeric(row.rowsLoaded) },
  {
    id: "commit",
    header: "Commit",
    render: (row) => <Mono>{row.commitSha?.slice(0, 10) ?? "-"}</Mono>,
  },
];

const scheduleColumns: Column<Schedule>[] = [
  { id: "trigger", header: "Trigger", render: (row) => <Mono>{scheduleTrigger(row)}</Mono> },
  { id: "timezone", header: "Timezone", render: (row) => row.timezone },
  {
    id: "state",
    header: "State",
    render: (row) => (
      <span className="inline-flex items-center gap-1">
        <ScheduleStateBadge enabled={row.enabled} paused={row.paused} />
        {row.catchup && <Badge variant="outline">catchup</Badge>}
      </span>
    ),
  },
  { id: "source", header: "Source", render: (row) => <Badge variant="outline">{row.source}</Badge> },
  { id: "nextFire", header: "Next fire", render: (row) => <RelativeTime value={row.nextFireUtc} /> },
  { id: "lastFire", header: "Last fire", render: (row) => <RelativeTime value={row.lastFireUtc} /> },
];

/** One pipeline: its definition facts, the YAML and parsed definition, its run history, and its schedules. */
export default function PipelineDetailPage() {
  const { pipelineId = "" } = useParams();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const [triggerOpen, setTriggerOpen] = useState(false);
  // A delivery flow opens on its delivery tab (the overview page and a submission page link there); the tab
  // query parameter names any other tab explicitly.
  const requestedTab = searchParams.get("tab");

  const detailQuery = useQuery({
    queryKey: ["pipelines", "detail", pipelineId],
    queryFn: () => pipelineApi.getById(pipelineId),
    enabled: pipelineId !== "",
  });

  // The workbench tab reads the pipeline's name once it is known, instead of the generic route title.
  useTabTitle(detailQuery.data?.name);

  if (detailQuery.isError) {
    return isApiError(detailQuery.error)
      ? <CorrelationError error={detailQuery.error} />
      : <p className="text-[13px] text-destructive">{String(detailQuery.error)}</p>;
  }

  const detail = detailQuery.data;
  if (detail === undefined) {
    return (
      <Page data-testid="page-pipeline-detail">
        <Card className="gap-0 rounded-lg p-4">
          <div className="flex flex-col gap-2">
            <Skeleton className="h-7 w-80" />
            <Skeleton className="h-4 w-[70%]" />
            <Skeleton className="h-4 w-1/2" />
          </div>
        </Card>
        <Skeleton className="h-80 w-full rounded-lg" />
      </Page>
    );
  }

  const isDelivery = detail.kind === "delivery";

  return (
    <Page data-testid="page-pipeline-detail">
      <DetailHeaderCard
        title={detail.name}
        badges={(
          <>
            <Badge variant="outline">{detail.kind}</Badge>
            <ActiveBadge active={detail.active} />
            {detail.executionMode === "manual" && (
              <Tooltip>
                <TooltipTrigger asChild>
                  <span>
                    <Badge
                      variant="secondary"
                      className="bg-warning/15 text-warning"
                      data-testid="pipeline-manual-mode"
                    >
                      manual
                    </Badge>
                  </span>
                </TooltipTrigger>
                <TooltipContent className="max-w-sm">
                  mode: manual - excluded from schedules and group runs; executes only when triggered directly.
                </TooltipContent>
              </Tooltip>
            )}
            {detail.executionMode === "disabled" && (
              <Tooltip>
                <TooltipTrigger asChild>
                  <span>
                    <Badge
                      variant="secondary"
                      className="bg-destructive/15 text-destructive"
                      data-testid="pipeline-disabled-mode"
                    >
                      disabled
                    </Badge>
                  </span>
                </TooltipTrigger>
                <TooltipContent className="max-w-sm">
                  mode: disabled - deactivated: never part of schedules or group runs; still runnable by a direct
                  trigger.
                </TooltipContent>
              </Tooltip>
            )}
          </>
        )}
        actions={(
          <Button size="sm" onClick={() => setTriggerOpen(true)} data-testid="open-trigger-run">
            <Play />
            Trigger run
          </Button>
        )}
        meta={(
          <>
            <IdChip
              label="repo"
              value={detail.repoId}
              to={`/repos/${detail.repoId}`}
              testId="pipeline-repo-link"
              copyTestId="copy-pipeline-repo"
            />
            <IdChip
              label="hash"
              value={detail.contentHash}
              display={detail.contentHash.slice(0, 10)}
              testId="pipeline-content-hash"
              copyTestId="copy-pipeline-hash"
            />
          </>
        )}
      >
        <DetailPair label="Project">
          <RouterLink
            to={`/repos/${detail.repoId}`}
            className="text-primary hover:underline"
            data-testid="pipeline-project-link"
          >
            {projectOf(detail.relativePath)}
          </RouterLink>
        </DetailPair>
        <DetailPair label="Batch">{detail.batch ?? "-"}</DetailPair>
        <DetailPair label="Wave">{detail.wave === -1 ? "-" : String(detail.wave)}</DetailPair>
        <DetailPair label="Source"><ConnectionRef value={detail.sourceServer} copyTestId="copy-pipeline-source" /></DetailPair>
        <DetailPair label="Target"><ConnectionRef value={detail.targetServer} copyTestId="copy-pipeline-target" /></DetailPair>
        <DetailPair label="Path">
          <TruncatedText text={detail.relativePath} mono maxWidth={240} copy copyTestId="copy-pipeline-path" />
        </DetailPair>
        <DetailPair label="First seen"><RelativeTime value={detail.firstSeenUtc} absolute /></DetailPair>
        <DetailPair label="Last seen"><RelativeTime value={detail.lastSeenUtc} absolute /></DetailPair>
      </DetailHeaderCard>

      <Tabs defaultValue={requestedTab ?? (isDelivery ? "delivery" : "yaml")}>
        <TabsList data-testid="pipeline-tabs">
          {isDelivery && <TabsTrigger value="delivery" data-testid="pipeline-tab-delivery">Delivery</TabsTrigger>}
          {isDelivery && <TabsTrigger value="records" data-testid="pipeline-tab-records">Records</TabsTrigger>}
          {isDelivery && <TabsTrigger value="submissions" data-testid="pipeline-tab-submissions">Submissions</TabsTrigger>}
          <TabsTrigger value="yaml" data-testid="pipeline-tab-yaml">YAML</TabsTrigger>
          <TabsTrigger value="runs" data-testid="pipeline-tab-runs">Runs</TabsTrigger>
          <TabsTrigger value="schedules" data-testid="pipeline-tab-schedules">Schedules</TabsTrigger>
          <TabsTrigger value="definition" data-testid="pipeline-tab-definition">Definition</TabsTrigger>
        </TabsList>

        {isDelivery && (
          <TabsContent value="delivery">
            <DeliveryFlowPanel pipelineId={detail.id} flowName={detail.name} section="overview" />
          </TabsContent>
        )}
        {isDelivery && (
          <TabsContent value="records">
            <DeliveryFlowPanel pipelineId={detail.id} flowName={detail.name} section="records" />
          </TabsContent>
        )}
        {isDelivery && (
          <TabsContent value="submissions">
            <DeliveryFlowPanel pipelineId={detail.id} flowName={detail.name} section="submissions" />
          </TabsContent>
        )}
        <TabsContent value="yaml">
          <CodeView value={detail.yaml} language="yaml" height={560} data-testid="pipeline-yaml" />
        </TabsContent>
        <TabsContent value="runs">
          <PagedTable
            queryKey={["runs", "by-pipeline", pipelineId]}
            fetchPage={(page, pageSize) => runApi.list({ pipelineId, page, pageSize })}
            columns={runColumns}
            rowKey={(row) => row.runId}
            onRowClick={(row) => navigate(`/runs/${row.runId}`)}
            pollMs={5000}
            emptyMessage="This pipeline has not run yet."
          />
        </TabsContent>
        <TabsContent value="schedules">
          <PagedTable
            queryKey={["schedules", "by-pipeline", pipelineId]}
            fetchPage={(page, pageSize) => scheduleApi.list({ pipelineId, page, pageSize })}
            columns={scheduleColumns}
            rowKey={(row) => row.id}
            emptyMessage="This pipeline has no schedules."
          />
        </TabsContent>
        <TabsContent value="definition">
          <CodeView
            value={prettyJson(detail.definitionJson)}
            language="json"
            height={560}
            data-testid="pipeline-definition"
          />
        </TabsContent>
      </Tabs>

      {triggerOpen && (
        <TriggerRunDialog
          open
          onClose={() => setTriggerOpen(false)}
          repoId={detail.repoId}
          flowName={detail.name}
          flowId={detail.id}
        />
      )}
    </Page>
  );
}
