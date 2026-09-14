import { Link as RouterLink, Navigate, useNavigate, useParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { CircleAlert } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { isApiError } from "../../api/client";
import { deliveryApi, type DeliveryAttempt, type DeliveryWorkBatch } from "../../api/delivery";
import { AttemptDetail } from "./AttemptDetail";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { DetailHeaderCard } from "../../components/DetailHeaderCard";
import { DetailPair } from "../../components/DetailPair";
import { IdChip } from "../../components/IdChip";
import { Page } from "../../components/Page";
import { RelativeTime } from "../../components/RelativeTime";
import { TruncatedText } from "../../components/TruncatedText";
import { useTabTitle } from "../../layout/workbench/useWorkbenchTabs";
import { SubmissionStatusBadge } from "./DeliveryBadges";
import { SubmissionCounts } from "./DeliveryFlowPanel";
import { prettyJson } from "./prettyJson";

/** Everything the ledger holds about one drop: what it was, how it went, every attempt it produced, and the runs
 * that carried it, with a way back to the records it touched. */
export default function DeliverySubmissionPage() {
  const { submissionId } = useParams<{ submissionId: string }>();
  if (!submissionId) {
    return <Navigate to="/delivery" replace />;
  }

  return <SubmissionContent submissionId={submissionId} />;
}

function SubmissionContent({ submissionId }: { submissionId: string }) {
  const navigate = useNavigate();
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
  // The records a source sent inline. A drop submission answers 404, which leaves the records tab out.
  const content = useQuery({
    queryKey: ["delivery", "submission", submissionId, "content"],
    queryFn: () => deliveryApi.submissionContent(submissionId),
    retry: false,
  });
  const inline = content.data;
  useTabTitle(query.data ? `Submission ${submissionId.slice(0, 8)}` : undefined);

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
            <Badge variant="outline" data-testid="submission-mapping">{s.mappingReference}</Badge>
            {inline !== undefined && <Badge variant="secondary" data-testid="submission-inline">records sent by {inline.receivedBy}</Badge>}
          </>
        )}
        actions={detail.pipelineId ? (
          <Button variant="outline" size="sm" onClick={() => navigate(`/pipelines/${detail.pipelineId}?tab=records&submission=${s.submissionId}`)} data-testid="submission-records">
            Records of this submission
          </Button>
        ) : undefined}
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
        <DetailPair label="Drop"><TruncatedText text={s.dropLocation} mono maxWidth={320} copy copyTestId="copy-submission-drop" /></DetailPair>
        <DetailPair label="Flow">
          {detail.pipelineId
            ? <RouterLink to={`/pipelines/${detail.pipelineId}`} className="text-primary hover:underline">{s.flowName}</RouterLink>
            : s.flowName}
        </DetailPair>
        <DetailPair label="Records"><span className="font-mono tabular-nums">{s.recordCount}</span></DetailPair>
        <DetailPair label="Batches"><span className="font-mono tabular-nums">{s.batchCount}</span></DetailPair>
        <DetailPair label="Partitions"><span className="font-mono tabular-nums">{s.partitions}</span></DetailPair>
        <DetailPair label="Work"><TruncatedText text={s.workLocation} mono maxWidth={320} /></DetailPair>
      </DetailHeaderCard>

      <Card className="gap-2 rounded-lg p-3">
        <h2 className="text-[13px] font-medium">Outcome</h2>
        <SubmissionCounts submission={s} />
      </Card>

      <Tabs defaultValue="attempts">
        <TabsList data-testid="submission-tabs">
          <TabsTrigger value="attempts" data-testid="submission-tab-attempts">Attempts</TabsTrigger>
          <TabsTrigger value="batches" data-testid="submission-tab-batches">Batches</TabsTrigger>
          {inline !== undefined && <TabsTrigger value="records" data-testid="submission-tab-records">Records sent</TabsTrigger>}
          <TabsTrigger value="parameters" data-testid="submission-tab-parameters">Parameters</TabsTrigger>
          <TabsTrigger value="context" data-testid="submission-tab-context">Render context</TabsTrigger>
        </TabsList>
        <TabsContent value="attempts">
          <DataTable
            columns={attemptColumns}
            rows={attempts.data}
            rowKey={(row) => row.attemptId}
            onRowClick={(row) => navigate(`/delivery/records/${row.deliveryKey}`)}
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
        {inline !== undefined && (
          <TabsContent value="records" className="flex flex-col gap-3">
            <Card className="gap-2 rounded-lg p-3" data-testid="submission-inline-summary">
              <div className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-3">
                <DetailPair label="Sent by">{inline.receivedBy}</DetailPair>
                <DetailPair label="Received"><RelativeTime value={inline.receivedUtc} absolute /></DetailPair>
                <DetailPair label="Operation">{inline.force ? `${inline.operation}, forced` : inline.operation}</DetailPair>
                <DetailPair label="Records">
                  <span className="font-mono tabular-nums">{inline.recordCount} ({inline.childRowCount} child rows)</span>
                </DetailPair>
                <DetailPair label="Written as a drop">
                  {inline.dropLocation !== null
                    ? <TruncatedText text={inline.dropLocation} mono maxWidth={320} copy copyTestId="copy-submission-inline-drop" />
                    : <span className="text-muted-foreground">not yet</span>}
                </DetailPair>
                <DetailPair label="Content hash">
                  <TruncatedText text={inline.contentHash} mono maxWidth={220} copy copyTestId="copy-submission-content-hash" />
                </DetailPair>
              </div>
            </Card>
            <CodeView value={JSON.stringify(inline.records, null, 2)} language="json" height={420} data-testid="submission-inline-records" />
          </TabsContent>
        )}
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
