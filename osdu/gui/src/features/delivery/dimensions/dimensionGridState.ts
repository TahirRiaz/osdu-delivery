import { useState } from "react";

/** A column a reader can leave out of a grid. */
export interface GridColumnChoice {
  id: string;
  label: string;
}

/**
 * Which columns of a grid a reader left out, remembered in the browser under `storageKey`, so a dimension with many
 * attributes keeps the ones worth their width. A grid starts without the columns of `atFirst`, until the reader chooses.
 * Returns the ids left out and the function that flips one.
 */
export function useHiddenColumns(storageKey: string, atFirst: readonly string[] = []): [ReadonlySet<string>, (id: string) => void] {
  const [hidden, setHidden] = useState<ReadonlySet<string>>(() => {
    try {
      const stored = window.localStorage.getItem(storageKey);
      if (stored === null) {
        return new Set(atFirst);
      }

      const kept: unknown = JSON.parse(stored);
      return new Set(Array.isArray(kept) ? kept.filter((id): id is string => typeof id === "string") : atFirst);
    } catch {
      // A browser that keeps nothing (or kept something else there) shows the grid as it starts.
      return new Set(atFirst);
    }
  });

  const toggle = (id: string) => {
    const next = new Set(hidden);
    if (next.has(id)) {
      next.delete(id);
    } else {
      next.add(id);
    }

    setHidden(next);
    try {
      window.localStorage.setItem(storageKey, JSON.stringify([...next]));
    } catch {
      // The choice still holds for this visit.
    }
  };

  return [hidden, toggle];
}
