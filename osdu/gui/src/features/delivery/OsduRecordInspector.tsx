import { useCallback, useMemo, useState, type ReactNode } from "react";
import { useMutation } from "@tanstack/react-query";
import {
  ArrowLeft, ChevronDown, CircleCheck, ChevronRight, ChevronsDownUp, ChevronsUpDown, Download, Eye, GitCompare, Globe, History, Link2,
  Loader2, Scale, SearchX, UserRoundCog, type LucideIcon,
} from "lucide-react";
import { toast } from "sonner";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuLabel, DropdownMenuRadioGroup, DropdownMenuRadioItem, DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { ResizableHandle, ResizablePanel, ResizablePanelGroup } from "@/components/ui/resizable";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { cn } from "@/lib/utils";
import { isApiError } from "@/api/client";
import type { ComputeTask, ComputeTaskAccepted } from "@/api/types";
import { CopyButton } from "@/components/CopyButton";
import { DataTable, type Column } from "@/components/DataTable";
import { DiffView } from "@/components/DiffView";
import { EmptyState } from "@/components/EmptyState";
import { IconAction } from "@/components/IconAction";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import type { DeliveryOsduRead } from "../../api/delivery";
import {
  canonicalText, differences, downloadJson, fileNameOf, shortValue, withoutOsduFields, withoutVersion, type DifferenceKind, type JsonDifference,
} from "./osduDocument";
import {
  branchPaths, buildModel, describeBranch, documentNode, isReferenceNode, loadLayout, matching, pathSegments, saveLayout, trail,
  type RecordModel, type RecordNode,
} from "./osduRecordModel";
import { RecordName } from "./RecordName";
import { ProblemView, TaskProgress } from "./TemplateSheet";
import { isTerminalTask, useComputeTask } from "./useComputeTask";

/** How many rows of one branch show before the rest wait behind a button, so a curve list of thousands stays usable. */
const PAGE = 100;

/** How many columns a table of items shows; the rest of an item's fields are a click into the item away. */
const TABLE_COLUMNS = 8;

/** The views the detail pane has that are not a block of the record: the whole document, its system fields, its access and legal, its links. */
const DOCUMENT = "document";
const RECORD = "record";
const ACCESS = "access";
const LINKS = "links";

const VIEW_NAMES: Record<string, string> = {
  [DOCUMENT]: "Full document",
  [RECORD]: "System fields",
  [ACCESS]: "Access & legal",
  [LINKS]: "Linked records",
};

/** How a branch of the record is shown: one level as fields (or a table of items), or the whole branch as JSON. */
type ViewMode = "fields" | "json";

/** One record open in the inspector: the read that fetched it, as it stands, and where in the record before it it was found. */
export interface InspectorEntry {
  id: string;
  task: ComputeTask | undefined;
  /** A refusal of the poll itself (not of the read): the task could not be followed. */
  error?: unknown;
  /** The path, in the record before it on the trail, of the value that named this record; absent for the first record. */
  from?: string | null;
}

/** A record earlier on the trail, as the location bar names it: which record, where the link to the next was, and its place. */
interface PriorCrumb {
  id: string;
  from: string | null;
  level: number;
}

/** How a record opens another from a value that names it: the id it names, and the path of that value in this record. */
type OpenLink = (id: string, from: string) => void;

function text(value: unknown): string | null {
  return typeof value === "string" ? value : typeof value === "number" ? String(value) : null;
}

function texts(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : [];
}

/** Text with every occurrence of the search term marked, so a match shows where it is rather than only that it is. */
function Highlight({ text: value, term }: { text: string; term: string }) {
  if (term === "") {
    return <>{value}</>;
  }

  const parts: ReactNode[] = [];
  const lower = value.toLowerCase();
  let from = 0;
  for (let at = lower.indexOf(term, from); at >= 0; at = lower.indexOf(term, from)) {
    if (at > from) {
      parts.push(value.slice(from, at));
    }

    parts.push(<mark key={at} className="rounded-sm bg-warning/40 text-inherit">{value.slice(at, at + term.length)}</mark>);
    from = at + term.length;
  }

  if (from < value.length) {
    parts.push(value.slice(from));
  }

  return <>{parts}</>;
}

/** A list of short values as chips, each carrying the glyph and hover title that say what it is; nothing when there are none. */
function Chips({ values, icon: Icon, what, testId }: { values: string[]; icon: LucideIcon; what: string; testId?: string }) {
  if (values.length === 0) {
    return null;
  }

  return (
    <span className="inline-flex flex-wrap gap-1" data-testid={testId}>
      {values.map((value) => (
        <Badge key={value} variant="outline" className="gap-1 font-mono text-[11px] font-normal" title={what}>
          <Icon className="size-3 text-muted-foreground" />
          {value}
        </Badge>
      ))}
    </span>
  );
}

/**
 * The record's history as one control: the version in view, with every version OSDU keeps of the record a pick away,
 * newest first, the latest and the one this flow's ledger holds as delivered marked. A version older than the latest
 * turns the control amber, so it is plain which record is on screen. A target that keeps no version list says so.
 */
function VersionPicker({ read, shown, ledgerVersion, loading, onPick }: {
  read: DeliveryOsduRead;
  /** The version in view, which a pick replaces. */
  shown: number | null;
  ledgerVersion: number | null;
  /** Whether a picked version is still being read. */
  loading: boolean;
  onPick?: (version: number) => void;
}) {
  const versions = read.versions ?? null;
  if (versions === null) {
    return read.historyError
      ? <span className="text-[11px] text-muted-foreground" title={read.historyError} data-testid="osdu-history-error">version list unavailable</span>
      : <span className="text-[11px] text-muted-foreground" data-testid="osdu-no-history">no version list</span>;
  }

  const latest = versions[0] ?? null;
  const older = shown !== null && latest !== null && shown !== latest;
  const marksOf = (version: number) => [version === latest ? "latest" : null, version === ledgerVersion ? "delivered by this flow" : null].filter((mark): mark is string => mark !== null);
  return (
    <span className="inline-flex items-center gap-2" data-testid="osdu-record-versions">
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button
            variant="outline"
            size="sm"
            className={cn("h-7 gap-1.5 font-normal", older && "border-warning/60 text-warning")}
            disabled={onPick === undefined || versions.length === 0}
            title={`${versions.length} version${versions.length === 1 ? "" : "s"} in OSDU; pick one to see the record as it was then`}
            data-testid="osdu-record-version"
            data-state={shown !== null ? "active" : undefined}
          >
            {loading ? <Loader2 className="animate-spin" /> : <History />}
            {shown === null
              ? <span className="text-muted-foreground">{versions.length === 0 ? "No versions" : "Version"}</span>
              : <span className="font-mono text-[11px] tabular-nums">{shown}</span>}
            {shown !== null && !older && shown === latest && <span className="text-[10px] uppercase tracking-wide text-muted-foreground">latest</span>}
            {shown !== null && shown === ledgerVersion && <CircleCheck className="size-3.5 text-success" aria-label="delivered by this flow" />}
            {older && <span className="text-[10px] uppercase tracking-wide" data-testid="osdu-version-older">older version</span>}
            <ChevronDown className="size-3.5 opacity-60" />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" className="max-h-80 overflow-y-auto" data-testid="osdu-version-menu">
          <DropdownMenuLabel className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">{`${versions.length} version${versions.length === 1 ? "" : "s"} in OSDU, newest first`}</DropdownMenuLabel>
          <DropdownMenuSeparator />
          <DropdownMenuRadioGroup value={shown === null ? "" : String(shown)} onValueChange={(value) => onPick?.(Number(value))}>
            {versions.map((version) => (
              <DropdownMenuRadioItem key={version} value={String(version)} className="gap-2 font-mono text-[12px] tabular-nums" data-testid="osdu-version">
                {version}
                {marksOf(version).map((mark) => <span key={mark} className={cn("font-sans text-[10px] uppercase tracking-wide", mark === "latest" ? "text-muted-foreground" : "text-success")}>{mark}</span>)}
              </DropdownMenuRadioItem>
            ))}
          </DropdownMenuRadioGroup>
        </DropdownMenuContent>
      </DropdownMenu>
      {ledgerVersion !== null && versions.length > 0 && !versions.includes(ledgerVersion) && (
        <span className="text-[11px] text-warning" title={`The ledger holds version ${ledgerVersion} as delivered by this flow, and OSDU no longer lists it.`} data-testid="osdu-version-missing">delivered version gone</span>
      )}
    </span>
  );
}

