import { useEffect, useState } from "react";
import { Link as RouterLink, Navigate, useNavigate, useParams } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { CircleAlert, ListTree, PackageCheck, Trash2 } from "lucide-react";
import { toast } from "sonner";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { isApiError } from "@/api/client";
import {
  deliveryApi,
  deliveryRecordRoute,
  type DeliveryAttempt,
  type DeliverySubmissionDetail,
  type DeliveryWorkBatch,
} from "../../api/delivery";
import { AttemptDetail } from "./AttemptDetail";
import { CodeView } from "@/components/CodeView";
import { CorrelationError } from "@/components/CorrelationError";
import { DataTable, type Column } from "@/components/DataTable";
import { DetailHeaderCard } from "@/components/DetailHeaderCard";
import { DetailPair } from "@/components/DetailPair";
import { IdChip } from "@/components/IdChip";
import { Page } from "@/components/Page";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import { useTabTitle } from "@/layout/workbench/TabsContext";
import { SubmissionStatusBadge } from "./DeliveryBadges";
import { SubmissionCounts } from "./DeliveryFlowPanel";
import { prettyJson } from "./prettyJson";
import { RemovalDialog } from "./RemovalDialog";
import { isTerminalTask, taskResultJson, useComputeTask } from "./useComputeTask";

/** Everything the ledger holds about one submission: what it was, how it went, every attempt it produced, and the runs
 * that carried it, with a way back to the records it touched and the removal of what it put into OSDU. */
export default function DeliverySubmissionPage() {
  const { submissionId } = useParams<{ submissionId: string }>();
  if (!submissionId) {
    return <Navigate to="/delivery" replace />;
  }

  return <SubmissionContent submissionId={submissionId} />;
}

/** The flow's Records tab, scoped to one of this submission's sets, on the interface the submission belongs to. */
function recordsLink(detail: DeliverySubmissionDetail, param: "submission" | "delivered"): string {
  const params = new URLSearchParams({ tab: "records", [param]: detail.submission.submissionId });
  if (detail.interface) {
    params.set("interface", detail.interface);
  }

  return `/pipelines/${detail.pipelineId}?${params.toString()}`;
}

/**
 * The batch as a thing that can be undone: the records this submission delivered are its own however many submissions
 * touched them since, so "remove the batch we ran" is one removal aimed at exactly that set, confirmed with the target
 * it leaves and the count it was shown, and refused if that count has moved by the time it runs.
 */
function BatchActions({ detail, onQueued }: { detail: DeliverySubmissionDetail; onQueued: (taskId: string) => void }) {
  const navigate = useNavigate();
  const [removeOpen, setRemoveOpen] = useState(false);
  const s = detail.submission;
  const pipelineId = detail.pipelineId;
  // The count the confirmation is built on, read the way the removal will resolve it, not the submission's own tally:
  // a record removed or purged since is not in OSDU any more, and the removal must be aimed at what is.
  const delivered = useQuery({
    queryKey: ["delivery", "records", pipelineId, detail.interface ?? null, "delivered-by", s.submissionId],
    queryFn: () => deliveryApi.records(pipelineId!, { deliveredBy: s.submissionId, page: 1, pageSize: 1, interface: detail.interface ?? undefined }),
    enabled: pipelineId !== null,
    refetchInterval: s.status === "completed" || s.status === "failed" ? 30000 : 5000,
  });
  const count = delivered.data?.total ?? 0;
  const capped = delivered.data?.totalCapped === true;

  if (pipelineId === null) {
    return null;
  }

  return (
    <>
      <Button variant="outline" size="sm" onClick={() => navigate(recordsLink(detail, "delivered"))} data-testid="submission-delivered-records">
        <PackageCheck />
        {delivered.data === undefined ? "Records it delivered" : `Records it delivered (${count.toLocaleString()}${capped ? "+" : ""})`}
      </Button>
      <Button variant="outline" size="sm" onClick={() => navigate(recordsLink(detail, "submission"))} data-testid="submission-records">
        <ListTree />
        Records it last planned
      </Button>
      <Button
        variant="destructive-outline"
        size="sm"
        onClick={() => setRemoveOpen(true)}
        disabled={delivered.data === undefined || count === 0}
        title={delivered.data !== undefined && count === 0 ? "This submission delivered nothing that is still in OSDU under its name." : undefined}
        data-testid="submission-remove-delivered"
      >
        <Trash2 />
        Remove what it delivered from OSDU
      </Button>
      <RemovalDialog
        open={removeOpen}
        onClose={() => setRemoveOpen(false)}
        pipelineId={pipelineId}
        interfaceName={detail.interface ?? null}
        flowName={detail.interface ? `${s.flowName} / ${detail.interface}` : s.flowName}
        selection={{ kind: "filter", filter: { deliveredBy: s.submissionId }, expected: count }}
        onQueued={(accepted) => {
          onQueued(accepted.taskId);
          toast.success(`Removal of ${accepted.records.toLocaleString()} record(s) this submission delivered queued on a node.`);
        }}
      />
    </>
  );
}

