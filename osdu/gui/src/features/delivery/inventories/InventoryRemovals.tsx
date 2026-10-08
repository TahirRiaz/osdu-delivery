import { useCallback, useEffect, useMemo, useState } from "react";
import { keepPreviousData, useInfiniteQuery } from "@tanstack/react-query";
import { CircleCheck, CircleSlash, CircleX, SkipForward, type LucideIcon } from "lucide-react";
import { DataTable, type Column } from "@/components/DataTable";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import { useOwnedPanel } from "@/layout/workbench/useOwnedPanel";
import { cn } from "@/lib/utils";
import {
  INVENTORY_REMOVAL_OUTCOMES, inventoryApi, type Inventory, type InventoryRemoval, type InventoryRemovalItem, type InventoryRemovalOutcome,
} from "../../../api/inventories";
import { RecordName } from "../RecordName";
import { RunRef } from "../DeliveryRefs";
import { ProblemView } from "../TemplateSheet";
import { GridFooter } from "../dimensions/DimensionGrid";
import { ExplainTip, FindingGlyph, RunStatusPill } from "./InventoryBadges";
import { findingVisual } from "./inventoryFormat";

/** The workbench's bottom panel content the removals raise: a removal's number after this prefix. */
const PANEL = "inventory-removal:";

/** The ids one page of a removal's outcomes reads. */
const PAGE = 200;

/** How often a running removal's outcomes are read again, so what it reached shows as it lands. */
const RUNNING_REFRESH_MS = 5000;

/** How an outcome is drawn: its glyph in its tone and its name. */
const OUTCOME_VISUALS: Record<InventoryRemovalOutcome, { icon: LucideIcon; tone: string; label: string }> = {
  removed: { icon: CircleCheck, tone: "text-success", label: "Removed" },
  gone: { icon: CircleSlash, tone: "text-muted-foreground", label: "Already gone" },
  skipped: { icon: SkipForward, tone: "text-warning", label: "Skipped" },
  failed: { icon: CircleX, tone: "text-destructive", label: "Failed" },
};

function outcomeVisual(outcome: string) {
  return OUTCOME_VISUALS[outcome as InventoryRemovalOutcome] ?? { icon: CircleSlash, tone: "text-muted-foreground", label: outcome };
}

/** How many ids of a removal came to each outcome. */
function tallyOf(removal: InventoryRemoval): Record<InventoryRemovalOutcome, number> {
  return { removed: removal.removed, gone: removal.gone, skipped: removal.skipped, failed: removal.failed };
}

/** A removal's outcomes in a line: each that any id came to, glyph and count, its name on hover; a quiet dash for none yet. */
function Tally({ removal, testId }: { removal: InventoryRemoval; testId: string }) {
  const tally = tallyOf(removal);
  const reached = INVENTORY_REMOVAL_OUTCOMES.filter((outcome) => tally[outcome] > 0);
  if (reached.length === 0) {
    return <span className="text-[12px] text-muted-foreground" data-testid={testId}>none reached yet</span>;
  }

  return (
    <span className="inline-flex items-center gap-x-2.5 whitespace-nowrap text-[12px]" data-testid={testId}>
      {reached.map((outcome) => {
        const visual = outcomeVisual(outcome);
        const Icon = visual.icon;
        return (
          <span key={outcome} className="inline-flex items-center gap-1" title={visual.label} data-outcome={outcome}>
            <Icon className={cn("size-3.5", visual.tone)} aria-hidden />
            <span className="font-mono tabular-nums">{tally[outcome].toLocaleString("en-US")}</span>
            <span className="text-muted-foreground">{outcome}</span>
          </span>
        );
      })}
    </span>
  );
}

/** What a removal was asked to do, in a line: how many ids of which finding, and how much of each record. */
function asked(removal: InventoryRemoval): string {
  const label = findingVisual(removal.finding).label.toLowerCase();
  return `${removal.requested.toLocaleString("en-US")} ${label}${removal.namesIds ? " picked" : ", every one"}, ${removal.scope === "everything" ? "purged" : "soft deleted"}`;
}

/**
 * What one removal did to each id, in the workbench's bottom panel: the removal in a line, its outcomes to pick from with
 * their counts, and its ids of the outcome picked (every one at first), each with the version it found and why, a page at a
 * time.
 */