/**
 * A value that names another OSDU record: the name itself is the link, and a click opens that record here, read
 * through the same flow's route, after this one on the trail. The copy beside it hands over the id verbatim.
 */
function ReferenceLink({ value, path, ownId, onOpenLink, opening, term, className }: {
  value: string; path: string; ownId: string | null; onOpenLink?: OpenLink; opening?: string | null; term: string; className?: string;
}) {
  const id = withoutVersion(value);
  const self = ownId !== null && id === withoutVersion(ownId);
  const matched = term !== "" && value.toLowerCase().includes(term);
  const name = matched
    ? <span className="min-w-0 break-all font-mono text-[12px]"><Highlight text={value} term={term} /></span>
    : <RecordName id={value} className="text-[12px]" />;
  return (
    <span className={cn("inline-flex min-w-0 max-w-full items-center gap-1", className)} data-testid="osdu-record-link" data-value={value}>
      {onOpenLink !== undefined && !self
        ? (
          <button
            type="button"
            className="inline-flex min-w-0 max-w-full items-center gap-1 rounded-sm text-primary underline-offset-2 hover:underline focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
            onClick={(event) => { event.stopPropagation(); onOpenLink(id, path); }}
            disabled={opening === id}
            title="Open this record here"
            data-testid="osdu-link-read"
          >
            {opening === id ? <Loader2 className="size-3.5 shrink-0 animate-spin" /> : <Link2 className="size-3.5 shrink-0" />}
            {name}
          </button>
        )
        : <span className="inline-flex min-w-0 max-w-full items-center gap-1 text-muted-foreground">{name}{self && <span className="text-[11px]">(this record)</span>}</span>}
      <CopyButton iconOnly label="Copy the id" text={value} testId="copy-osdu-link" />
    </span>
  );
}

/** A leaf as it reads: a reference as a link, text as text, numbers and booleans tinted, the search term marked. */
function Leaf({ node, term, ownId, onOpenLink, opening, compact = false }: {
  node: RecordNode; term: string; ownId: string | null; onOpenLink?: OpenLink; opening?: string | null; compact?: boolean;
}) {
  const value = node.value;
  if (isReferenceNode(node)) {
    return <ReferenceLink value={node.value} path={node.path} ownId={ownId} onOpenLink={onOpenLink} opening={opening} term={term} />;
  }

  const matched = term !== "" && node.valueText.includes(term);
  if (typeof value === "string") {
    return matched
      ? <span className="min-w-0 break-all text-[12px]"><Highlight text={value} term={term} /></span>
      : <TruncatedText text={value} maxWidth={compact ? 220 : 640} copy={!compact && value.length > 40} className="text-[12px]" />;
  }

  if (typeof value === "number") {
    return <span className="font-mono text-[12px] tabular-nums text-info"><Highlight text={String(value)} term={term} /></span>;
  }

  if (typeof value === "boolean") {
    return <span className="font-mono text-[12px] text-warning"><Highlight text={String(value)} term={term} /></span>;
  }

  return <span className="font-mono text-[12px] text-muted-foreground"><Highlight text="null" term={term} /></span>;
}

/** One row of the outline: a branch of the record, or one of the views that are not a branch. */
function OutlineRow({ depth, selected, open, onToggle, onSelect, label, mono = false, detail, hits, testId }: {
  depth: number; selected: boolean; open?: boolean; onToggle?: () => void; onSelect: () => void;
  label: ReactNode; /** A label that is one of the record's own keys, set in the mono face as keys are everywhere else. */ mono?: boolean;
  detail?: string; hits?: number; testId?: string;
}) {
  return (
    <div
      className={cn("group flex min-w-0 items-center gap-1 rounded-md py-1 pr-2 text-[12px] hover:bg-accent/50", selected && "bg-accent text-accent-foreground")}
      style={{ paddingLeft: 6 + depth * 14 }}
      data-testid={testId}
      data-state={selected ? "active" : undefined}
    >
      {onToggle !== undefined
        ? (
          <button type="button" className="flex size-4 shrink-0 items-center justify-center text-muted-foreground hover:text-foreground" onClick={(event) => { event.stopPropagation(); onToggle(); }} aria-expanded={open} aria-label={open ? "Fold" : "Unfold"}>
            {open ? <ChevronDown className="size-3.5" /> : <ChevronRight className="size-3.5" />}
          </button>
        )
        : <span className="size-4 shrink-0" aria-hidden="true" />}
      <button type="button" className="flex min-w-0 flex-1 items-center gap-1.5 text-left" onClick={onSelect}>
        <span className={cn("truncate", mono && "font-mono")}>{label}</span>
        {detail !== undefined && <span className="shrink-0 text-[11px] text-muted-foreground">{detail}</span>}
        {hits !== undefined && hits > 0 && <span className="ml-auto shrink-0 rounded-full bg-warning/25 px-1.5 text-[10px] font-medium tabular-nums">{hits}</span>}
      </button>
    </div>
  );
}

/** A caption over a group of outline rows, in the workbench's own navigation voice. */
function OutlineGroup({ label }: { label: string }) {
  return <div className="px-2 pt-2 pb-0.5 text-[10px] font-medium uppercase tracking-wider text-muted-foreground">{label}</div>;
}

function CrumbSeparator() {
  return <ChevronRight className="size-3.5 shrink-0 text-muted-foreground/50" aria-hidden="true" />;
}

