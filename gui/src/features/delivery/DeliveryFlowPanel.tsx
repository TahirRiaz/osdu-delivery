import { useState } from "react";
import { Link as RouterLink, useNavigate, useSearchParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Radar, Send, Unlock } from "lucide-react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { Label } from "@/components/ui/label";
import { isApiError } from "../../api/client";
import {
  DELIVERY_RECORD_STATUSES, deliveryApi,
  type DeliveryRecord, type DeliveryRecordStatus, type DeliverySubmission,
} from "../../api/delivery";
import { CodeView } from "../../components/CodeView";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { FilterBar } from "../../components/FilterBar";
import { KpiCard } from "../../components/KpiCard";
import { PagedTable } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { SearchInput } from "../../components/SearchInput";
import { TruncatedText } from "../../components/TruncatedText";
import { BlockedBadge, RecordStatusBadge, SubmissionStatusBadge, VerifyOutcomeBadge } from "./DeliveryBadges";
import { SubmitDropDialog } from "./SubmitDropDialog";
import { isTerminalTask, useComputeTask } from "./useComputeTask";

const ALL = "all";

const recordColumns: Column<DeliveryRecord>[] = [
  { id: "status", header: "Status", render: (row) => <RecordStatusBadge status={row.status} /> },
  {
    id: "label",
    header: "Record",
    render: (row) => (
      <div className="flex min-w-0 flex-col">
        <span className="truncate font-medium">{row.label ?? row.sourceKey}</span>
        {row.label !== null && <span className="truncate font-mono text-[11px] text-muted-foreground">{row.sourceKey}</span>}
      </div>
    ),
  },
  { id: "target", header: "OSDU id", render: (row) => <TruncatedText text={row.targetId} mono maxWidth={260} /> },
  { id: "version", header: "Version", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.targetVersion ?? "-"}</span> },
  { id: "delivered", header: "Delivered", render: (row) => <RelativeTime value={row.lastDeliveredUtc} /> },
  { id: "verify", header: "Verify", render: (row) => <VerifyOutcomeBadge outcome={row.lastVerifyOutcome} /> },
  { id: "attempts", header: "Attempts", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.attemptCount}</span> },
  {
    id: "flags",
    header: "",
    render: (row) => (
      <span className="inline-flex gap-1">
        {row.blocked && <BlockedBadge />}
        {row.hasPendingDocument && <Badge variant="outline">pending</Badge>}
      </span>
    ),
  },
  { id: "error", header: "Last error", render: (row) => <TruncatedText text={row.lastError} maxWidth={320} /> },
];

