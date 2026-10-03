import { useQuery, type QueryKey } from "@tanstack/react-query";
import { isApiError } from "@/api/client";
import type { ComputeTaskAccepted } from "@/api/types";
import type { ExplorerAnswer, ExplorerFilter, ExplorerIndex, ExplorerSort } from "../../../api/explorer";
import { runComputeTask } from "../useComputeTask";

// What the explorer keeps between its parts: the scope a reader narrowed to (every type, a group, a type, one kind), the
// property values they narrowed by, the kinds of a partition as a tree of groups and types, and the reads, each a node task
// the page waits on and keeps by what it asked, so going back to a type or a page already read shows it at once.

/** How long an answer stands before a page that shows it again reads it again; what OSDU holds moves slowly. */
const FRESH_MS = 60_000;

/**
 * One read of the explorer, queued when it is first asked and kept by what it asked (`key`): the same question asked again
 * answers from what was read, until it is a minute old (or `freshMs`). A read is never retried by itself; a failure is the reader's to
 * see, and the page's refresh asks again.
 */
export function useExplorerRead<T>(key: QueryKey, queue: (() => Promise<ComputeTaskAccepted>) | null, freshMs = FRESH_MS) {
  return useQuery({
    queryKey: ["explorer", ...key],
    queryFn: ({ signal }) => runComputeTask<ExplorerAnswer<T>>(queue!, signal),
    enabled: queue !== null,
    staleTime: freshMs,
    gcTime: 10 * 60_000,
    retry: false,
    refetchOnWindowFocus: false,
  });
}

/** The four parts of a kind, `authority:source:group--Type:version`, with the group and type of its entity type apart. */
export interface KindParts {
  authority: string;
  source: string;
  entityType: string;
  group: string;
  type: string;
  version: string;
}

export function kindParts(kind: string): KindParts {
  const [authority = "", source = "", entityType = "", version = ""] = kind.split(":");
  const dashes = entityType.indexOf("--");
  return {
    authority,
    source,
    entityType,
    group: dashes < 0 ? "" : entityType.slice(0, dashes),
    type: dashes < 0 ? entityType : entityType.slice(dashes + 2),
    version,
  };
}

/** What the reader narrowed the records to: every type, one group of types, one type in every version, or one kind. */
export type ExplorerScope =
  | { level: "all" }
  | { level: "group"; group: string }
  | { level: "type"; entityType: string }
  | { level: "kind"; kind: string };

/** The kind pattern the search is asked of for a scope; none for every type. */
export function scopeKind(scope: ExplorerScope): string | undefined {
  switch (scope.level) {
    case "all":
      return undefined;
    case "group":
      return `*:*:${scope.group}--*:*`;
    case "type":
      return `*:*:${scope.entityType}:*`;
    default:
      return scope.kind;
  }
}

/** The kind pattern of every type, which the address carries once a reader picked every type. */
export const ALL_KINDS = "*:*:*:*";

/** The scope a kind pattern in the address names; every type for none, or for one the explorer did not write. */
export function scopeOf(kind: string | null): ExplorerScope {
  if (kind === null || kind.trim() === "" || kind === ALL_KINDS) {
    return { level: "all" };
  }

  const group = /^\*:\*:([\w.-]+)--\*:\*$/.exec(kind);
  if (group !== null) {
    return { level: "group", group: group[1] };
  }

  const type = /^\*:\*:([\w.-]+--[\w.-]+):\*$/.exec(kind);
  if (type !== null) {
    return { level: "type", entityType: type[1] };
  }

  return /^[\w.*-]+:[\w.*-]+:[\w.*-]+:[\d.*]+$/.test(kind) ? { level: "kind", kind } : { level: "all" };
}

/** Whether two scopes are the same. */
export function sameScope(a: ExplorerScope, b: ExplorerScope): boolean {
  return scopeKind(a) === scopeKind(b);
}

