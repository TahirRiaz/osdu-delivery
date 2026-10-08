import { useEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from "react";
import { Link } from "react-router-dom";
import { keepPreviousData, useInfiniteQuery } from "@tanstack/react-query";
import { Card } from "@/components/ui/card";
import { DataTable, type Column } from "@/components/DataTable";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { TruncatedText } from "@/components/TruncatedText";
import { cn } from "@/lib/utils";
import { deliveryRecordRoute } from "../../../api/delivery";
import { inventoryApi, type Inventory, type InventoryRecord } from "../../../api/inventories";
import { RecordName } from "../RecordName";
import { ProblemView } from "../TemplateSheet";
import { useWindowFit } from "../useWindowFit";
import { DimensionGrid, GridFooter, GridViewMenu } from "../dimensions/DimensionGrid";
import { useHiddenColumns, type GridColumnChoice } from "../dimensions/dimensionGridState";
import { ExplainTip, FindingGlyph } from "./InventoryBadges";
import { InventoryIdDock } from "./InventoryIdDock";
import { clampShare, findingVisual, findingWhy } from "./inventoryFormat";

const PAGE = 200;

/** The columns a grid of one finding starts without. */
const AT_FIRST_FINDING: readonly string[] = ["created"];

/** The columns a grid of every id starts without. */
const AT_FIRST_EVERY_ID: readonly string[] = ["created", "since"];

/** What stays under the grid and its panel: the page's bottom padding and the workbench's status bar. */
const BELOW = 46;

/** The least height the grid keeps alone, and with its panel under it, so a short window still shows a few rows of each. */
const MIN_HEIGHT = 300;
const MIN_HEIGHT_WITH_PANEL = 420;

/** The share of the height the panel takes when it first opens, and where the reader's own is remembered. */
const FIRST_SHARE = 0.55;
const SHARE_KEY = "osdu.inventories.panel.share";

/** The row whose id is open in the panel, marked as the row a reader is on. */
const OPEN_ROW: CSSProperties = { backgroundColor: "color-mix(in oklab, var(--primary) 10%, transparent)" };

/** The panel's share as this browser remembers it, or the first one. */
function rememberedShare(): number {
  try {
    const kept = Number.parseFloat(window.localStorage.getItem(SHARE_KEY) ?? "");
    return Number.isFinite(kept) ? clampShare(kept) : FIRST_SHARE;
  } catch {
    // A browser that keeps nothing opens the panel at its first share.
    return FIRST_SHARE;
  }
}

/** Whether a key went to a field, which keeps every key it is given. */
function typedInField(target: EventTarget | null): boolean {
  return target instanceof HTMLElement && (target.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName));
}

/** Whether an arrow key went to something that moves by arrows of its own: a field, a menu, a tree, the panel and its handle. */
function arrowsKeptByTarget(target: EventTarget | null): boolean {
  return typedInField(target)
    || (target instanceof HTMLElement
      && target.closest("[role=menu],[role=listbox],[role=tree],[role=dialog],[role=separator],[data-testid=inventory-dock]") !== null);
}

/**
 * The ledger that holds the id, as the record's status there (or the state of the artifact it rests on), which opens the
 * record, or the ledger's records when the id rests on none; the ledger's name and the whole of what it holds of the id,
 * versions included, on hover.
 */
function LedgerCell({ row }: { row: InventoryRecord }) {
  if (row.ledgerFlowId === undefined) {
    return <span className="text-[12px] text-muted-foreground">none</span>;
  }

  const name = row.ledger ?? row.ledgerFlowId;
  const state = row.ledgerStatus ?? (row.artifactState === undefined ? "held" : `artifact ${row.artifactState}`);
  const held = [
    `Ledger ${name}`,
    row.ledgerStatus === undefined ? undefined : `record ${row.ledgerStatus}${row.ledgerVersion === undefined ? ", no version" : ` at version ${row.ledgerVersion}`}`,
    row.deliveryKey === undefined ? undefined : `delivery key ${row.deliveryKey}`,
    row.artifactId === undefined ? undefined : `artifact ${row.artifactId}${row.artifactState === undefined ? "" : ` ${row.artifactState}`}`,
  ].filter((part): part is string => part !== undefined).join("\n");
  const to = row.deliveryKey === undefined
    ? `/delivery/records?flow=${encodeURIComponent(row.ledgerFlowId)}`
    : deliveryRecordRoute({ flowId: row.ledgerFlowId, deliveryKey: row.deliveryKey });
  return (
    <RichTooltip title="What the ledger holds" body={held} mono>
      <Link
        to={to}
        className="whitespace-nowrap text-[12px] text-primary hover:underline"
        onClick={(event) => event.stopPropagation()}
        data-testid="inventory-record-ledger"
      >
        {state}
      </Link>
    </RichTooltip>
  );
}