function RemovalItemsPanel({ inventory, removal }: { inventory: Inventory; removal: InventoryRemoval }) {
  const [outcome, setOutcome] = useState<InventoryRemovalOutcome | null>(null);
  const pages = useInfiniteQuery({
    queryKey: ["delivery", "inventories", "removal-items", inventory.partition, removal.inventoryRemovalId, outcome],
    queryFn: ({ pageParam }) => inventoryApi.removalItems(inventory.partition, removal.inventoryRemovalId, { outcome: outcome ?? undefined, after: pageParam, limit: PAGE }),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: (last) => last.next,
    placeholderData: keepPreviousData,
    refetchInterval: removal.status === "running" ? RUNNING_REFRESH_MS : false,
  });
  const rows = useMemo(() => pages.data?.pages.flatMap((page) => page.items), [pages.data]);
  const tally = tallyOf(removal);
  const total = outcome === null ? INVENTORY_REMOVAL_OUTCOMES.reduce((sum, o) => sum + tally[o], 0) : tally[outcome];
  const more = () => {
    if (pages.hasNextPage && !pages.isFetchingNextPage) {
      void pages.fetchNextPage();
    }
  };

  const columns: Column<InventoryRemovalItem>[] = [
    {
      id: "id",
      header: "Id",
      fill: true,
      floor: 200,
      render: (row) => <RecordName id={row.targetId} copy className="text-[12.5px]" testId="inventory-removal-item-id" />,
    },
    {
      id: "outcome",
      header: "Outcome",
      render: (row) => {
        const visual = outcomeVisual(row.outcome);
        const Icon = visual.icon;
        return (
          <span className="inline-flex items-center gap-1 whitespace-nowrap text-[12px]" data-testid="inventory-removal-item-outcome" data-outcome={row.outcome}>
            <Icon className={cn("size-3.5", visual.tone)} aria-hidden />
            {visual.label.toLowerCase()}
          </span>
        );
      },
    },
    {
      id: "version",
      header: "Version",
      align: "right",
      render: (row) => (row.version === undefined
        ? <span className="text-[12px] text-muted-foreground">-</span>
        : <span className="font-mono text-[12px] tabular-nums">{row.version}</span>),
    },
    { id: "reason", header: "Why", fill: true, floor: 220, render: (row) => <TruncatedText text={row.reason ?? ""} maxWidth={900} className="text-[12px]" /> },
    { id: "recorded", header: "When", render: (row) => <span className="whitespace-nowrap text-[12px]"><RelativeTime value={row.recordedUtc} absolute={false} /></span> },
  ];

  const tab = (value: InventoryRemovalOutcome | null, label: string, count: number) => (
    <button
      key={value ?? "all"}
      type="button"
      onClick={() => setOutcome(value)}
      aria-pressed={outcome === value}
      className={cn(
        "relative inline-flex h-8 shrink-0 items-center gap-1.5 whitespace-nowrap rounded-sm px-2.5 text-[12.5px] outline-none transition-colors focus-visible:bg-accent/60",
        "after:absolute after:inset-x-2 after:bottom-0 after:h-0.5 after:rounded-full",
        outcome === value ? "text-foreground after:bg-primary" : "text-muted-foreground after:bg-transparent hover:text-foreground",
      )}
      data-testid={`inventory-removal-outcome-${value ?? "all"}`}
    >
      {label}
      <span className="rounded-full bg-muted px-1.5 font-mono text-[11px] tabular-nums leading-4 text-muted-foreground">{count.toLocaleString("en-US")}</span>
    </button>
  );

  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="inventory-removal-panel" data-removal={removal.inventoryRemovalId}>
      <div className="flex shrink-0 flex-wrap items-center gap-x-3 gap-y-1 border-b border-border px-3 py-1.5 text-[12.5px]">
        <span className="inline-flex items-center gap-1.5">
          <FindingGlyph finding={removal.finding} />
          <span className="font-medium">{asked(removal)}</span>
        </span>
        <span className="text-muted-foreground">
          {"by "}
          <span className="font-mono text-[12px] text-foreground">{removal.actor}</span>
          {", "}
          <RelativeTime value={removal.startedUtc} absolute={false} />
        </span>
        <RunStatusPill status={removal.status} testId="inventory-removal-panel-status" />
        {removal.runId !== undefined && <RunRef runId={removal.runId} />}
        {removal.error !== undefined && (
          <ExplainTip title="Why it stopped" text={removal.error} testId="inventory-removal-panel-error">
            <span className="min-w-0 max-w-[32rem] truncate text-destructive">{removal.error}</span>
          </ExplainTip>
        )}
      </div>
      <div className="flex shrink-0 items-center gap-0.5 border-b border-border px-1.5" role="toolbar" aria-label="Outcomes">
        {tab(null, "Every id", INVENTORY_REMOVAL_OUTCOMES.reduce((sum, o) => sum + tally[o], 0))}
        {INVENTORY_REMOVAL_OUTCOMES.map((value) => tab(value, outcomeVisual(value).label, tally[value]))}
      </div>
      <div className="min-h-0 flex-1 overflow-auto p-2">
        {pages.isError
          ? <ProblemView error={pages.error} testId="inventory-removal-items-error" />
          : (
            <DataTable
              columns={columns}
              rows={rows}
              rowKey={(row) => row.inventoryRemovalItemId}
              footer={(
                <GridFooter
                  shown={rows?.length ?? 0}
                  total={total}
                  noun="id"
                  hasMore={pages.hasNextPage}
                  loading={pages.isFetchingNextPage}
                  onMore={more}
                  testId="inventory-removal-items-footer"
                />
              )}
              emptyMessage={outcome === null ? "The removal has reached no id yet." : `No id came to ${outcomeVisual(outcome).label.toLowerCase()}.`}
              data-testid="inventory-removal-items-table"
            />
          )}
      </div>
    </div>
  );
}

