import { useQuery, type QueryKey } from "@tanstack/react-query";
import type { ExplorerAnswer, ExplorerCondition, ExplorerFilter, ExplorerIndex, ExplorerSort } from "../../../api/explorer";

// What the explorer keeps between its parts: the scope a reader narrowed to (every type, a group, a type, one kind), the
// property values they narrowed by, the kinds of a partition as a tree of groups and types, and the reads, each answered
// by the control plane and kept by what it asked, so going back to a type or a page already read shows it at once.

/** How long an answer stands before a page that shows it again reads it again; what OSDU holds moves slowly. */
const FRESH_MS = 60_000;

/**
 * One read of the explorer, made when it is first asked and kept by what it asked (`key`): the same question asked again
 * answers from what was read, until it is a minute old (or `freshMs`). A read is never retried by itself; a failure is the reader's to
 * see, and the page's refresh asks again.
 */
export function useExplorerRead<T>(key: QueryKey, read: (() => Promise<ExplorerAnswer<T>>) | null, freshMs = FRESH_MS) {
  return useQuery({
    queryKey: ["explorer", ...key],
    queryFn: () => read!(),
    enabled: read !== null,
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

const CONDITIONS = new Set<ExplorerCondition>(["is", "isNot", "anyOf", "contains", "startsWith", "range", "exists", "missing"]);

/** A condition as the address writes it: short keys, and nothing a condition leaves at its default. */
interface FilterEntry {
  p: string;
  i: ExplorerIndex;
  c?: ExplorerCondition;
  v?: string;
  vs?: string[];
  t?: string;
  n?: string;
}

/** One condition of the address, or null for an entry that is not one. */
function filterOf(item: unknown): ExplorerFilter | null {
  // A value picked from a property's groups, as links made before conditions had names carry it: [path, index, value].
  if (Array.isArray(item)) {
    return item.length === 3 && typeof item[0] === "string" && INDEXES.has(item[1] as ExplorerIndex) && typeof item[2] === "string"
      ? { path: item[0], index: item[1] as ExplorerIndex, value: item[2] }
      : null;
  }

  if (typeof item !== "object" || item === null) {
    return null;
  }

  const entry = item as Partial<FilterEntry>;
  if (typeof entry.p !== "string" || !INDEXES.has(entry.i as ExplorerIndex) || (entry.c !== undefined && !CONDITIONS.has(entry.c))) {
    return null;
  }

  const text = (value: unknown) => (typeof value === "string" ? value : undefined);
  const values = Array.isArray(entry.vs) ? entry.vs.filter((value): value is string => typeof value === "string") : undefined;
  return {
    path: entry.p,
    index: entry.i as ExplorerIndex,
    ...(text(entry.n) !== undefined ? { nested: text(entry.n) } : {}),
    ...(entry.c !== undefined && entry.c !== "is" ? { condition: entry.c } : {}),
    ...(text(entry.v) !== undefined ? { value: text(entry.v) } : {}),
    ...(values !== undefined ? { values } : {}),
    ...(text(entry.t) !== undefined ? { to: text(entry.t) } : {}),
  };
}

/** The conditions in the address (`f`, a JSON list); none for anything else. */
export function filtersOf(text: string | null): ExplorerFilter[] {
  if (text === null || text === "") {
    return [];
  }

  try {
    const parsed: unknown = JSON.parse(text);
    return Array.isArray(parsed) ? parsed.map(filterOf).filter((filter): filter is ExplorerFilter => filter !== null) : [];
  } catch {
    return [];
  }
}

/** The conditions as the address carries them; null for none. */
export function filtersText(filters: ExplorerFilter[]): string | null {
  if (filters.length === 0) {
    return null;
  }

  return JSON.stringify(filters.map((filter): FilterEntry => ({
    p: filter.path,
    i: filter.index,
    ...(filter.condition !== undefined && filter.condition !== "is" ? { c: filter.condition } : {}),
    ...(filter.value !== undefined ? { v: filter.value } : {}),
    ...(filter.values !== undefined ? { vs: filter.values } : {}),
    ...(filter.to !== undefined ? { t: filter.to } : {}),
    ...(filter.nested ? { n: filter.nested } : {}),
  })));
}

/** Whether two conditions ask the same of the same property. */
export function sameFilter(a: ExplorerFilter, b: ExplorerFilter): boolean {
  return filtersText([a]) === filtersText([b]);
}

/** How a condition reads in a picker and on its chip. */
export const CONDITION_LABELS: Record<ExplorerCondition, string> = {
  contains: "contains",
  is: "is",
  isNot: "is not",
  anyOf: "is one of",
  startsWith: "starts with",
  range: "is in a range",
  exists: "has a value",
  missing: "has no value",
};

/** What a condition finds, in a line under its name in the picker. */
export const CONDITION_HINTS: Record<ExplorerCondition, string> = {
  contains: "The words anywhere in the value, in any case.",
  is: "The whole value, exactly.",
  isNot: "Every record but those holding the value.",
  anyOf: "The whole value is one of several.",
  startsWith: "The whole value starts with the text, in the same case.",
  range: "From a value (included) up to another (not included); either end may stay open.",
  exists: "Any value at all.",
  missing: "No value at all.",
};

/**
 * The conditions a property can be asked, the likeliest first: text by its words before its whole value, a number or a date
 * by a range. Inside a nested list a start and whether it holds a value are not asked, since the search service rewrites a
 * nested query and `_exists_` does not see inside one.
 */
export function conditionsFor(field: { index: ExplorerIndex; nested?: string | null }): ExplorerCondition[] {
  const nested = Boolean(field.nested);
  const presence: ExplorerCondition[] = nested ? [] : ["exists", "missing"];
  switch (field.index) {
    case "text":
      return ["contains", "is", "isNot", "anyOf", ...(nested ? [] : ["startsWith" as const]), ...presence];
    case "keyword":
      return ["is", "isNot", "anyOf", ...(nested ? [] : ["startsWith" as const]), ...presence];
    case "number":
    case "date":
      return ["range", "is", "isNot", "anyOf", ...presence];
    default:
      return ["is", ...presence];
  }
}

/** The condition a text typed in the search box becomes when it is searched in one property: its words for text, else its whole value. */
export function searchInCondition(field: { index: ExplorerIndex; nested?: string | null }): ExplorerCondition {
  return field.index === "text" ? "contains" : field.index === "keyword" && !field.nested ? "startsWith" : "is";
}

/** What a condition compares, as its chip says it after the property and the condition; empty for one that compares nothing. */
export function conditionValueText(filter: ExplorerFilter): string {
  switch (filter.condition ?? "is") {
    case "exists":
    case "missing":
      return "";
    case "anyOf":
      return (filter.values ?? (filter.value === undefined ? [] : [filter.value])).join(", ");
    case "range":
      return [filter.value ? `from ${filter.value}` : null, filter.to ? `up to ${filter.to}` : null].filter(Boolean).join(" ");
    default:
      return filter.value ?? "";
  }
}

/** A condition as a sentence: `WellboreName starts with NO 34/10`. */
export function filterSentence(filter: ExplorerFilter): string {
  const condition = filter.condition ?? "is";
  const value = conditionValueText(filter);
  return [fieldLabel(filter.path), condition === "range" ? "is" : CONDITION_LABELS[condition], value].filter((part) => part !== "").join(" ");
}

/** Why a drafted condition cannot be asked yet, in a few words; null when it can. */
export function filterProblem(filter: ExplorerFilter): string | null {
  const condition = filter.condition ?? "is";
  switch (condition) {
    case "exists":
    case "missing":
      return null;
    case "anyOf":
      return (filter.values ?? []).length === 0 ? "Pick or type at least one value." : null;
    case "range":
      return !filter.value && !filter.to ? "Give a lower bound, an upper bound, or both." : null;
    default:
      return (filter.value ?? "") === "" ? "Type the value to compare." : null;
  }
}

/** The properties a search is shown with as columns: those its conditions ask, each once, but the record's name, which the grid shows already. */
export function columnsOf(filters: ExplorerFilter[], nameFields: readonly string[]): string[] {
  const columns: string[] = [];
  for (const filter of filters) {
    if (!columns.includes(filter.path) && !nameFields.includes(filter.path) && filter.path !== "kind" && filter.path !== "id") {
      columns.push(filter.path);
    }
  }

  return columns.slice(0, EXPLORER_MAX_COLUMNS);
}

/** The properties a record's name is read from, in the order the explorer tries them (RecordExplorer.NameFields). */
export const NAME_FIELDS = ["data.FacilityName", "data.Name", "data.ProjectName", "data.Code", "data.DatasetProperties.FileSourceInfo.Name"] as const;

/** The most properties a page shows as columns. */
export const EXPLORER_MAX_COLUMNS = 8;

const SEARCHED_IN_KEY = "sqlflow.osdu.explorer.searchedIn";
const SEARCHED_IN_MAX = 5;

/** The properties searched in lately in this browser for a type (an entity type, or every type), newest first. */
export function searchedIn(entityType: string): string[] {
  try {
    const stored: unknown = JSON.parse(window.localStorage.getItem(SEARCHED_IN_KEY) ?? "{}");
    const paths = typeof stored === "object" && stored !== null ? (stored as Record<string, unknown>)[entityType] : undefined;
    return Array.isArray(paths) ? paths.filter((path): path is string => typeof path === "string").slice(0, SEARCHED_IN_MAX) : [];
  } catch {
    return [];
  }
}

/** Remembers a property as searched in now for a type, ahead of the others; a browser that keeps nothing simply forgets it. */
export function rememberSearchedIn(entityType: string, path: string): void {
  try {
    const stored: unknown = JSON.parse(window.localStorage.getItem(SEARCHED_IN_KEY) ?? "{}");
    const all = typeof stored === "object" && stored !== null && !Array.isArray(stored) ? (stored as Record<string, unknown>) : {};
    const kept = searchedIn(entityType).filter((other) => other !== path);
    window.localStorage.setItem(SEARCHED_IN_KEY, JSON.stringify({ ...all, [entityType]: [path, ...kept].slice(0, SEARCHED_IN_MAX) }));
  } catch {
    // Nothing to do: the search box offers the record's name properties alone.
  }
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
