import type { DeliveryCacheHistoryEntry, DeliveryCacheHistoryType } from "../../api/delivery";

/** How many characters of a content hash name it where the whole would not fit; the whole is on hover. */
export const SHORT_HASH = 12;

/** A content hash as a list or a picker names it. */
export function shortHash(hash: string): string {
  return hash.slice(0, SHORT_HASH);
}

/**
 * One version of a cached type: a version of the partition's cache at which the type's own content hash moved, because it
 * arrived, changed or left. The cache writes a version whenever anything in it moves, so most of its versions hold a type
 * exactly as the one before did; the versions of the type are the ones that did not, and any two of them differ.
 */
export interface CacheTypeVersion {
  /** The label of the cache version that moved the type. */
  version: string;
  capturedUtc: string;
  change: DeliveryCacheHistoryType["change"];
  /** The type's content hash in it; null when it removed the type, or was written before types were hashed. */
  hash: string | null;
  /** How many records of the type it holds. */
  items: number;
  /** How many records of the type it changed, added and removed against the version of the type before it. */
  changed: number;
  added: number;
  removed: number;
  /**
   * The version the type's content is compared with: the version of the type before this one, or, for the oldest one
   * listed, the cache version before it, which holds the type's earlier content as it stood. Null for the cache's first.
   */
  previous: string | null;
  /** True when {@link previous} is the version of the type before this one, rather than the cache version before it. */
  previousOfType: boolean;
  /** True for the newest version of the type, when the type is still held: the current version holds its content. */
  holdsCurrent: boolean;
  /**
   * True for an earlier version of the type whose content came back: its hash is the one the current version holds, so
   * comparing it with the current version finds no change.
   */
  sameAsCurrent: boolean;
  /** True when the cache's retention pruned this version's records: it is listed with its counts, and cannot be read. */
  pruned: boolean;
  /** True when the version it is compared with ({@link previous}) is pruned, so the records that differ cannot be listed. */
  previousPruned: boolean;
  /** The cache version as the history describes it. */
  entry: DeliveryCacheHistoryEntry;
}

/** Whether the cache's retention pruned a version's records. The field is left out of an answer while they are kept. */
export function isPruned(version: { prunedUtc?: string | null }): boolean {
  return Boolean(version.prunedUtc);
}

/**
 * The versions of `type`, newest first, from the cache's history read for that type: every version that moved it, each
 * with the version of the type it replaced. Two neighbours always differ, since the type's hash moved between them; an
 * earlier version can hold what the current one holds only when the content came back.
 */
export function typeVersions(entries: DeliveryCacheHistoryEntry[], type: string): CacheTypeVersion[] {
  const moved = entries
    .map((entry) => ({ entry, moved: entry.types.find((candidate) => candidate.name.toLowerCase() === type.toLowerCase()) }))
    .filter((candidate): candidate is { entry: DeliveryCacheHistoryEntry; moved: DeliveryCacheHistoryType } => candidate.moved !== undefined);

  // Nothing moved the type after its newest version, so the current version holds that content, unless it removed the type.
  const head = moved.at(0);
  const currentHash = head !== undefined && head.moved.change !== "removed" ? head.moved.hash : null;

  return moved.map(({ entry, moved: typed }, index): CacheTypeVersion => {
    const holdsCurrent = index === 0 && typed.change !== "removed";
    const before = moved.at(index + 1);
    return {
      version: entry.version.version,
      capturedUtc: entry.version.capturedUtc,
      change: typed.change,
      hash: typed.hash,
      items: typed.items,
      changed: typed.changed,
      added: typed.added,
      removed: typed.removed,
      previous: before?.entry.version.version ?? entry.before,
      previousOfType: before !== undefined,
      holdsCurrent,
      sameAsCurrent: !holdsCurrent && typed.hash !== null && currentHash !== null && typed.hash === currentHash,
      pruned: isPruned(entry.version),
      previousPruned: before !== undefined ? isPruned(before.entry.version) : Boolean(entry.beforePruned),
      entry,
    };
  });
}

/**
 * The earlier versions of a type a reader can open and compare with the current one: every version that added or changed
 * it, but the newest, whose content the current version holds and which the picker's Current version already reads. A
 * version that removed the type holds none of it, so there is nothing of the type to read in it. A version the cache's
 * retention pruned is listed by the picker as such, and cannot be picked.
 */
export function earlierTypeVersions(versions: CacheTypeVersion[]): CacheTypeVersion[] {
  return versions.filter((candidate) => !candidate.holdsCurrent && candidate.change !== "removed");
}