/**
 * The ids of an inventory with one finding, or every id, as a grid in a frame as tall as the window leaves: the findings
 * to pick from lead its toolbar, its rows scroll inside it, and more are read as it is scrolled, a page at a time after the
 * last id read. Each id is named as a reader knows it (the whole id on hover, its copy on the row's hover), with its
 * finding (why it has it on hover, to copy), its version, the ledger that holds it (which opens its record), who created
 * it and when it last changed, and since when it has its finding; columns are left out under View. A reader who can read
 * OSDU opens an id by its row in the panel that slides up under the grid, and steps through the ids there with Up and Down;
 * Escape closes it. The id open is the page's, so a link lands on it.
 */
export function InventoryRecordsGrid({ inventory, finding, total, leading, trailing, openId, onOpen }: {
  inventory: Inventory;
  /** The finding whose ids are listed; null lists every id, each with its finding. */
  finding: string | null;
  /** How many ids the grid can show, as the counts say; null when unknown. */
  total: number | null;
  /** What leads the grid's toolbar: the findings to pick from. */
  leading?: ReactNode;
  /** What the toolbar holds before View at its end (the export). */
  trailing?: ReactNode;
  /** The OSDU id open in the panel; null when the panel is closed. */
  openId: string | null;
  /** Opens an id in the panel, or closes it with null; absent where the reader cannot read OSDU. */
  onOpen?: (id: string | null) => void;
}) {
  // Every id adds a Finding column, so that view starts without the time each id has had its finding too.
  const atFirst = finding === null ? AT_FIRST_EVERY_ID : AT_FIRST_FINDING;
  const [hidden, toggleColumn] = useHiddenColumns(`osdu.inventories.records.columns.${finding === null ? "all" : "finding"}`, atFirst);
  const pages = useInfiniteQuery({
    queryKey: ["delivery", "inventories", "records", inventory.partition, inventory.inventoryId, finding],
    queryFn: ({ pageParam }) => inventoryApi.records(inventory.partition, inventory.inventoryId, {
      finding: finding ?? undefined, after: pageParam, limit: PAGE,
    }),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: (last) => last.next,
    placeholderData: keepPreviousData,
  });
  const rows = useMemo(() => pages.data?.pages.flatMap((page) => page.items), [pages.data]);
  const more = () => {
    if (pages.hasNextPage && !pages.isFetchingNextPage) {
      void pages.fetchNextPage();
    }
  };

  const panelOpen = openId !== null && onOpen !== undefined;
  const [share, setShare] = useState(rememberedShare);
  const [maximized, setMaximized] = useState(false);
  const keepShare = (next: number) => {
    const kept = clampShare(next);
    setShare(kept);
    try {
      window.localStorage.setItem(SHARE_KEY, String(kept));
    } catch {
      // The share still holds for this visit.
    }
  };

  const frame = useRef<HTMLDivElement>(null);
  useWindowFit(frame, BELOW, panelOpen ? MIN_HEIGHT_WITH_PANEL : MIN_HEIGHT, undefined, "height");

  const at = panelOpen && rows !== undefined ? rows.findIndex((row) => row.targetId === openId) : -1;
  const canBack = at > 0;
  const canForward = at >= 0 && (at < (rows?.length ?? 0) - 1 || pages.hasNextPage);
  // The id stepped to last, ahead of the address that follows it, so keys pressed faster than the page renders (a key
  // held down) each step on from the one before rather than from the same row.
  const stepped = useRef(openId);
  useEffect(() => {
    stepped.current = openId;
  }, [openId]);
  const step = (by: -1 | 1) => {
    if (rows === undefined || onOpen === undefined) {
      return;
    }

    const from = rows.findIndex((row) => row.targetId === stepped.current);
    const next = from + by;
    if (from < 0 || next < 0) {
      return;
    }

    if (next >= rows.length - 1) {
      // The panel keeps going where the grid's scrolling would: the next page is read as the last row read is reached.
      more();
    }

    if (next < rows.length) {
      stepped.current = rows[next].targetId;
      onOpen(rows[next].targetId);
    }
  };

  // The row of the id open stays in view of the grid as the panel steps through the ids.
  useEffect(() => {
    if (at < 0 || frame.current === null) {
      return;
    }

    const row = frame.current.querySelectorAll<HTMLTableRowElement>('[data-slot="table-container"] tbody tr')[at];
    row?.scrollIntoView({ block: "nearest" });
  }, [at, maximized]);

  // Up and Down step through the ids while the panel is open, unless they are for something that moves by arrows of its
  // own (the panel's record view among them); Escape closes it from anywhere but a field, once whatever it would close
  // first (a menu, a hover card) has taken it. The listener is set once per opening and reaches the latest rows through
  // the ref.
  const keys = useRef({ step, close: () => onOpen?.(null) });
  useEffect(() => {
    keys.current = { step, close: () => onOpen?.(null) };
  });
  useEffect(() => {
    if (!panelOpen) {
      return undefined;
    }

    const keyed = (event: KeyboardEvent) => {
      if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey || typedInField(event.target)) {
        return;
      }

      if ((event.key === "ArrowDown" || event.key === "ArrowUp") && !arrowsKeptByTarget(event.target)) {
        event.preventDefault();
        keys.current.step(event.key === "ArrowDown" ? 1 : -1);
      } else if (event.key === "Escape") {
        keys.current.close();
      }
    };
    window.addEventListener("keydown", keyed);
    return () => window.removeEventListener("keydown", keyed);
  }, [panelOpen]);

  const choices: GridColumnChoice[] = [
    { id: "version", label: "Version" },
    { id: "ledger", label: "Ledger" },
    { id: "created", label: "Created by" },
    { id: "changed", label: "Changed" },
    { id: "since", label: "Since" },
  ];
  const all: Column<InventoryRecord>[] = [
    {
      id: "id",
      header: "Id",
      fill: true,
      floor: 200,
      render: (row) => (
        <span className="flex min-w-0 items-center gap-1.5">
          <RecordName id={row.targetId} kind={row.kind} copy className="text-[12.5px]" testId="inventory-record-id" copyTestId="inventory-record-copy-id" />
          {row.goneUtc !== undefined && (
            <RichTooltip body="OSDU no longer serves it: the inventory's read did not list it.">
              <span className="shrink-0 rounded-sm border border-border px-1 text-[10.5px] text-muted-foreground">gone <RelativeTime value={row.goneUtc} absolute={false} /></span>
            </RichTooltip>
          )}
        </span>
      ),
    },
    {
      id: "why",
      header: finding === null ? "Finding" : "",
      render: (row) => (
        <ExplainTip title={`Why ${findingVisual(row.finding).label.toLowerCase()}`} text={findingWhy(row)} testId="inventory-record-why">
          {finding === null
            ? (
              <span className="inline-flex items-center gap-1 whitespace-nowrap text-[12px]" data-testid="inventory-record-finding" data-finding={row.finding}>
                <FindingGlyph finding={row.finding} />
                {findingVisual(row.finding).label.toLowerCase()}
              </span>
            )
            : (
              <button type="button" className="inline-flex rounded-sm outline-none focus-visible:ring-2 focus-visible:ring-ring/50" aria-label="Why it has its finding" data-testid="inventory-record-why">
                <FindingGlyph finding={row.finding} />
              </button>
            )}
        </ExplainTip>
      ),
    },
    {
      id: "version",
      header: "Version",
      align: "right",
      render: (row) => (row.version === undefined
        ? <span className="text-[12px] text-muted-foreground">-</span>
        : <span className="font-mono text-[12px] tabular-nums">{row.version}</span>),
    },
    { id: "ledger", header: "Ledger", render: (row) => <LedgerCell row={row} /> },
    {
      id: "created",
      header: "Created by",
      render: (row) => (row.createUser === undefined
        ? <span className="text-[12px] text-muted-foreground">-</span>
        : <TruncatedText text={row.createUser} mono maxWidth={200} />),
    },
    {
      id: "changed",
      header: "Changed",
      render: (row) => {
        const changedAt = row.modifyTime ?? row.createTime;
        if (changedAt === undefined) {
          return <span className="text-[12px] text-muted-foreground">-</span>;
        }

        const by = row.modifyUser ?? row.createUser;
        const time = <span className="whitespace-nowrap text-[12px]"><RelativeTime value={changedAt} absolute={false} /></span>;
        return by === undefined ? time : <RichTooltip title="Changed by" body={by} mono>{time}</RichTooltip>;
      },
    },
    {
      id: "since",
      header: "Since",
      render: (row) => (
        <RichTooltip body={`It has had this finding since this reconcile${row.firstSeenUtc === undefined ? "." : "; the inventory first listed it earlier or then."}`}>
          <span className="whitespace-nowrap text-[12px] text-muted-foreground"><RelativeTime value={row.findingUtc} absolute={false} /></span>
        </RichTooltip>
      ),
    },
  ];
  const columns = all.filter((column) => !hidden.has(column.id));
  const noun = finding === null ? "id" : `${findingVisual(finding).label.toLowerCase()} id`;
  const toolbar = (
    <div className="flex min-w-0 items-center gap-1 border-b border-border pl-1.5 pr-2">
      {leading}
      <div className="ml-auto flex shrink-0 items-center gap-0.5">
        {trailing}
        <GridViewMenu columns={choices} hidden={hidden} atFirst={atFirst} onToggle={toggleColumn} compact testId="inventory-records-view" />
      </div>
    </div>
  );

  return (
    <div ref={frame} className="flex min-h-0 min-w-0 flex-col" data-testid="inventory-records">
      {pages.isError
        ? (
          // The findings stay over the problem, so another finding can still be picked.
          <Card className="gap-0 overflow-hidden rounded-lg p-0">
            {toolbar}
            <div className="p-3"><ProblemView error={pages.error} testId="inventory-records-error" /></div>
          </Card>
        )
        : (
          <DimensionGrid
            fill
            onNearEnd={more}
            // The copy of an id shows on its row's hover, or while it has the focus, so a page of ids reads as ids.
            className={cn(
              "[&_tbody_tr:not(:hover):not(:focus-within)_[data-testid=inventory-record-copy-id]]:opacity-0",
              // A row stepped to scrolls clear of the headers that stay over the rows.
              "[&_tbody_tr]:scroll-mt-8",
              panelOpen && maximized && "hidden",
            )}
            testId="inventory-grid"
          >
            <DataTable
              toolbar={toolbar}
              columns={columns}
              rows={rows}
              rowKey={(row) => row.inventoryRecordId}
              onRowClick={onOpen === undefined ? undefined : (row) => onOpen(row.targetId)}
              rowSx={(row) => (panelOpen && row.targetId === openId ? OPEN_ROW : undefined)}
              footer={(
                <GridFooter
                  shown={rows?.length ?? 0}
                  total={total}
                  noun={noun}
                  hasMore={pages.hasNextPage}
                  loading={pages.isFetchingNextPage}
                  onMore={more}
                  testId="inventory-records-footer"
                />
              )}
              emptyMessage={finding === null ? "The inventory holds no id yet." : `No id is ${findingVisual(finding).label.toLowerCase()}.`}
              data-testid="inventory-records-table"
            />
          </DimensionGrid>
        )}
      {panelOpen && (
        <InventoryIdDock
          partition={inventory.partition}
          id={openId}
          record={at >= 0 ? rows?.[at] : undefined}
          place={at >= 0 ? { at: at + 1, of: total } : null}
          onStep={step}
          canBack={canBack}
          canForward={canForward}
          onOpenId={(id) => onOpen(id)}
          onClose={() => onOpen(null)}
          share={share}
          onShare={keepShare}
          maximized={maximized}
          onMaximized={setMaximized}
        />
      )}
    </div>
  );
}
