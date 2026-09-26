import { useCallback, useMemo, useState, type ReactNode } from "react";
import {
  BookOpenCheck, Braces, ChevronDown, ChevronRight, ChevronsDownUp, ChevronsUpDown, Download, Eye, Globe, History, Info, Link2,
  ListTree, Scale, SearchX, UserRoundCog, X, type LucideIcon,
} from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { ResizableHandle, ResizablePanel, ResizablePanelGroup } from "@/components/ui/resizable";
import { cn } from "@/lib/utils";
import type { ComputeTask } from "@/api/types";
import { CodeView } from "@/components/CodeView";
import { CopyButton } from "@/components/CopyButton";
import { DataTable, type Column } from "@/components/DataTable";
import { EmptyState } from "@/components/EmptyState";
import { IconAction } from "@/components/IconAction";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import type { DeliveryOsduRead } from "../../api/delivery";
import { downloadJson, fileNameOf, withoutVersion } from "./osduDocument";
import {
  branchPaths, buildModel, describeBranch, isReferenceNode, loadLayout, matching, saveLayout, trail,
  type RecordModel, type RecordNode,
} from "./osduRecordModel";
import { ProblemView, TaskProgress } from "./TemplateSheet";
import { isTerminalTask } from "./useComputeTask";

/** How many rows of one branch show before the rest wait behind a button, so a curve list of thousands stays usable. */
const PAGE = 100;

/** How many columns a table of items shows; the rest of an item's fields are a click into the item away. */
const TABLE_COLUMNS = 8;

/** How many versions show before the older ones wait behind a button. */
const VERSIONS_SHOWN = 10;

/** The two views the detail pane has that are not a branch of the record. */
const ABOUT = "about";
const LINKS = "links";

/** One record open in the inspector: the read that fetched it, as it stands. */
export interface InspectorEntry {
  id: string;
  task: ComputeTask | undefined;
  /** A refusal of the poll itself (not of the read): the task could not be followed. */
  error?: unknown;
}

function text(value: unknown): string | null {
  return typeof value === "string" ? value : typeof value === "number" ? String(value) : null;
}

function texts(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : [];
}

