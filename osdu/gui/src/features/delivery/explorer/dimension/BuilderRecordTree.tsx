import { useMemo, useState } from "react";
import { ChevronRight, CornerDownRight, MoreHorizontal, Search } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import type { DimensionSampleCut } from "../../../../api/explorer";
import { ROLE_GLYPHS } from "../../dimensions/blueprintModel";
import { isRecordReference } from "../../osduDocument";
import { isReadable, namesOf, namesRecords, type Location, type PickAction } from "./dimensionDraft";

/** A role a property plays in the dimension, as its row shows it. */
export interface RowMark {
  role: "key" | "collect" | "label" | "attribute" | "step";
  /** The attribute's name, or what a step leads to. */
  name: string | null;
}

/** What a record's tree offers on each property: the actions, and why one is not offered when it is not. */
export interface TreeOffer {
  actions: (location: Location, value: unknown) => { action: PickAction; label: string; disabled?: string | null }[];
  marks: (path: string) => RowMark[];
}

interface Row {
  location: Location;
  key: string;
  name: string;
  depth: number;
  value: unknown;
  /** An object or a list, which opens. */
  branch: boolean;
  /** Items left out of a long list, when it was cut. */
  more: number;
}

/** The properties a record holds at its root that are its data, shown first; the rest (id, kind, acl, legal, tags) follow. */
const DATA = "data";

const locationKey = (location: Location) => JSON.stringify(location);

/** A short text of an object a list holds: its first few values, so an item of a list says what it is. */
function gist(value: Record<string, unknown>): string {
  const parts: string[] = [];
  for (const [name, held] of Object.entries(value)) {
    if (typeof held === "string" || typeof held === "number" || typeof held === "boolean") {
      const text = String(held);
      parts.push(`${name}: ${isRecordReference(text) ? text.split(":").filter((part) => part !== "").pop() ?? text : text}`);
    }

    if (parts.length === 2) {
      break;
    }
  }

  return parts.join(", ");
}

/** A value as a row shows it: a text, a number, a boolean, or how many a list or an object holds. */
function preview(value: unknown): string {
  if (Array.isArray(value)) {
    return `[${value.length}]`;
  }

  if (value !== null && typeof value === "object") {
    return `{${Object.keys(value).length}}`;
  }

  if (value === null) {
    return "null";
  }

  return String(value);
}

/**
 * The properties of one record as the search holds it, as a tree a person picks from: its data first, opened one level, then
 * the rest of the record; a list opened shows its items by place. Each property the dimension reads is marked with what it
 * is read for, and opening a property's menu offers what it can be made into here. A search of the tree finds a property by
 * its name, its path or its value, and opens what leads to it.
 */
export function BuilderRecordTree({ record, cut, offer, onPick, testId }: {
  record: Record<string, unknown>;
  cut: DimensionSampleCut[];
  offer: TreeOffer;
  onPick: (action: PickAction, location: Location) => void;
  testId: string;
}) {
  const [opened, setOpened] = useState<Set<string>>(() => new Set([locationKey([DATA])]));
  const [find, setFind] = useState("");
  const cuts = useMemo(() => new Map(cut.map((c) => [c.path, c])), [cut]);

  const rows = useMemo(() => {
    const term = find.trim().toLowerCase();
    const all: Row[] = [];
    const visit = (value: unknown, location: Location, depth: number) => {
      const entries: [string | number, unknown][] = Array.isArray(value)
        ? value.map((item, index) => [index, item])
        : Object.entries(value as Record<string, unknown>);
      if (depth === 0) {
        entries.sort(([a], [b]) => (a === DATA ? -1 : b === DATA ? 1 : 0));
      }

      for (const [part, child] of entries) {
        const at = [...location, part];
        const branch = child !== null && typeof child === "object";
        const where = at.join(".");
        const row: Row = {
          location: at,
          key: locationKey(at),
          name: typeof part === "number" ? `[${part}]` : part,
          depth,
          value: child,
          branch,
          more: Array.isArray(child) ? Math.max(0, (cuts.get(where)?.held ?? child.length) - child.length) : 0,
        };
        all.push(row);
        if (branch && (term !== "" || opened.has(row.key))) {
          visit(child, at, depth + 1);
        }
      }
    };

    visit(record, [], 0);
    if (term === "") {
      return all;
    }

    // A search keeps the rows that match and every row leading to one, so a match is seen where it sits.
    const matching = new Set<string>();
    for (const row of all) {
      const path = namesOf(row.location).join(".").toLowerCase();
      if (path.includes(term) || (!row.branch && preview(row.value).toLowerCase().includes(term))) {
        for (let length = 1; length <= row.location.length; length++) {
          matching.add(locationKey(row.location.slice(0, length)));
        }
      }
    }

    return all.filter((row) => matching.has(row.key));
  }, [record, opened, find, cuts]);

  const toggle = (key: string) => setOpened((current) => {
    const next = new Set(current);
    if (!next.delete(key)) {
      next.add(key);
    }

    return next;
  });

  return (
    <div className="flex min-h-0 flex-col" data-testid={testId}>
      <div className="relative border-b border-border/60 px-2 py-1.5">
        <Search className="pointer-events-none absolute left-3.5 top-1/2 size-3.5 -translate-y-1/2 text-muted-foreground" aria-hidden />
        <Input
          value={find}
          onChange={(event) => setFind(event.target.value)}
          placeholder="Find a property or a value"
          className="h-7 pl-7 text-[12px]"
          data-testid={`${testId}-find`}
        />
      </div>
      <div className="min-h-0 flex-1 overflow-auto py-1" role="tree">
        {rows.length === 0 && <p className="px-3 py-2 text-[12px] text-muted-foreground">Nothing here matches.</p>}
        {rows.map((row) => (
          <TreeRow key={row.key} row={row} open={opened.has(row.key) || find.trim() !== ""} offer={offer} onToggle={toggle} onPick={onPick} testId={testId} />
        ))}
      </div>
    </div>
  );
}

