import { useRef, type KeyboardEvent, type PointerEvent as ReactPointerEvent, type ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, ChevronUp, Maximize2, Minimize2, Telescope, Unplug, X } from "lucide-react";
import { EmptyState } from "@/components/EmptyState";
import { IconAction } from "@/components/IconAction";
import { cn } from "@/lib/utils";
import { explorerApi } from "../../../api/explorer";
import type { InventoryRecord } from "../../../api/inventories";
import { ExplorerProblem } from "../explorer/ExplorerProblem";
import { ExplorerRecord } from "../explorer/ExplorerRecord";
import { ALL_KINDS, scopeKind } from "../explorer/explorerModel";
import { TaskProgress } from "../TemplateSheet";
import { ExplainTip, FindingGlyph } from "./InventoryBadges";
import { MAX_SHARE, MIN_SHARE, clampShare, findingVisual, findingWhy } from "./inventoryFormat";

/** How far a key on the handle moves it, as a share of the frame's height. */
const KEY_STEP = 0.05;

/** The explorer's address for what it opens: a record, a place, or a search, in the partition named. */
function explorerHref(partition: string, changes: Record<string, string>): string {
  return `/delivery/explorer?${new URLSearchParams({ partition, ...changes }).toString()}`;
}

/**
 * The line over the panel's record: the id's finding and why it has it (the whole of it on hover, to copy), where the id
 * is among the ids read with the way to the one before and after, and the panel's own controls.
 */
function DockHeader({ record, place, onStep, canBack, canForward, explorer, maximized, onMaximized, onClose }: {
  record: InventoryRecord | undefined;
  place: { at: number; of: number | null } | null;
  onStep: (by: -1 | 1) => void;
  canBack: boolean;
  canForward: boolean;
  explorer: () => void;
  maximized: boolean;
  onMaximized: (maximized: boolean) => void;
  onClose: () => void;
}) {
  const visual = record === undefined ? null : findingVisual(record.finding);
  return (
    <div className="flex h-9 shrink-0 items-center gap-2 px-1" data-testid="inventory-dock-header">
      {record !== undefined && visual !== null
        ? (
          <ExplainTip title={`Why ${visual.label.toLowerCase()}`} text={findingWhy(record)} testId="inventory-dock-why">
            <span className="flex min-w-0 items-center gap-1.5 text-[12.5px]" data-testid="inventory-dock-finding" data-finding={record.finding}>
              <FindingGlyph finding={record.finding} />
              <span className="shrink-0 font-medium">{visual.label}</span>
              {record.detail !== undefined && record.detail !== "" && <span className="min-w-0 truncate text-muted-foreground">{record.detail}</span>}
            </span>
          </ExplainTip>
        )
        : <span className="text-[12.5px] text-muted-foreground">As OSDU holds it</span>}
      <div className="ml-auto flex shrink-0 items-center gap-0.5">
        {place !== null && (
          <span className="mr-1 font-mono text-[11.5px] tabular-nums text-muted-foreground" data-testid="inventory-dock-place">
            {place.at.toLocaleString("en-US")}
            {place.of === null ? "" : ` of ${place.of.toLocaleString("en-US")}`}
          </span>
        )}
        <IconAction label="The id before (Up)" icon={<ChevronUp />} disabled={!canBack} onClick={() => onStep(-1)} className="size-7" data-testid="inventory-dock-back" />
        <IconAction label="The id after (Down)" icon={<ChevronDown />} disabled={!canForward} onClick={() => onStep(1)} className="size-7" data-testid="inventory-dock-forward" />
        <span className="mx-1 h-4 w-px bg-border" aria-hidden />
        <IconAction label="Open in the explorer" icon={<Telescope />} onClick={explorer} className="size-7" data-testid="inventory-dock-explorer" />
        <IconAction
          label={maximized ? "Give the grid its room back" : "Let the panel take the grid's room"}
          icon={maximized ? <Minimize2 /> : <Maximize2 />}
          onClick={() => onMaximized(!maximized)}
          className="size-7"
          data-testid="inventory-dock-maximize"
        />
        <IconAction label="Close (Esc)" icon={<X />} onClick={onClose} className="size-7" data-testid="inventory-dock-close" />
      </div>
    </div>
  );
}

/**
 * One id of an inventory as OSDU holds it, in a panel that slides up under the grid and shares the page's height with it:
 * read through the partition's connection in the explorer's record view (its versions, its links, the records that mention
 * it, its check against its schema), with the id's finding and why above it. Its top edge is a handle that gives it more
 * or less of the height (a key moves it too, a double click lets it take the grid's room); the way to the id before and
 * after steps through the grid's ids without leaving it, and the explorer opens the id, or a place or search the record
 * leads to, with everything it offers. What the explorer reads is OSDU's alone: nothing of the ledgers is in the record.
 */
