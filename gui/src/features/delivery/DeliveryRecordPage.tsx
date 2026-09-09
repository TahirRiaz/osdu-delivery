import { useCallback, useEffect, useState } from "react";
import { Navigate, useNavigate, useParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { BookOpenCheck, CircleAlert, RotateCcw, ShieldCheck, Trash2, Unlock } from "lucide-react";
import { toast } from "sonner";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { isApiError } from "../../api/client";
import { deliveryApi, type DeliveryActivity, type DeliveryAttempt, type DeliveryAttemptResult } from "../../api/delivery";
import { CodeView } from "../../components/CodeView";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { DetailHeaderCard } from "../../components/DetailHeaderCard";
import { DetailPair } from "../../components/DetailPair";
import { IdChip } from "../../components/IdChip";
import { Page } from "../../components/Page";
import { RelativeTime } from "../../components/RelativeTime";
import { TruncatedText } from "../../components/TruncatedText";
import { useTabTitle } from "../../layout/workbench/TabsContext";
import { BlockedBadge, RecordStatusBadge, VerifyOutcomeBadge } from "./DeliveryBadges";
import { prettyJson } from "./DeliveryFlowPanel";
import { RemovalDialog } from "./RemovalDialog";
import { isTerminalTask, useComputeTask } from "./useComputeTask";

/** The steps of one try, compactly: name, status, duration, and whether an earlier try had completed it. */
function AttemptSteps({ result }: { result: DeliveryAttemptResult | null }) {
  if (result === null || result.steps.length === 0) {
    return <span className="text-muted-foreground">-</span>;
  }

  return (
    <div className="flex flex-wrap gap-1">
      {result.steps.map((step, index) => (
        <Badge
          key={`${step.name}-${index}`}
          variant="outline"
          className={step.error !== undefined ? "text-destructive" : undefined}
          title={step.returned !== undefined ? JSON.stringify(step.returned) : undefined}
        >
          {step.name}
          {step.status !== undefined ? ` ${step.status}` : ""}
          {step.ms !== undefined ? ` ${step.ms}ms` : ""}
          {step.resumed === true ? " (resumed)" : ""}
        </Badge>
      ))}
    </div>
  );
}

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
  { id: "phase", header: "Phase", render: (row) => <span className="font-mono text-[12px]">{row.phase}</span> },
  { id: "steps", header: "Steps", render: (row) => <AttemptSteps result={row.result} /> },
  { id: "version", header: "Version", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.targetVersion ?? "-"}</span> },
  { id: "batch", header: "Batch", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.workBatch ?? "-"}</span> },
  { id: "worker", header: "Worker", render: (row) => <TruncatedText text={row.worker} mono maxWidth={200} /> },
  { id: "run", header: "Run", render: (row) => (row.runId ? <RunLink runId={row.runId} /> : <span className="text-muted-foreground">-</span>) },
  { id: "submission", header: "Submission", render: (row) => (row.submissionId ? <SubmissionLink submissionId={row.submissionId} /> : <span className="text-muted-foreground">-</span>) },
  { id: "error", header: "Detail", render: (row) => <TruncatedText text={row.error} maxWidth={360} /> },
];

const activityColumns: Column<DeliveryActivity>[] = [
  { id: "started", header: "When", render: (row) => <RelativeTime value={row.startedUtc} absolute /> },
  { id: "kind", header: "Action", render: (row) => <span className="font-medium">{row.kind}</span> },
  { id: "actor", header: "By", render: (row) => <span className="font-mono text-[12px]">{row.actor}</span> },
  {
    id: "outcome",
    header: "Outcome",
    render: (row) => (
      <Badge variant="secondary" className={row.outcome === "completed" ? "bg-success/15 text-success" : row.outcome === "failed" ? "bg-destructive/15 text-destructive" : undefined}>
        {row.outcome}
      </Badge>
    ),
  },
  { id: "run", header: "Run", render: (row) => (row.runId ? <RunLink runId={row.runId} /> : <span className="text-muted-foreground">-</span>) },
  { id: "summary", header: "Summary", render: (row) => <TruncatedText text={row.summary} maxWidth={420} /> },
];

function RunLink({ runId }: { runId: string }) {
  const navigate = useNavigate();
  return (
    <button type="button" className="font-mono text-[12px] text-primary hover:underline" onClick={(event) => { event.stopPropagation(); navigate(`/runs/${runId}`); }}>
      {runId.slice(0, 8)}
    </button>
  );
}

