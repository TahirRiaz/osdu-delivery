import { useCallback, useMemo, useState, type ReactNode } from "react";
import { BookOpenCheck, Braces, ChevronDown, ChevronRight, ChevronsDownUp, ChevronsUpDown, Filter, Link2, ListTree } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Toggle } from "@/components/ui/toggle";
import { cn } from "@/lib/utils";
import { CodeView } from "@/components/CodeView";
import { CopyButton } from "@/components/CopyButton";
import { EmptyState } from "@/components/EmptyState";
import { IconAction } from "@/components/IconAction";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import { isRecordReference, withoutVersion } from "./osduDocument";

/** The fields OSDU's envelope carries on every record, shown in the header of the read and kept out of the way here. */
const ENVELOPE_KEYS = new Set(["id", "kind", "version", "acl", "legal", "createUser", "createTime", "modifyUser", "modifyTime"]);

/** How many children of one node show before the rest wait behind a button, so a curve list of thousands stays usable. */
const PAGE = 100;

/** Where a reader's layout for a kind is kept between records, and how many opened paths it may hold. */
const LAYOUT_PREFIX = "osdu-delivery.record-tree.";
const LAYOUT_MAX_PATHS = 300;

/** What the tree opens on a record whose kind it has not seen: the record itself, with everything under it folded. */
const DEFAULT_OPEN: readonly string[] = ["data"];

type Kind = "object" | "array" | "leaf";

/** One value of the record and where it is: its path, its key, what it holds, and the text a search matches it by. */
interface Node {
  path: string;
  key: string;
  kind: Kind;
  value: unknown;
  children: Node[];
  /** The key, lower-cased once for the search. */
  keyText: string;
  /** The leaf's value as text, lower-cased once for the search; empty for a branch. */
  valueText: string;
}

function childPath(path: string, key: string | number): string {
  return typeof key === "number" ? `${path}[${key}]` : path === "" ? key : `${path}.${key}`;
}

function leafText(value: unknown): string {
  return value === null ? "null" : typeof value === "string" ? value : JSON.stringify(value) ?? "";
}

function build(key: string, value: unknown, path: string): Node {
  const keyText = key.toLowerCase();
  if (Array.isArray(value)) {
    return { path, key, kind: "array", value, keyText, valueText: "", children: value.map((item, index) => build(`${index}`, item, childPath(path, index))) };
  }

  if (value !== null && typeof value === "object") {
    const children = Object.entries(value as Record<string, unknown>).map(([k, v]) => build(k, v, childPath(path, k)));
    return { path, key, kind: "object", value, keyText, valueText: "", children };
  }

  return { path, key, kind: "leaf", value, keyText, valueText: leafText(value).toLowerCase(), children: [] };
}

/**
 * The record's top level in the order a reader wants it: `data` first, since that is the record; then whatever else the
 * record carries (meta, ancestry, tags); the envelope fields last, since the header of the read already shows them.
 */
function roots(record: Record<string, unknown>): Node[] {
  const keys = Object.keys(record);
  const ordered = [
    ...keys.filter((key) => key === "data"),
    ...keys.filter((key) => key !== "data" && !ENVELOPE_KEYS.has(key)).sort(),
    ...keys.filter((key) => ENVELOPE_KEYS.has(key)),
  ];
  return ordered.map((key) => build(key, record[key], key));
}

/** The paths of every node a search term matches, and of every branch above one, so a match is reachable and in view. */
function matching(nodes: Node[], term: string): { hits: Set<string>; open: Set<string> } {
  const hits = new Set<string>();
  const open = new Set<string>();
  const walk = (node: Node, ancestors: string[]): boolean => {
    const own = node.keyText.includes(term) || node.valueText.includes(term);
    let any = own;
    for (const child of node.children) {
      if (walk(child, [...ancestors, node.path])) {
        any = true;
      }
    }

    if (own) {
      hits.add(node.path);
    }

    if (any) {
      for (const ancestor of ancestors) {
        open.add(ancestor);
      }

      if (!own) {
        open.add(node.path);
      }
    }

    return any;
  };
  for (const node of nodes) {
    walk(node, []);
  }

  return { hits, open };
}

