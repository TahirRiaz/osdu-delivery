import { useCallback, useEffect, useMemo, useState } from "react";
import { Link as RouterLink, useNavigate, useSearchParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Radar, RefreshCw, Trash2, Unlock } from "lucide-react";
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
  DELIVERY_RECORD_STATUSES, deliveryApi, deliveryRecordRoute, ledgerLabel,
  type DeliveryInterface, type DeliveryRecord, type DeliveryRecordFilter, type DeliveryRecordStatus, type DeliverySubmission,
  type DeliverySyncRequest,
} from "../../api/delivery";
import { CodeView } from "@/components/CodeView";
import { ConfirmDialog } from "@/components/ConfirmDialog";
import { CorrelationError } from "@/components/CorrelationError";
import { DataTable, type Column } from "@/components/DataTable";
import { FilterBar } from "@/components/FilterBar";
import { PagedTable } from "@/components/PagedTable";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import { BlockedBadge, RecordStatusBadge, SubmissionStatusBadge, VerifyOutcomeBadge } from "./DeliveryBadges";
import { FlowStatusBar } from "./FlowStatusBar";
import { InterfacePicker } from "./InterfacePicker";
import { PartitionPicker } from "./PartitionPicker";
import { CompactTime, OsduTarget, RecordIdentity, type RecordOrigin } from "./RecordCells";
import { useInterfaceChoice } from "./useInterfaceChoice";
import { RemovalDialog, type RemovalSelection } from "./RemovalDialog";
import { isTerminalTask, taskResultJson, useComputeTask } from "./useComputeTask";

const ALL = "all";

/** The URL filters that name records of one ledger, which the view of another interface or partition drops. */
const SCOPED_TO_LEDGER = ["submission", "delivered", "run"] as const;

/** The file and row a record's newest version came from: the queued version's while work waits, as the lookup names it. */
function originOf(row: DeliveryRecord): RecordOrigin {
  return row.pendingSourceFileName !== null
    ? { fileName: row.pendingSourceFileName, rowNumber: row.pendingSourceRowNumber }
    : { fileName: row.sourceFileName, rowNumber: row.sourceRowNumber };
}

