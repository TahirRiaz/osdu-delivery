import { useEffect, useState } from "react";
import { Check, ChevronDown } from "lucide-react";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { cn } from "@/lib/utils";
import type { InventoryCount } from "../../../api/inventories";
import { ExplainTip, FindingGlyph } from "./InventoryBadges";
import { findingVisual } from "./inventoryFormat";

/** The room between two tabs of the strip, in pixels, as its gap class sets it. */
const GAP = 2;

/** What the strip's last tab says when it holds the most it can, measured so its room is kept before the tabs are fitted. */
const WIDEST_MORE = "99 more";

/** One tab of the strip: every id (no finding), or a finding with how many ids have it. */
interface StripTab {
  finding: string | null;
  count: number;
}

/** How wide the strip is and how wide each of its tabs would be, as last measured; null before the first measure. */
interface StripRoom {
  available: number;
  widths: number[];
  more: number;
}

const tabClass = (selected: boolean) => cn(
  "relative inline-flex h-9 shrink-0 items-center gap-1.5 whitespace-nowrap rounded-sm px-2.5 text-[12.5px] outline-none transition-colors",
  "focus-visible:bg-accent/60 after:absolute after:inset-x-2 after:bottom-0 after:h-0.5 after:rounded-full",
  selected ? "text-foreground after:bg-primary" : "text-muted-foreground after:bg-transparent hover:text-foreground",
);

/** A tab's count, as a quiet pill after its name. */
function CountPill({ count, selected }: { count: number; selected: boolean }) {
  return (
    <span className={cn("rounded-full px-1.5 font-mono text-[11px] tabular-nums leading-4", selected ? "bg-primary/10 text-foreground" : "bg-muted text-muted-foreground")}>
      {count.toLocaleString("en-US")}
    </span>
  );
}

/** What a tab shows: the finding's glyph in its tone, its name and its count; every id has no glyph. */
function TabFace({ tab, selected }: { tab: StripTab; selected: boolean }) {
  return (
    <>
      {tab.finding !== null && <FindingGlyph finding={tab.finding} />}
      <span>{tab.finding === null ? "Every id" : findingVisual(tab.finding).label}</span>
      <CountPill count={tab.count} selected={selected} />
    </>
  );
}

/**
 * Which tabs are shown: as many as the strip has room for, in their order, the one picked always among them (in place of
 * the last that would fit otherwise), with room kept for the menu when it is shown.
 */
function fitted(room: StripRoom | null, count: number, picked: number, menuAlways: boolean): Set<number> {
  const every = new Set(Array.from({ length: count }, (_, index) => index));
  if (room === null || room.widths.length !== count) {
    return every;
  }

  const total = room.widths.reduce((sum, width) => sum + width + GAP, 0);
  if (!menuAlways && total <= room.available) {
    return every;
  }

  const left = room.available - room.more - GAP;
  const shown = new Set<number>();
  let used = 0;
  if (picked >= 0) {
    shown.add(picked);
    used += room.widths[picked] + GAP;
  }

  for (let index = 0; index < count; index++) {
    if (index === picked) {
      continue;
    }

    if (used + room.widths[index] + GAP > left) {
      break;
    }

    shown.add(index);
    used += room.widths[index] + GAP;
  }

  return shown;
}

/**
 * The findings of an inventory as one line of tabs over its grid: every id first, then each finding that ids have, the
 * raised ones in report order before the rest, each with its count and what it means on hover. The line never wraps: what
 * it has no room for, and the findings no id has, are in the menu at its end, which lists every finding with its count, so
 * a narrow page keeps one quiet line rather than rows of buttons. The finding picked always stays in view.
 */