/** A scope as a reader says it: the type, the group, or every type. */
export function scopeLabel(scope: ExplorerScope): string {
  switch (scope.level) {
    case "all":
      return "every type";
    case "group":
      return scope.group;
    case "type":
      return kindParts(`*:*:${scope.entityType}:*`).type;
    default: {
      const parts = kindParts(scope.kind);
      return `${parts.type} ${parts.version}`;
    }
  }
}

/** One kind of a type, with its count. */
export interface KindNode {
  kind: string;
  parts: KindParts;
  count: number;
}

/** One type (an entity type in every kind of it), with its kinds and their count. */
export interface TypeNode {
  entityType: string;
  group: string;
  type: string;
  count: number;
  kinds: KindNode[];
}

/** One group of types (master-data, reference-data, work-product-component), with its types and their count. */
export interface GroupNode {
  group: string;
  count: number;
  types: TypeNode[];
}

/** The kinds a partition holds as groups of types, each in name order, with the counts summed upward. */
export function typeTree(kinds: { kind: string; count: number }[]): GroupNode[] {
  const groups = new Map<string, Map<string, KindNode[]>>();
  for (const { kind, count } of kinds) {
    const parts = kindParts(kind);
    const group = parts.group === "" ? "other" : parts.group;
    const types = groups.get(group) ?? new Map<string, KindNode[]>();
    groups.set(group, types);
    types.set(parts.entityType, [...(types.get(parts.entityType) ?? []), { kind, parts, count }]);
  }

  const byName = (a: string, b: string) => a.localeCompare(b, "en", { sensitivity: "base" });
  return [...groups.entries()]
    .sort(([a], [b]) => byName(a, b))
    .map(([group, types]) => {
      const nodes = [...types.entries()]
        .map(([entityType, kindNodes]): TypeNode => ({
          entityType,
          group,
          type: kindNodes[0].parts.type,
          count: kindNodes.reduce((sum, node) => sum + node.count, 0),
          kinds: [...kindNodes].sort((a, b) => byName(a.kind, b.kind)),
        }))
        .sort((a, b) => byName(a.type, b.type));
      return { group, count: nodes.reduce((sum, node) => sum + node.count, 0), types: nodes };
    });
}

const INDEXES = new Set<ExplorerIndex>(["text", "keyword", "number", "boolean", "date"]);

/**
 * An id as a reader typed it, as the record and the version it names: `p:t:k` and `p:t:k:` name the record at its
 * latest, `p:t:k:1712345678901234` the record at that version.
 */
export function recordAt(typed: string): { id: string; version: number | null } {
  const text = typed.trim();
  const last = text.lastIndexOf(":");
  const tail = last < 0 ? "" : text.slice(last + 1);
  const colons = text.split(":").length - 1;
  if (colons < 3 || !/^\d*$/.test(tail)) {
    return { id: text, version: null };
  }

  const version = tail === "" ? null : Number(tail);
  return { id: text.slice(0, last), version: version !== null && Number.isSafeInteger(version) && version > 0 ? version : null };
}

/** The property values in the address (`f`, a JSON list of [path, index, value]); none for anything else. */
export function filtersOf(text: string | null): ExplorerFilter[] {
  if (text === null || text === "") {
    return [];
  }

  try {
    const parsed: unknown = JSON.parse(text);
    if (!Array.isArray(parsed)) {
      return [];
    }

    return parsed.flatMap((item): ExplorerFilter[] => (
      Array.isArray(item) && item.length === 3 && typeof item[0] === "string" && INDEXES.has(item[1] as ExplorerIndex) && typeof item[2] === "string"
        ? [{ path: item[0], index: item[1] as ExplorerIndex, value: item[2] }]
        : []));
  } catch {
    return [];
  }
}

/** The property values as the address carries them; null for none. */
export function filtersText(filters: ExplorerFilter[]): string | null {
  return filters.length === 0 ? null : JSON.stringify(filters.map((filter) => [filter.path, filter.index, filter.value]));
}

const SORTS = new Set<ExplorerSort>(["relevance", "modified", "created"]);