const recordColumns: Column<DeliveryRecord>[] = [
  { id: "status", header: "Status", render: (row) => <RecordStatusBadge status={row.status} /> },
  // The record whole, with the file it came from and its last error under it; everything beside it is compact, so the
  // grid fits its panel, and OSDU comes last. The OSDU version is on the record's OSDU tab: in a row it is sixteen
  // digits that repeat the delivery time.
  {
    id: "label",
    header: "Record",
    fill: true,
    floor: 220,
    render: (row) => <RecordIdentity label={row.label} sourceKey={row.sourceKey} origin={originOf(row)} error={row.lastError} />,
  },
  {
    id: "delivered",
    header: "Delivered",
    render: (row) => <CompactTime value={row.lastDeliveredUtc} caption="Delivered" absent="not yet" className="text-[12px]" />,
  },
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
  { id: "target", header: "OSDU", render: (row) => <OsduTarget record={row} /> },
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
  // The chips point at records of the ledger that was showing; another interface's or partition's records are not those.
  const {
    rows, names, many, interfaceName, partitions, partition, headerPartition, outside, scope, ready, selectInterface, selectPartition,
  } = useInterfaceChoice(pipelineId, SCOPED_TO_LEDGER);
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

  // The overview shows the source in the partition in view: its counts are every interface's, with the breakdown below
  // them. The records and submissions views show one interface, because that is what their ledgers are.
  const statsOf = section === "overview" ? null : interfaceName;
  const stats = useQuery({
    queryKey: ["delivery", "stats", pipelineId, statsOf, partition],
    queryFn: () => deliveryApi.stats(pipelineId, { interfaceName: statsOf, partition }),
    enabled: ready && (section === "overview" || !many || interfaceName !== null),
    refetchInterval: 10000,
  });
  const submissions = useQuery({
    queryKey: ["delivery", "submissions", pipelineId, interfaceName, partition],
    queryFn: () => deliveryApi.submissions(pipelineId, 100, scope),
    enabled: ready && section !== "records" && (!many || interfaceName !== null),
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
    mutationFn: () => deliveryApi.probe(pipelineId, scope),
    onSuccess: (accepted) => setProbeTaskId(accepted.taskId),
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });
  const releaseAll = useMutation({
    mutationFn: () => deliveryApi.releaseFlow(pipelineId, undefined, scope),
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

  // A sync reads the records' rows from the ingestion tables and consolidates the ledger with them; it sends nothing to
  // OSDU, so it asks for no confirmation. Its run says what it found; the lists poll, and pick up what it wrote.
  const syncSource = useMutation({
    mutationFn: (request: DeliverySyncRequest | undefined) => deliveryApi.syncFlow(pipelineId, scope, request),
    onSuccess: (accepted, request) => {
      if (request !== undefined) {
        clearSelection();
      }

      toast.success("Timeline sync queued.", { action: { label: "Open run", onClick: () => navigate(`/runs/${accepted.runId}`) } });
    },
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });
  // A flow's sync reads one interface's ledger: the single form's, or the interface picked.
  const canSync = !many || interfaceName !== null;
  const syncTitle = canSync
    ? "Read every record's row from the ingestion table and consolidate the ledger with it: arrivals, changes the ledger never saw (planned by the next run), rows that are gone. Nothing is sent."
    : "Pick an interface: a sync reads one interface's records.";

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
  // What the ledger in view is called wherever the view names it: the flow, its interface, and its partition.
  const ledgerName = ledgerLabel(flowName, scope);

  return (
    <div className="flex flex-col gap-4" data-testid={`delivery-panel-${section}`}>
      {partitions.length > 0 && (
        <PartitionPicker
          partitions={partitions}
          partition={partition}
          onSelect={selectPartition}
          caption={outside !== null
            ? `this flow does not deliver to ${outside}, the workbench's partition; it is shown in ${partition}`
            : partitions.length === 1
              ? "the one partition this flow delivers to; it keeps its own ledger"
              : `of ${partitions.length} partitions; each keeps its own ledger, and every count and action below is this one's`}
        />
      )}
      {partitions.length === 0 && headerPartition !== null && (
        <p className="text-[13px] text-muted-foreground" data-testid="delivery-header-partition">
          Delivers to <span className="font-mono text-foreground">{headerPartition}</span>, the partition its target.headers name
          {outside !== null ? `; the workbench is in ${outside}, and nothing shown here is that partition's` : ""}.
        </p>
      )}
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
          {s === undefined ? (
            <Skeleton className="h-28 w-full rounded-lg" />
          ) : (
            <Card className="gap-3 rounded-lg p-3" data-testid="delivery-overview">
              <FlowStatusBar
                stats={s}
                actions={(
                  <>
                    <Button variant="outline" size="sm" onClick={() => probeTarget.mutate()} disabled={probeTarget.isPending} data-testid="delivery-probe">
                      <Radar />
                      Probe target
                    </Button>
                    <Button variant="outline" size="sm" onClick={() => setReleaseOpen(true)} disabled={blocked === 0} data-testid="delivery-release-all">
                      <Unlock />
                      Release blocked ({blocked})
                    </Button>
                    <Button variant="outline" size="sm" onClick={() => syncSource.mutate(undefined)} disabled={!canSync || syncSource.isPending} title={syncTitle} data-testid="delivery-sync-all">
                      <RefreshCw />
                      Sync timelines
                    </Button>
                  </>
                )}
              />
              {s.lastSubmission && (
                <div className="flex flex-wrap items-center gap-x-2 gap-y-1 border-t pt-2.5 text-[13px]" data-testid="delivery-last-submission">
                  <span className="font-medium">Last submission</span>
                  <SubmissionStatusBadge status={s.lastSubmission.status} />
                  <RouterLink
                    to={`/delivery/submissions/${s.lastSubmission.submissionId}`}
                    className="font-mono text-[12px] text-primary hover:underline"
                    title={s.lastSubmission.submissionId}
                  >
                    {s.lastSubmission.submissionId.slice(0, 8)}
                  </RouterLink>
                  <span className="text-muted-foreground">received <RelativeTime value={s.lastSubmission.receivedUtc} /></span>
                  <SubmissionCounts submission={s.lastSubmission} hideZeros />
                  <RouterLink to="?tab=submissions" className="ml-auto text-[12px] text-primary hover:underline" data-testid="delivery-kpi-submissions">
                    {`All ${s.submissions.toLocaleString()} ${s.submissions === 1 ? "submission" : "submissions"}`}
                  </RouterLink>
                </div>
              )}
            </Card>
          )}
          {many && (
            <Card className="gap-2 overflow-hidden rounded-lg p-0" data-testid="delivery-interfaces">
              <div className="flex flex-wrap items-baseline gap-2 px-3 pt-3 text-[13px]">
                <span className="font-medium">Interfaces</span>
                <span className="text-muted-foreground">
                  in the order a run takes them; every wave runs together, after the waves before it
                </span>
                {rows?.find((row) => row.orderProblem !== null)?.orderProblem && (
                  <span className="text-warning">
                    Order from <span className="font-mono">after:</span> alone: {rows.find((row) => row.orderProblem !== null)!.orderProblem}
                  </span>
                )}
              </div>
              <DataTable
                columns={interfaceColumns}
                rows={rows}
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
          {/* A flow that works in partitions has no records until the partition in view is known. */}
          {!ready ? <Skeleton className="h-40 w-full rounded-lg" /> : (
          <PagedTable
            queryKey={["delivery", "records", pipelineId, interfaceName, partition, search, status, drifted, contains, submissionFilter, deliveredFilter, runFilter]}
            fetchPage={(page, pageSize) => deliveryApi.records(pipelineId, {
              page, pageSize, interface: interfaceName ?? undefined, partition: partition ?? undefined, ...filter,
            })}
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
                  variant="outline"
                  size="sm"
                  className="ml-auto h-7"
                  onClick={() => syncSource.mutate(syncRequestFor(allMatching, filter, matched, selected))}
                  disabled={!canSync || syncSource.isPending}
                  title="Read the selected records' rows from the ingestion table and consolidate the ledger with them. Nothing is sent."
                  data-testid="delivery-sync-selected"
                >
                  <RefreshCw />
                  Sync timelines
                </Button>
                <Button
                  variant="destructive-outline"
                  size="sm"
                  className="h-7"
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
          )}
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
            flowScope={scope}
            flowName={ledgerName}
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
        message={`Release every held, failed and deleted record of ${ledgerName} back to pending? Records that still hold a rendered document are queued at once; the others are planned again on the next submission.`}
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
/**
 * What a sync of the selection reads: the ticked records, every record the filter matches, or, for all matching an empty
 * filter, every record of the interface, which the sync pages itself rather than naming each.
 */
function syncRequestFor(
  allMatching: boolean, filter: DeliveryRecordFilter, matched: number, selected: ReadonlySet<string>,
): DeliverySyncRequest | undefined {
  if (!allMatching) {
    return { keys: [...selected] };
  }

  const narrowed = Object.values(filter).some((value) => value !== undefined);
  return narrowed ? { filter, expected: matched } : undefined;
}

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

/**
 * The counts of one submission as one compact line. `hideZeros` keeps only the counts that hold something, for a line
 * that sits beside other facts: a submission that planned nothing then says so in two words instead of ten zeros.
 */
export function SubmissionCounts({ submission, hideZeros = false }: { submission: DeliverySubmission; hideZeros?: boolean }) {
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
  const shown = hideZeros ? parts.filter((part) => part.value > 0) : parts;
  if (shown.length === 0) {
    return <span className="text-[13px] text-muted-foreground" data-testid="submission-counts">nothing changed</span>;
  }

  return (
    <div className="flex flex-wrap gap-x-3 gap-y-1 text-[13px] text-muted-foreground" data-testid="submission-counts">
      {shown.map((part) => (
        <span key={part.label} className={part.className}>
          <span className="font-mono tabular-nums">{part.value.toLocaleString()}</span> {part.label}
        </span>
      ))}
    </div>
  );
}