export function InventoryFindingStrip({ total, counts, selected, onSelect }: {
  /** How many ids the inventory holds. */
  total: number;
  /** Every finding with its count, in report order. */
  counts: readonly InventoryCount[];
  /** The finding in view; null for every id. */
  selected: string | null;
  /** Picks a finding; null picks every id. */
  onSelect: (finding: string | null) => void;
}) {
  const held = counts.filter((count) => count.count > 0);
  const tabs: StripTab[] = [
    { finding: null, count: total },
    ...held.filter((count) => count.raised),
    ...held.filter((count) => !count.raised),
  ];
  // The menu is where the findings no id has are, so it stays whenever one has none.
  const menuAlways = counts.some((count) => count.count === 0);
  const picked = tabs.findIndex((tab) => tab.finding === selected);

  const [strip, setStrip] = useState<HTMLDivElement | null>(null);
  const [ruler, setRuler] = useState<HTMLDivElement | null>(null);
  const [room, setRoom] = useState<StripRoom | null>(null);
  useEffect(() => {
    if (strip === null || ruler === null) {
      return undefined;
    }

    // The ruler holds every tab and the widest menu, laid out unseen, so each is measured whichever are shown. It sits out
    // of the flow, so measuring it never moves the strip, and the observer reports both on its first look.
    const measure = () => {
      const faces = [...ruler.children].map((child) => child.getBoundingClientRect().width);
      const next: StripRoom = { available: strip.clientWidth, widths: faces.slice(0, -1), more: faces.at(-1) ?? 0 };
      setRoom((was) => (was !== null && was.available === next.available && was.more === next.more
        && was.widths.length === next.widths.length && was.widths.every((width, index) => width === next.widths[index])
        ? was
        : next));
    };
    const observer = new ResizeObserver(measure);
    observer.observe(strip);
    observer.observe(ruler);
    return () => observer.disconnect();
  }, [strip, ruler]);

  const shown = fitted(room, tabs.length, picked, menuAlways);
  const hidden = tabs.filter((_, index) => !shown.has(index)).length;
  const raised = counts.filter((count) => count.raised);
  const rest = counts.filter((count) => !count.raised);
  const item = (count: InventoryCount) => (
    <DropdownMenuItem
      key={count.finding}
      onSelect={() => onSelect(count.finding)}
      className="gap-2"
      data-testid={`inventory-findings-menu-${count.finding}`}
    >
      <FindingGlyph finding={count.finding} quiet={count.count === 0} />
      <span className={cn("flex-1", count.count === 0 && "text-muted-foreground")}>{findingVisual(count.finding).label}</span>
      <span className={cn("font-mono text-[11.5px] tabular-nums", count.count === 0 ? "text-muted-foreground/70" : "text-muted-foreground")}>
        {count.count.toLocaleString("en-US")}
      </span>
      <Check className={cn("size-3.5", count.finding === selected ? "opacity-100" : "opacity-0")} aria-hidden />
    </DropdownMenuItem>
  );

  return (
    <div ref={setStrip} className="relative flex min-w-0 flex-1 items-center gap-0.5 overflow-hidden" role="toolbar" aria-label="Findings" data-testid="inventory-findings">
      {tabs.map((tab, index) => {
        if (!shown.has(index)) {
          return null;
        }

        const isSelected = tab.finding === selected;
        const button = (
          <button
            type="button"
            onClick={() => onSelect(tab.finding)}
            aria-pressed={isSelected}
            className={tabClass(isSelected)}
            data-testid={tab.finding === null ? "inventory-finding-all" : `inventory-finding-${tab.finding}`}
            data-finding={tab.finding ?? undefined}
            data-count={tab.count}
          >
            <TabFace tab={tab} selected={isSelected} />
          </button>
        );
        return tab.finding === null
          ? <span key="all" className="inline-flex">{button}</span>
          : (
            <ExplainTip key={tab.finding} title={findingVisual(tab.finding).label} text={findingVisual(tab.finding).hint} testId={`inventory-finding-${tab.finding}`}>
              {button}
            </ExplainTip>
          );
      })}
      {(menuAlways || hidden > 0) && (
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <button type="button" className={tabClass(false)} data-testid="inventory-findings-more">
              {hidden > 0 ? `${hidden} more` : "More"}
              <ChevronDown className="size-3.5" aria-hidden />
            </button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="start" className="w-64" data-testid="inventory-findings-menu">
            <DropdownMenuItem onSelect={() => onSelect(null)} className="gap-2" data-testid="inventory-findings-menu-all">
              <span className="flex-1">Every id</span>
              <span className="font-mono text-[11.5px] tabular-nums text-muted-foreground">{total.toLocaleString("en-US")}</span>
              <Check className={cn("size-3.5", selected === null ? "opacity-100" : "opacity-0")} aria-hidden />
            </DropdownMenuItem>
            <DropdownMenuSeparator />
            <DropdownMenuLabel className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Raised</DropdownMenuLabel>
            {raised.map(item)}
            {rest.length > 0 && (
              <>
                <DropdownMenuSeparator />
                <DropdownMenuLabel className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Not raised</DropdownMenuLabel>
                {rest.map(item)}
              </>
            )}
          </DropdownMenuContent>
        </DropdownMenu>
      )}
      {/* Every tab and the widest menu, unseen and out of the flow, for their widths. */}
      <div ref={setRuler} className="pointer-events-none invisible absolute left-0 top-0 flex w-max gap-0.5" aria-hidden>
        {tabs.map((tab) => (
          <span key={tab.finding ?? "all"} className={tabClass(false)}>
            <TabFace tab={tab} selected={false} />
          </span>
        ))}
        <span className={tabClass(false)}>
          {WIDEST_MORE}
          <ChevronDown className="size-3.5" aria-hidden />
        </span>
      </div>
    </div>
  );
}
