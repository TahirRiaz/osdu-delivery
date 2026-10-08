import { useState, type ReactNode } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ChevronRight, ChevronsUpDown, CircleX, ClipboardList, Download, Info, Loader2, Play } from "lucide-react";
import { toast } from "sonner";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { DataTable, type Column } from "@/components/DataTable";
import { EmptyState } from "@/components/EmptyState";
import { PageHeader } from "@/components/PageHeader";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { TruncatedText } from "@/components/TruncatedText";
import { isApiError } from "@/api/client";
import { TriggerRunDialog } from "@/features/runs/TriggerRunDialog";
import { formatDurationSeconds, parseUtc } from "@/lib/time";
import { cn } from "@/lib/utils";
import { inventoryApi, type Inventory, type InventoryCount, type InventoryDetail, type InventoryRun } from "../../../api/inventories";
import { KindText } from "../KindText";
import { useActivePartition } from "../activePartition";
import { RunRef } from "../DeliveryRefs";
import { ProblemView } from "../TemplateSheet";
import { ExplainTip, FindingChip, RunStatusPill, StandingGlyph } from "./InventoryBadges";
import { InventoryLookupBox } from "./InventoryLookup";
import { InventoryRecordsGrid } from "./InventoryRecordsGrid";
import {
  EVERY_ID, allCounts, counted, downloadInventory, findingVisual, inventoriesPayload, openingFinding, ownersSourceText, runOutcome, standingOf,
  type FindingPick, type InventoryRef, type InventoryView,
} from "./inventoryFormat";

/**
 * How often an inventory is read again while one of its runs is under way, so its outcome shows as it lands. Its counts are
 * read from every row it holds, and the rows change only while a run is going, so an inventory no run is working on is read
 * again only once a minute, to notice a run started elsewhere.
 */
const REFRESH_MS = 30000;

/** How often an inventory no run is working on is read again. */
const IDLE_REFRESH_MS = 60000;

/** The runs the Runs tab lists. */
const RUNS_SHOWN = 50;

/** The way back to every inventory, as the first words of the report. */
function InventoriesCrumb({ onBack }: { onBack: () => void }) {
  return (
    <>
      <button
        type="button"
        onClick={onBack}
        className="rounded-sm font-normal text-muted-foreground outline-none hover:text-foreground hover:underline focus-visible:underline"
        data-testid="inventories-back"
      >
        Inventories
      </button>
      <ChevronRight className="size-4 shrink-0 text-muted-foreground/60" />
    </>
  );
}

