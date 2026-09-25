import { useCallback, useEffect, useMemo, useState } from "react";
import { Link as RouterLink, useNavigate, useSearchParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Radar, Trash2, Unlock } from "lucide-react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { Label } from "@/components/ui/label";
import { isApiError } from "@/api/client";
import {
  DELIVERY_RECORD_STATUSES, deliveryApi, deliveryRecordRoute,
  type DeliveryInterface, type DeliveryRecord, type DeliveryRecordFilter, type DeliveryRecordStatus, type DeliverySubmission,
} from "../../api/delivery";
import { CodeView } from "@/components/CodeView";
import { ConfirmDialog } from "@/components/ConfirmDialog";
import { CorrelationError } from "@/components/CorrelationError";
import { DataTable, type Column } from "@/components/DataTable";
import { FilterBar } from "@/components/FilterBar";
import { KpiCard } from "@/components/KpiCard";
import { PagedTable } from "@/components/PagedTable";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import { BlockedBadge, RecordStatusBadge, SubmissionStatusBadge, VerifyOutcomeBadge } from "./DeliveryBadges";
import { InterfacePicker } from "./InterfacePicker";
import { OpenInOsduLink } from "./OpenInOsduLink";
import { useInterfaceChoice } from "./useInterfaceChoice";
import { RemovalDialog, type RemovalSelection } from "./RemovalDialog";
import { isTerminalTask, taskResultJson, useComputeTask } from "./useComputeTask";

const ALL = "all";

/** The URL filters that name records of one interface, which another interface's view drops. */
const SCOPED_TO_INTERFACE = ["submission", "delivered", "run"] as const;

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
  { id: "osdu", header: "", render: (row) => (row.targetId !== null && row.status !== "deleted" ? <OpenInOsduLink record={row} /> : null) },
];