/** One step of the location as a crumb: the current step in full weight, an earlier one a step back to it. */
function Crumb({ label, mono = true, current = false, onClick, title }: { label: ReactNode; mono?: boolean; current?: boolean; onClick?: () => void; title?: string }) {
  const face = cn("shrink-0", mono && "font-mono");
  return current || onClick === undefined
    ? <span className={cn(face, current ? "font-medium text-foreground" : "text-muted-foreground")} title={title} aria-current={current ? "location" : undefined}>{label}</span>
    : <button type="button" className={cn(face, "rounded-sm text-muted-foreground hover:text-foreground hover:underline")} onClick={onClick} title={title}>{label}</button>;
}

/**
 * The records before the current one on the trail, each named once and followed by the path of the value that led on
 * from it, so the location reads as one path across records: log, data, WellboreID, then the wellbore. Any step of
 * an earlier record goes back to it, as it was left.
 */
function PriorCrumbs({ prior, onBack }: { prior: PriorCrumb[]; onBack: (level: number) => void }) {
  return (
    <>
      {prior.map((crumb) => (
        <span key={crumb.level} className="contents">
          <button type="button" className="min-w-0 max-w-[200px] rounded-sm text-muted-foreground [flex-shrink:4] hover:text-foreground hover:underline" onClick={() => onBack(crumb.level)} title={`Back to ${crumb.id}`}>
            <RecordName id={crumb.id} />
          </button>
          {crumb.from !== null && pathSegments(crumb.from).map((segment, index) => (
            <span key={index} className="contents">
              <CrumbSeparator />
              <Crumb label={segment.index ? `[${segment.key}]` : segment.key} onClick={() => onBack(crumb.level)} title={`Back to ${crumb.id}, where ${crumb.from} named the next record`} />
            </span>
          ))}
          <CrumbSeparator />
        </span>
      ))}
    </>
  );
}

/**
 * The one bar that says where the reader is and holds what acts on it: a step back while a linked record is open, the
 * location from the first record to the branch in view, and on the right the view, the version, and the page's own
 * controls over the read. Nothing below it repeats the record's name or its place.
 */
function LocationBar({ level, onBack, location, controls }: {
  level: number;
  onBack: (level: number) => void;
  /** The crumbs, from the first record to where the reader is. */
  location: ReactNode;
  controls?: ReactNode;
}) {
  return (
    <div className="flex min-h-11 items-center gap-x-3 border-b px-2 py-1.5">
      {level > 0 && (
        <IconAction label="Back to the record before" icon={<ArrowLeft />} variant="ghost" className="size-7 shrink-0" onClick={() => onBack(level - 1)} data-testid="osdu-linked-close" />
      )}
      <nav className={cn("flex min-w-0 flex-1 flex-nowrap items-center gap-x-1 overflow-hidden whitespace-nowrap text-[12px]", level === 0 && "pl-1")} aria-label="Where in OSDU" data-testid="osdu-trail">
        {location}
      </nav>
      {controls !== undefined && <div className="ml-auto flex shrink-0 items-center gap-1.5">{controls}</div>}
    </div>
  );
}

/** Rows of caption and value, the way the two envelope views read. */
function CaptionRows({ rows, testId }: { rows: { label: string; value: ReactNode }[]; testId: string }) {
  return (
    <div className="flex flex-col" data-testid={testId}>
      {rows.map((row) => (
        <div key={row.label} className="flex min-w-0 items-baseline gap-3 border-b px-3 py-1.5 last:border-b-0">
          <span className="w-32 shrink-0 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">{row.label}</span>
          <span className="min-w-0 flex-1">{row.value ?? <span className="text-muted-foreground">-</span>}</span>
        </div>
      ))}
    </div>
  );
}

/** The record's system fields, as OSDU keeps them on every record: what it is, which version, who wrote it and when; and what the read itself was. */
function RecordView({ read, record }: { read: DeliveryOsduRead; record: Record<string, unknown> }) {
  return (
    <CaptionRows
      testId="osdu-record-fields"
      rows={[
        { label: "Id", value: <TruncatedText text={read.targetId} mono maxWidth={560} copy title="OSDU id" /> },
        { label: "Kind", value: <TruncatedText text={text(record.kind)} mono maxWidth={560} copy title="Kind" /> },
        { label: "Version", value: <span className="font-mono text-[12px] tabular-nums">{text(record.version) ?? "-"}</span> },
        {
          label: "Created",
          value: <span className="text-[12px]"><RelativeTime value={text(record.createTime)} absolute />{text(record.createUser) && <span className="text-muted-foreground">{` by ${text(record.createUser)}`}</span>}</span>,
        },
        {
          label: "Last modified",
          value: <span className="text-[12px]"><RelativeTime value={text(record.modifyTime)} absolute />{text(record.modifyUser) && <span className="text-muted-foreground">{` by ${text(record.modifyUser)}`}</span>}</span>,
        },
        { label: "Read", value: <span className="text-[12px]"><RelativeTime value={read.readUtc} absolute />{` through ${read.flow}`}</span> },
        { label: "Correlation id", value: <TruncatedText text={read.correlationId ?? null} mono maxWidth={360} copy={Boolean(read.correlationId)} /> },
      ]}
    />
  );
}

/** The record's access and legal, as OSDU's `acl` and `legal` blocks hold them: who may see and own it, and under which legal tags. */
function AccessView({ record }: { record: Record<string, unknown> }) {
  const acl = (record.acl ?? {}) as Record<string, unknown>;
  const legal = (record.legal ?? {}) as Record<string, unknown>;
  return (
    <CaptionRows
      testId="osdu-access"
      rows={[
        { label: "Viewers", value: <Chips values={texts(acl.viewers)} icon={Eye} what="viewer group" testId="osdu-record-viewers" /> },
        { label: "Owners", value: <Chips values={texts(acl.owners)} icon={UserRoundCog} what="owner group" testId="osdu-record-owners" /> },
        { label: "Legal tags", value: <Chips values={texts(legal.legaltags)} icon={Scale} what="legal tag" testId="osdu-record-legal" /> },
        { label: "Countries", value: <Chips values={texts(legal.otherRelevantDataCountries)} icon={Globe} what="relevant country" /> },
        { label: "Legal status", value: <span className="text-[12px]">{text(legal.status) ?? "-"}</span> },
      ]}
    />
  );
}

/** The records this one refers to, each opened by its name, with the paths that name it, each a step to that place. */
function LinksView({ model, ownId, onOpenLink, opening, onSelect }: { model: RecordModel; ownId: string | null; onOpenLink?: OpenLink; opening?: string | null; onSelect: (path: string) => void }) {
  if (model.references.length === 0) {
    return <EmptyState title="No linked records" description="No value of this record names another OSDU record." />;
  }

  return (
    <div className="flex flex-col">
      {model.references.map((reference) => (
        <div key={reference.id} className="flex min-w-0 items-baseline gap-3 border-b px-3 py-1.5 last:border-b-0">
          <ReferenceLink value={reference.id} path={reference.paths[0]} ownId={ownId} onOpenLink={onOpenLink} opening={opening} term="" className="min-w-0 flex-1" />
          <span className="flex shrink-0 flex-wrap justify-end gap-x-2 text-[11px] text-muted-foreground">
            {reference.paths.map((path) => {
              const holder = model.byPath.get(path)?.parent ?? "";
              return <button key={path} type="button" className="font-mono hover:text-foreground hover:underline" onClick={() => onSelect(holder === "" ? path : holder)} title="Go to where the record names it">{path}</button>;
            })}
          </span>
        </div>
      ))}
    </div>
  );
}