function SubmissionContent({ submissionId }: { submissionId: string }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [removalTaskId, setRemovalTaskId] = useState<string | null>(null);
  const query = useQuery({
    queryKey: ["delivery", "submission", submissionId],
    queryFn: () => deliveryApi.submission(submissionId),
    refetchInterval: (q) => {
      const status = q.state.data?.submission.status;
      return status === "completed" || status === "failed" ? false : 5000;
    },
  });
  const attempts = useQuery({
    queryKey: ["delivery", "submission", submissionId, "attempts"],
    queryFn: () => deliveryApi.submissionAttempts(submissionId, 1000),
    refetchInterval: 10000,
  });
  const batches = useQuery({
    queryKey: ["delivery", "submission", submissionId, "batches"],
    queryFn: () => deliveryApi.submissionBatches(submissionId, { page: 1, pageSize: 500 }),
    refetchInterval: 10000,
  });
  useTabTitle(query.data ? `Submission ${submissionId.slice(0, 8)}` : undefined);

  // A removal that finished on a node changed the ledger for every record it touched: the attempts, the counts and
  // the delivered set are read again once the task settles, so the page shows what it did without a manual reload.
  const removal = useComputeTask(removalTaskId);
  const finishedRemoval = isTerminalTask(removal.data) ? removal.data!.taskId : null;
  useEffect(() => {
    if (finishedRemoval !== null) {
      void queryClient.invalidateQueries({ queryKey: ["delivery"] });
    }
  }, [finishedRemoval, queryClient]);

  if (query.isError) {
    return (
      <Page data-testid="page-delivery-submission">
        {isApiError(query.error) ? <CorrelationError error={query.error} /> : <p className="text-[13px] text-destructive">{String(query.error)}</p>}
      </Page>
    );
  }

  const detail = query.data;
  if (detail === undefined) {
    return (
      <Page data-testid="page-delivery-submission">
        <Skeleton className="h-60 w-full rounded-lg" />
        <Skeleton className="h-80 w-full rounded-lg" />
      </Page>
    );
  }

  const s = detail.submission;
  const removalJson = isTerminalTask(removal.data) ? taskResultJson(removal.data) : null;
  const attemptColumns: Column<DeliveryAttempt>[] = [
    { id: "started", header: "When", render: (row) => <RelativeTime value={row.startedUtc} absolute /> },
    {
      id: "outcome",
      header: "Outcome",
      render: (row) => (
        <Badge
          variant="secondary"
          className={row.outcome === "delivered" ? "bg-success/15 text-success" : row.outcome === "failed" ? "bg-destructive/15 text-destructive" : row.outcome === "held" ? "bg-warning/15 text-warning" : undefined}
        >
          {row.outcome}
        </Badge>
      ),
    },
    { id: "key", header: "Record", render: (row) => <span className="font-mono text-[12px]">{row.deliveryKey}</span> },
    { id: "phase", header: "Phase", render: (row) => <span className="font-mono text-[12px]">{row.phase}</span> },
    { id: "version", header: "Version", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.targetVersion ?? "-"}</span> },
    { id: "worker", header: "Worker", render: (row) => <TruncatedText text={row.worker} mono maxWidth={200} /> },
    { id: "error", header: "Detail", render: (row) => <AttemptDetail attempt={row} /> },
  ];
  const batchColumns: Column<DeliveryWorkBatch>[] = [
    { id: "index", header: "Batch", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.index}</span> },
    {
      id: "status",
      header: "Status",
      render: (row) => (
        <Badge
          variant="secondary"
          className={row.status === "done" ? "bg-success/15 text-success" : row.status === "failed" ? "bg-destructive/15 text-destructive" : row.status === "running" ? "bg-info/12 text-info" : undefined}
        >
          {row.status}
        </Badge>
      ),
    },
    { id: "records", header: "Records", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.recordCount}</span> },
    { id: "delivered", header: "Delivered", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.delivered}</span> },
    { id: "held", header: "Held", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.held}</span> },
    { id: "failed", header: "Failed", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.failed}</span> },
    { id: "retrying", header: "Retrying", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.retrying}</span> },
    { id: "waiting", header: "Waiting", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.waiting}</span> },
    { id: "started", header: "Started", render: (row) => <RelativeTime value={row.startedUtc} absolute /> },
    { id: "completed", header: "Completed", render: (row) => <RelativeTime value={row.completedUtc} absolute /> },
    {
      id: "run",
      header: "Run",
      render: (row) => (row.runId
        ? <RouterLink to={`/runs/${row.runId}`} className="font-mono text-[12px] text-primary hover:underline">{row.runId.slice(0, 8)}</RouterLink>
        : <span className="text-muted-foreground">-</span>),
    },
    { id: "error", header: "Error", render: (row) => <TruncatedText text={row.error} maxWidth={300} /> },
  ];

  return (
    <Page data-testid="page-delivery-submission">
      {s.error !== null && (
        <Alert variant="destructive" data-testid="submission-error">
          <CircleAlert />
          <AlertDescription>{s.error}</AlertDescription>
        </Alert>
      )}

      <DetailHeaderCard
        title={`Submission ${s.submissionId.slice(0, 8)}`}
        badges={(
          <>
            <SubmissionStatusBadge status={s.status} />
            <Badge variant="outline" className="font-mono" data-testid="submission-kind">{s.kind}</Badge>
            <Badge variant="outline" data-testid="submission-mapping">{s.mappingReference}</Badge>
          </>
        )}
        actions={<BatchActions detail={detail} onQueued={setRemovalTaskId} />}
        meta={(
          <>
            <IdChip label="submission" value={s.submissionId} testId="submission-id" copyTestId="copy-submission-id" />
            {detail.pipelineId && <IdChip label="flow" value={detail.pipelineId} display={s.flowName} to={`/pipelines/${detail.pipelineId}`} testId="submission-pipeline-link" copyTestId="copy-submission-pipeline" />}
            {detail.runIds.map((runId) => (
              <IdChip key={runId} label="run" value={runId} to={`/runs/${runId}`} testId={`submission-run-${runId}`} copyTestId={`copy-submission-run-${runId}`} />
            ))}
          </>
        )}
      >
        <DetailPair label="Received"><RelativeTime value={s.receivedUtc} absolute /></DetailPair>
        <DetailPair label="Started"><RelativeTime value={s.startedUtc} absolute /></DetailPair>
        <DetailPair label="Completed"><RelativeTime value={s.completedUtc} absolute /></DetailPair>
        <DetailPair label="Flow">
          {detail.pipelineId
            ? <RouterLink to={`/pipelines/${detail.pipelineId}`} className="text-primary hover:underline">{s.flowName}</RouterLink>
            : s.flowName}
        </DetailPair>
        <DetailPair label="Records"><span className="font-mono tabular-nums">{s.recordCount}</span></DetailPair>
        <DetailPair label="Batches"><span className="font-mono tabular-nums">{s.batchCount}</span></DetailPair>
        <DetailPair label="Key slices"><span className="font-mono tabular-nums">{s.slices}</span></DetailPair>
        <DetailPair label="Ingestion table">
          <TruncatedText text={s.sourceObject} mono maxWidth={320} />
        </DetailPair>
        <DetailPair label="Connection">
          <TruncatedText text={s.sourceConnection} mono maxWidth={320} />
        </DetailPair>
        <DetailPair label="Change window">
          {s.windowFromUtc === null && s.windowToUtc === null
            ? <span className="text-muted-foreground">the whole scope</span>
            : (
              <span className="inline-flex flex-wrap items-center gap-1" data-testid="submission-window">
                <RelativeTime value={s.windowFromUtc} absolute />
                <span className="text-muted-foreground">to</span>
                <RelativeTime value={s.windowToUtc} absolute />
              </span>
            )}
        </DetailPair>
        <DetailPair label="Untracked"><span className="font-mono tabular-nums">{s.untracked}</span></DetailPair>
        <DetailPair label="Work"><TruncatedText text={s.workLocation} mono maxWidth={320} /></DetailPair>
      </DetailHeaderCard>

      <Card className="gap-2 rounded-lg p-3">
        <h2 className="text-[13px] font-medium">Outcome</h2>
        <SubmissionCounts submission={s} />
      </Card>

      {removalTaskId !== null && (
        <Card className="gap-2 rounded-lg p-3" data-testid="submission-removal-result">
          <div className="flex items-center gap-2 text-[13px] font-medium">
            Removal of what this submission delivered
            <Badge variant="outline">{removal.data?.status ?? "queued"}</Badge>
            {removal.data?.claimedByNode && <span className="font-mono text-[11px] text-muted-foreground">{removal.data.claimedByNode}</span>}
          </div>
          {removal.data?.error && <p className="text-[13px] text-destructive">{removal.data.error}</p>}
          {removalJson !== null && (
            <CodeView value={removalJson} language="json" height={260} data-testid="submission-removal-json" />
          )}
        </Card>
      )}

      <Tabs defaultValue="attempts">
        <TabsList data-testid="submission-tabs">
          <TabsTrigger value="attempts" data-testid="submission-tab-attempts">Attempts</TabsTrigger>
          <TabsTrigger value="batches" data-testid="submission-tab-batches">Batches</TabsTrigger>
          <TabsTrigger value="parameters" data-testid="submission-tab-parameters">Parameters</TabsTrigger>
          <TabsTrigger value="context" data-testid="submission-tab-context">Render context</TabsTrigger>
        </TabsList>
        <TabsContent value="attempts">
          <DataTable
            columns={attemptColumns}
            rows={attempts.data}
            rowKey={(row) => row.attemptId}
            onRowClick={(row) => navigate(deliveryRecordRoute({ flowId: s.flowId, deliveryKey: row.deliveryKey }))}
            emptyMessage="No delivery attempts were made for this submission (everything was unchanged, or it has not run yet)."
            data-testid="submission-attempts"
          />
        </TabsContent>
        <TabsContent value="batches">
          <DataTable
            columns={batchColumns}
            rows={batches.data?.items}
            rowKey={(row) => row.index}
            emptyMessage="No work batches: the intake planned nothing to deliver, or it has not run yet."
            data-testid="submission-batches"
          />
        </TabsContent>
        <TabsContent value="parameters">
          <CodeView value={prettyJson(s.parametersJson)} language="json" height={200} data-testid="submission-parameters" />
        </TabsContent>
        <TabsContent value="context">
          <CodeView value={prettyJson(s.renderContext)} language="json" height={280} data-testid="submission-render-context" />
        </TabsContent>
      </Tabs>
    </Page>
  );
}