/** The other inventories of the partition, one click away from the one in view. */
function Switcher({ current, siblings, onOpen }: { current: InventoryRef; siblings: readonly Inventory[]; onOpen: (ref: InventoryRef) => void }) {
  if (siblings.filter((sibling) => sibling.inventoryId !== current.inventoryId || sibling.partition !== current.partition).length === 0) {
    return null;
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="icon" className="size-6 text-muted-foreground" aria-label="Open another inventory" data-testid="inventory-switcher">
          <ChevronsUpDown />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="max-h-80 min-w-72 overflow-y-auto">
        {siblings.map((sibling) => (
          <DropdownMenuItem
            key={`${sibling.partition}-${sibling.inventoryId}`}
            onSelect={() => onOpen({ partition: sibling.partition, inventoryId: sibling.inventoryId })}
            className={cn("gap-2", sibling.inventoryId === current.inventoryId && sibling.partition === current.partition && "bg-accent/60")}
            data-testid="inventory-switcher-item"
          >
            <StandingGlyph standing={standingOf(sibling)} />
            <span className="min-w-0 flex-1 truncate">{sibling.name}</span>
            <span className="shrink-0 truncate font-mono text-[11px] text-muted-foreground">{sibling.flowName}</span>
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/** A run in a line of facts: when it ended (or started, while it runs), what it did on hover, to copy. */
function RunFact({ label, run, at, testId }: { label: string; run: InventoryRun | undefined; at: string | undefined; testId: string }) {
  if (at === undefined && run === undefined) {
    return <span data-testid={testId}>never {label}</span>;
  }

  const when = run?.completedUtc ?? at ?? run?.startedUtc;
  const shown = <span data-testid={testId}>{label} {when === undefined ? "" : <RelativeTime value={when} absolute={false} />}</span>;
  if (run === undefined) {
    return shown;
  }

  const text = `Run ${run.inventoryRunId} (${run.operation}, ${run.status}) by ${run.actor}, through ${run.read}: ${runOutcome(run)}.${run.error === undefined ? "" : `\n${run.error}`}`;
  return (
    <ExplainTip title={`Last ${run.operation}`} text={text} testId={testId}>
      <button type="button" className="rounded-sm outline-none hover:text-foreground hover:underline focus-visible:underline">{shown}</button>
    </ExplainTip>
  );
}

/** The owners the last reconcile used and how it knew them, on hover, to copy. */
function OwnersFact({ detail }: { detail: InventoryDetail }) {
  const owners = detail.owners;
  const identities = owners?.identities ?? [];
  const text = owners === undefined
    ? "No reconcile has said which identities write as this estate yet. An id no ledger knows is an orphan when one of them created it, else foreign."
    : `${ownersSourceText(owners.source)}.${identities.length === 0 ? "" : "\n\n" + identities.map((o) => `${o.identity}: created ${counted(o.records, "id")} a ledger claims`).join("\n")}\n\nAn id no ledger knows is an orphan when one of them created it, else foreign.`;
  const label = identities.length === 0 ? "no owners" : identities.length === 1 ? `owner ${identities[0].identity}` : `${identities.length} owners`;
  return (
    <ExplainTip title="Owners" text={text} testId="inventory-owners">
      <button type="button" className="max-w-[22rem] truncate rounded-sm outline-none hover:text-foreground hover:underline focus-visible:underline" data-testid="inventory-owners">
        {label}
        {owners?.source === undefined ? "" : ` (${owners.source})`}
      </button>
    </ExplainTip>
  );
}

/** The whole of an inventory's ids, or those of the finding in view, downloaded as CSV. */
function ExportMenu({ inventory, finding }: { inventory: Inventory; finding: string | null }) {
  const [busy, setBusy] = useState(false);
  const download = async (what: string | null) => {
    setBusy(true);
    try {
      await downloadInventory(inventory, what);
      toast.success(`Downloaded the ${what === null ? "" : `${findingVisual(what).label.toLowerCase()} `}ids of ${inventory.name}`);
    } catch (error) {
      toast.error(`The ids of ${inventory.name} could not be downloaded: ${isApiError(error) ? error.detail ?? error.title : String(error)}`);
    } finally {
      setBusy(false);
    }
  };

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="outline" size="sm" disabled={busy} data-testid="inventory-export">
          {busy ? <Loader2 className="animate-spin" /> : <Download />}
          Export
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-72">
        {finding !== null && (
          <DropdownMenuItem onSelect={() => void download(finding)} data-testid="inventory-export-finding">
            <Download />
            The {findingVisual(finding).label.toLowerCase()} ids as CSV
          </DropdownMenuItem>
        )}
        <DropdownMenuItem onSelect={() => void download(null)} data-testid="inventory-export-all">
          <Download />
          Every id as CSV, with its finding
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/** The choice of every id, beside the findings: how many ids the inventory holds. */
function EveryIdChip({ total, selected, onSelect }: { total: number; selected: boolean; onSelect: () => void }) {
  return (
    <button
      type="button"
      onClick={onSelect}
      aria-pressed={selected}
      className={cn(
        "inline-flex h-7 items-center gap-1.5 whitespace-nowrap rounded-md border px-2 text-[12px] outline-none transition-colors focus-visible:ring-2 focus-visible:ring-ring/50",
        selected ? "border-primary/60 bg-accent text-foreground" : "border-border bg-muted/40 hover:bg-accent/60",
      )}
      data-testid="inventory-finding-all"
    >
      <span className="font-mono font-medium tabular-nums">{total.toLocaleString("en-US")}</span>
      <span className="text-muted-foreground">every id</span>
    </button>
  );
}

/** The inventory's builds and reconciles, newest first: what each read, changed and raised, who ran it and why one failed. */
function InventoryRuns({ inventory }: { inventory: Inventory }) {
  const runs = useQuery({
    queryKey: ["delivery", "inventories", "runs", inventory.partition, inventory.inventoryId],
    queryFn: () => inventoryApi.runs(inventory.partition, inventory.inventoryId, RUNS_SHOWN),
    refetchInterval: REFRESH_MS,
  });
  if (runs.isError) {
    return <ProblemView error={runs.error} testId="inventory-runs-error" />;
  }

  const columns: Column<InventoryRun>[] = [
    { id: "run", header: "Run", render: (row) => <span className="font-mono text-[12px] tabular-nums">{row.inventoryRunId}</span> },
    { id: "operation", header: "Operation", render: (row) => <span className="font-mono text-[12px]">{row.operation}</span> },
    {
      id: "status",
      header: "Status",
      render: (row) => (row.error === undefined
        ? <RunStatusPill status={row.status} testId="inventory-run-status" />
        : (
          <ExplainTip title="Why it failed" text={row.error} testId="inventory-run-error">
            <span className="inline-flex"><RunStatusPill status={row.status} testId="inventory-run-status" /></span>
          </ExplainTip>
        )),
    },
    { id: "started", header: "Started", render: (row) => <span className="whitespace-nowrap text-[12px]"><RelativeTime value={row.startedUtc} absolute={false} /></span> },
    {
      id: "took",
      header: "Took",
      align: "right",
      render: (row) => (row.completedUtc === undefined
        ? <span className="text-[12px] text-muted-foreground">-</span>
        : (
          <span className="font-mono text-[12px] tabular-nums">
            {formatDurationSeconds(Math.max(0, (parseUtc(row.completedUtc).getTime() - parseUtc(row.startedUtc).getTime()) / 1000))}
          </span>
        )),
    },
    { id: "outcome", header: "What it did", fill: true, floor: 260, render: (row) => <TruncatedText text={runOutcome(row, false)} maxWidth={640} /> },
    {
      id: "raised",
      header: "Raised",
      align: "right",
      render: (row) => (row.raised === undefined
        ? <span className="text-[12px] text-muted-foreground">-</span>
        : <span className={cn("font-mono text-[12px] tabular-nums", row.raised === 0 && "text-muted-foreground")}>{row.raised.toLocaleString("en-US")}</span>),
    },
    { id: "actor", header: "By", render: (row) => <TruncatedText text={row.actor} mono maxWidth={180} /> },
    { id: "platform", header: "", align: "right", render: (row) => (row.runId === undefined ? null : <RunRef runId={row.runId} />) },
  ];

  return (
    <DataTable
      columns={columns}
      rows={runs.data}
      rowKey={(row) => row.inventoryRunId}
      emptyMessage="No build or reconcile has run yet."
      data-testid="inventory-runs-table"
    />
  );
}

/**
 * One inventory of a partition, on the whole width of the page. Its heading is one block: the way back to every inventory,
 * its name and where it stands, the others a click away, the lookup, Export and Run pipeline; under it, in two lines, the
 * facts it is read against (its ids and how many are raised, its last build and reconcile and the owners they used, each
 * explained on hover) and what it reads. Then what its newest run asks of the reader, and its tabs: its ids by finding,
 * picked from its counts and paged in place in a grid that scrolls inside the page, and its runs. The finding and the tab
 * live in the address, so a link lands on the same view.
 */
export function InventoryReport({ reference, siblings, finding, onFinding, view, onView, onOpen }: {
  reference: InventoryRef;
  /** The inventories the switcher lists. */
  siblings: readonly Inventory[];
  finding: FindingPick;
  onFinding: (finding: string) => void;
  view: InventoryView;
  onView: (view: InventoryView) => void;
  /** Opens another inventory, or every inventory for null. */
  onOpen: (ref: InventoryRef | null) => void;
}) {
  const [launching, setLaunching] = useState(false);
  const [active] = useActivePartition();
  const detail = useQuery({
    queryKey: ["delivery", "inventories", "detail", reference.partition, reference.inventoryId],
    queryFn: () => inventoryApi.inventory(reference.partition, reference.inventoryId),
    refetchInterval: (query) => (query.state.data?.inventory.latest?.status === "running" ? REFRESH_MS : IDLE_REFRESH_MS),
  });

  if (detail.isError) {
    return (
      <div className="flex flex-col gap-3" data-testid="inventory-report">
        <PageHeader title={<span className="flex items-center gap-1.5"><InventoriesCrumb onBack={() => onOpen(null)} />Inventory {reference.inventoryId}</span>} />
        <ProblemView error={detail.error} testId="inventory-error" />
      </div>
    );
  }

  if (detail.data === undefined) {
    return (
      <div className="flex flex-col gap-3" data-testid="inventory-report">
        <Skeleton className="h-14 w-full rounded-lg" />
        <Skeleton className="h-96 w-full rounded-lg" />
      </div>
    );
  }

  const data = detail.data;
  const inventory = data.inventory;
  const standing = standingOf(inventory, data.declared !== false);
  const counts = allCounts(data.counts);
  const chosen = finding === EVERY_ID ? null : finding ?? openingFinding(counts);
  // What ids have comes first, in report order; the raised findings no id has follow, quiet, so a narrow page wraps them alone.
  const listed = counts.filter((count) => count.raised || count.count > 0 || count.finding === chosen);
  const shownCounts = [...listed.filter((count) => count.count > 0), ...listed.filter((count) => count.count === 0)];
  const total = chosen === null ? data.ids : counts.find((count) => count.finding === chosen)?.count ?? null;
  const dot = <span className="text-muted-foreground/50" aria-hidden>·</span>;
  const latest = inventory.latest;
  const failedAfter = latest !== undefined && latest.status === "failed" && latest.inventoryRunId !== inventory.lastReconcileRunId ? latest : undefined;
  const canRun = data.pipelineId !== undefined && data.repoId !== undefined && data.declared !== false;
  const built = inventory.lastBuiltUtc !== undefined || inventory.lastReconcileRunId !== undefined;

  const chips: ReactNode = (
    <>
      <EveryIdChip total={data.ids} selected={chosen === null} onSelect={() => onFinding(EVERY_ID)} />
      {shownCounts.map((count: InventoryCount) => (
        <FindingChip
          key={count.finding}
          count={count}
          selected={chosen === count.finding}
          onSelect={() => onFinding(count.finding)}
          testId={`inventory-finding-${count.finding}`}
        />
      ))}
    </>
  );

  return (
    <div className="flex min-w-0 flex-col gap-3" data-testid="inventory-report" data-inventory={inventory.name}>
      <PageHeader
        title={(
          <span className="flex min-w-0 flex-wrap items-center gap-x-1.5 gap-y-1">
            <InventoriesCrumb onBack={() => onOpen(null)} />
            <StandingGlyph standing={standing} testId="inventory-standing" />
            <span className="min-w-0 truncate" data-testid="inventory-name">{inventory.name}</span>
            <Switcher current={reference} siblings={siblings} onOpen={onOpen} />
            {data.description !== undefined && (
              <RichTooltip title="What it is for" body={data.description}>
                <Info className="size-4 shrink-0 text-muted-foreground" aria-label="What the inventory is for" data-testid="inventory-description" />
              </RichTooltip>
            )}
          </span>
        )}
        subtitle={(
          <div className="flex flex-col gap-0.5">
            <div className="flex flex-wrap items-center gap-x-1.5 gap-y-0.5 text-[13px]" data-testid="inventory-summary">
              <span><span className="font-mono font-medium tabular-nums text-foreground">{data.ids.toLocaleString("en-US")}</span> {data.ids === 1 ? "id" : "ids"}</span>
              {dot}
              <span className={data.raised > 0 ? "text-foreground" : undefined} data-testid="inventory-summary-raised">
                <span className="font-mono font-medium tabular-nums">{data.raised.toLocaleString("en-US")}</span> raised
              </span>
              {dot}
              <RunFact label="built" run={data.lastBuild} at={inventory.lastBuiltUtc} testId="inventory-summary-built" />
              {dot}
              <RunFact label="reconciled" run={data.lastReconcile} at={inventory.lastReconciledUtc} testId="inventory-summary-reconciled" />
              {dot}
              <OwnersFact detail={data} />
            </div>
            <div className="flex min-w-0 flex-wrap items-center gap-x-1.5 gap-y-0.5 text-[12px]">
              <span className="min-w-0 max-w-[26rem]"><KindText kind={inventory.kind} /></span>
              {inventory.query !== undefined && (
                <RichTooltip title="Narrowed by" body={inventory.query} mono>
                  <span className="shrink-0 rounded-sm border border-border px-1 text-[10.5px]">query</span>
                </RichTooltip>
              )}
              {dot}
              <span>through {inventory.read}, {inventory.versions === "all" ? "every version" : "the latest version"}</span>
              {dot}
              <span>{inventory.partition === active ? "kept by" : `in ${inventory.partition}, kept by`}</span>
              {data.pipelineId === undefined
                ? <span className="font-mono text-foreground">{inventory.flowName}</span>
                : <Link to={`/pipelines/${data.pipelineId}?tab=inventories`} className="font-mono text-foreground hover:underline">{inventory.flowName}</Link>}
              {data.declared === false && <span>(no longer declared)</span>}
            </div>
          </div>
        )}
        actions={(
          <>
            {!built && <InventoryLookupBox />}
            <ExportMenu inventory={inventory} finding={chosen} />
            {canRun && (
              <RichTooltip body={`Runs ${inventory.flowName} with this inventory picked: a build reads every id of its kind again and reconciles it; the dialog can pick a reconcile, a plan, or the flow's other inventories.`}>
                <Button size="sm" onClick={() => setLaunching(true)} disabled={latest?.status === "running"} data-testid="inventory-run">
                  <Play />
                  Run pipeline
                </Button>
              </RichTooltip>
            )}
          </>
        )}
      />

      {failedAfter !== undefined && (
        <Alert variant="destructive" data-testid="inventory-failed">
          <CircleX />
          <AlertTitle className="flex flex-wrap items-center gap-x-1.5">
            The newest {failedAfter.operation} failed <RelativeTime value={failedAfter.completedUtc ?? failedAfter.startedUtc} absolute={false} />
          </AlertTitle>
          <AlertDescription>
            <ExplainTip title="Why it failed" text={failedAfter.error ?? "The run kept no reason."} testId="inventory-failed-why">
              <span className="line-clamp-1 break-all">{failedAfter.error ?? "The run kept no reason."}</span>
            </ExplainTip>
          </AlertDescription>
        </Alert>
      )}

      {!built
        ? (
          <Card className="gap-0 rounded-lg p-0">
            <EmptyState
              icon={<ClipboardList />}
              title={`${inventory.name} has not been built in ${inventory.partition}`}
              description="A build reads every id its kind holds, with who created and changed each, and compares every id with the ledgers of the partition."
              action={canRun ? <Button size="sm" onClick={() => setLaunching(true)} data-testid="inventory-run-first"><Play />Run pipeline</Button> : undefined}
              data-testid="inventory-not-built"
            />
          </Card>
        )
        : (
          <Tabs value={view} onValueChange={(next) => onView(next as InventoryView)} className="min-w-0 gap-2">
            <div className="flex flex-wrap items-end justify-between gap-2 border-b border-border">
              <TabsList variant="line" data-testid="inventory-tabs">
                <TabsTrigger value="ids" data-testid="inventory-tab-ids">Ids</TabsTrigger>
                <TabsTrigger value="runs" data-testid="inventory-tab-runs">Runs</TabsTrigger>
              </TabsList>
              <div className="pb-1.5">
                <InventoryLookupBox />
              </div>
            </div>
            <TabsContent value="ids">
              <InventoryRecordsGrid key={chosen ?? EVERY_ID} inventory={inventory} finding={chosen} total={total} leading={chips} />
            </TabsContent>
            <TabsContent value="runs">
              <InventoryRuns inventory={inventory} />
            </TabsContent>
          </Tabs>
        )}

      {launching && canRun && (
        <TriggerRunDialog
          open
          onClose={() => setLaunching(false)}
          repoId={data.repoId}
          flowName={inventory.flowName}
          flowId={data.pipelineId}
          flowKind="inventory"
          initialOperation="build"
          initialValues={null}
          initialPayload={inventoriesPayload([inventory.name])}
        />
      )}
    </div>
  );
}