/** The flow's stats strip, its submissions, and its searchable records, with the flow-level interventions. */
export function DeliveryFlowPanel({ pipelineId, flowName, section }: { pipelineId: string; flowName: string; section: "overview" | "records" | "submissions" }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [searchParams, setSearchParams] = useSearchParams();
  // A submission page links here scoped to its records; clearing the chip widens the list again.
  const submissionFilter = searchParams.get("submission");
  const [search, setSearch] = useState("");
  const [status, setStatus] = useState<string>(ALL);
  const [drifted, setDrifted] = useState(false);
  const [contains, setContains] = useState(false);
  const [submitOpen, setSubmitOpen] = useState(false);
  const [releaseOpen, setReleaseOpen] = useState(false);
  const [probeTaskId, setProbeTaskId] = useState<string | null>(null);

  const stats = useQuery({
    queryKey: ["delivery", "stats", pipelineId],
    queryFn: () => deliveryApi.stats(pipelineId),
    refetchInterval: 10000,
  });
  const submissions = useQuery({
    queryKey: ["delivery", "submissions", pipelineId],
    queryFn: () => deliveryApi.submissions(pipelineId, 100),
    enabled: section !== "records",
    refetchInterval: 10000,
  });
  const probe = useComputeTask(probeTaskId);

  const probeTarget = useMutation({
    mutationFn: () => deliveryApi.probe(pipelineId),
    onSuccess: (accepted) => setProbeTaskId(accepted.taskId),
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });
  const releaseAll = useMutation({
    mutationFn: () => deliveryApi.releaseFlow(pipelineId),
    onSuccess: (result) => {
      setReleaseOpen(false);
      toast.success(`Released ${result.released} record${result.released === 1 ? "" : "s"}.`);
      void queryClient.invalidateQueries({ queryKey: ["delivery"] });
    },
    onError: (error) => {
      setReleaseOpen(false);
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error));
    },
  });

  if (stats.isError) {
    return isApiError(stats.error)
      ? <CorrelationError error={stats.error} />
      : <p className="text-[13px] text-destructive">{String(stats.error)}</p>;
  }

  const s = stats.data;
  const blocked = s ? s.held + s.failed + s.deleted : 0;
  const probeResult = probe.data;

  return (
    <div className="flex flex-col gap-4" data-testid={`delivery-panel-${section}`}>
      {section === "overview" && (
        <>
          <div className="flex flex-wrap items-center gap-2">
            <Button size="sm" onClick={() => setSubmitOpen(true)} data-testid="delivery-submit-drop">
              <Send />
              Submit drop
            </Button>
            <Button variant="outline" size="sm" onClick={() => probeTarget.mutate()} disabled={probeTarget.isPending} data-testid="delivery-probe">
              <Radar />
              Probe target
            </Button>
            <Button variant="outline" size="sm" onClick={() => setReleaseOpen(true)} disabled={blocked === 0} data-testid="delivery-release-all">
              <Unlock />
              Release blocked ({blocked})
            </Button>
          </div>
          {s === undefined ? (
            <Skeleton className="h-24 w-full rounded-lg" />
          ) : (
            <div className="grid grid-cols-2 gap-3 md:grid-cols-4 xl:grid-cols-8" data-testid="delivery-stats">
              <KpiCard label="Records" value={s.total} testId="delivery-kpi-total" />
              <KpiCard label="Delivered" value={s.delivered} color="success" caption={`${s.deliveredLast24h} in the last 24h`} testId="delivery-kpi-delivered" />
              <KpiCard label="Pending" value={s.pending + s.delivering} color="info" caption={s.delivering > 0 ? `${s.delivering} delivering now` : undefined} testId="delivery-kpi-pending" />
              <KpiCard label="Held" value={s.held} color={s.held > 0 ? "warning" : undefined} testId="delivery-kpi-held" />
              <KpiCard label="Failed" value={s.failed} color={s.failed > 0 ? "error" : undefined} testId="delivery-kpi-failed" />
              <KpiCard label="Deleted" value={s.deleted} testId="delivery-kpi-deleted" />
              <KpiCard label="Drifted" value={s.drifted} color={s.drifted > 0 ? "warning" : undefined} caption={s.lastVerifiedUtc ? "since the last verify" : "never verified"} testId="delivery-kpi-drifted" />
              <KpiCard label="Submissions" value={s.submissions} testId="delivery-kpi-submissions" />
            </div>
          )}
          {probeTaskId !== null && (
            <Card className="gap-2 rounded-lg p-3" data-testid="delivery-probe-result">
              <div className="flex items-center gap-2 text-[13px] font-medium">
                Target probe
                <Badge variant="outline">{probeResult?.status ?? "queued"}</Badge>
                {probeResult?.claimedByNode && <span className="font-mono text-[11px] text-muted-foreground">{probeResult.claimedByNode}</span>}
              </div>
              {probeResult?.error && <p className="text-[13px] text-destructive">{probeResult.error}</p>}
              {isTerminalTask(probeResult) && probeResult?.resultJson && (
                <CodeView value={prettyJson(probeResult.resultJson)} language="json" height={180} data-testid="delivery-probe-json" />
              )}
            </Card>
          )}
          {s?.lastSubmission && (
            <Card className="gap-1 rounded-lg p-3" data-testid="delivery-last-submission">
              <div className="flex flex-wrap items-center gap-2 text-[13px]">
                <span className="font-medium">Last submission</span>
                <SubmissionStatusBadge status={s.lastSubmission.status} />
                <RouterLink to={`/delivery/submissions/${s.lastSubmission.submissionId}`} className="font-mono text-[12px] text-primary hover:underline">
                  {s.lastSubmission.submissionId}
                </RouterLink>
                <span className="text-muted-foreground">received <RelativeTime value={s.lastSubmission.receivedUtc} /></span>
              </div>
              <SubmissionCounts submission={s.lastSubmission} />
            </Card>
          )}
        </>
      )}

      {section === "records" && (
        <>
          <FilterBar>
            <SearchInput value={search} onChange={setSearch} placeholder="Delivery key, label, source key or OSDU id" label="Search records" testId="delivery-records-search" className="sm:w-96" />
            <Select value={status} onValueChange={setStatus}>
              <SelectTrigger size="sm" className="h-8 w-40" data-testid="delivery-records-status">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ALL}>All statuses</SelectItem>
                {DELIVERY_RECORD_STATUSES.map((value) => <SelectItem key={value} value={value}>{value}</SelectItem>)}
              </SelectContent>
            </Select>
            <Label className="flex items-center gap-2 text-[13px] font-normal">
              <Switch checked={drifted} onCheckedChange={setDrifted} data-testid="delivery-records-drifted" />
              Drifted only
            </Label>
            <Label className="flex items-center gap-2 text-[13px] font-normal">
              <Switch checked={contains} onCheckedChange={setContains} data-testid="delivery-records-contains" />
              Match anywhere
            </Label>
            {submissionFilter && (
              <Button variant="outline" size="sm" className="h-8" onClick={() => setSearchParams((current) => { const next = new URLSearchParams(current); next.delete("submission"); return next; })} data-testid="delivery-records-clear-submission">
                submission {submissionFilter.slice(0, 8)}: clear
              </Button>
            )}
          </FilterBar>
          <PagedTable
            queryKey={["delivery", "records", pipelineId, search, status, drifted, contains, submissionFilter]}
            fetchPage={(page, pageSize) => deliveryApi.records(pipelineId, {
              page,
              pageSize,
              search: search.trim() === "" ? undefined : search.trim(),
              mode: contains ? "contains" : undefined,
              status: status === ALL ? undefined : (status as DeliveryRecordStatus),
              drifted: drifted || undefined,
              submissionId: submissionFilter ?? undefined,
            })}
            columns={recordColumns}
            rowKey={(row) => row.deliveryKey}
            onRowClick={(row) => navigate(`/delivery/records/${row.deliveryKey}`)}
            pollMs={10000}
            emptyMessage="No records match. A flow's records appear here once its first submission has been planned."
            data-testid="delivery-records-table"
          />
        </>
      )}

      {section === "submissions" && (
        <DataTable
          columns={submissionColumns}
          rows={submissions.data}
          rowKey={(row) => row.submissionId}
          onRowClick={(row) => navigate(`/delivery/submissions/${row.submissionId}`)}
          emptyMessage="No submissions yet. A submission is one drop handed over for delivery."
          data-testid="delivery-submissions-table"
        />
      )}

      <SubmitDropDialog open={submitOpen} onClose={() => setSubmitOpen(false)} pipelineId={pipelineId} flowName={flowName} />
      <ConfirmDialog
        open={releaseOpen}
        title="Release blocked records"
        message={`Release every held, failed and deleted record of ${flowName} back to pending? Records that still hold a rendered document are queued at once; the others are planned again on the next submission.`}
        confirmLabel="Release"
        busy={releaseAll.isPending}
        onConfirm={() => releaseAll.mutate()}
        onClose={() => setReleaseOpen(false)}
      />
    </div>
  );
}

