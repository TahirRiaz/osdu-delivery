import { useCallback, useEffect, useMemo, useState } from "react";
import { Link as RouterLink, Navigate, useNavigate, useParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { BookOpenCheck, CircleAlert, Database, Hourglass, RotateCcw, Send, ShieldCheck, Trash2, Unlock } from "lucide-react";
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
  type DeliveryActivity,
  type DeliveryAttempt,
  type DeliveryAttemptResult,
  type DeliveryRecordLink,
  type DeliveryRecordRef,
  type DeliveryRecordReference,
} from "../../api/delivery";
import { AttemptDetail } from "./AttemptDetail";
import { CodeView } from "@/components/CodeView";
import { ConfirmDialog } from "@/components/ConfirmDialog";
import { CorrelationError } from "@/components/CorrelationError";
import { DataTable, type Column } from "@/components/DataTable";
import { DetailHeaderCard } from "@/components/DetailHeaderCard";
import { DetailPair } from "@/components/DetailPair";
import { IdChip } from "@/components/IdChip";
import { Page } from "@/components/Page";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import { useTabTitle } from "@/layout/workbench/TabsContext";
import { BlockedBadge, RecordStatusBadge, VerifyOutcomeBadge } from "./DeliveryBadges";
import { prettyJson } from "./prettyJson";
import { RemovalDialog } from "./RemovalDialog";
import { isTerminalTask, taskResultJson, useComputeTask } from "./useComputeTask";

