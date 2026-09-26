import { isRecordReference, withoutVersion } from "./osduDocument";

/** The fields OSDU's envelope carries on every record: shown as the record's "About", kept out of its data. */
export const ENVELOPE_KEYS = new Set(["id", "kind", "version", "acl", "legal", "createUser", "createTime", "modifyUser", "modifyTime"]);

/** Where a reader's layout for a kind is kept between records, and how many opened paths it may hold. */
const LAYOUT_PREFIX = "osdu-delivery.record-outline.";
const LAYOUT_MAX_PATHS = 300;

/** What the outline opens on a record whose kind it has not seen: the record itself, with everything under it folded. */
const DEFAULT_OPEN: readonly string[] = ["data"];

export type NodeKind = "object" | "array" | "leaf";

/** One value of the record and where it is: its path, its key, what it holds, and the text a search matches it by. */
export interface RecordNode {
  path: string;
  /** The path of the branch holding it; empty for a top-level section. */
  parent: string;
  key: string;
  kind: NodeKind;
  value: unknown;
  children: RecordNode[];
  /** The key, lower-cased once for the search. */
  keyText: string;
  /** The leaf's value as text, lower-cased once for the search; empty for a branch. */
  valueText: string;
  /** How many values the branch holds at every depth; one for a leaf. */
  leaves: number;
}

/** The record as the inspector walks it: its sections in reading order, and every node by its path. */
export interface RecordModel {
  sections: RecordNode[];
  byPath: ReadonlyMap<string, RecordNode>;
  /** The OSDU records the document refers to, each with the paths it is found at. */
  references: { id: string; paths: string[] }[];
}

export function childPath(path: string, key: string | number): string {
  return typeof key === "number" ? `${path}[${key}]` : path === "" ? key : `${path}.${key}`;
}

function leafText(value: unknown): string {
  return value === null ? "null" : typeof value === "string" ? value : JSON.stringify(value) ?? "";
}

function build(key: string, value: unknown, path: string, parent: string, byPath: Map<string, RecordNode>): RecordNode {
  const keyText = key.toLowerCase();
  let node: RecordNode;
  if (Array.isArray(value)) {
    const children = value.map((item, index) => build(`${index}`, item, childPath(path, index), path, byPath));
    node = { path, parent, key, kind: "array", value, keyText, valueText: "", children, leaves: children.reduce((sum, child) => sum + child.leaves, 0) };
  } else if (value !== null && typeof value === "object") {
    const children = Object.entries(value as Record<string, unknown>).map(([k, v]) => build(k, v, childPath(path, k), path, byPath));
    node = { path, parent, key, kind: "object", value, keyText, valueText: "", children, leaves: children.reduce((sum, child) => sum + child.leaves, 0) };
  } else {
    node = { path, parent, key, kind: "leaf", value, keyText, valueText: leafText(value).toLowerCase(), children: [], leaves: 1 };
  }

  byPath.set(path, node);
  return node;
}

/** Whether a leaf names another OSDU record: a reference anywhere but the record's own id and kind. */
export function isReferenceNode(node: RecordNode): node is RecordNode & { value: string } {
  return node.kind === "leaf" && isRecordReference(node.value) && node.path !== "id" && node.path !== "kind";
}

/**
 * The record's sections in the order a reader wants them: `data` first, since that is the record; then whatever else
 * the record carries (meta, ancestry, tags); the envelope fields are not sections, the "About" view shows them.
 */
export function buildModel(record: Record<string, unknown>, ownId: string | null): RecordModel {
  const byPath = new Map<string, RecordNode>();
  const keys = Object.keys(record);
  const ordered = [
    ...keys.filter((key) => key === "data"),
    ...keys.filter((key) => key !== "data" && !ENVELOPE_KEYS.has(key)).sort(),
  ];
  const sections = ordered.map((key) => build(key, record[key], key, "", byPath));

  const own = ownId === null ? null : withoutVersion(ownId);
  const found = new Map<string, string[]>();
  for (const node of byPath.values()) {
    if (isReferenceNode(node)) {
      const id = withoutVersion(node.value);
      if (id !== own) {
        found.set(id, [...(found.get(id) ?? []), node.path]);
      }
    }
  }

  return { sections, byPath, references: [...found.entries()].map(([id, paths]) => ({ id, paths })) };
}

/** The nodes a search term matches, and the branches holding one at any depth, so a match is reachable and counted. */
export function matching(model: RecordModel, term: string): { hits: Set<string>; holding: Map<string, number> } {
  const hits = new Set<string>();
  const holding = new Map<string, number>();
  const walk = (node: RecordNode): number => {
    let count = node.keyText.includes(term) || node.valueText.includes(term) ? 1 : 0;
    for (const child of node.children) {
      count += walk(child);
    }

    if (count > 0) {
      if (node.keyText.includes(term) || node.valueText.includes(term)) {
        hits.add(node.path);
      }

      if (node.kind !== "leaf") {
        holding.set(node.path, count);
      }
    }

    return count;
  };
  for (const section of model.sections) {
    walk(section);
  }

  return { hits, holding };
}

/** Every branch of the record, for an outline opened all the way. */
export function branchPaths(nodes: RecordNode[], into: Set<string> = new Set()): Set<string> {
  for (const node of nodes) {
    if (node.kind !== "leaf") {
      into.add(node.path);
      branchPaths(node.children, into);
    }
  }

  return into;
}

/**
 * The layout a reader left a kind in: which branches of the outline were open the last time a record of that kind was
 * read here. Kept in this browser alone, as a convenience; a browser that keeps nothing gets the default every time.
 */
export function loadLayout(kind: string | null): ReadonlySet<string> {
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
      // A blocked or full storage is not the outline's problem: the default layout serves.
    }
  }

  return new Set(DEFAULT_OPEN);
}

export function saveLayout(kind: string | null, open: ReadonlySet<string>): void {
  if (kind === null) {
    return;
  }

  try {
    window.localStorage.setItem(LAYOUT_PREFIX + kind, JSON.stringify([...open].slice(0, LAYOUT_MAX_PATHS)));
  } catch {
    // Nothing to do: the layout is a convenience, and the page works without remembering it.
  }
}

/** A branch described in a few words: what it is and how much it holds. */
export function describeBranch(node: RecordNode): string {
  const count = node.children.length;
  return node.kind === "array" ? `${count} item${count === 1 ? "" : "s"}` : `${count} field${count === 1 ? "" : "s"}`;
}

/** The crumbs from the record down to a path: each branch on the way, as the model knows it. */
export function trail(model: RecordModel, path: string): RecordNode[] {
  const crumbs: RecordNode[] = [];
  for (let node = model.byPath.get(path); node !== undefined; node = node.parent === "" ? undefined : model.byPath.get(node.parent)) {
    crumbs.unshift(node);
  }

  return crumbs;
}