const submissionColumns: Column<DeliverySubmission>[] = [
  { id: "status", header: "Status", render: (row) => <SubmissionStatusBadge status={row.status} /> },
  { id: "id", header: "Submission", render: (row) => <span className="font-mono text-[12px]">{row.submissionId}</span> },
  { id: "received", header: "Received", render: (row) => <RelativeTime value={row.receivedUtc} /> },
  { id: "records", header: "Records", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.recordCount}</span> },
  { id: "planned", header: "Planned", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.planned}</span> },
  { id: "delivered", header: "Delivered", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.delivered}</span> },
  { id: "unchanged", header: "Unchanged", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.skippedUnchanged}</span> },
  { id: "held", header: "Held", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.held}</span> },
  { id: "failed", header: "Failed", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.failed}</span> },
  { id: "drop", header: "Drop", render: (row) => <TruncatedText text={row.dropLocation} mono maxWidth={280} /> },
  { id: "error", header: "Error", render: (row) => <TruncatedText text={row.error} maxWidth={280} /> },
];

/** The counts of one submission as one compact line. */
export function SubmissionCounts({ submission }: { submission: DeliverySubmission }) {
  const parts: { label: string; value: number; className?: string }[] = [
    { label: "records", value: submission.recordCount },
    { label: "planned", value: submission.planned },
    { label: "delivered", value: submission.delivered, className: "text-success" },
    { label: "unchanged", value: submission.skippedUnchanged },
    { label: "blocked", value: submission.blocked },
    { label: "held", value: submission.held, className: submission.held > 0 ? "text-warning" : undefined },
    { label: "failed", value: submission.failed, className: submission.failed > 0 ? "text-destructive" : undefined },
  ];
  return (
    <div className="flex flex-wrap gap-x-3 gap-y-1 text-[13px] text-muted-foreground" data-testid="submission-counts">
      {parts.map((part) => (
        <span key={part.label} className={part.className}>
          <span className="font-mono tabular-nums">{part.value.toLocaleString()}</span> {part.label}
        </span>
      ))}
    </div>
  );
}

export function prettyJson(raw: string): string {
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}