export function sortOf(text: string | null): ExplorerSort {
  return text !== null && SORTS.has(text as ExplorerSort) ? (text as ExplorerSort) : "relevance";
}

export const SORT_LABELS: Record<ExplorerSort, string> = {
  relevance: "Best match",
  modified: "Last modified",
  created: "Last created",
};

/** A property's path as a reader reads it: without the `data.` every property of a record's content starts with. */
export function fieldLabel(path: string): string {
  return path.startsWith("data.") ? path.slice(5) : path;
}

/** A count as a page says it. */
export function counted(count: number, noun: string): string {
  return `${count.toLocaleString("en-US")} ${noun}${count === 1 ? "" : "s"}`;
}

/** A record opened in the explorer, remembered so the reader finds it again. */
export interface RecentRecord {
  id: string;
  name: string | null;
  kind: string | null;
  partition: string | null;
  openedUtc: string;
}

const RECENT_KEY = "sqlflow.osdu.explorer.recent";
const RECENT_MAX = 20;

/** The records opened last in this browser, newest first; none when nothing was remembered or storage cannot be read. */
export function recentRecords(): RecentRecord[] {
  try {
    const stored: unknown = JSON.parse(window.localStorage.getItem(RECENT_KEY) ?? "[]");
    return Array.isArray(stored)
      ? stored.filter((item): item is RecentRecord => typeof item === "object" && item !== null && typeof (item as RecentRecord).id === "string").slice(0, RECENT_MAX)
      : [];
  } catch {
    return [];
  }
}

/** A type browsed lately: the kind pattern the address carried, and the place it names. */
export interface RecentType {
  kind: string;
  scope: ExplorerScope;
}

const TYPES_KEY = "sqlflow.osdu.explorer.types";
const TYPES_MAX = 12;

/** The types (a group, a type, a kind) browsed last in this browser, newest first; none when nothing was remembered. */
export function recentTypes(): RecentType[] {
  try {
    const stored: unknown = JSON.parse(window.localStorage.getItem(TYPES_KEY) ?? "[]");
    return Array.isArray(stored)
      ? stored
        .filter((kind): kind is string => typeof kind === "string")
        .map((kind) => ({ kind, scope: scopeOf(kind) }))
        .filter((type) => type.scope.level !== "all")
        .slice(0, TYPES_MAX)
      : [];
  } catch {
    return [];
  }
}

/** Remembers a type as browsed now, ahead of the others; every type is not one worth remembering. */
export function rememberType(scope: ExplorerScope): void {
  const kind = scopeKind(scope);
  if (kind === undefined) {
    return;
  }

  try {
    const kept = recentTypes().map((type) => type.kind).filter((other) => other !== kind);
    window.localStorage.setItem(TYPES_KEY, JSON.stringify([kind, ...kept].slice(0, TYPES_MAX)));
  } catch {
    // Nothing to do: the welcome simply lists fewer types.
  }
}

/** Forgets every record opened in this browser. */
export function forgetRecentRecords(): void {
  try {
    window.localStorage.removeItem(RECENT_KEY);
  } catch {
    // A browser that keeps nothing has nothing to forget.
  }
}

/** Remembers a record as opened now, ahead of the others; a browser that keeps nothing simply forgets it. */
export function rememberRecord(record: Omit<RecentRecord, "openedUtc">, openedUtc: string): void {
  try {
    const kept = recentRecords().filter((item) => !(item.id === record.id && item.partition === record.partition));
    window.localStorage.setItem(RECENT_KEY, JSON.stringify([{ ...record, openedUtc }, ...kept].slice(0, RECENT_MAX)));
  } catch {
    // Nothing to do: the explorer works the same without its history.
  }
}

/** A read that failed, in one line of text: the problem the API answered with, or what the node said. */
export function explorerErrorText(error: unknown): string {
  if (isApiError(error)) {
    return error.detail ?? error.title;
  }

  return error instanceof Error ? error.message : String(error);
}