/** A branch as rows: each field or item with its value, a nested branch as a step into it. */
function FieldRows({ node, term, hits, ownId, onOpenLink, opening, onSelect, shown, onShowMore }: {
  node: RecordNode; term: string; hits: Set<string>; ownId: string | null; onOpenLink?: OpenLink; opening?: string | null;
  onSelect: (path: string) => void; shown: number; onShowMore: () => void;
}) {
  return (
    <div className="flex flex-col">
      {node.children.slice(0, shown).map((child) => (
        <div key={child.path} className={cn("flex min-w-0 items-baseline gap-3 border-b px-3 py-1.5 last:border-b-0", hits.has(child.path) && "bg-warning/10")}>
          <span className="w-56 shrink-0 truncate font-mono text-[12px] text-muted-foreground">
            <Highlight text={node.kind === "array" ? `[${child.key}]` : child.key} term={term} />
          </span>
          <span className="min-w-0 flex-1">
            {child.kind === "leaf"
              ? <Leaf node={child} term={term} ownId={ownId} onOpenLink={onOpenLink} opening={opening} />
              : (
                <button type="button" className="inline-flex items-center gap-1 text-[12px] text-primary hover:underline" onClick={() => onSelect(child.path)} data-testid="osdu-drill">
                  {describeBranch(child)}
                  <ChevronRight className="size-3.5" />
                </button>
              )}
          </span>
        </div>
      ))}
      {shown < node.children.length && (
        <div className="px-3 py-2">
          <Button variant="outline" size="sm" className="h-7" onClick={onShowMore} data-testid="osdu-show-more">{`Show the other ${node.children.length - shown}`}</Button>
        </div>
      )}
    </div>
  );
}

/** The columns a table of items has: the keys the items share, the most common first, capped so the table stays readable. */
function tableColumns(items: RecordNode[]): string[] {
  const counts = new Map<string, number>();
  for (const item of items) {
    for (const child of item.children) {
      counts.set(child.key, (counts.get(child.key) ?? 0) + 1);
    }
  }

  return [...counts.entries()].sort((a, b) => b[1] - a[1]).slice(0, TABLE_COLUMNS).map(([key]) => key);
}

/** An array of objects as a table: one row per item, one column per shared field, each row a step into its item. */
function ItemTable({ node, term, hits, ownId, onOpenLink, opening, onSelect, shown, onShowMore }: {
  node: RecordNode; term: string; hits: Set<string>; ownId: string | null; onOpenLink?: OpenLink; opening?: string | null;
  onSelect: (path: string) => void; shown: number; onShowMore: () => void;
}) {
  const keys = useMemo(() => tableColumns(node.children), [node]);
  const columns: Column<RecordNode>[] = [
    { id: "#", header: "#", width: 48, align: "right", render: (item) => <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{item.key}</span> },
    ...keys.map((key): Column<RecordNode> => ({
      id: key,
      header: key,
      render: (item) => {
        const field = item.children.find((child) => child.key === key);
        if (field === undefined) {
          return <span className="text-muted-foreground">-</span>;
        }

        return field.kind === "leaf"
          ? <Leaf node={field} term={term} ownId={ownId} onOpenLink={onOpenLink} opening={opening} compact />
          : <span className="text-[11px] text-muted-foreground">{describeBranch(field)}</span>;
      },
    })),
    {
      id: "more",
      header: "",
      render: (item) => {
        const rest = item.children.length - item.children.filter((child) => keys.includes(child.key)).length;
        return <span className="text-[11px] text-muted-foreground">{rest > 0 ? `+${rest}` : ""}</span>;
      },
    },
  ];
  return (
    <div className="flex flex-col gap-2 p-2">
      <DataTable
        columns={columns}
        rows={node.children.slice(0, shown)}
        rowKey={(item) => item.path}
        onRowClick={(item) => onSelect(item.path)}
        rowSx={(item) => (hits.has(item.path) || item.children.some((child) => hits.has(child.path)) ? { backgroundColor: "color-mix(in oklab, var(--warning) 10%, transparent)" } : undefined)}
        emptyMessage="An empty list"
        minWidth={Math.max(480, 120 * (keys.length + 1))}
        data-testid="osdu-item-table"
      />
      {shown < node.children.length && (
        <div>
          <Button variant="outline" size="sm" className="h-7" onClick={onShowMore} data-testid="osdu-show-more">{`Show the other ${node.children.length - shown}`}</Button>
        </div>
      )}
    </div>
  );
}

const PUNCTUATION = "text-muted-foreground";

/**
 * A branch of the record as JSON that can be worked with, not only read: every object and list folds at its caret, a
 * key that holds a branch steps into it (and the brace of a list's item into that item), and a value that names another
 * OSDU record opens it. Lists show their first hundred items and the rest on a click; the search term is marked.
 */