function branchPaths(nodes: Node[], into: Set<string> = new Set()): Set<string> {
  for (const node of nodes) {
    if (node.kind !== "leaf") {
      into.add(node.path);
      branchPaths(node.children, into);
    }
  }

  return into;
}

/**
 * The layout a reader left a kind in: which branches were open the last time a record of that kind was read here.
 * Kept in this browser alone, as a convenience; a browser that keeps nothing gets the default every time.
 */
function loadLayout(kind: string | null): ReadonlySet<string> {
  if (kind !== null) {
    try {
      const raw = window.localStorage.getItem(LAYOUT_PREFIX + kind);
      if (raw !== null) {
        const parsed: unknown = JSON.parse(raw);
        if (Array.isArray(parsed) && parsed.every((item) => typeof item === "string")) {
          return new Set(parsed as string[]);
        }
      }
    } catch {
      // A blocked or full storage is not the tree's problem: the default layout serves.
    }
  }

  return new Set(DEFAULT_OPEN);
}

function saveLayout(kind: string | null, open: ReadonlySet<string>): void {
  if (kind === null) {
    return;
  }

  try {
    window.localStorage.setItem(LAYOUT_PREFIX + kind, JSON.stringify([...open].slice(0, LAYOUT_MAX_PATHS)));
  } catch {
    // Nothing to do: the layout is a convenience, and the page works without remembering it.
  }
}

/** Text with every occurrence of the search term marked, so a match shows where it is rather than only that it is. */
function Highlight({ text, term }: { text: string; term: string }) {
  if (term === "") {
    return <>{text}</>;
  }

  const parts: ReactNode[] = [];
  const lower = text.toLowerCase();
  let from = 0;
  for (let at = lower.indexOf(term, from); at >= 0; at = lower.indexOf(term, from)) {
    if (at > from) {
      parts.push(text.slice(from, at));
    }

    parts.push(<mark key={at} className="rounded-sm bg-warning/40 text-inherit">{text.slice(at, at + term.length)}</mark>);
    from = at + term.length;
  }

  if (from < text.length) {
    parts.push(text.slice(from));
  }

  return <>{parts}</>;
}

