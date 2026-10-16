import { Suspense, type ReactNode } from "react";
import { Skeleton } from "@/components/ui/skeleton";

/**
 * A panel whose code loads with it: a placeholder the size of a panel while it loads, or nothing for a part of a page that
 * may render empty anyway (a card that only some runs have, a search category with no hits).
 */
export function Deferred({ children, placeholder = true }: { children: ReactNode; placeholder?: boolean }) {
  return (
    <Suspense fallback={placeholder ? <Skeleton className="h-60 w-full rounded-lg" /> : null}>
      {children}
    </Suspense>
  );
}