/** The steps of one try, compactly: name, status, duration, and whether an earlier try had completed it. */
function AttemptSteps({ result }: { result: DeliveryAttemptResult | null }) {
  const steps = result?.steps ?? [];
  if (steps.length === 0) {
    return <span className="text-muted-foreground">-</span>;
  }

  return (
    <div className="flex flex-wrap gap-1">
      {steps.map((step, index) => (
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
  { id: "error", header: "Detail", render: (row) => <AttemptDetail attempt={row} /> },
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

/** A link to another record's page, named as the ledger names the record. */
function RecordLink({ link, testId }: { link: DeliveryRecordLink; testId?: string }) {
  return (
    <RouterLink to={`/delivery/records/${link.flowId}/${link.deliveryKey}`} className="text-primary hover:underline" data-testid={testId}>
      {link.label ?? link.sourceKey}
    </RouterLink>
  );
}

const referenceColumns: Column<DeliveryRecordReference>[] = [
  { id: "id", header: "OSDU id", fill: true, render: (row) => <TruncatedText text={row.id} mono maxWidth={520} /> },
  { id: "property", header: "Property", render: (row) => <span className="font-mono text-[12px]">{row.property}</span> },
];

const waiterColumns: Column<DeliveryRecordLink>[] = [
  { id: "record", header: "Record", render: (row) => <RecordLink link={row} testId="record-waiter-link" /> },
  { id: "flow", header: "Flow", render: (row) => <span className="text-[12px]">{row.interface ? `${row.flowName ?? "?"} / ${row.interface}` : row.flowName ?? row.flowId}</span> },
  { id: "status", header: "Status", render: (row) => <RecordStatusBadge status={row.status} testId="record-waiter-status" /> },
];

function Hash({ value }: { value: string | null }) {
  return value ? <span className="font-mono text-[12px]" title={value}>{value.slice(0, 12)}</span> : <span className="text-muted-foreground">-</span>;
}

/** Everything the ledger knows about one record: its custody state, every delivery try, every intervention, the
 * document waiting to go, and the buttons that act on it (verify, redeliver, read back, release, delete). */
export default function DeliveryRecordPage() {
  const { flowId, key } = useParams<{ flowId: string; key: string }>();
  if (!flowId || !key) {
    return <Navigate to="/delivery" replace />;
  }

  return <DeliveryRecordContent flowId={flowId} deliveryKey={key} />;
}

/** One flow's record: the same source row read by another flow is that flow's record, with a page of its own. */
function DeliveryRecordContent({ flowId, deliveryKey }: DeliveryRecordRef) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [confirm, setConfirm] = useState<"redeliver" | "send-now" | null>(null);
  const [removeOpen, setRemoveOpen] = useState(false);
  const [taskId, setTaskId] = useState<string | null>(null);
  const [taskLabel, setTaskLabel] = useState("");
  const ref = useMemo<DeliveryRecordRef>(() => ({ flowId, deliveryKey }), [flowId, deliveryKey]);

  const query = useQuery({
    queryKey: ["delivery", "record", flowId, deliveryKey],
    queryFn: () => deliveryApi.record(ref),
    refetchInterval: (q) => {
      const status = q.state.data?.record.status;
      return status === "pending" || status === "delivering" || status === "waiting" ? 3000 : 15000;
    },
  });
  const attempts = useQuery({
    queryKey: ["delivery", "record", flowId, deliveryKey, "attempts"],
    queryFn: () => deliveryApi.attempts(ref, 200),
    refetchInterval: 10000,
  });
  const activities = useQuery({
    queryKey: ["delivery", "record", flowId, deliveryKey, "activities"],
    queryFn: () => deliveryApi.recordActivities(ref, 200),
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
    mutationFn: () => deliveryApi.verify(ref),
    onSuccess: (accepted) => { toast.success("Verify run queued."); navigate(`/runs/${accepted.runId}`); },
    onError: fail,
  });
  const redeliver = useMutation({
    mutationFn: () => deliveryApi.redeliver(ref, "all", true),
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
    mutationFn: () => deliveryApi.release(ref),
    onSuccess: (result) => {
      setConfirm(null);
      toast.success(result.released > 0 ? "Record released." : "Nothing to release.");
      refresh();
    },
    onError: (error) => { setConfirm(null); fail(error); },
  });
  const readBack = useMutation({
    mutationFn: () => deliveryApi.read(ref),
    onSuccess: (accepted) => { setTaskLabel("Read back from OSDU"); setTaskId(accepted.taskId); },
    onError: fail,
  });
  // Where the record came from: its rows as the ingestion tables hold them now, read on a node with the flow's own
  // connection, with the origin file and row the ledger records against every delivered version.
  const readSource = useMutation({
    mutationFn: () => deliveryApi.readSource(ref),
    onSuccess: (accepted) => { setTaskLabel("The record in the ingestion tables"); setTaskId(accepted.taskId); },
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
  const busy = verify.isPending || redeliver.isPending || release.isPending || readBack.isPending || readSource.isPending;
  const canActOnTarget = record.targetId !== null && record.status !== "deleted";
  const references = record.references ?? [];
  const waiters = detail.waitedOnBy ?? [];
  const taskState = task.data;
  const taskJson = isTerminalTask(taskState) ? taskResultJson(taskState) : null;

  return (
    <Page data-testid="page-delivery-record">
      {record.lastError !== null && (record.status === "held" || record.status === "failed") && (
        <Alert variant="destructive" data-testid="record-error">
          <CircleAlert />
          <AlertDescription>{record.lastError}</AlertDescription>
        </Alert>
      )}
      {record.status === "waiting" && (
        <Alert data-testid="record-waiting">
          <Hourglass />
          <AlertDescription>
            <span>{record.lastError ?? `Waits for ${record.waitingFor ?? "a record it refers to"}.`}</span>
            {detail.waitsOn && (
              <span>
                {"It goes out on its own once "}
                <RecordLink link={detail.waitsOn} testId="record-waits-on-link" />
                {" is delivered."}
              </span>
            )}
          </AlertDescription>
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
            <Button variant="outline" size="sm" onClick={() => readSource.mutate()} disabled={busy || detail.pipelineId === null} data-testid="record-read-source">
              <Database />
              Source row
            </Button>
            {record.blocked && (
              <Button variant="outline" size="sm" onClick={() => release.mutate()} disabled={busy} data-testid="record-release">
                <Unlock />
                Release
              </Button>
            )}
            {record.status === "waiting" && (
              <Button variant="outline" size="sm" onClick={() => setConfirm("send-now")} disabled={busy} data-testid="record-send-now">
                <Send />
                Send without waiting
              </Button>
            )}
            <Button
              variant="destructive-outline"
              size="sm"
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
        <DetailPair label="Source last modified">{record.sourceModifiedUtc ? <RelativeTime value={record.sourceModifiedUtc} absolute /> : "-"}</DetailPair>
        <DetailPair label="Payload files modified">{record.payloadModifiedUtc ? <RelativeTime value={record.payloadModifiedUtc} absolute /> : "-"}</DetailPair>
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
          {taskJson !== null && (
            <CodeView value={taskJson} language="json" height={360} data-testid="record-task-json" />
          )}
        </Card>
      )}

      <Tabs defaultValue="history">
        <TabsList data-testid="record-tabs">
          <TabsTrigger value="history" data-testid="record-tab-history">History</TabsTrigger>
          <TabsTrigger value="activity" data-testid="record-tab-activity">Interventions</TabsTrigger>
          <TabsTrigger value="document" data-testid="record-tab-document">Target state</TabsTrigger>
          <TabsTrigger value="context" data-testid="record-tab-context">Render context</TabsTrigger>
          <TabsTrigger value="references" data-testid="record-tab-references">
            References
            {waiters.length > 0 && <Badge variant="secondary" className="ml-1">{waiters.length}</Badge>}
          </TabsTrigger>
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
        <TabsContent value="references">
          <div className="flex flex-col gap-4">
            <div className="flex flex-col gap-1">
              <h3 className="text-[13px] font-medium">What the waiting document refers to</h3>
              <p className="text-[12px] text-muted-foreground">
                A record another record of the ledger holds and has not delivered is waited for; any other id is OSDU&apos;s or another system&apos;s.
              </p>
              <DataTable
                columns={referenceColumns}
                rows={references}
                rowKey={(row) => row.id}
                emptyMessage={record.hasPendingDocument ? "The waiting document refers to no other record." : "No document is waiting, so nothing is referred to."}
                data-testid="record-references"
              />
            </div>
            <div className="flex flex-col gap-1">
              <h3 className="text-[13px] font-medium">Records waiting for this one</h3>
              <DataTable
                columns={waiterColumns}
                rows={waiters}
                rowKey={(row) => `${row.flowId}:${row.deliveryKey}`}
                emptyMessage="No record waits for this one."
                data-testid="record-waiters"
              />
            </div>
          </div>
        </TabsContent>
      </Tabs>

      <ConfirmDialog
        open={confirm === "send-now"}
        title="Send without waiting"
        message="Send this record on its next claim as it is, without waiting for the record it refers to. Until that record lands, the reference points at nothing, and a route that checks references can refuse it. The release is recorded under your name."
        confirmLabel="Send without waiting"
        busy={release.isPending}
        onConfirm={() => release.mutate()}
        onClose={() => setConfirm(null)}
      />
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