function SubmissionLink({ submissionId }: { submissionId: string }) {
  const navigate = useNavigate();
  return (
    <button type="button" className="font-mono text-[12px] text-primary hover:underline" onClick={(event) => { event.stopPropagation(); navigate(`/delivery/submissions/${submissionId}`); }}>
      {submissionId.slice(0, 8)}
    </button>
  );
}

function Hash({ value }: { value: string | null }) {
  return value ? <span className="font-mono text-[12px]" title={value}>{value.slice(0, 12)}</span> : <span className="text-muted-foreground">-</span>;
}

/** Everything the ledger knows about one record: its custody state, every delivery try, every intervention, the
 * document waiting to go, and the buttons that act on it (verify, redeliver, read back, release, delete). */
export default function DeliveryRecordPage() {
  const { key } = useParams<{ key: string }>();
  if (!key) {
    return <Navigate to="/delivery" replace />;
  }

  return <DeliveryRecordContent deliveryKey={key} />;
}

function DeliveryRecordContent({ deliveryKey }: { deliveryKey: string }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [confirm, setConfirm] = useState<"redeliver" | null>(null);
  const [removeOpen, setRemoveOpen] = useState(false);
  const [taskId, setTaskId] = useState<string | null>(null);
  const [taskLabel, setTaskLabel] = useState("");

  const query = useQuery({
    queryKey: ["delivery", "record", deliveryKey],
    queryFn: () => deliveryApi.record(deliveryKey),
    refetchInterval: (q) => {
      const status = q.state.data?.record.status;
      return status === "pending" || status === "delivering" ? 3000 : 15000;
    },
  });
  const attempts = useQuery({
    queryKey: ["delivery", "record", deliveryKey, "attempts"],
    queryFn: () => deliveryApi.attempts(deliveryKey, 200),
    refetchInterval: 10000,
  });
  const activities = useQuery({
    queryKey: ["delivery", "record", deliveryKey, "activities"],
    queryFn: () => deliveryApi.recordActivities(deliveryKey, 200),
    refetchInterval: 10000,
  });
  const task = useComputeTask(taskId);
  useTabTitle(query.data ? (query.data.record.label ?? query.data.record.sourceKey) : undefined);

  const refresh = useCallback(() => void queryClient.invalidateQueries({ queryKey: ["delivery"] }), [queryClient]);

  // A removal that finished on a node changed the ledger (the record is now deleted and blocked): refetch the
  // record once the task reaches a terminal state, so the page reflects it without a manual reload.
  const finishedTask = task.data && ["succeeded", "failed", "cancelled"].includes(task.data.status) ? task.data.taskId : null;
  useEffect(() => {
    if (finishedTask !== null) {
      refresh();
    }
  }, [finishedTask, refresh]);
  const fail = (error: unknown) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error));

  const verify = useMutation({
    mutationFn: () => deliveryApi.verify(deliveryKey),
    onSuccess: (accepted) => { toast.success("Verify run queued."); navigate(`/runs/${accepted.runId}`); },
    onError: fail,
  });
  const redeliver = useMutation({
    mutationFn: () => deliveryApi.redeliver(deliveryKey, "all", true),
    onSuccess: (result) => {
      setConfirm(null);
      toast.success("Redelivery run queued.");
      if (result.runId) {
        navigate(`/runs/${result.runId}`);
      }
    },
    onError: (error) => { setConfirm(null); fail(error); },
  });
  const release = useMutation({
    mutationFn: () => deliveryApi.release(deliveryKey),
    onSuccess: (result) => { toast.success(result.released > 0 ? "Record released." : "Nothing to release."); refresh(); },
    onError: fail,
  });
  const readBack = useMutation({
    mutationFn: () => deliveryApi.read(deliveryKey),
    onSuccess: (accepted) => { setTaskLabel("Read back from OSDU"); setTaskId(accepted.taskId); },
    onError: fail,
  });
  if (query.isError) {
    return (
      <Page data-testid="page-delivery-record">
        {isApiError(query.error) ? <CorrelationError error={query.error} /> : <p className="text-[13px] text-destructive">{String(query.error)}</p>}
      </Page>
    );
  }

  const detail = query.data;
  if (detail === undefined) {
    return (
      <Page data-testid="page-delivery-record">
        <Skeleton className="h-60 w-full rounded-lg" />
        <Skeleton className="h-80 w-full rounded-lg" />
      </Page>
    );
  }

  const record = detail.record;
  const busy = verify.isPending || redeliver.isPending || release.isPending || readBack.isPending;
  const canActOnTarget = record.targetId !== null && record.status !== "deleted";
  const taskState = task.data;

  return (
    <Page data-testid="page-delivery-record">
      {record.lastError !== null && (record.status === "held" || record.status === "failed") && (
        <Alert variant="destructive" data-testid="record-error">
          <CircleAlert />
          <AlertDescription>{record.lastError}</AlertDescription>
        </Alert>
      )}

      <DetailHeaderCard
        title={record.label ?? record.sourceKey}
        badges={(
          <>
            <RecordStatusBadge status={record.status} />
            {record.blocked && <BlockedBadge />}
            <VerifyOutcomeBadge outcome={record.lastVerifyOutcome} />
            <Badge variant="outline" data-testid="record-mapping">{record.mappingName}</Badge>
            {record.hasPendingDocument && (
              <Badge variant="outline">pending {record.pendingMetadata && record.pendingPayload ? "metadata+payload" : record.pendingPayload ? "payload" : "metadata"}</Badge>
            )}
          </>
        )}
        actions={(
          <>
            <Button variant="outline" size="sm" onClick={() => verify.mutate()} disabled={busy || !canActOnTarget} data-testid="record-verify">
              <ShieldCheck />
              Verify
            </Button>
            <Button variant="outline" size="sm" onClick={() => setConfirm("redeliver")} disabled={busy || detail.pipelineId === null} data-testid="record-redeliver">
              <RotateCcw />
              Redeliver
            </Button>
            <Button variant="outline" size="sm" onClick={() => readBack.mutate()} disabled={busy || !canActOnTarget} data-testid="record-read">
              <BookOpenCheck />
              Read back
            </Button>
            {record.blocked && (
              <Button variant="outline" size="sm" onClick={() => release.mutate()} disabled={busy} data-testid="record-release">
                <Unlock />
                Release
              </Button>
            )}
            <Button
              variant="outline"
              size="sm"
              className="border-destructive/40 text-destructive hover:bg-destructive/10 hover:text-destructive"
              onClick={() => setRemoveOpen(true)}
              disabled={busy || !canActOnTarget || detail.pipelineId === null}
              data-testid="record-delete"
            >
              <Trash2 />
              Remove from OSDU
            </Button>
          </>
        )}
        meta={(
          <>
            <IdChip label="key" value={record.deliveryKey} testId="record-key" copyTestId="copy-record-key" />
            {record.targetId && <IdChip label="osdu" value={record.targetId} display={record.targetId} testId="record-target" copyTestId="copy-record-target" />}
            {detail.pipelineId && <IdChip label="flow" value={detail.pipelineId} display={detail.flowName ?? undefined} to={`/pipelines/${detail.pipelineId}`} testId="record-pipeline-link" copyTestId="copy-record-pipeline" />}
            {record.lastSubmissionId && <IdChip label="submission" value={record.lastSubmissionId} to={`/delivery/submissions/${record.lastSubmissionId}`} testId="record-submission-link" copyTestId="copy-record-submission" />}
          </>
        )}
      >
        <DetailPair label="Source key"><span className="break-all font-mono text-[12px]">{record.sourceKey}</span></DetailPair>
        <DetailPair label="OSDU version"><span className="font-mono tabular-nums">{record.targetVersion ?? "-"}</span></DetailPair>
        <DetailPair label="Last delivered"><RelativeTime value={record.lastDeliveredUtc} absolute /></DetailPair>
        <DetailPair label="Last verified"><RelativeTime value={record.lastVerifiedUtc} absolute /></DetailPair>
        <DetailPair label="Attempts"><span className="font-mono tabular-nums">{record.attemptCount}</span></DetailPair>
        <DetailPair label="Next attempt"><RelativeTime value={record.nextAttemptUtc} absolute /></DetailPair>
        <DetailPair label="Lease">{record.leaseOwner ? <span className="font-mono text-[12px]">{record.leaseOwner}</span> : "-"}</DetailPair>
        <DetailPair label="Metadata hash"><Hash value={record.metadataHash} /></DetailPair>
        <DetailPair label="Payload hash"><Hash value={record.payloadHash} /></DetailPair>
        <DetailPair label="Source fingerprint"><Hash value={record.sourceFingerprint} /></DetailPair>
        <DetailPair label="Payload location"><TruncatedText text={record.pendingPayloadLocation} mono maxWidth={260} /></DetailPair>
        <DetailPair label="Created"><RelativeTime value={record.createdUtc} absolute /></DetailPair>
        <DetailPair label="Updated"><RelativeTime value={record.updatedUtc} absolute /></DetailPair>
      </DetailHeaderCard>

      {taskId !== null && (
        <Card className="gap-2 rounded-lg p-3" data-testid="record-task">
          <div className="flex items-center gap-2 text-[13px] font-medium">
            {taskLabel}
            <Badge variant="outline">{taskState?.status ?? "queued"}</Badge>
            {taskState?.claimedByNode && <span className="font-mono text-[11px] text-muted-foreground">{taskState.claimedByNode}</span>}
          </div>
          {taskState?.error && <p className="text-[13px] text-destructive">{taskState.error}</p>}
          {isTerminalTask(taskState) && taskState?.resultJson && (
            <CodeView value={prettyJson(taskState.resultJson)} language="json" height={360} data-testid="record-task-json" />
          )}
        </Card>
      )}

      <Tabs defaultValue="history">
        <TabsList data-testid="record-tabs">
          <TabsTrigger value="history" data-testid="record-tab-history">History</TabsTrigger>
          <TabsTrigger value="activity" data-testid="record-tab-activity">Interventions</TabsTrigger>
          <TabsTrigger value="document" data-testid="record-tab-document">Target state</TabsTrigger>
          <TabsTrigger value="context" data-testid="record-tab-context">Render context</TabsTrigger>
        </TabsList>
        <TabsContent value="history">
          <DataTable columns={attemptColumns} rows={attempts.data} rowKey={(row) => row.attemptId} emptyMessage="No delivery attempts yet." data-testid="record-attempts" />
        </TabsContent>
        <TabsContent value="activity">
          <DataTable columns={activityColumns} rows={activities.data} rowKey={(row) => row.activityId} emptyMessage="No interventions on this record." data-testid="record-activities" />
        </TabsContent>
        <TabsContent value="document">
          <div className="flex flex-col gap-3">
            {record.hasPendingDocument
              ? (
                <p className="text-[13px] text-muted-foreground" data-testid="record-pending-ref">
                  {`A rendered document is waiting in work batch ${record.workBatch ?? "?"} of submission ${record.lastSubmissionId ?? "?"} (reference ${record.pendingDocumentRef}); the node that drains the batch reads it from the flow's work location.`}
                </p>
              )
              : <p className="text-[13px] text-muted-foreground">No document is waiting: the record is not pending. Use Read back to see what OSDU holds.</p>}
            {record.pendingSteps !== null && (
              <div className="flex flex-col gap-1">
                <h3 className="text-[13px] font-medium">Steps the last try completed</h3>
                <CodeView value={prettyJson(JSON.stringify(record.pendingSteps))} language="json" height={160} data-testid="record-pending-steps" />
              </div>
            )}
            <div className="flex flex-col gap-1">
              <h3 className="text-[13px] font-medium">What OSDU returned</h3>
              {record.targetState !== null
                ? <CodeView value={prettyJson(JSON.stringify(record.targetState))} language="json" height={220} data-testid="record-target-state" />
                : <p className="text-[13px] text-muted-foreground">Nothing yet: the record has not been delivered.</p>}
            </div>
          </div>
        </TabsContent>
        <TabsContent value="context">
          {record.renderContext
            ? <CodeView value={prettyJson(record.renderContext)} language="json" height={280} data-testid="record-render-context" />
            : <p className="text-[13px] text-muted-foreground">Recorded once the record has been delivered.</p>}
        </TabsContent>
      </Tabs>

      <ConfirmDialog
        open={confirm === "redeliver"}
        title="Redeliver record"
        message="Forget what OSDU holds for this record and queue a deliver run that renders and sends it again from the current source. The run is recorded under your name."
        confirmLabel="Redeliver"
        busy={redeliver.isPending}
        onConfirm={() => redeliver.mutate()}
        onClose={() => setConfirm(null)}
      />
      {detail.pipelineId !== null && (
        <RemovalDialog
          open={removeOpen}
          onClose={() => setRemoveOpen(false)}
          pipelineId={detail.pipelineId}
          flowName={detail.flowName ?? "this flow"}
          selection={{ kind: "keys", keys: [deliveryKey] }}
          singleLabel={record.label ?? record.sourceKey}
          onQueued={(accepted) => {
            setTaskLabel(accepted.scope === "history" ? "Purge history in OSDU" : accepted.scope === "everything" ? "Purge from OSDU" : "Remove from OSDU");
            setTaskId(accepted.taskId);
            toast.success("Removal queued on a node.");
          }}
        />
      )}
    </Page>
  );
}