/** The flow's stats strip, its submissions, and its searchable records, with the flow-level interventions. */
export function DeliveryFlowPanel({ pipelineId, flowName, section }: { pipelineId: string; flowName: string; section: "overview" | "records" | "submissions" }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [searchParams, setSearchParams] = useSearchParams();
  // A submission or run page links here scoped to the records it touched; clearing the chip widens the list again.
  // `submission` is the records a submission last planned, `delivered` the records it delivered, which stay its own
  // however many submissions touch them afterwards.
  const submissionFilter = searchParams.get("submission");
  const deliveredFilter = searchParams.get("delivered");
  const runFilter = searchParams.get("run");
  // The chips point at records of the interface that was showing; another interface's records are not those.
  const { interfaces, names, many, interfaceName, selectInterface } = useInterfaceChoice(pipelineId, SCOPED_TO_INTERFACE);
  const [search, setSearch] = useState("");
  const [status, setStatus] = useState<string>(ALL);
  const [drifted, setDrifted] = useState(false);
  const [contains, setContains] = useState(false);
  const [releaseOpen, setReleaseOpen] = useState(false);
  const [probeTaskId, setProbeTaskId] = useState<string | null>(null);
  // Ticked rows survive paging and filter changes because the page owns them, not the table. `allMatching` is the
  // other selection: not a list of keys but the filter itself, resolved when the removal runs.
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [allMatching, setAllMatching] = useState(false);
  const [matched, setMatched] = useState(0);
  const [matchedCapped, setMatchedCapped] = useState(false);
  const [removeOpen, setRemoveOpen] = useState(false);
  const [removalTaskId, setRemovalTaskId] = useState<string | null>(null);

  const filter = useMemo<DeliveryRecordFilter>(() => ({
    search: search.trim() === "" ? undefined : search.trim(),
    mode: contains ? "contains" : undefined,
    status: status === ALL ? undefined : (status as DeliveryRecordStatus),
    drifted: drifted || undefined,
    submissionId: submissionFilter ?? undefined,
    deliveredBy: deliveredFilter ?? undefined,
    runId: runFilter ?? undefined,
  }), [search, contains, status, drifted, submissionFilter, deliveredFilter, runFilter]);

  const clearSelection = useCallback(() => {
    setSelected(new Set());
    setAllMatching(false);
  }, []);

  // A filter change makes the ticked keys mean something different from what the operator sees, and an
  // all-matching selection would silently re-aim at the new filter. Both are dropped.
  const filterKey = JSON.stringify(filter);
  const [shownFilter, setShownFilter] = useState(filterKey);
  if (filterKey !== shownFilter) {
    setShownFilter(filterKey);
    clearSelection();
  }

  const onPageLoaded = useCallback((_rows: DeliveryRecord[], total: number, capped: boolean) => {
    setMatched(total);
    setMatchedCapped(capped);
  }, []);
  const removal = useComputeTask(removalTaskId);

  // The overview shows the source: its counts are every interface's, with the breakdown below them. The records and
  // submissions views show one interface, because that is what their ledgers are.
  const statsOf = section === "overview" ? null : interfaceName;
  const stats = useQuery({
    queryKey: ["delivery", "stats", pipelineId, statsOf],
    queryFn: () => deliveryApi.stats(pipelineId, statsOf),
    enabled: section === "overview" || !many || interfaceName !== null,
    refetchInterval: 10000,
  });
  const submissions = useQuery({
    queryKey: ["delivery", "submissions", pipelineId, interfaceName],
    queryFn: () => deliveryApi.submissions(pipelineId, 100, interfaceName),
    enabled: section !== "records" && (!many || interfaceName !== null),
    refetchInterval: 10000,
  });
  const probe = useComputeTask(probeTaskId);

  // A removal that finished on a node changed the ledger for every record it touched: refetch the list and the
  // stats once the task settles, so the page shows what it did without a manual reload.
  const finishedRemoval = isTerminalTask(removal.data) ? removal.data!.taskId : null;
  useEffect(() => {
    if (finishedRemoval !== null) {
      void queryClient.invalidateQueries({ queryKey: ["delivery"] });
    }
  }, [finishedRemoval, queryClient]);

  const probeTarget = useMutation({
    mutationFn: () => deliveryApi.probe(pipelineId, interfaceName),
    onSuccess: (accepted) => setProbeTaskId(accepted.taskId),
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });
  const releaseAll = useMutation({
    mutationFn: () => deliveryApi.releaseFlow(pipelineId, undefined, interfaceName),
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
  const probeJson = isTerminalTask(probeResult) ? taskResultJson(probeResult) : null;
  const removalJson = isTerminalTask(removal.data) ? taskResultJson(removal.data) : null;

  return (
    <div className="flex flex-col gap-4" data-testid={`delivery-panel-${section}`}>
      {many && (
        <InterfacePicker
          names={names}
          interfaceName={interfaceName}
          onSelect={selectInterface}
          caption={section === "overview"
            ? `of ${names.length} interfaces; the counts below are the whole source`
            : `of ${names.length} interfaces of ${flowName}`}
        />
      )}

      {section === "overview" && (
        <>
          <div className="flex flex-wrap items-center gap-2">
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
            <div className="grid grid-cols-2 gap-3 md:grid-cols-5 xl:grid-cols-9" data-testid="delivery-stats">
              <KpiCard label="Records" value={s.total} testId="delivery-kpi-total" />
              <KpiCard label="Delivered" value={s.delivered} color="success" caption={`${s.deliveredLast24h} in the last 24h`} testId="delivery-kpi-delivered" />
              <KpiCard label="Pending" value={s.pending + s.delivering} color="info" caption={s.delivering > 0 ? `${s.delivering} delivering now` : undefined} testId="delivery-kpi-pending" />
              <KpiCard
                label="Waiting"
                value={s.waiting}
                color={s.waiting > 0 ? "info" : undefined}
                caption={s.waiting > 0 ? "for records they refer to" : undefined}
                testId="delivery-kpi-waiting"
              />
              <KpiCard label="Held" value={s.held} color={s.held > 0 ? "warning" : undefined} testId="delivery-kpi-held" />
              <KpiCard label="Failed" value={s.failed} color={s.failed > 0 ? "error" : undefined} testId="delivery-kpi-failed" />
              <KpiCard label="Deleted" value={s.deleted} testId="delivery-kpi-deleted" />
              <KpiCard label="Drifted" value={s.drifted} color={s.drifted > 0 ? "warning" : undefined} caption={s.lastVerifiedUtc ? "since the last verify" : "never verified"} testId="delivery-kpi-drifted" />
              <KpiCard label="Submissions" value={s.submissions} testId="delivery-kpi-submissions" />
            </div>
          )}
          {many && (
            <Card className="gap-2 overflow-hidden rounded-lg p-0" data-testid="delivery-interfaces">
              <div className="flex flex-wrap items-baseline gap-2 px-3 pt-3 text-[13px]">
                <span className="font-medium">Interfaces</span>
                <span className="text-muted-foreground">
                  in the order a run takes them; every wave runs together, after the waves before it
                </span>
                {interfaces.data?.find((row) => row.orderProblem !== null)?.orderProblem && (
                  <span className="text-warning">
                    Order from <span className="font-mono">after:</span> alone: {interfaces.data.find((row) => row.orderProblem !== null)!.orderProblem}
                  </span>
                )}
              </div>
              <DataTable
                columns={interfaceColumns}
                rows={interfaces.data}
                rowKey={(row) => row.interface ?? row.flowId}
                onRowClick={(row) => row.interface !== null && selectInterface(row.interface)}
                emptyMessage="This source declares no interfaces."
                data-testid="delivery-interfaces-table"
              />
            </Card>
          )}
          {probeTaskId !== null && (
            <Card className="gap-2 rounded-lg p-3" data-testid="delivery-probe-result">
              <div className="flex items-center gap-2 text-[13px] font-medium">
                Target probe
                <Badge variant="outline">{probeResult?.status ?? "queued"}</Badge>
                {probeResult?.claimedByNode && <span className="font-mono text-[11px] text-muted-foreground">{probeResult.claimedByNode}</span>}
              </div>
              {probeResult?.error && <p className="text-[13px] text-destructive">{probeResult.error}</p>}
              {probeJson !== null && (
                <CodeView value={probeJson} language="json" height={180} data-testid="delivery-probe-json" />
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
                last planned by submission {submissionFilter.slice(0, 8)}: clear
              </Button>
            )}
            {deliveredFilter && (
              <Button variant="outline" size="sm" className="h-8" onClick={() => setSearchParams((current) => { const next = new URLSearchParams(current); next.delete("delivered"); return next; })} data-testid="delivery-records-clear-delivered">
                delivered by submission {deliveredFilter.slice(0, 8)}: clear
              </Button>
            )}
            {runFilter && (
              <Button variant="outline" size="sm" className="h-8" onClick={() => setSearchParams((current) => { const next = new URLSearchParams(current); next.delete("run"); return next; })} data-testid="delivery-records-clear-run">
                run {runFilter.slice(0, 8)}: clear
              </Button>
            )}
          </FilterBar>
          <PagedTable
            queryKey={["delivery", "records", pipelineId, interfaceName, search, status, drifted, contains, submissionFilter, deliveredFilter, runFilter]}
            fetchPage={(page, pageSize) => deliveryApi.records(pipelineId, { page, pageSize, interface: interfaceName ?? undefined, ...filter })}
            columns={recordColumns}
            rowKey={(row) => row.deliveryKey}
            onRowClick={(row) => navigate(deliveryRecordRoute(row))}
            pollMs={selected.size > 0 || allMatching ? undefined : 10000}
            onPageLoaded={onPageLoaded}
            selection={{ selected, onChange: (next) => { setSelected(next); setAllMatching(false); } }}
            toolbar={(selected.size > 0 || allMatching) && (
              <div className="flex flex-wrap items-center gap-2 border-b border-border bg-accent/40 px-3 py-1.5" data-testid="delivery-selection-bar">
                <span className="text-[13px] font-medium tabular-nums" data-testid="delivery-selection-count">
                  {allMatching
                    ? `All ${matched.toLocaleString()} matching records selected`
                    : `${selected.size.toLocaleString()} selected`}
                </span>
                {!allMatching && matched > selected.size && !matchedCapped && (
                  <Button variant="link" size="sm" className="h-6 px-0 text-[13px]" onClick={() => setAllMatching(true)} data-testid="delivery-select-all-matching">
                    Select all {matched.toLocaleString()} matching
                  </Button>
                )}
                {!allMatching && matchedCapped && (
                  <span className="text-[13px] text-muted-foreground" data-testid="delivery-select-all-capped">
                    More than {matched.toLocaleString()} match; narrow the filter to remove them together
                  </span>
                )}
                <Button variant="link" size="sm" className="h-6 px-0 text-[13px] text-muted-foreground" onClick={clearSelection} data-testid="delivery-clear-selection">
                  Clear
                </Button>
                <Button
                  variant="destructive-outline"
                  size="sm"
                  className="ml-auto h-7"
                  onClick={() => setRemoveOpen(true)}
                  data-testid="delivery-remove-selected"
                >
                  <Trash2 />
                  Remove from OSDU
                </Button>
              </div>
            )}
            emptyMessage="No records match. A flow's records appear here once its first submission has been planned."
            data-testid="delivery-records-table"
          />
          {removalTaskId !== null && (
            <Card className="gap-2 rounded-lg p-3" data-testid="delivery-removal-result">
              <div className="flex items-center gap-2 text-[13px] font-medium">
                Removal
                <Badge variant="outline">{removal.data?.status ?? "queued"}</Badge>
                {removal.data?.claimedByNode && <span className="font-mono text-[11px] text-muted-foreground">{removal.data.claimedByNode}</span>}
              </div>
              {removal.data?.error && <p className="text-[13px] text-destructive">{removal.data.error}</p>}
              {removalJson !== null && (
                <CodeView value={removalJson} language="json" height={260} data-testid="delivery-removal-json" />
              )}
            </Card>
          )}
          <RemovalDialog
            open={removeOpen}
            onClose={() => setRemoveOpen(false)}
            pipelineId={pipelineId}
            interfaceName={interfaceName}
            flowName={interfaceName === null ? flowName : `${flowName} / ${interfaceName}`}
            selection={selectionFor(allMatching, filter, matched, selected)}
            onQueued={(accepted) => {
              clearSelection();
              setRemovalTaskId(accepted.taskId);
              toast.success(`Removal of ${accepted.records.toLocaleString()} record(s) queued on a node.`);
            }}
          />
        </>
      )}

      {section === "submissions" && (
        <DataTable
          columns={submissionColumns}
          rows={submissions.data}
          rowKey={(row) => row.submissionId}
          onRowClick={(row) => navigate(`/delivery/submissions/${row.submissionId}`)}
          emptyMessage="No submissions yet. A submission is one plan of this flow over its ingestion tables."
          data-testid="delivery-submissions-table"
        />
      )}

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

/**
 * The selection the removal dialog acts on. Ticked keys travel as keys; "all matching" travels as the filter
 * itself with the count that was shown, so the removal covers records no page ever rendered and the API can
 * refuse it if that count has moved.
 */
function selectionFor(
  allMatching: boolean, filter: DeliveryRecordFilter, matched: number, selected: ReadonlySet<string>,
): RemovalSelection {
  return allMatching ? { kind: "filter", filter, expected: matched } : { kind: "keys", keys: [...selected] };
}

/** One line per interface of a source: where its records go, when it runs, and how its ledger stands. */
const interfaceColumns: Column<DeliveryInterface>[] = [
  {
    id: "interface",
    header: "Interface",
    render: (row) => (
      <div className="flex min-w-0 flex-col">
        <span className="font-medium">{row.interface ?? "(single)"}</span>
        <span className="truncate font-mono text-[11px] text-muted-foreground">{row.mapping}</span>
      </div>
    ),
  },
  { id: "wave", header: "Wave", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.wave}</span> },
  {
    id: "route",
    header: "Route",
    render: (row) => (
      <div className="flex min-w-0 flex-col">
        <Badge variant="outline" className="w-fit font-mono text-[11px]">{row.route}</Badge>
        {row.routeReason && <TruncatedText text={row.routeReason} maxWidth={260} />}
      </div>
    ),
  },
  { id: "kind", header: "Kind", render: (row) => <TruncatedText text={row.kind} maxWidth={260} /> },
  {
    id: "waits",
    header: "Waits for",
    render: (row) => (
      <span className="text-[13px] text-muted-foreground">
        {(row.waitsFor ?? []).length === 0 ? "nothing" : (row.waitsFor ?? []).map((wait) => wait.interface).join(", ")}
      </span>
    ),
  },
  { id: "total", header: "Records", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.stats.total.toLocaleString()}</span> },
  { id: "delivered", header: "Delivered", align: "right", render: (row) => <span className="font-mono tabular-nums text-success">{row.stats.delivered.toLocaleString()}</span> },
  { id: "pending", header: "Pending", align: "right", render: (row) => <span className="font-mono tabular-nums">{(row.stats.pending + row.stats.delivering).toLocaleString()}</span> },
  { id: "waiting", header: "Waiting", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.stats.waiting.toLocaleString()}</span> },
  {
    id: "blocked",
    header: "Blocked",
    align: "right",
    render: (row) => {
      const blocked = row.stats.held + row.stats.failed + row.stats.deleted;
      return <span className={`font-mono tabular-nums${blocked > 0 ? " text-warning" : ""}`}>{blocked.toLocaleString()}</span>;
    },
  },
];

const submissionColumns: Column<DeliverySubmission>[] = [
  { id: "status", header: "Status", render: (row) => <SubmissionStatusBadge status={row.status} /> },
  {
    id: "id",
    header: "Submission",
    // The sending system's own name for the work sits above this ledger's id, because it is what an operator holding a
    // filename or a ticket recognises; the id stays, because it is what every other page is keyed by.
    render: (row) => (
      <div className="flex min-w-0 flex-col">
        <span className="font-mono text-[12px] text-muted-foreground">{row.submissionId}</span>
      </div>
    ),
  },
  { id: "kind", header: "Read", render: (row) => <Badge variant="outline" className="font-mono text-[11px]">{row.kind}</Badge> },
  { id: "received", header: "Received", render: (row) => <RelativeTime value={row.receivedUtc} /> },
  { id: "records", header: "Records", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.recordCount}</span> },
  { id: "planned", header: "Planned", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.planned}</span> },
  { id: "delivered", header: "Delivered", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.delivered}</span> },
  { id: "unchanged", header: "Unchanged", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.skippedUnchanged + row.unchangedAtPush}</span> },
  { id: "stale", header: "Stale", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.skippedStale}</span> },
  { id: "held", header: "Held", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.held}</span> },
  { id: "failed", header: "Failed", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.failed}</span> },
  { id: "waiting", header: "Waiting", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.waiting}</span> },
  { id: "error", header: "Error", render: (row) => <TruncatedText text={row.error} maxWidth={280} /> },
];

/** The counts of one submission as one compact line. */
export function SubmissionCounts({ submission }: { submission: DeliverySubmission }) {
  const parts: { label: string; value: number; className?: string }[] = [
    { label: "records", value: submission.recordCount },
    { label: "planned", value: submission.planned },
    { label: "delivered", value: submission.delivered, className: "text-success" },
    { label: "unchanged", value: submission.skippedUnchanged + submission.unchangedAtPush },
    { label: "awaiting approval", value: submission.awaitingApproval, className: submission.awaitingApproval > 0 ? "text-warning" : undefined },
    { label: "stale", value: submission.skippedStale },
    { label: "blocked", value: submission.blocked },
    { label: "held", value: submission.held, className: submission.held > 0 ? "text-warning" : undefined },
    { label: "failed", value: submission.failed, className: submission.failed > 0 ? "text-destructive" : undefined },
    { label: "waiting", value: submission.waiting, className: submission.waiting > 0 ? "text-info" : undefined },
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
