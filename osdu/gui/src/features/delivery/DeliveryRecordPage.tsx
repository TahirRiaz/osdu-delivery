import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Navigate, useNavigate, useParams, useSearchParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AppWindow, BookOpenCheck, RotateCcw, Send, ShieldCheck, Trash2, Unlock } from "lucide-react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { isApiError } from "@/api/client";
import { useAuth } from "@/auth/AuthContext";
import { deliveryApi, type DeliveryRecordLink, type DeliveryRecordRef, type DeliveryRecordReference } from "../../api/delivery";
import { ConfirmDialog } from "@/components/ConfirmDialog";
import { CorrelationError } from "@/components/CorrelationError";
import { DataTable, type Column } from "@/components/DataTable";
import { EmptyState } from "@/components/EmptyState";
import { IconAction } from "@/components/IconAction";
import { IdChip } from "@/components/IdChip";
import { Page } from "@/components/Page";
import { TruncatedText } from "@/components/TruncatedText";
import { useTabTitle } from "@/layout/workbench/TabsContext";
import { BlockedBadge, RecordStatusBadge } from "./DeliveryBadges";
import { OsduRecordPanel } from "./OsduRecordView";
import { RecordCompare } from "./RecordCompare";
import { RecordDocumentTab } from "./RecordDocumentTab";
import { RecordJourney, RecordMilestones } from "./RecordJourney";
import { RecordLink, RecordSituation } from "./RecordSituation";
import { RecordSourceTab } from "./RecordSourceTab";
import { RemovalDialog } from "./RemovalDialog";
import { TaskResultCard } from "./TaskResultCard";
import { ProblemView } from "./TemplateSheet";
import { isTerminalTask, useComputeTask } from "./useComputeTask";

/**
 * The record page's tabs, one question each: what happened to it, where it came from, what the ledger holds to send,
 * what OSDU holds, whether the two agree, and what it is linked to. `?tab=` opens the page on one of them, and
 * `?tab=osdu` also reads the record from OSDU.
 */
const RECORD_TABS = ["timeline", "source", "document", "osdu", "compare", "references"] as const;
type RecordTab = (typeof RECORD_TABS)[number];