function TreeRow({ row, open, offer, onToggle, onPick, testId }: {
  row: Row;
  open: boolean;
  offer: TreeOffer;
  onToggle: (key: string) => void;
  onPick: (action: PickAction, location: Location) => void;
  testId: string;
}) {
  const path = namesOf(row.location).join(".");
  const marks = typeof row.location[row.location.length - 1] === "number" ? [] : offer.marks(path);
  const actions = offer.actions(row.location, row.value);
  const reference = namesRecords(row.value);
  return (
    <div
      role="treeitem"
      aria-expanded={row.branch ? open : undefined}
      className={cn("group flex min-w-0 items-center gap-1 pr-1 text-[12px] hover:bg-muted/60", marks.length > 0 && "bg-primary/[0.06]")}
      style={{ paddingLeft: 6 + row.depth * 14 }}
      data-testid={`${testId}-row`}
      data-path={path}
    >
      {row.branch ? (
        <button type="button" className="flex size-4 shrink-0 items-center justify-center text-muted-foreground" onClick={() => onToggle(row.key)} aria-label={open ? "Close" : "Open"}>
          <ChevronRight className={cn("size-3.5 transition-transform", open && "rotate-90")} />
        </button>
      ) : (
        <span className="size-4 shrink-0" />
      )}
      <span className={cn("shrink-0 font-mono", typeof row.location[row.location.length - 1] === "number" ? "text-muted-foreground" : "text-foreground")}>{row.name}</span>
      {!row.branch && (
        <RichTooltip title={path} body={preview(row.value)} mono>
          <span className={cn("min-w-0 truncate font-mono", reference ? "text-primary" : "text-muted-foreground")} data-testid={`${testId}-value`}>
            {preview(row.value)}
          </span>
        </RichTooltip>
      )}
      {row.branch && <span className="shrink-0 font-mono text-muted-foreground/70">{preview(row.value)}</span>}
      {row.branch && typeof row.location[row.location.length - 1] === "number" && row.value !== null && !Array.isArray(row.value) && (
        <span className="min-w-0 truncate text-[11.5px] text-muted-foreground" data-testid={`${testId}-gist`}>{gist(row.value as Record<string, unknown>)}</span>
      )}
      {row.more > 0 && <span className="shrink-0 text-[11px] text-muted-foreground">+{row.more.toLocaleString("en-US")} not shown</span>}
      <span className="ml-auto flex shrink-0 items-center gap-1">
        {marks.map((mark, index) => <Mark key={index} mark={mark} />)}
        {actions.length > 0 && (!row.branch || isReadable(row.value)) && (
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button
                variant="ghost"
                size="icon-xs"
                className="opacity-60 group-hover:opacity-100 focus-visible:opacity-100"
                aria-label={`Use ${path}`}
                data-testid={`${testId}-menu`}
              >
                <MoreHorizontal />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end" className="w-64">
              <DropdownMenuLabel className="truncate font-mono text-[11.5px] font-normal text-muted-foreground">{path}</DropdownMenuLabel>
              <DropdownMenuSeparator />
              {actions.map(({ action, label, disabled }) => (
                <DropdownMenuItem
                  key={action}
                  disabled={disabled !== null && disabled !== undefined}
                  onSelect={() => onPick(action, row.location)}
                  className="flex-col items-start gap-0.5"
                  data-testid={`${testId}-pick-${action}`}
                >
                  <span className="flex items-center gap-1.5">
                    <ActionGlyph action={action} />
                    {label}
                  </span>
                  {disabled !== null && disabled !== undefined && <span className="pl-5 text-[11px] text-muted-foreground">{disabled}</span>}
                </DropdownMenuItem>
              ))}
            </DropdownMenuContent>
          </DropdownMenu>
        )}
      </span>
    </div>
  );
}

function ActionGlyph({ action }: { action: PickAction }) {
  const Icon = action === "key" ? ROLE_GLYPHS.key.icon
    : action === "collect" ? ROLE_GLYPHS.collect.icon
    : action === "value" ? ROLE_GLYPHS.label.icon
    : action === "attribute" ? ROLE_GLYPHS.attribute.icon
    : CornerDownRight;
  return <Icon className="size-3.5 text-muted-foreground" aria-hidden />;
}

/** What a property is read for, as a quiet chip: its glyph, and the attribute's name. */
function Mark({ mark }: { mark: RowMark }) {
  const glyph = mark.role === "label" ? ROLE_GLYPHS.label : mark.role === "step" ? ROLE_GLYPHS.step : ROLE_GLYPHS[mark.role];
  const Icon = glyph.icon;
  return (
    <span className="inline-flex items-center gap-1 rounded border border-border bg-background px-1 py-px text-[11px] text-muted-foreground" data-testid="builder-mark" data-role={mark.role}>
      <Icon className="size-3" aria-hidden />
      {mark.name ?? glyph.label}
    </span>
  );
}