export function InventoryIdDock({
  partition, id, record, place, onStep, canBack, canForward, onOpenId, onClose, share, onShare, maximized, onMaximized,
}: {
  partition: string;
  /** The OSDU id in the panel. */
  id: string;
  /** The id's row in the grid, once the grid has read it: its finding and why. */
  record: InventoryRecord | undefined;
  /** Where the id is among the ids the grid has read, counted from one, and how many the grid can show; null when it is not among them. */
  place: { at: number; of: number | null } | null;
  onStep: (by: -1 | 1) => void;
  canBack: boolean;
  canForward: boolean;
  /** Opens another id in the panel: one the record view offers in place of an id OSDU holds nothing under. */
  onOpenId: (id: string) => void;
  onClose: () => void;
  /** The share of its frame's height the panel takes, unless it takes the grid's room. */
  share: number;
  onShare: (share: number) => void;
  /** Whether the panel takes the grid's room as well. */
  maximized: boolean;
  onMaximized: (maximized: boolean) => void;
}) {
  const navigate = useNavigate();
  const panel = useRef<HTMLElement>(null);
  const connection = useQuery({
    queryKey: ["explorer", "connection", partition],
    queryFn: () => explorerApi.connection(partition),
    staleTime: 60_000,
  });

  // The handle follows the pointer on the element itself, so the grid above gives way frame by frame without the page
  // rendering again; the share is kept once the pointer lets go.
  const drag = (event: ReactPointerEvent<HTMLDivElement>) => {
    const frame = panel.current?.parentElement;
    if (event.button !== 0 || maximized || frame === null || frame === undefined || panel.current === null) {
      return;
    }

    event.preventDefault();
    const handle = event.currentTarget;
    const element = panel.current;
    const box = frame.getBoundingClientRect();
    let dragged = share;
    const move = (moved: PointerEvent) => {
      dragged = clampShare((box.bottom - moved.clientY) / box.height);
      element.style.height = `${dragged * 100}%`;
    };
    const release = () => {
      handle.removeEventListener("pointermove", move);
      handle.removeEventListener("pointerup", release);
      handle.removeEventListener("pointercancel", release);
      onShare(dragged);
    };
    handle.setPointerCapture(event.pointerId);
    handle.addEventListener("pointermove", move);
    handle.addEventListener("pointerup", release);
    handle.addEventListener("pointercancel", release);
  };
  const keyed = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key === "ArrowUp" || event.key === "ArrowDown") {
      event.preventDefault();
      event.stopPropagation();
      onShare(clampShare(share + (event.key === "ArrowUp" ? KEY_STEP : -KEY_STEP)));
    } else if (event.key === "Enter") {
      event.preventDefault();
      onMaximized(!maximized);
    }
  };

  let body: ReactNode;
  if (connection.isError) {
    body = <div className="p-3"><ExplorerProblem error={connection.error} /></div>;
  } else if (connection.data === undefined) {
    body = <div className="p-3"><TaskProgress label={`Finding the connection to ${partition}`} testId="inventory-dock-connecting" /></div>;
  } else if (!connection.data.available) {
    body = (
      <EmptyState
        icon={<Unplug />}
        title={`No connection to ${partition}`}
        description={connection.data.reason ?? undefined}
        data-testid="inventory-dock-no-connection"
      />
    );
  } else {
    body = (
      <ExplorerRecord
        key={`${partition}|${id}`}
        partition={partition}
        id={id}
        version={null}
        onScope={(scope) => navigate(explorerHref(partition, { kind: scopeKind(scope) ?? ALL_KINDS }))}
        onOpenId={onOpenId}
        onBrowseQuery={(query) => navigate(explorerHref(partition, { q: query, lq: "1" }))}
        onSwitchPartition={(other) => navigate(explorerHref(other, { id }))}
      />
    );
  }

  return (
    <section
      ref={panel}
      className={cn(
        "flex min-h-0 flex-col",
        maximized ? "flex-1" : "min-h-[220px] max-h-[calc(100%-140px)] shrink-0",
        "animate-in fade-in-0 slide-in-from-bottom-8 duration-200 motion-reduce:animate-none",
      )}
      style={maximized ? undefined : { height: `${share * 100}%` }}
      aria-label="The id as OSDU holds it"
      data-testid="inventory-dock"
      data-id={id}
    >
      <div
        role="separator"
        aria-orientation="horizontal"
        aria-label="Resize the panel"
        aria-valuemin={Math.round(MIN_SHARE * 100)}
        aria-valuemax={Math.round(MAX_SHARE * 100)}
        aria-valuenow={Math.round((maximized ? 1 : share) * 100)}
        tabIndex={0}
        onPointerDown={drag}
        onKeyDown={keyed}
        onDoubleClick={() => onMaximized(!maximized)}
        title="Drag to resize; a double click lets the panel take the grid's room"
        className={cn(
          "group flex h-3 shrink-0 touch-none items-center justify-center rounded-sm outline-none focus-visible:bg-accent/60",
          maximized ? "cursor-default" : "cursor-row-resize",
        )}
        data-testid="inventory-dock-handle"
      >
        <span className="h-1 w-10 rounded-full bg-border transition-colors group-hover:bg-muted-foreground/50" aria-hidden />
      </div>
      <DockHeader
        record={record}
        place={place}
        onStep={onStep}
        canBack={canBack}
        canForward={canForward}
        explorer={() => navigate(explorerHref(partition, { id }))}
        maximized={maximized}
        onMaximized={onMaximized}
        onClose={onClose}
      />
      {/* The record view's card takes what the panel leaves rather than the height a window of its own would ask for. */}
      <div className="flex min-h-0 flex-1 flex-col [&_[data-testid=osdu-panel]>div>[data-slot=card]]:min-h-0">
        {body}
      </div>
    </section>
  );
}