/** The tab a `?tab=` names, including the names the page's earlier tabs went by, so an old link still lands somewhere. */
function tabFromParam(value: string | null): RecordTab {
  if (value !== null && (RECORD_TABS as readonly string[]).includes(value)) {
    return value as RecordTab;
  }

  return value === "history" || value === "activity" ? "timeline" : value === "context" ? "document" : "timeline";
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

/**
 * One record of the ledger, top down: who it is and where it stands (the header: identity, custody state, the
 * situation its state calls for, and the operations on it), how far it has come (the milestones), and the evidence
 * behind each answer, one tab per question.
 */
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
  // The removal a node runs for this record, shown under the header whatever tab is open, since it changes the record.
  const [removal, setRemoval] = useState<{ taskId: string; label: string } | null>(null);
  // A read of the record's rows from the ingestion tables, shown on the Source tab where it was asked for.
  const [sourceTaskId, setSourceTaskId] = useState<string | null>(null);
  const ref = useMemo<DeliveryRecordRef>(() => ({ flowId, deliveryKey }), [flowId, deliveryKey]);
  const { hasScope } = useAuth();
  const canOperate = hasScope("operate");
  const [searchParams] = useSearchParams();
  const askedTab = searchParams.get("tab");
  const [tab, setTab] = useState<RecordTab>(tabFromParam(askedTab));
  // A read of the record from OSDU, shown on the In OSDU tab. A page opened with ?tab=osdu reads it once, as soon as the
  // record is known to have an id OSDU may hold.
  const [osduTaskId, setOsduTaskId] = useState<string | null>(null);
  const osduTask = useComputeTask(osduTaskId);
  const autoRead = useRef(askedTab === "osdu");

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
  // Where the record has been before the ledger: the runs that carried its file through pre-ingestion and ingestion.
  // They are past runs of other flows, so they are read once rather than polled.
  const chain = useQuery({
    queryKey: ["delivery", "record", flowId, deliveryKey, "chain"],
    queryFn: () => deliveryApi.recordChain(ref),
    staleTime: 60000,
  });
  const removalTask = useComputeTask(removal?.taskId ?? null);
  const sourceTask = useComputeTask(sourceTaskId);
  useTabTitle(query.data ? (query.data.record.label ?? query.data.record.sourceKey) : undefined);

  const refresh = useCallback(() => void queryClient.invalidateQueries({ queryKey: ["delivery"] }), [queryClient]);

  // A removal that finished on a node changed the ledger (the record is now deleted and blocked): refetch the
  // record once the task reaches a terminal state, so the page reflects it without a manual reload.
  const finishedRemoval = isTerminalTask(removalTask.data) ? removalTask.data?.taskId ?? null : null;
  useEffect(() => {
    if (finishedRemoval !== null) {
      refresh();
    }
  }, [finishedRemoval, refresh]);
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
    mutationFn: (version: number | void) => deliveryApi.read(ref, version === undefined ? undefined : version),
    onSuccess: (accepted) => { setOsduTaskId(accepted.taskId); setTab("osdu"); },
    onError: fail,
  });
  const { mutate: readFromOsdu } = readBack;
  const readable = query.data !== undefined && query.data.record.targetId !== null && query.data.record.status !== "deleted" && canOperate;
  useEffect(() => {
    if (autoRead.current && readable) {
      autoRead.current = false;
      readFromOsdu();
    }
  }, [readable, readFromOsdu]);
  // Where the record came from: its rows as the ingestion tables hold them now, read on a node with the flow's own
  // connection, with the origin file and row the ledger records against every delivered version.
  const readSource = useMutation({
    mutationFn: () => deliveryApi.readSource(ref),
    onSuccess: (accepted) => setSourceTaskId(accepted.taskId),
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
        <Skeleton className="h-28 w-full rounded-lg" />
        <Skeleton className="h-16 w-full rounded-lg" />
        <Skeleton className="h-80 w-full rounded-lg" />
      </Page>
    );
  }

  const record = detail.record;
  const busy = verify.isPending || redeliver.isPending || release.isPending || readBack.isPending || readSource.isPending;
  const canActOnTarget = record.targetId !== null && record.status !== "deleted";
  const references = record.references ?? [];
  const waiters = detail.waitedOnBy ?? [];
  const flowLabel = detail.flowName === null ? undefined : detail.interface ? `${detail.flowName} / ${detail.interface}` : detail.flowName;

  return (
    <Page data-testid="page-delivery-record">
      <Card className="gap-3 rounded-lg p-4" data-testid="record-header">
        <div className="flex flex-wrap items-center gap-2">
          <h1 className="min-w-0 break-words text-lg font-semibold leading-7">{record.label ?? record.sourceKey}</h1>
          <RecordStatusBadge status={record.status} />
          {record.blocked && <BlockedBadge />}
          <div className="grow" />
          <div className="flex flex-wrap items-center gap-2">
            <Button variant="outline" size="sm" onClick={() => verify.mutate()} disabled={busy || !canActOnTarget} title="Queue a verify run scoped to this record: compares what OSDU holds against the ledger" data-testid="record-verify">
              <ShieldCheck />
              Verify
            </Button>
            <Button variant="outline" size="sm" onClick={() => setConfirm("redeliver")} disabled={busy || detail.pipelineId === null} title="Render and send the record again from its current source row" data-testid="record-redeliver">
              <RotateCcw />
              Redeliver
            </Button>
            {record.blocked && (
              <Button variant="outline" size="sm" onClick={() => release.mutate()} disabled={busy} title="Let the record be sent again" data-testid="record-release">
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
          </div>
        </div>
        <div className="flex flex-wrap items-center gap-1.5">
          {record.targetId && <IdChip label="osdu" value={record.targetId} display={record.targetId} testId="record-target" copyTestId="copy-record-target" />}
          {detail.pipelineId && <IdChip label="flow" value={detail.pipelineId} display={flowLabel} to={`/pipelines/${detail.pipelineId}`} testId="record-pipeline-link" copyTestId="copy-record-pipeline" />}
          {record.lastSubmissionId && <IdChip label="submission" value={record.lastSubmissionId} to={`/delivery/submissions/${record.lastSubmissionId}`} testId="record-submission-link" copyTestId="copy-record-submission" />}
        </div>
        <RecordSituation record={record} waitsOn={detail.waitsOn} />
      </Card>

      <RecordMilestones record={record} attempts={attempts.data} chain={chain.data} />

      {removal !== null && <TaskResultCard key={removal.taskId} label={removal.label} task={removalTask.data} testId="record-task" />}

      <Tabs value={tab} onValueChange={(next) => setTab(tabFromParam(next))}>
        <TabsList data-testid="record-tabs">
          <TabsTrigger value="timeline" data-testid="record-tab-timeline">Timeline</TabsTrigger>
          <TabsTrigger value="source" data-testid="record-tab-source">Source</TabsTrigger>
          <TabsTrigger value="document" data-testid="record-tab-document">Document</TabsTrigger>
          <TabsTrigger value="osdu" data-testid="record-tab-osdu">In OSDU</TabsTrigger>
          <TabsTrigger value="compare" data-testid="record-tab-compare">Compare</TabsTrigger>
          <TabsTrigger value="references" data-testid="record-tab-references">
            References
            {(references.length > 0 || waiters.length > 0) && <Badge variant="secondary" className="ml-1">{references.length + waiters.length}</Badge>}
          </TabsTrigger>
        </TabsList>
        <TabsContent value="timeline">
          <RecordJourney record={record} attempts={attempts.data} activities={activities.data} chain={chain.data} />
        </TabsContent>
        <TabsContent value="source">
          <RecordSourceTab
            record={record}
            canRead={detail.pipelineId !== null && !busy}
            reading={sourceTaskId !== null && !isTerminalTask(sourceTask.data)}
            onRead={() => readSource.mutate()}
            task={sourceTaskId === null ? null : { id: sourceTaskId, state: sourceTask.data }}
          />
        </TabsContent>
        <TabsContent value="document">
          <RecordDocumentTab record={record} />
        </TabsContent>
        <TabsContent value="osdu">
          <div className="flex flex-col gap-3">
            <div className="flex flex-wrap items-center gap-2">
              <Button
                variant="outline"
                size="sm"
                onClick={() => readBack.mutate()}
                disabled={busy || !canActOnTarget || !canOperate || (osduTaskId !== null && !isTerminalTask(osduTask.data))}
                title={canOperate ? "Reads the record as OSDU holds it now, through its flow's route and credentials, on a node. Nothing is written." : "A read runs on a node, which takes the operate scope."}
                data-testid="record-osdu-read"
              >
                <BookOpenCheck />
                {osduTaskId === null ? "Read from OSDU" : "Read again"}
              </Button>
              <IconAction
                label="Open this view in a window of its own"
                icon={<AppWindow />}
                variant="outline"
                className="ml-auto size-8"
                onClick={() => window.open(`${window.location.origin}${window.location.pathname}?tab=osdu`, "_blank", "popup=yes,width=1280,height=900")}
                data-testid="record-osdu-popout"
              />
            </div>
            {osduTaskId === null && (
              <EmptyState
                icon={<BookOpenCheck />}
                title={canActOnTarget ? "Not read yet" : record.status === "deleted" ? "Removed from OSDU" : "No OSDU id yet"}
                description={canActOnTarget
                  ? "Read from OSDU shows the record as OSDU holds it now, through its flow's route and credentials, on a node. Nothing is written."
                  : record.status === "deleted"
                    ? "The record was removed from OSDU, so there is nothing of this flow's to read there."
                    : "The record has not been planned for delivery, so OSDU holds nothing of it."}
                data-testid="record-osdu-empty"
              />
            )}
            {osduTaskId !== null && (osduTask.isError
              ? <ProblemView error={osduTask.error} testId="record-osdu-error" />
              : (
                <OsduRecordPanel
                  key={osduTaskId}
                  pipelineId={detail.pipelineId}
                  interfaceName={detail.interface ?? null}
                  task={osduTask.data}
                  label="Reading the record from OSDU through its flow's route"
                  onReadVersion={canActOnTarget && canOperate ? (version) => readBack.mutate(version) : undefined}
                  ledgerVersion={record.targetVersion}
                />
              ))}
          </div>
        </TabsContent>
        <TabsContent value="compare">
          <RecordCompare
            recordRef={ref}
            targetId={canActOnTarget ? record.targetId : null}
            canRun={canOperate && detail.pipelineId !== null}
          />
        </TabsContent>
        <TabsContent value="references">
          <div className="flex flex-col gap-4">
            <div className="flex flex-col gap-1">
              <h3 className="text-[13px] font-medium">What the document refers to</h3>
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
            setRemoval({
              taskId: accepted.taskId,
              label: accepted.scope === "history" ? "Purge history in OSDU" : accepted.scope === "everything" ? "Purge from OSDU" : "Remove from OSDU",
            });
            toast.success("Removal queued on a node.");
          }}
        />
      )}
    </Page>
  );
}
