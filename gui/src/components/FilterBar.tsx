import type { ReactNode } from "react";

/**
 * The wrapper every filter row renders through (DESIGN.md 7.1): one gap, one wrap behaviour, stacking
 * full-width on the narrowest screens and sitting in a row from the small breakpoint up. Controls inside
 * are 32px (`h-8`), so the filter bars line up the same on every list page.
 */
export function FilterBar({ children }: { children: ReactNode }) {
  return (
    <div className="flex flex-col flex-wrap gap-2 sm:flex-row sm:items-center">
      {children}
    </div>
  );
}