/** An OSDU id as a crumb names it: its entity type and unique part, without the partition. */
function shortId(id: string): string {
  const parts = withoutVersion(id).split(":");
  return parts.length >= 3 ? parts.slice(1).join(":") : id;
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
 * The record's history as the target keeps it: every version, newest first, each a click away, with the one being
 * shown, the latest, and the one this flow's ledger holds as delivered marked. A target that keeps no version list
 * says so in one line; a list that could not be read says why.
 */
function RecordVersions({ read, ledgerVersion, onReadVersion }: {
  read: DeliveryOsduRead;
  ledgerVersion: number | null;
  onReadVersion?: (version: number) => void;
}) {
  const [all, setAll] = useState(false);
  const versions = read.versions ?? null;
  if (versions === null) {
    return read.historyError
      ? <span className="text-[12px] text-muted-foreground" data-testid="osdu-history-error">{`The version list could not be read: ${read.historyError}`}</span>
      : <span className="text-[12px] text-muted-foreground" data-testid="osdu-no-history">This target keeps no version list, so the record is read at its latest alone.</span>;
  }

  const shown = read.readVersion ?? read.version ?? null;
  const latest = versions[0] ?? null;
  const listed = all ? versions : versions.slice(0, VERSIONS_SHOWN);
  return (
    <div className="flex flex-wrap items-center gap-x-2 gap-y-1.5" data-testid="osdu-record-versions">
      <span className="inline-flex items-center gap-1 text-[11px] font-medium uppercase tracking-wide text-muted-foreground" title="Every version OSDU keeps of this record, newest first; each reads the record as it was then">
        <History className="size-3.5" />
        {versions.length === 0 ? "No versions" : `${versions.length} version${versions.length === 1 ? "" : "s"}`}
      </span>
      {listed.map((version) => {
        const current = version === shown;
        const marks = [version === latest ? "latest" : null, version === ledgerVersion ? "delivered by this flow" : null].filter((mark): mark is string => mark !== null);
        const title = current ? "The version shown" : "Read the record as it was at this version";
        return (
          <span key={version} className="inline-flex items-center gap-1">
            {current
              ? (
                <span className="inline-flex h-7 items-center rounded-md bg-primary/15 px-2.5 font-mono text-[11px] font-semibold tabular-nums text-primary ring-1 ring-inset ring-primary/40" title={title} data-testid="osdu-record-version" data-state="active">
                  {version}
                </span>
              )
              : (
                <Button variant="outline" size="sm" className="h-7 font-mono text-[11px] tabular-nums" onClick={() => onReadVersion?.(version)} disabled={onReadVersion === undefined} title={title} data-testid="osdu-version">
                  {version}
                </Button>
              )}
            {marks.map((mark) => (
              <span key={mark} className={cn("text-[10px] uppercase tracking-wide", mark === "latest" ? "text-muted-foreground" : "text-success")}>{mark}</span>
            ))}
          </span>
        );
      })}
      {versions.length > VERSIONS_SHOWN && (
        <Button variant="ghost" size="sm" className="h-7" onClick={() => setAll((was) => !was)} data-testid="osdu-versions-more">
          {all ? "Fewer" : `${versions.length - VERSIONS_SHOWN} older`}
        </Button>
      )}
      {shown !== null && latest !== null && shown !== latest && (
        <Badge variant="secondary" className="bg-warning/15 text-warning" data-testid="osdu-version-older">{`showing version ${shown}, not the latest`}</Badge>
      )}
      {ledgerVersion !== null && versions.length > 0 && !versions.includes(ledgerVersion) && (
        <span className="text-[12px] text-warning" data-testid="osdu-version-missing">{`The ledger holds version ${ledgerVersion} as delivered by this flow, and OSDU no longer lists it.`}</span>
      )}
    </div>
  );
}

/** A reference to another OSDU record, as a link with its copy and the button that reads it in turn. */
function ReferenceLink({ value, ownId, onOpenLink, opening, term }: { value: string; ownId: string | null; onOpenLink?: (id: string) => void; opening?: string | null; term: string }) {
  const id = withoutVersion(value);
  const self = ownId !== null && id === withoutVersion(ownId);
  return (
    <span className="inline-flex min-w-0 max-w-full items-center gap-1" data-testid="osdu-record-link">
      <Link2 className="size-3.5 shrink-0 text-primary" />
      {term !== "" && value.toLowerCase().includes(term)
        ? <span className="min-w-0 break-all font-mono text-[12px] text-primary"><Highlight text={value} term={term} /></span>
        : <TruncatedText text={value} mono maxWidth={480} className="text-primary" title="OSDU record it refers to" />}
      <CopyButton iconOnly label="Copy the id" text={value} testId="copy-osdu-link" />
      {onOpenLink !== undefined && !self && (
        <IconAction
          label="Open this record here, read through the same route"
          icon={<BookOpenCheck />}
          className="size-6"
          onClick={(event) => { event.stopPropagation(); onOpenLink(id); }}
          disabled={opening === id}
          data-testid="osdu-link-read"
        />
      )}
      {self && <span className="text-[11px] text-muted-foreground">this record</span>}
    </span>
  );
}

/** A leaf as it reads: a reference as a link, text as text, numbers and booleans tinted, the search term marked. */
function Leaf({ node, term, ownId, onOpenLink, opening, compact = false }: {
  node: RecordNode; term: string; ownId: string | null; onOpenLink?: (id: string) => void; opening?: string | null; compact?: boolean;
}) {
  const value = node.value;
  if (isReferenceNode(node)) {
    return <ReferenceLink value={node.value} ownId={ownId} onOpenLink={onOpenLink} opening={opening} term={term} />;
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

/** One row of the outline: a branch of the record, or one of the two views that are not a branch. */
function OutlineRow({ depth, selected, open, onToggle, onSelect, icon: Icon, label, detail, hits, testId }: {
  depth: number; selected: boolean; open?: boolean; onToggle?: () => void; onSelect: () => void;
  icon?: LucideIcon; label: ReactNode; detail?: string; hits?: number; testId?: string;
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
        {Icon !== undefined && <Icon className="size-3.5 shrink-0 text-muted-foreground" />}
        <span className={cn("truncate", Icon === undefined && "font-mono")}>{label}</span>
        {detail !== undefined && <span className="shrink-0 text-[11px] text-muted-foreground">{detail}</span>}
        {hits !== undefined && hits > 0 && <span className="ml-auto shrink-0 rounded-full bg-warning/25 px-1.5 text-[10px] font-medium tabular-nums">{hits}</span>}
      </button>
    </div>
  );
}

/** The crumbs above the detail pane: the record, then each branch down to the one shown, each a step back up. */
function Crumbs({ record, crumbs, onSelect }: { record: string; crumbs: RecordNode[]; onSelect: (path: string) => void }) {
  return (
    <nav className="flex min-w-0 flex-wrap items-center gap-1 text-[12px]" aria-label="Where in the record" data-testid="osdu-crumbs">
      <button type="button" className="truncate text-muted-foreground hover:text-foreground hover:underline" onClick={() => onSelect(ABOUT)}>{record}</button>
      {crumbs.map((crumb, index) => (
        <span key={crumb.path} className="flex items-center gap-1">
          <ChevronRight className="size-3.5 text-muted-foreground/60" />
          {index === crumbs.length - 1
            ? <span className="font-mono font-medium">{crumb.key}</span>
            : <button type="button" className="font-mono text-muted-foreground hover:text-foreground hover:underline" onClick={() => onSelect(crumb.path)}>{crumb.key}</button>}
        </span>
      ))}
    </nav>
  );
}

/** The record's envelope: what it is, who wrote it, who may see it, and what the read itself was. */
function AboutView({ read, record }: { read: DeliveryOsduRead; record: Record<string, unknown> }) {
  const acl = (record.acl ?? {}) as Record<string, unknown>;
  const legal = (record.legal ?? {}) as Record<string, unknown>;
  const rows: { label: string; value: ReactNode }[] = [
    { label: "Id", value: <TruncatedText text={read.targetId} mono maxWidth={560} copy /> },
    { label: "Kind", value: <TruncatedText text={text(record.kind)} mono maxWidth={560} copy /> },
    { label: "Version", value: <span className="font-mono text-[12px] tabular-nums">{text(record.version) ?? "-"}</span> },
    {
      label: "Created",
      value: <span className="text-[12px]"><RelativeTime value={text(record.createTime)} absolute />{text(record.createUser) && <span className="text-muted-foreground">{` by ${text(record.createUser)}`}</span>}</span>,
    },
    {
      label: "Last modified",
      value: <span className="text-[12px]"><RelativeTime value={text(record.modifyTime)} absolute />{text(record.modifyUser) && <span className="text-muted-foreground">{` by ${text(record.modifyUser)}`}</span>}</span>,
    },
    { label: "Viewers", value: <Chips values={texts(acl.viewers)} icon={Eye} what="viewer group" testId="osdu-record-viewers" /> },
    { label: "Owners", value: <Chips values={texts(acl.owners)} icon={UserRoundCog} what="owner group" testId="osdu-record-owners" /> },
    { label: "Legal tags", value: <Chips values={texts(legal.legaltags)} icon={Scale} what="legal tag" testId="osdu-record-legal" /> },
    { label: "Countries", value: <Chips values={texts(legal.otherRelevantDataCountries)} icon={Globe} what="relevant country" /> },
    { label: "Legal status", value: <span className="text-[12px]">{text(legal.status) ?? "-"}</span> },
    { label: "Read", value: <span className="text-[12px]"><RelativeTime value={read.readUtc} absolute />{` through ${read.flow}`}</span> },
    { label: "Correlation id", value: <TruncatedText text={read.correlationId ?? null} mono maxWidth={360} copy={Boolean(read.correlationId)} /> },
  ];
  return (
    <div className="flex flex-col" data-testid="osdu-about">
      {rows.map((row) => (
        <div key={row.label} className="flex min-w-0 items-baseline gap-3 border-b px-3 py-1.5 last:border-b-0">
          <span className="w-32 shrink-0 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">{row.label}</span>
          <span className="min-w-0 flex-1">{row.value ?? <span className="text-muted-foreground">-</span>}</span>
        </div>
      ))}
    </div>
  );
}

/** The records this one refers to, each with where it is referred to from, and the button that opens it here. */
function LinksView({ model, onOpenLink, opening, onSelect }: { model: RecordModel; onOpenLink?: (id: string) => void; opening?: string | null; onSelect: (path: string) => void }) {
  if (model.references.length === 0) {
    return <EmptyState icon={<Link2 />} title="No linked records" description="No value of this record names another OSDU record." />;
  }

  return (
    <div className="flex flex-col">
      {model.references.map((reference) => (
        <div key={reference.id} className="flex min-w-0 flex-col gap-0.5 border-b px-3 py-2 last:border-b-0">
          <ReferenceLink value={reference.id} ownId={null} onOpenLink={onOpenLink} opening={opening} term="" />
          <span className="flex flex-wrap gap-x-2 text-[11px] text-muted-foreground">
            at
            {reference.paths.map((path) => {
              const holder = model.byPath.get(path)?.parent ?? "";
              return <button key={path} type="button" className="font-mono hover:text-foreground hover:underline" onClick={() => onSelect(holder === "" ? path : holder)}>{path}</button>;
            })}
          </span>
        </div>
      ))}
    </div>
  );
}

/** A branch as rows: each field or item with its value, a nested branch as a step into it. */
function FieldRows({ node, term, hits, ownId, onOpenLink, opening, onSelect, shown, onShowMore }: {
  node: RecordNode; term: string; hits: Set<string>; ownId: string | null; onOpenLink?: (id: string) => void; opening?: string | null;
  onSelect: (path: string) => void; shown: number; onShowMore: () => void;
}) {
  return (
    <div className="flex flex-col">
      {node.children.slice(0, shown).map((child) => (
        <div key={child.path} className={cn("flex min-w-0 items-baseline gap-3 border-b px-3 py-1.5 last:border-b-0", hits.has(child.path) && "bg-warning/10")}>
          <span className="w-56 shrink-0 truncate font-mono text-[12px] text-muted-foreground"><Highlight text={child.key} term={term} /></span>
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
  node: RecordNode; term: string; hits: Set<string>; ownId: string | null; onOpenLink?: (id: string) => void; opening?: string | null;
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

/**
 * One record as an inspector: an outline of its branches on the left that never moves, and on the right one level of
 * it at a time under a breadcrumb, so a record of ten thousand values is read the way a file tree is, never as one
 * tall page. A search counts its matches on every branch of the outline and marks them in the detail; an array of
 * items is a table with a row per item; every value that names another record is a link that opens that record here.
 */
function RecordInspector({ read, record, level, ledgerVersion, onOpenLink, onReadVersion, opening }: {
  read: DeliveryOsduRead;
  record: Record<string, unknown>;
  level: number;
  ledgerVersion: number | null;
  onOpenLink?: (id: string) => void;
  onReadVersion?: (version: number) => void;
  opening?: string | null;
}) {
  const kind = text(record.kind);
  const model = useMemo(() => buildModel(record, read.targetId), [record, read.targetId]);
  const [selected, setSelected] = useState<string>(() => (model.sections.length > 0 ? model.sections[0].path : ABOUT));
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() => loadLayout(kind));
  const [search, setSearch] = useState("");
  const [asJson, setAsJson] = useState(false);
  const [shownCounts, setShownCounts] = useState<ReadonlyMap<string, number>>(() => new Map());
  const [matchAt, setMatchAt] = useState(0);
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
    setSelected(path);
    setAsJson(false);
    // Selecting a branch opens the way to it in the outline, so what is shown is always in view on the left.
    const node = model.byPath.get(path);
    if (node !== undefined) {
      const next = new Set(expanded);
      for (const crumb of trail(model, path)) {
        if (crumb.kind !== "leaf") {
          next.add(crumb.path);
        }
      }

      remember(next);
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

  const renderOutline = (node: RecordNode, depth: number): ReactNode => {
    if (node.kind === "leaf") {
      return null;
    }

    // An array's items are the detail pane's to show, as a table or a list: the outline stays the record's shape
    // (its sections and the objects within them) rather than one row per curve.
    const branches = node.kind === "array" ? [] : node.children.filter((child) => child.kind !== "leaf");
    const holding = found?.holding.get(node.path);
    if (found !== null && holding === undefined && selected !== node.path) {
      return null;
    }

    const open = isOpen(node.path);
    return (
      <div key={node.path}>
        <OutlineRow
          depth={depth}
          selected={selected === node.path}
          open={branches.length > 0 ? open : undefined}
          onToggle={branches.length > 0 ? () => toggle(node.path) : undefined}
          onSelect={() => select(node.path)}
          label={node.key}
          detail={describeBranch(node)}
          hits={holding}
          testId="osdu-outline-branch"
        />
        {open && branches.map((child) => renderOutline(child, depth + 1))}
      </div>
    );
  };

  const node = model.byPath.get(selected);
  const shown = node === undefined ? 0 : Math.min(node.children.length, shownCounts.get(node.path) ?? PAGE);
  const showMore = () => { if (node !== undefined) { setShownCounts((current) => new Map(current).set(node.path, node.children.length)); } };
  const isTable = node !== undefined && node.kind === "array" && node.children.length > 0 && node.children.every((child) => child.kind === "object");
  const recordName = kind !== null ? shortId(kind) : shortId(read.targetId);

  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="osdu-record">
      <div className="flex items-start gap-3 border-b px-3 py-2">
        <span className="inline-flex shrink-0 items-center gap-1.5 pt-1">
          {level > 0 && <Badge variant="secondary" className="text-[10px]">linked</Badge>}
          <TruncatedText text={kind ?? read.targetId} mono maxWidth={360} copy className="text-[12px] font-medium text-foreground" title="Kind" />
        </span>
        <div className="min-w-0 flex-1 pt-0.5">
          <RecordVersions read={read} ledgerVersion={level === 0 ? ledgerVersion : null} onReadVersion={onReadVersion} />
        </div>
        <IconAction label="Download the record as JSON" icon={<Download />} variant="ghost" className="size-7 shrink-0" onClick={() => downloadJson(fileNameOf("osdu", read.targetId), record)} data-testid="osdu-record-download" />
      </div>
      <ResizablePanelGroup orientation="horizontal" className="min-h-0 flex-1">
        <ResizablePanel defaultSize={280} minSize={200} maxSize="45" className="flex min-h-0 flex-col">
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
            <OutlineRow depth={0} selected={selected === ABOUT} onSelect={() => select(ABOUT)} icon={Info} label="About" testId="osdu-outline-about" />
            {model.sections.map((section) => renderOutline(section, 0))}
            <OutlineRow depth={0} selected={selected === LINKS} onSelect={() => select(LINKS)} icon={Link2} label="Linked records" detail={`${model.references.length}`} testId="osdu-outline-links" />
          </div>
          <div className="flex items-center gap-1 border-t p-1">
            <IconAction label="Unfold every branch" icon={<ChevronsUpDown />} variant="ghost" className="size-7" onClick={() => remember(branchPaths(model.sections))} data-testid="osdu-tree-expand" />
            <IconAction label="Fold every branch" icon={<ChevronsDownUp />} variant="ghost" className="size-7" onClick={() => remember(new Set())} data-testid="osdu-tree-collapse" />
          </div>
        </ResizablePanel>
        <ResizableHandle />
        <ResizablePanel className="flex min-h-0 flex-col">
          <div className="flex items-center gap-2 border-b px-3 py-1.5">
            {selected === ABOUT
              ? <span className="text-[12px] font-medium">About this record</span>
              : selected === LINKS
                ? <span className="text-[12px] font-medium">Linked records</span>
                : <Crumbs record={recordName} crumbs={node === undefined ? [] : trail(model, node.path)} onSelect={select} />}
            {node !== undefined && (
              <span className="ml-auto flex items-center gap-1">
                <CopyButton iconOnly label="Copy the path" text={node.path} testId="copy-osdu-path" />
                <IconAction label={asJson ? "Show as fields" : "Show as JSON"} icon={asJson ? <ListTree /> : <Braces />} variant="ghost" className="size-7" onClick={() => setAsJson((was) => !was)} data-testid="osdu-tree-raw" />
              </span>
            )}
          </div>
          <div className="min-h-0 flex-1 overflow-y-auto" data-testid="osdu-record-json">
            {selected === ABOUT
              ? <AboutView read={read} record={record} />
              : selected === LINKS
                ? <LinksView model={model} onOpenLink={onOpenLink} opening={opening} onSelect={select} />
                : node === undefined
                  ? <EmptyState title="Nothing selected" description="Pick a branch of the record on the left." />
                  : asJson
                    ? <div className="p-2"><CodeView value={JSON.stringify(node.value, null, 2)} language="json" height={520} /></div>
                    : node.children.length === 0
                      ? <EmptyState title={node.kind === "array" ? "An empty list" : "An empty object"} />
                      : isTable
                        ? <ItemTable node={node} term={term} hits={hits} ownId={read.targetId} onOpenLink={onOpenLink} opening={opening} onSelect={select} shown={shown} onShowMore={showMore} />
                        : <FieldRows node={node} term={term} hits={hits} ownId={read.targetId} onOpenLink={onOpenLink} opening={opening} onSelect={select} shown={shown} onShowMore={showMore} />}
          </div>
        </ResizablePanel>
      </ResizablePanelGroup>
    </div>
  );
}

/**
 * The records open in the inspector, the first read from the page and each next one opened from a link in the one
 * before: a trail across records, not a stack of them. The last is shown; a crumb steps back to an earlier one and
 * closes what was opened from it.
 */
export function OsduRecordInspector({ entries, ledgerVersion, opening, onOpenLink, onReadVersion, onBack }: {
  entries: InspectorEntry[];
  ledgerVersion?: number | null;
  opening?: string | null;
  /** Opens a record a link in the shown record names, after it in the trail. */
  onOpenLink?: (id: string) => void;
  /** Reads the shown record again at one of its versions, in its place in the trail. */
  onReadVersion?: (version: number) => void;
  /** Steps back to the entry at `level`, closing everything opened after it. */
  onBack: (level: number) => void;
}) {
  const level = entries.length - 1;
  const current = entries[level];
  const read = isTerminalTask(current.task) && current.task?.status === "succeeded" ? (current.task.result as DeliveryOsduRead | null) : null;

  let body: ReactNode;
  if (current.error !== undefined) {
    body = <div className="p-3"><ProblemView error={current.error} /></div>;
  } else if (!isTerminalTask(current.task)) {
    body = <div className="p-3"><TaskProgress label={level === 0 ? "Reading the record from OSDU through its flow's route" : "Reading the linked record through the flow's route"} task={current.task} testId="osdu-read-progress" /></div>;
  } else if (current.task?.status !== "succeeded" || read === null) {
    body = (
      <Alert variant="destructive" className="m-3 w-auto" data-testid="osdu-read-failed">
        <AlertTitle>The record could not be read</AlertTitle>
        <AlertDescription className="whitespace-pre-wrap">{current.task?.error ?? `The read ended ${current.task?.status ?? "without an answer"}.`}</AlertDescription>
      </Alert>
    );
  } else if (!read.found || !read.record) {
    body = (
      <Alert className="m-3 w-auto" data-testid="osdu-not-found">
        <SearchX />
        <AlertTitle>OSDU holds no record under this id</AlertTitle>
        <AlertDescription>
          <span className="font-mono text-[12px] break-all">{read.targetId}</span>
          <span className="text-[12px] text-muted-foreground">
            {`Read through ${read.flow}`}
            {read.correlationId ? `, correlation id ${read.correlationId}` : ""}
            {". A record never delivered, or removed with a purge, reads this way."}
          </span>
        </AlertDescription>
      </Alert>
    );
  } else {
    body = (
      <RecordInspector
        key={`${current.task?.taskId ?? current.id}`}
        read={read}
        record={read.record}
        level={level}
        ledgerVersion={ledgerVersion ?? null}
        onOpenLink={onOpenLink}
        onReadVersion={onReadVersion}
        opening={opening}
      />
    );
  }

  return (
    <div className="flex flex-col gap-2" data-testid="osdu-panel">
      {entries.length > 1 && (
        <nav className="flex flex-wrap items-center gap-1 text-[12px]" aria-label="Records opened" data-testid="osdu-trail">
          {entries.map((entry, index) => (
            <span key={`${index}-${entry.id}`} className="flex items-center gap-1">
              {index > 0 && <ChevronRight className="size-3.5 text-muted-foreground/60" />}
              {index === level
                ? <span className="max-w-[360px] truncate font-mono font-medium" title={entry.id}>{shortId(entry.id)}</span>
                : <button type="button" className="max-w-[360px] truncate font-mono text-muted-foreground hover:text-foreground hover:underline" onClick={() => onBack(index)} title={`Back to ${entry.id}`}>{shortId(entry.id)}</button>}
            </span>
          ))}
          <IconAction label="Close this linked record" icon={<X />} variant="ghost" className="ml-1 size-6" onClick={() => onBack(level - 1)} data-testid="osdu-linked-close" />
        </nav>
      )}
      <Card className={cn("flex h-[72vh] min-h-[520px] flex-col gap-0 overflow-hidden rounded-lg p-0", read !== null && !read.found && "h-auto min-h-0")} data-testid={level > 0 ? "osdu-linked" : undefined}>
        {body}
      </Card>
    </div>
  );
}