function JsonView({ node, term, hits, ownId, onOpenLink, opening, onSelect }: {
  node: RecordNode; term: string; hits: Set<string>; ownId: string | null; onOpenLink?: OpenLink; opening?: string | null; onSelect: (path: string) => void;
}) {
  const [folded, setFolded] = useState<ReadonlySet<string>>(() => new Set());
  const [shownItems, setShownItems] = useState<ReadonlyMap<string, number>>(() => new Map());
  const fold = (path: string) => setFolded((current) => {
    const next = new Set(current);
    if (next.has(path)) {
      next.delete(path);
    } else {
      next.add(path);
    }

    return next;
  });

  const lines: ReactNode[] = [];
  const line = (key: string, depth: number, content: ReactNode, caret?: { open: boolean; path: string }, hit = false) => {
    lines.push(
      <div key={key} className={cn("flex min-w-0 items-start rounded-sm pr-2", hit && "bg-warning/10")} style={{ paddingLeft: depth * 16 }}>
        <span className="flex h-5 w-4 shrink-0 items-center justify-center">
          {caret !== undefined && (
            <button type="button" className="text-muted-foreground hover:text-foreground" onClick={() => fold(caret.path)} aria-label={caret.open ? "Fold" : "Unfold"} data-testid="osdu-json-fold">
              {caret.open ? <ChevronDown className="size-3.5" /> : <ChevronRight className="size-3.5" />}
            </button>
          )}
        </span>
        <span className="min-w-0 whitespace-pre-wrap break-all">{content}</span>
      </div>,
    );
  };

  const leafValue = (leaf: RecordNode): ReactNode => {
    const value = leaf.value;
    if (isReferenceNode(leaf)) {
      const id = withoutVersion(leaf.value);
      const self = ownId !== null && id === withoutVersion(ownId);
      return (
        <span>
          <span className="text-foreground">&quot;</span>
          {onOpenLink !== undefined && !self
            ? (
              <button type="button" className="text-left text-primary underline-offset-2 hover:underline" onClick={() => onOpenLink(id, leaf.path)} disabled={opening === id} title="Open this record here" data-testid="osdu-json-link">
                <Highlight text={leaf.value} term={term} />
              </button>
            )
            : <span className="text-foreground"><Highlight text={leaf.value} term={term} /></span>}
          <span className="text-foreground">&quot;</span>
        </span>
      );
    }

    if (typeof value === "string") {
      return <span className="text-foreground">&quot;<Highlight text={value} term={term} />&quot;</span>;
    }

    if (typeof value === "number") {
      return <span className="tabular-nums text-info"><Highlight text={String(value)} term={term} /></span>;
    }

    if (typeof value === "boolean") {
      return <span className="text-warning"><Highlight text={String(value)} term={term} /></span>;
    }

    return <span className="text-muted-foreground">null</span>;
  };

  const walk = (current: RecordNode, depth: number, keyOf: RecordNode | null, last: boolean) => {
    const comma = last ? null : <span className={PUNCTUATION}>,</span>;
    const label = keyOf === null
      ? null
      : current.kind === "leaf"
        ? <span className="text-muted-foreground">&quot;<Highlight text={current.key} term={term} />&quot;<span className={PUNCTUATION}>: </span></span>
        : (
          <span>
            <button type="button" className="text-muted-foreground hover:text-primary hover:underline" onClick={() => onSelect(current.path)} title={`Go to ${current.path}`} data-testid="osdu-json-key">
              &quot;<Highlight text={current.key} term={term} />&quot;
            </button>
            <span className={PUNCTUATION}>: </span>
          </span>
        );
    const hit = hits.has(current.path);
    if (current.kind === "leaf") {
      line(current.path, depth, <>{label}{leafValue(current)}{comma}</>, undefined, hit);
      return;
    }

    const [open, close] = current.kind === "array" ? ["[", "]"] : ["{", "}"];
    // An item of a list has no key to step into it by, so its opening brace does that work.
    const opener = keyOf === null && depth > 0
      ? <button type="button" className={cn(PUNCTUATION, "hover:text-primary")} onClick={() => onSelect(current.path)} title={`Go to ${current.path}`}>{open}</button>
      : <span className={PUNCTUATION}>{open}</span>;
    if (current.children.length === 0) {
      line(current.path, depth, <>{label}<span className={PUNCTUATION}>{open}{close}</span>{comma}</>, undefined, hit);
      return;
    }

    if (folded.has(current.path)) {
      line(
        current.path,
        depth,
        <>
          {label}{opener}
          <button type="button" className="mx-1 rounded bg-muted px-1 text-[11px] text-muted-foreground hover:text-foreground" onClick={() => fold(current.path)}>{describeBranch(current)}</button>
          <span className={PUNCTUATION}>{close}</span>{comma}
        </>,
        { open: false, path: current.path },
        hit,
      );
      return;
    }

    line(`${current.path}:open`, depth, <>{label}{opener}</>, { open: true, path: current.path }, hit);
    const limit = current.kind === "array" ? Math.min(current.children.length, shownItems.get(current.path) ?? PAGE) : current.children.length;
    current.children.slice(0, limit).forEach((child, index) => {
      walk(child, depth + 1, current.kind === "object" ? child : null, index === current.children.length - 1);
    });
    if (limit < current.children.length) {
      lines.push(
        <div key={`${current.path}:more`} style={{ paddingLeft: (depth + 1) * 16 + 16 }}>
          <button type="button" className="text-[11px] text-primary hover:underline" onClick={() => setShownItems((was) => new Map(was).set(current.path, current.children.length))} data-testid="osdu-json-more">
            {`${current.children.length - limit} more items`}
          </button>
        </div>,
      );
    }

    line(`${current.path}:close`, depth, <><span className={PUNCTUATION}>{close}</span>{comma}</>);
  };

  walk(node, 0, null, true);
  return (
    <div className="relative p-2 font-mono text-[12px] leading-5" data-testid="osdu-json">
      <div className="sticky top-0 z-10 float-right">
        <CopyButton iconOnly label="Copy this branch as JSON" text={JSON.stringify(node.value, null, 2)} testId="copy-osdu-json" />
      </div>
      {lines}
    </div>
  );
}

const CHANGE_LABELS: Record<DifferenceKind, string> = {
  changed: "changed",
  onlyInOsdu: "added since",
  onlyInPreview: "removed since",
  placeholder: "changed",
};

const CHANGE_TONES: Record<DifferenceKind, string> = {
  changed: "bg-info/12 text-info",
  onlyInOsdu: "bg-success/15 text-success",
  onlyInPreview: "bg-warning/15 text-warning",
  placeholder: "bg-info/12 text-info",
};

/**
 * What changed between a picked version and the latest: the count of each kind of change, the two documents side by
 * side with the unchanged stretches folded away, and every changed value with its path, so a reader sees at once
 * what a later delivery (or someone else) did to the record.
 */
function CompareView({ latest, latestVersion, picked, pickedVersion }: {
  latest: Record<string, unknown>; latestVersion: number | null; picked: Record<string, unknown>; pickedVersion: number;
}) {
  const compared = useMemo(() => {
    const then = withoutOsduFields(picked);
    const now = withoutOsduFields(latest);
    // The picked version is the earlier text and the latest the later, so "added since" reads as the record grew.
    return { ...differences(now, then), original: canonicalText(then), modified: canonicalText(now) };
  }, [latest, picked]);
  const columns: Column<JsonDifference>[] = [
    { id: "path", header: "Path", render: (row) => <span className="font-mono text-[12px] break-all">{row.path || "(the record)"}</span> },
    { id: "kind", header: "", render: (row) => <Badge variant="secondary" className={CHANGE_TONES[row.kind]}>{CHANGE_LABELS[row.kind]}</Badge> },
    { id: "then", header: `Version ${pickedVersion}`, fill: true, render: (row) => <TruncatedText text={row.preview === undefined ? null : shortValue(row.preview)} mono maxWidth={360} /> },
    { id: "now", header: latestVersion === null ? "Latest" : `Latest (${latestVersion})`, fill: true, render: (row) => <TruncatedText text={row.osdu === undefined ? null : shortValue(row.osdu)} mono maxWidth={360} /> },
  ];
  return (
    <div className="flex min-h-0 flex-col gap-3" data-testid="osdu-version-compare">
      <div className="flex flex-wrap items-center gap-2 text-[13px]" data-testid="osdu-version-compare-counts">
        {compared.items.length === 0
          ? <Badge variant="secondary" className="bg-success/15 text-success">Nothing changed between the two versions</Badge>
          : (["changed", "onlyInOsdu", "onlyInPreview"] as DifferenceKind[]).map((kind) => {
            const count = compared.items.filter((item) => item.kind === kind || (kind === "changed" && item.kind === "placeholder")).length;
            return count === 0 ? null : <Badge key={kind} variant="secondary" className={CHANGE_TONES[kind]}>{`${count} ${CHANGE_LABELS[kind]}`}</Badge>;
          })}
        {compared.truncated && <span className="text-muted-foreground">(the first {compared.items.length} differences)</span>}
      </div>
      <DiffView
        original={compared.original}
        modified={compared.modified}
        language="json"
        height={440}
        foldUnchanged
        sideLabels={{ original: `Version ${pickedVersion}`, modified: latestVersion === null ? "Latest" : `Latest (${latestVersion})` }}
        data-testid="osdu-version-diff"
      />
      {compared.items.length > 0 && (
        <DataTable columns={columns} rows={compared.items} rowKey={(row) => `${row.kind}|${row.path}`} emptyMessage="No differences." data-testid="osdu-version-differences" />
      )}
    </div>
  );
}

