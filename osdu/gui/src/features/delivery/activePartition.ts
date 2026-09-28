import { useSyncExternalStore } from "react";

/**
 * Whether `value` is a partition id as flows name it and caches are kept under: letters, digits, underscore, hyphen and
 * dot, at most 200 characters. A cache kept under a reference the sync could not resolve is not one, and is never made
 * the partition the workbench works in.
 */
export function isPartitionId(value: string): boolean {
  return /^[\w\-.]{1,200}$/.test(value);
}

/** Where the partition picked in the title bar is remembered between visits. */
const STORAGE_KEY = "sqlflow.osdu.partition";

function read(): string | null {
  try {
    const stored = window.localStorage.getItem(STORAGE_KEY);
    return stored !== null && stored.trim() !== "" ? stored : null;
  } catch {
    // Storage that cannot be read (a private window, blocked site data) only means nothing was remembered.
    return null;
  }
}

let active: string | null = read();
const listeners = new Set<() => void>();

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

function snapshot(): string | null {
  return active;
}

/**
 * Makes `next` the partition every OSDU page is read in, and remembers it. The title bar's switcher sets it, and so does a
 * page opened on a link that names its partition (`?partition=`), so the title bar always says which partition the page
 * in view is about.
 */
export function setActivePartition(next: string | null): void {
  if (next === active) {
    return;
  }

  active = next;
  try {
    if (next === null) {
      window.localStorage.removeItem(STORAGE_KEY);
    } else {
      window.localStorage.setItem(STORAGE_KEY, next);
    }
  } catch {
    // The choice still holds for this visit when it cannot be remembered.
  }

  for (const listener of listeners) {
    listener();
  }
}

/**
 * The OSDU partition the workbench is working in (docs/partitions-design.md section 7): the one picked in the title bar,
 * remembered between visits, or null before anything was picked. Every OSDU page reads it: the cache page shows that
 * partition's cache, a flow's pages open in it when the flow names it, and the mapping builder checks against its cache.
 */
export function useActivePartition(): [string | null, (next: string | null) => void] {
  return [useSyncExternalStore(subscribe, snapshot, snapshot), setActivePartition];
}