/** A leaf as it reads: a reference as a link that opens the record it names, text as text, numbers and booleans tinted. */
function Leaf({ node, term, ownId, onOpenLink, opening }: { node: Node; term: string; ownId: string | null; onOpenLink?: (id: string) => void; opening?: string | null }) {
  const value = node.value;
  if (isRecordReference(value) && node.path !== "id" && node.path !== "kind") {
    const id = withoutVersion(value);
    const self = ownId !== null && id === withoutVersion(ownId);
    return (
      <span className="inline-flex min-w-0 max-w-full items-center gap-1" data-testid="osdu-record-link">
        <Link2 className="size-3.5 shrink-0 text-primary" />
        <TruncatedText text={value} mono maxWidth={520} className="text-primary" title="OSDU record it refers to" />
        <CopyButton iconOnly label="Copy the id" text={value} testId="copy-osdu-link" />
        {onOpenLink !== undefined && !self && (
          <IconAction
            label="Read this record through the same route"
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

  const matched = term !== "" && node.valueText.includes(term);
  if (typeof value === "string") {
    return matched
      ? <span className="min-w-0 break-all text-[12px]"><Highlight text={value} term={term} /></span>
      : <TruncatedText text={value} maxWidth={640} copy={value.length > 40} className="text-[12px]" />;
  }

  if (typeof value === "number") {
    return <span className="font-mono text-[12px] tabular-nums text-info"><Highlight text={String(value)} term={term} /></span>;
  }

  if (typeof value === "boolean") {
    return <span className="font-mono text-[12px] text-warning"><Highlight text={String(value)} term={term} /></span>;
  }

  return <span className="font-mono text-[12px] text-muted-foreground"><Highlight text="null" term={term} /></span>;
}

function Row({ depth, toggle, open, hit, children }: { depth: number; toggle?: () => void; open?: boolean; hit?: boolean; children: ReactNode }) {
  return (
    <div
      className={cn("flex min-w-0 items-start gap-1 rounded px-1 py-0.5 hover:bg-accent/40", hit && "bg-warning/10")}
      style={{ paddingLeft: 4 + depth * 16 }}
      data-hit={hit ? "true" : undefined}
    >
      {toggle !== undefined
        ? (
          <button type="button" className="mt-0.5 flex size-4 shrink-0 items-center justify-center text-muted-foreground hover:text-foreground" onClick={toggle} aria-expanded={open} data-testid="osdu-tree-toggle">
            {open ? <ChevronDown className="size-3.5" /> : <ChevronRight className="size-3.5" />}
          </button>
        )
        : <span className="size-4 shrink-0" aria-hidden="true" />}
      {children}
    </div>
  );
}

/**
 * An OSDU record as a tree rather than a wall of JSON: `data` open, every object and array folding, a search that
 * marks the fields and values it matches where they stand (or keeps only those, on a switch), and every value that
 * names another OSDU record shown as a link where it stands, with a button that reads that record in turn. The
 * branches a reader opens are remembered for the record's kind, so the next record of that kind opens the same way.
 * The raw JSON is a click away for anyone who wants to paste it.
 */
export function OsduRecordTree({ record, ownId, onOpenLink, opening, actions, testId = "osdu-record-json" }: {
  record: Record<string, unknown>;
  /** The record's own id, so a reference to itself is named as such rather than offered to read. */
  ownId: string | null;
  /** Reads a record the document refers to; absent where links are not followed. */
  onOpenLink?: (id: string) => void;
  /** The linked record being read now, whose button shows it. */
  opening?: string | null;
  /** Actions on the whole record (a download), placed with the raw JSON switch at the right of the tools. */
  actions?: ReactNode;
  testId?: string;
}) {
  const kind = typeof record.kind === "string" ? record.kind : null;
  const nodes = useMemo(() => roots(record), [record]);
  const [raw, setRaw] = useState(false);
  const [search, setSearch] = useState("");
  const [matchesOnly, setMatchesOnly] = useState(false);
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() => loadLayout(kind));
  const [shownCounts, setShownCounts] = useState<ReadonlyMap<string, number>>(() => new Map());
  const term = search.trim().toLowerCase();
  const found = useMemo(() => (term === "" ? null : matching(nodes, term)), [nodes, term]);
  const references = useMemo(() => {
    const ids = new Set<string>();
    const walk = (node: Node) => {
      if (node.kind === "leaf") {
        if (isRecordReference(node.value) && node.path !== "id" && node.path !== "kind") {
          ids.add(withoutVersion(node.value));
        }
      } else {
        node.children.forEach(walk);
      }
    };
    nodes.forEach(walk);
    return ids.size;
  }, [nodes]);

  const remember = useCallback((next: ReadonlySet<string>) => {
    setExpanded(next);
    saveLayout(kind, next);
  }, [kind]);
  const isOpen = (path: string) => (found !== null ? found.open.has(path) || expanded.has(path) : expanded.has(path));
  const toggle = (path: string) => {
    const next = new Set(expanded);
    if (next.has(path)) {
      next.delete(path);
    } else {
      next.add(path);
    }

    remember(next);
  };
  const showMore = (path: string, total: number) => setShownCounts((current) => new Map(current).set(path, total));

  const render = (node: Node, depth: number): ReactNode => {
    const hit = found !== null && found.hits.has(node.path);
    if (found !== null && matchesOnly && !hit && !found.open.has(node.path)) {
      return null;
    }

    if (node.kind === "leaf") {
      return (
        <Row key={node.path} depth={depth} hit={hit}>
          <span className="shrink-0 font-mono text-[12px] text-muted-foreground"><Highlight text={node.key} term={term} /></span>
          <span className="shrink-0 text-[12px] text-muted-foreground">:</span>
          <Leaf node={node} term={term} ownId={ownId} onOpenLink={onOpenLink} opening={opening} />
        </Row>
      );
    }

    const open = isOpen(node.path);
    const total = node.children.length;
    const shown = found !== null ? total : Math.min(total, shownCounts.get(node.path) ?? PAGE);
    return (
      <div key={node.path}>
        <Row depth={depth} toggle={() => toggle(node.path)} open={open} hit={hit}>
          <span className="font-mono text-[12px] font-medium"><Highlight text={node.key} term={term} /></span>
          <span className="text-[11px] text-muted-foreground">
            {node.kind === "array" ? `${total} item${total === 1 ? "" : "s"}` : `${total} field${total === 1 ? "" : "s"}`}
          </span>
        </Row>
        {open && (
          <div>
            {node.children.slice(0, shown).map((child) => render(child, depth + 1))}
            {shown < total && (
              <div style={{ paddingLeft: 4 + (depth + 1) * 16 }} className="py-1">
                <Button variant="outline" size="sm" className="h-7" onClick={() => showMore(node.path, total)} data-testid="osdu-tree-more">
                  {`Show the other ${total - shown}`}
                </Button>
              </div>
            )}
          </div>
        )}
      </div>
    );
  };

  const hits = found?.hits.size ?? 0;
  return (
    <div className="flex flex-col gap-2" data-testid={testId}>
      <div className="flex flex-wrap items-center gap-2">
        {!raw && (
          <>
            <SearchInput value={search} onChange={setSearch} placeholder="Find a field or value" label="Find a field or value in the record" className="w-64" testId="osdu-tree-search" />
            {term !== "" && <span className="text-[12px] text-muted-foreground" data-testid="osdu-tree-hits">{`${hits} match${hits === 1 ? "" : "es"}`}</span>}
            {term !== "" && (
              <Toggle size="sm" variant="outline" pressed={matchesOnly} onPressedChange={setMatchesOnly} aria-label="Show the matches alone" className="h-8 gap-1 px-2 text-xs" data-testid="osdu-tree-matches-only">
                <Filter />
                Matches only
              </Toggle>
            )}
            <IconAction label="Expand everything" icon={<ChevronsUpDown />} variant="outline" className="size-8" onClick={() => remember(branchPaths(nodes))} data-testid="osdu-tree-expand" />
            <IconAction label="Collapse everything" icon={<ChevronsDownUp />} variant="outline" className="size-8" onClick={() => remember(new Set())} data-testid="osdu-tree-collapse" />
          </>
        )}
        <span className="ml-auto flex items-center gap-2">
          {references > 0 && (
            <span className="inline-flex items-center gap-1 text-[11px] text-muted-foreground" title="OSDU records this one refers to; each is a link in the tree">
              <Link2 className="size-3.5" />
              {`${references} linked record${references === 1 ? "" : "s"}`}
            </span>
          )}
          <IconAction
            label={raw ? "Show the record as a tree" : "Show the raw JSON"}
            icon={raw ? <ListTree /> : <Braces />}
            variant="outline"
            className="size-8"
            onClick={() => setRaw((was) => !was)}
            data-testid="osdu-tree-raw"
          />
          {actions}
        </span>
      </div>
      {raw
        ? <CodeView value={JSON.stringify(record, null, 2)} language="json" height={480} />
        : (
          <div className="max-h-[560px] overflow-y-auto rounded-md bg-muted/20 p-1" data-testid="osdu-tree">
            {found !== null && hits === 0
              ? <EmptyState title="Nothing in the record matches" description="The search looks at field names and values, at every depth." />
              : nodes.map((node) => render(node, 0))}
          </div>
        )}
    </div>
  );
}