/**
 * The outline row that stands for a path: the path itself when the outline shows it, otherwise the nearest branch
 * above it that does. The outline shows a record's objects but not the items of its lists, so an item opened from a
 * table keeps its list marked on the left.
 */
function outlineRowFor(model: RecordModel, path: string): string {
  let row = path;
  for (const crumb of trail(model, path)) {
    row = crumb.path;
    if (crumb.kind === "array") {
      break;
    }
  }

  return row;
}

/**
 * One record as an inspector: an outline of its branches on the left that never moves, and on the right one level of
 * it at a time, as fields or as JSON, so a record of ten thousand values is read the way a file tree is, never as one
 * tall page. Where the reader is lives in one place, the location bar, from the first record on the trail to the
 * branch in view. A picked version replaces the record in place, with the outline and the place in it kept.
 */
function RecordInspector({ read, level, prior, ledgerVersion, onOpenLink, onBack, readVersion, opening, actions }: {
  /** The read of the record at its latest. */
  read: DeliveryOsduRead & { record: Record<string, unknown> };
  level: number;
  /** The records before this one on the trail. */
  prior: PriorCrumb[];
  ledgerVersion: number | null;
  onOpenLink?: OpenLink;
  onBack: (level: number) => void;
  /** Queues a read of this record at one of its versions; absent where it cannot be asked for. */
  readVersion?: (version: number) => Promise<ComputeTaskAccepted>;
  opening?: string | null;
  /** The page's own controls over the read (read again, open in a window), kept on the location bar. */
  actions?: ReactNode;
}) {
  const [picked, setPicked] = useState<{ version: number; taskId: string } | null>(null);
  const [compareOpen, setCompareOpen] = useState(false);
  const pickedTask = useComputeTask(picked?.taskId ?? null);
  const pick = useMutation({
    mutationFn: (version: number) => readVersion!(version),
    onSuccess: (accepted, version) => setPicked({ version, taskId: accepted.taskId }),
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });
  const latestVersion = read.readVersion ?? read.version ?? null;
  const pickedRead = picked !== null && isTerminalTask(pickedTask.data) && pickedTask.data?.status === "succeeded"
    ? (pickedTask.data.result as DeliveryOsduRead | null)
    : null;
  const pickedRecord = pickedRead?.found === true && pickedRead.record ? pickedRead.record : null;
  const pickedFailure = picked !== null && isTerminalTask(pickedTask.data)
    ? (pickedTask.data?.status !== "succeeded" ? (pickedTask.data?.error ?? "the read did not answer") : pickedRecord === null ? `OSDU holds no version ${picked.version} of the record` : null)
    : pickedTask.isError ? String(pickedTask.error) : null;
  const pickedLoading = pick.isPending || (picked !== null && !isTerminalTask(pickedTask.data) && !pickedTask.isError);
  // The record in view: the picked version once it has arrived, the latest until then and when the pick is the latest.
  const showingPicked = pickedRecord !== null && picked !== null && picked.version !== latestVersion;
  const shownRead = showingPicked && pickedRead !== null ? pickedRead : read;
  const record = showingPicked ? pickedRecord : read.record;
  const shownVersion = showingPicked ? picked.version : latestVersion;

  const kind = text(record.kind);
  const model = useMemo(() => buildModel(record, read.targetId), [record, read.targetId]);
  const whole = useMemo(() => documentNode(record), [record]);
  const [chosen, setChosen] = useState<string>(() => (model.sections.length > 0 ? model.sections[0].path : RECORD));
  const [mode, setMode] = useState<ViewMode>("fields");
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() => loadLayout(kind));
  const [search, setSearch] = useState("");
  const [shownCounts, setShownCounts] = useState<ReadonlyMap<string, number>>(() => new Map());
  const [matchAt, setMatchAt] = useState(0);
  // A version may lack the branch chosen in another: the record's first section stands in, and the choice is kept.
  const selected = chosen in VIEW_NAMES || model.byPath.has(chosen) ? chosen : (model.sections[0]?.path ?? RECORD);
  const term = search.trim().toLowerCase();
  const found = useMemo(() => (term === "" ? null : matching(model, term)), [model, term]);
  const hits = found?.hits ?? new Set<string>();
  const hitList = useMemo(() => (found === null ? [] : [...found.hits]), [found]);

  const remember = useCallback((next: ReadonlySet<string>) => {
    setExpanded(next);
    saveLayout(kind, next);
  }, [kind]);
  const isOpen = (path: string) => expanded.has(path) || (found !== null && found.holding.has(path));
  const toggle = (path: string) => {
    const next = new Set(expanded);
    if (next.has(path)) {
      next.delete(path);
    } else {
      next.add(path);
    }

    remember(next);
  };
  const select = (path: string) => {
    setChosen(path);
    // Selecting a branch opens the way to it in the outline, so what is shown is always marked on the left.
    if (model.byPath.has(path)) {
      const next = new Set(expanded);
      for (const crumb of trail(model, path)) {
        if (crumb.kind === "object") {
          next.add(crumb.path);
        }
      }

      remember(next);
    }
  };
  const goFromDocument = (path: string) => {
    if (model.byPath.has(path)) {
      setMode("json");
      select(path);
    } else if (path.startsWith("acl") || path.startsWith("legal")) {
      select(ACCESS);
    }
  };
  const nextMatch = () => {
    if (hitList.length === 0) {
      return;
    }

    const at = matchAt % hitList.length;
    const hit = model.byPath.get(hitList[at]);
    if (hit !== undefined) {
      select(hit.kind === "leaf" ? (hit.parent === "" ? hit.path : hit.parent) : hit.path);
    }

    setMatchAt(at + 1);
  };
  const pickVersion = (version: number) => {
    if (version === latestVersion) {
      setPicked(null);
    } else if (picked?.version !== version) {
      pick.mutate(version);
    }
  };

  const node = model.byPath.get(selected);
  const marked = node === undefined ? selected : outlineRowFor(model, node.path);
  const renderOutline = (branch: RecordNode, depth: number): ReactNode => {
    if (branch.kind === "leaf") {
      return null;
    }

    // An array's items are the detail pane's to show, as a table or a list: the outline stays the record's shape
    // (its sections and the objects within them) rather than one row per curve.
    const branches = branch.kind === "array" ? [] : branch.children.filter((child) => child.kind !== "leaf");
    const holding = found?.holding.get(branch.path);
    if (found !== null && holding === undefined && marked !== branch.path) {
      return null;
    }

    const open = isOpen(branch.path);
    return (
      <div key={branch.path}>
        <OutlineRow
          depth={depth}
          selected={marked === branch.path}
          open={branches.length > 0 ? open : undefined}
          onToggle={branches.length > 0 ? () => toggle(branch.path) : undefined}
          onSelect={() => select(branch.path)}
          label={branch.key}
          mono
          detail={describeBranch(branch)}
          hits={holding}
          testId="osdu-outline-branch"
        />
        {open && branches.map((child) => renderOutline(child, depth + 1))}
      </div>
    );
  };

  const shown = node === undefined ? 0 : Math.min(node.children.length, shownCounts.get(node.path) ?? PAGE);
  const showMore = () => { if (node !== undefined) { setShownCounts((current) => new Map(current).set(node.path, node.children.length)); } };
  const isTable = node !== undefined && node.kind === "array" && node.children.length > 0 && node.children.every((child) => child.kind === "object");
  const crumbs = node === undefined ? [] : trail(model, node.path);

  const location = (
    <>
      <PriorCrumbs prior={prior} onBack={onBack} />
      <button
        type="button"
        className={cn("min-w-0 max-w-[280px] shrink rounded-sm hover:underline", selected === RECORD ? "text-foreground" : "text-muted-foreground hover:text-foreground")}
        onClick={() => select(RECORD)}
        title="The record's system fields"
        data-testid="osdu-crumb-record"
      >
        <RecordName id={read.targetId} kind={kind} className="font-medium" />
      </button>
      {node === undefined
        ? selected !== RECORD && <><CrumbSeparator /><Crumb label={VIEW_NAMES[selected]} mono={false} current /></>
        : crumbs.map((crumb, index) => {
          const isItem = model.byPath.get(crumb.parent)?.kind === "array";
          return (
            <span key={crumb.path} className="contents">
              <CrumbSeparator />
              <Crumb label={isItem ? `[${crumb.key}]` : crumb.key} current={index === crumbs.length - 1} onClick={() => select(crumb.path)} />
            </span>
          );
        })}
      {node !== undefined && <span className="shrink-0"><CopyButton iconOnly label="Copy the path" text={node.path} testId="copy-osdu-path" /></span>}
    </>
  );

  const controls = (
    <>
      {node !== undefined && (
        <ToggleGroup type="single" value={mode} onValueChange={(value) => { if (value === "fields" || value === "json") { setMode(value); } }} variant="outline" size="sm" data-testid="osdu-view-mode">
          <ToggleGroupItem value="fields" className="h-7 px-2.5 text-xs" data-testid="osdu-mode-fields">Fields</ToggleGroupItem>
          <ToggleGroupItem value="json" className="h-7 px-2.5 text-xs" data-testid="osdu-tree-raw">JSON</ToggleGroupItem>
        </ToggleGroup>
      )}
      <VersionPicker read={read} shown={shownVersion} ledgerVersion={level === 0 ? ledgerVersion : null} loading={pickedLoading} onPick={readVersion === undefined ? undefined : pickVersion} />
      {showingPicked && (
        <Button variant="outline" size="sm" className="h-7" onClick={() => setCompareOpen(true)} title="What changed between this version and the latest" data-testid="osdu-version-compare-toggle">
          <GitCompare />
          Compare with latest
        </Button>
      )}
      {pickedFailure !== null && <span className="max-w-[240px] truncate text-[11px] text-destructive" title={pickedFailure} data-testid="osdu-version-error">{pickedFailure}</span>}
      <span className="mx-0.5 h-5 w-px bg-border" aria-hidden="true" />
      {actions}
      <IconAction label="Download the record as JSON" icon={<Download />} variant="ghost" className="size-7" onClick={() => downloadJson(fileNameOf("osdu", read.targetId, shownVersion === null ? null : String(shownVersion)), record)} data-testid="osdu-record-download" />
    </>
  );

  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="osdu-record">
      <LocationBar level={level} onBack={onBack} location={location} controls={controls} />
      <ResizablePanelGroup orientation="horizontal" className="min-h-0 flex-1">
        <ResizablePanel defaultSize={260} minSize={200} maxSize="45" className="flex min-h-0 flex-col">
          <div className="flex items-center gap-1 border-b p-2">
            <SearchInput value={search} onChange={(value) => { setSearch(value); setMatchAt(0); }} placeholder="Find in record" label="Find a field or value in the record" className="min-w-0 flex-1" testId="osdu-tree-search" />
          </div>
          {term !== "" && (
            <div className="flex items-center gap-2 border-b px-2 py-1 text-[11px] text-muted-foreground">
              <span data-testid="osdu-tree-hits">{`${hitList.length} match${hitList.length === 1 ? "" : "es"}`}</span>
              {hitList.length > 0 && <Button variant="ghost" size="sm" className="ml-auto h-6 px-2 text-[11px]" onClick={nextMatch} data-testid="osdu-next-match">Next match</Button>}
            </div>
          )}
          <div className="min-h-0 flex-1 overflow-y-auto p-1" data-testid="osdu-outline">
            <OutlineGroup label="Record" />
            <OutlineRow depth={0} selected={selected === DOCUMENT} onSelect={() => select(DOCUMENT)} label={VIEW_NAMES[DOCUMENT]} detail={`${whole.leaves} values`} testId="osdu-outline-document" />
            <OutlineRow depth={0} selected={selected === RECORD} onSelect={() => select(RECORD)} label={VIEW_NAMES[RECORD]} testId="osdu-outline-record" />
            <OutlineRow depth={0} selected={selected === ACCESS} onSelect={() => select(ACCESS)} label={VIEW_NAMES[ACCESS]} testId="osdu-outline-access" />
            <OutlineGroup label="Content" />
            {model.sections.map((section) => renderOutline(section, 0))}
            <OutlineGroup label="References" />
            <OutlineRow depth={0} selected={selected === LINKS} onSelect={() => select(LINKS)} label={VIEW_NAMES[LINKS]} detail={`${model.references.length}`} testId="osdu-outline-links" />
          </div>
          <div className="flex items-center gap-1 border-t p-1">
            <IconAction label="Unfold every branch" icon={<ChevronsUpDown />} variant="ghost" className="size-7" onClick={() => remember(branchPaths(model.sections))} data-testid="osdu-tree-expand" />
            <IconAction label="Fold every branch" icon={<ChevronsDownUp />} variant="ghost" className="size-7" onClick={() => remember(new Set())} data-testid="osdu-tree-collapse" />
          </div>
        </ResizablePanel>
        <ResizableHandle />
        <ResizablePanel className="flex min-h-0 flex-col">
          {/* Keyed by what is shown, so every step opens at the top of what it shows rather than where the last view was scrolled. */}
          <div key={`${selected}:${mode}:${shownVersion ?? ""}`} className="min-h-0 flex-1 overflow-y-auto" data-testid="osdu-record-json">
            {selected === DOCUMENT
              ? <JsonView key={`document:${shownVersion ?? ""}`} node={whole} term={term} hits={hits} ownId={read.targetId} onOpenLink={onOpenLink} opening={opening} onSelect={goFromDocument} />
              : selected === RECORD
              ? <RecordView read={shownRead} record={record} />
              : selected === ACCESS
                ? <AccessView record={record} />
                : selected === LINKS
                  ? <LinksView model={model} ownId={read.targetId} onOpenLink={onOpenLink} opening={opening} onSelect={select} />
                  : node === undefined
                    ? <EmptyState title="Nothing selected" description="Pick a branch of the record on the left." />
                    : mode === "json"
                      ? <JsonView key={node.path} node={node} term={term} hits={hits} ownId={read.targetId} onOpenLink={onOpenLink} opening={opening} onSelect={select} />
                      : node.children.length === 0
                        ? <EmptyState title={node.kind === "array" ? "An empty list" : "An empty object"} />
                        : isTable
                          ? <ItemTable node={node} term={term} hits={hits} ownId={read.targetId} onOpenLink={onOpenLink} opening={opening} onSelect={select} shown={shown} onShowMore={showMore} />
                          : <FieldRows node={node} term={term} hits={hits} ownId={read.targetId} onOpenLink={onOpenLink} opening={opening} onSelect={select} shown={shown} onShowMore={showMore} />}
          </div>
        </ResizablePanel>
      </ResizablePanelGroup>
      {showingPicked && picked !== null && (
        <Dialog open={compareOpen} onOpenChange={setCompareOpen}>
          <DialogContent className="flex max-h-[90vh] flex-col gap-3 overflow-hidden sm:max-w-6xl" data-testid="osdu-version-compare-dialog">
            <DialogHeader>
              <DialogTitle>{`Version ${picked.version} compared with the latest`}</DialogTitle>
              <DialogDescription>
                <RecordName id={read.targetId} kind={kind} />
                {": what a later delivery, or someone else, changed on the record since this version."}
              </DialogDescription>
            </DialogHeader>
            <div className="min-h-0 flex-1 overflow-y-auto">
              <CompareView latest={read.record} latestVersion={latestVersion} picked={record} pickedVersion={picked.version} />
            </div>
          </DialogContent>
        </Dialog>
      )}
    </div>
  );
}

