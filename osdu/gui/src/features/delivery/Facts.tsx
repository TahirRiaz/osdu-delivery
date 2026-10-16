import type { ReactNode } from "react";
import { cn } from "@/lib/utils";

/**
 * Facts about a record, one line each, in up to three columns as the width allows: the caption in the chrome caption
 * voice, right-aligned against its value, so a caption meets its value at the column's gutter however short either is.
 * The gap inside a fact stays narrower than the gap between one fact's value and the next column's caption, which is
 * what ties each caption to its own value rather than to the value before it; a left-aligned caption column left a
 * short caption ("Mapping") further from its value than from its neighbour's.
 *
 * A column is as wide as its facts need and the width left over is shared out, so a column of dates takes a date's
 * width and a column of ids and file names gets the rest; equal columns clipped the ids while the dates beside them sat
 * half empty. Where `DetailPair` stacks a caption over its value and costs two lines per fact, this costs one, which is
 * what a page whose values are ids, hashes and file names wants. A value clips at its cell, never past it: a long value
 * renders through `TruncatedText`, whose cap is bounded by the cell it sits in.
 */
export function FactGrid({ children, className, "data-testid": testId }: {
  children: ReactNode;
  className?: string;
  "data-testid"?: string;
}) {
  // The column count follows the width the grid is given, not the window's, so the same facts lay out alike in a tab
  // and in an opened timeline entry. minmax(0, auto) sizes a column by its content and lets it shrink below that when
  // the row is short of room, where the value then clips. The facts of a row share one baseline: a value with a copy
  // button is taller than its text, and top-aligned it sat lower than the facts beside it.
  return (
    <div className="@container/facts min-w-0">
      <dl
        className={cn(
          "grid grid-cols-1 items-baseline gap-x-10 gap-y-1 @xl/facts:grid-cols-[repeat(2,minmax(0,auto))] @4xl/facts:grid-cols-[repeat(3,minmax(0,auto))]",
          className,
        )}
        data-testid={testId}
      >
        {children}
      </dl>
    </div>
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
    <div className={cn("flex min-w-0 items-baseline gap-2.5 text-[12px] leading-5", wide && "col-span-full")} data-testid={testId}>
      <dt className="w-28 shrink-0 truncate text-right text-[11px] font-medium uppercase tracking-wide text-muted-foreground" title={label}>{label}</dt>
      <dd className="min-w-0 flex-1">{children}</dd>
    </div>
  );
}

/** The value a fact has none of. */
export function NoFact({ children = "-" }: { children?: ReactNode }) {
  return <span className="text-muted-foreground">{children}</span>;
}
