import type { CSSProperties, ReactNode } from "react";
import { cn } from "@/lib/utils";

/**
 * Facts about a record, one line each: the caption on the left in the chrome caption voice, the value beside it, in as
 * many columns as the width allows. Where `DetailPair` stacks a caption over its value and costs two lines per fact,
 * this costs one, which is what a page whose values are ids, hashes and file names (long, and clipped whatever the
 * layout) wants: the caption column lines the values up, and the eye runs down it. A value clips at its cell, never
 * past it: a long value renders through `TruncatedText`, whose cap is bounded by the cell it sits in.
 */
export function FactGrid({ children, minWidth = 260, className, "data-testid": testId }: {
  children: ReactNode;
  /** The narrowest a column may be, in pixels, before the grid drops a column. */
  minWidth?: number;
  className?: string;
  "data-testid"?: string;
}) {
  return (
    <dl
      className={cn("grid gap-x-6 gap-y-1", className)}
      style={{ gridTemplateColumns: `repeat(auto-fill, minmax(min(${minWidth}px, 100%), 1fr))` } satisfies CSSProperties}
      data-testid={testId}
    >
      {children}
    </dl>
  );
}

/** One fact of a `FactGrid`: its caption, then its value on the same line. */
export function Fact({ label, children, wide = false, testId }: {
  label: string;
  children: ReactNode;
  /** A fact whose value earns the whole row (a sentence, a list of chips). */
  wide?: boolean;
  testId?: string;
}) {
  return (
    <div className={cn("flex min-w-0 items-baseline gap-2 text-[12px] leading-5", wide && "col-span-full")} data-testid={testId}>
      <dt className="w-28 shrink-0 truncate text-[11px] font-medium uppercase tracking-wide text-muted-foreground" title={label}>{label}</dt>
      <dd className="min-w-0 flex-1">{children}</dd>
    </div>
  );
}

/** The value a fact has none of. */
export function NoFact({ children = "-" }: { children?: ReactNode }) {
  return <span className="text-muted-foreground">{children}</span>;
}
