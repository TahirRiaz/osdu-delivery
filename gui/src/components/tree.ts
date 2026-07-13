// The pure tree model behind DataTable's grouped view: turning a flat, path-tagged row list into a nested
// forest. Kept free of React so the nesting logic can be reasoned about (and tested) on its own; DataTable
// renders the forest this produces.

/** One ancestor node a row nests under. */
export interface TreeSegment {
  /** Identity of the node among its siblings, and its collapse-state key within the path. */
  key: string;
  /** Optional numeric order for siblings at this level: when any sibling sets it, siblings sort by it ascending
   *  (stable); otherwise siblings keep first-appearance order (which, for a pre-sorted list, is the sort order). */
  sort?: number;
}

/** The context handed to a node renderer for one interior node. */
export interface TreeNodeContext<T> {
  /** The segment keys from the outermost ancestor down to this node (so a renderer can resolve a display label
   *  from an id key, e.g. a repo id -> repo name). The node's own key is the last element. */
  keys: string[];
  /** Tree depth, 0 for the outermost nodes. */
  depth: number;
  /** Every row in this node's whole subtree, for counts and aggregates. */
  rows: T[];
}

/** A built tree node: its key, its whole-subtree rows, its child nodes, and the rows that leaf directly under it. */
export interface TreeNode<T> {
  key: string;
  sort?: number;
  rows: T[];
  children: TreeNode<T>[];
  leaves: T[];
}

/** The children and direct-leaf rows of one node (or of the virtual root). */
export interface TreeContents<T> {
  children: TreeNode<T>[];
  leaves: T[];
}

/**
 * Builds the contents of the node at the given depth from entries that already share the first `depth` segments:
 * a row whose path ends here is a direct leaf, a row that goes deeper joins the child node named by its next
 * segment (recursively, so rows sharing a path prefix nest under the SAME node rather than repeating the prefix).
 * Child nodes keep first-appearance order unless any sibling carries a numeric sort, in which case siblings sort
 * by it ascending (stable) so batch steps read in wave order.
 */
export function buildContents<T>(entries: { row: T; segs: TreeSegment[] }[], depth: number): TreeContents<T> {
  const leaves: T[] = [];
  const order: string[] = [];
  const buckets = new Map<string, { row: T; segs: TreeSegment[] }[]>();
  const sorts = new Map<string, number | undefined>();

  for (const entry of entries) {
    if (entry.segs.length === depth) {
      leaves.push(entry.row);
      continue;
    }

    const seg = entry.segs[depth];
    let bucket = buckets.get(seg.key);
    if (bucket === undefined) {
      bucket = [];
      buckets.set(seg.key, bucket);
      order.push(seg.key);
      sorts.set(seg.key, seg.sort);
    }

    bucket.push(entry);
  }

  let children: TreeNode<T>[] = order.map((key) => {
    const bucket = buckets.get(key)!;
    const inner = buildContents(bucket, depth + 1);
    return { key, sort: sorts.get(key), rows: bucket.map((e) => e.row), children: inner.children, leaves: inner.leaves };
  });

  if (children.some((child) => child.sort !== undefined)) {
    children = children
      .map((child, index) => ({ child, index }))
      .sort((a, b) => {
        const as = a.child.sort ?? Number.POSITIVE_INFINITY;
        const bs = b.child.sort ?? Number.POSITIVE_INFINITY;
        return as !== bs ? as - bs : a.index - b.index;
      })
      .map((entry) => entry.child);
  }

  return { children, leaves };
}