/**
 * The records open in the inspector, the first read from the page and each next one opened from a link in the one
 * before: a trail across records, not a stack of them. Every record on the trail stays as the reader left it, and
 * the last is shown; a step back along the location bar returns to an earlier one as it was and closes what was opened
 * after it.
 */
export function OsduRecordInspector({ entries, ledgerVersion, opening, onOpenLink, readVersionAt, onBack, actions }: {
  entries: InspectorEntry[];
  ledgerVersion?: number | null;
  opening?: string | null;
  /** Opens a record a value of the record at `level` names, after it on the trail. */
  onOpenLink?: (level: number, id: string, from: string) => void;
  /** How the record at `level` is read at one of its versions; undefined where it cannot be asked for. */
  readVersionAt: (level: number) => ((version: number) => Promise<ComputeTaskAccepted>) | undefined;
  /** Steps back to the entry at `level`, closing everything opened after it. */
  onBack: (level: number) => void;
  /** The page's own controls over the read, shown on the location bar. */
  actions?: ReactNode;
}) {
  const shownLevel = entries.length - 1;

  const body = (entry: InspectorEntry, level: number): { content: ReactNode; inspector: boolean } => {
    const prior: PriorCrumb[] = entries.slice(0, level).map((earlier, index) => ({ id: earlier.id, from: entries[index + 1]?.from ?? null, level: index }));
    const read = isTerminalTask(entry.task) && entry.task?.status === "succeeded" ? (entry.task.result as DeliveryOsduRead | null) : null;
    if (read !== null && read.found && read.record) {
      return {
        inspector: true,
        content: (
          <RecordInspector
            read={{ ...read, record: read.record }}
            level={level}
            prior={prior}
            ledgerVersion={ledgerVersion ?? null}
            onOpenLink={onOpenLink === undefined ? undefined : (id, from) => onOpenLink(level, id, from)}
            onBack={onBack}
            readVersion={readVersionAt(level)}
            opening={opening}
            actions={actions}
          />
        ),
      };
    }

    let message: ReactNode;
    if (entry.error !== undefined) {
      message = <div className="p-3"><ProblemView error={entry.error} /></div>;
    } else if (!isTerminalTask(entry.task)) {
      message = <div className="p-3"><TaskProgress label={level === 0 ? "Reading the record from OSDU through its flow's route" : "Reading the linked record through the flow's route"} task={entry.task} testId="osdu-read-progress" /></div>;
    } else if (entry.task?.status !== "succeeded" || read === null) {
      message = (
        <Alert variant="destructive" className="m-3 w-auto" data-testid="osdu-read-failed">
          <AlertTitle>The record could not be read</AlertTitle>
          <AlertDescription className="whitespace-pre-wrap">{entry.task?.error ?? `The read ended ${entry.task?.status ?? "without an answer"}.`}</AlertDescription>
        </Alert>
      );
    } else {
      message = (
        <Alert className="m-3 w-auto" data-testid="osdu-not-found">
          <SearchX />
          <AlertTitle>OSDU holds no record under this id</AlertTitle>
          <AlertDescription>
            <RecordName id={read.targetId} copy className="text-[12px]" />
            <span className="text-[12px] text-muted-foreground">
              {`Read through ${read.flow}`}
              {read.correlationId ? `, correlation id ${read.correlationId}` : ""}
              {". A record never delivered, or removed with a purge, reads this way."}
            </span>
          </AlertDescription>
        </Alert>
      );
    }

    return {
      inspector: false,
      content: (
        <>
          <LocationBar
            level={level}
            onBack={onBack}
            location={<><PriorCrumbs prior={prior} onBack={onBack} /><RecordName id={entry.id} className="font-medium" /></>}
            controls={actions}
          />
          {message}
        </>
      ),
    };
  };

  return (
    <div data-testid="osdu-panel">
      {entries.map((entry, level) => {
        const { content, inspector } = body(entry, level);
        return (
          // Every record on the trail stays mounted, so stepping back finds it as it was left; only the last is shown.
          <div key={`${level}:${entry.task?.taskId ?? entry.id}`} hidden={level !== shownLevel} data-testid={level > 0 ? "osdu-linked" : undefined}>
            <Card className={cn("flex flex-col gap-0 overflow-hidden rounded-lg p-0", inspector ? "h-[72vh] min-h-[520px]" : "h-auto")}>
              {content}
            </Card>
          </div>
        );
      })}
    </div>
  );
}