/**
 * An inventory's removals, newest first: what each was asked to remove and how much of each record, where it stands and what
 * it came to so far (its tallies grow a chunk at a time while it runs), who asked and when, and its run. A removal opens in the
 * workbench's bottom panel with what it did to each id, and why.
 */
export function InventoryRemovals({ inventory, removals, error }: { inventory: Inventory; removals: InventoryRemoval[] | undefined; error: unknown }) {
  const { ownedId, show } = useOwnedPanel(PANEL);
  const open = useCallback((removal: InventoryRemoval) => show(
    String(removal.inventoryRemovalId),
    `Removal · ${removal.inventoryRemovalId}`,
    <RemovalItemsPanel key={removal.inventoryRemovalId} inventory={inventory} removal={removal} />,
  ), [show, inventory]);

  // The removal in the panel is raised again as the list is read again, so a running one's tallies and status keep up.
  const shown = ownedId === null ? undefined : removals?.find((removal) => String(removal.inventoryRemovalId) === ownedId);
  useEffect(() => {
    if (shown !== undefined) {
      open(shown);
    }
  }, [shown, open]);

  if (error !== null && error !== undefined) {
    return <ProblemView error={error} testId="inventory-removals-error" />;
  }

  const columns: Column<InventoryRemoval>[] = [
    { id: "removal", header: "Removal", render: (row) => <span className="font-mono text-[12px] tabular-nums">{row.inventoryRemovalId}</span> },
    {
      id: "asked",
      header: "Asked",
      fill: true,
      floor: 220,
      render: (row) => (
        <span className="inline-flex min-w-0 items-center gap-1.5 text-[12.5px]">
          <FindingGlyph finding={row.finding} />
          <TruncatedText text={asked(row)} maxWidth={420} />
        </span>
      ),
    },
    {
      id: "status",
      header: "Status",
      render: (row) => (row.error === undefined
        ? <RunStatusPill status={row.status} testId="inventory-removal-status" />
        : (
          <ExplainTip title="Why it stopped" text={row.error} testId="inventory-removal-error">
            <span className="inline-flex"><RunStatusPill status={row.status} testId="inventory-removal-status" /></span>
          </ExplainTip>
        )),
    },
    { id: "tally", header: "Came to", render: (row) => <Tally removal={row} testId={`inventory-removal-tally-${row.inventoryRemovalId}`} /> },
    { id: "started", header: "Started", render: (row) => <span className="whitespace-nowrap text-[12px]"><RelativeTime value={row.startedUtc} absolute={false} /></span> },
    { id: "actor", header: "By", render: (row) => <TruncatedText text={row.actor} mono maxWidth={180} /> },
    { id: "run", header: "", align: "right", render: (row) => (row.runId === undefined ? null : <RunRef runId={row.runId} />) },
  ];

  return (
    <DataTable
      columns={columns}
      rows={removals}
      rowKey={(row) => row.inventoryRemovalId}
      onRowClick={open}
      rowSx={(row) => (ownedId === String(row.inventoryRemovalId) ? { backgroundColor: "var(--accent)" } : undefined)}
      emptyMessage="Nothing has been removed through this inventory. Pick ids of a removable finding on the Ids tab to remove them."
      data-testid="inventory-removals-table"
    />
  );
}
