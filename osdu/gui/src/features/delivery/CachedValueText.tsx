import { RichTooltip } from "@/components/RichTooltip";
import { useClipped } from "@/components/useClipped";
import { cn } from "@/lib/utils";
import { cachedCell, valueSegments } from "./cacheFormat";

/**
 * One cached value in a table cell, drawn so a column of them reads: the value exactly as captured, its percent escapes
 * (%20, %5B) stepped back so the words between them stand out, the stretches the search matched marked, and a value that
 * only repeats what the row already said (a Code equal to the id, a Name equal to the Code) in muted text, so the values
 * that say something new are the ones the eye lands on. Clipped at `maxWidth`, or at its cell in a column that takes the
 * table's spare width, with the whole value on hover only when something is actually hidden.
 */
export function CachedValueText({ value, seen = [], search = "", strong = false, maxWidth = 280, fill = false, testId }: {
  /** The captured value, in whatever shape it came: a set reads as its values, an object as its JSON. */
  value: unknown;
  /** What the row says before this value (the record's identity, the values to its left); a value reading the same is drawn muted. */
  seen?: readonly string[];
  /** The search term in force, whose matches are marked. */
  search?: string;
  /** Draws the value as the row's identity: in the foreground weight the other cells step back from. */
  strong?: boolean;
  /**
   * The widest the value may draw, in pixels. A plain length rather than `min(100%, cap)` as TruncatedText caps: a table
   * sizes its columns from their content, and a percentage in the cap makes the browser ignore the whole cap there, so a
   * column of long values would stretch the table past its card.
   */
  maxWidth?: number;
  /** The value sits in a column that takes the table's spare width: it clips at its cell instead of at `maxWidth`. */
  fill?: boolean;
  testId?: string;
}) {
  const text = cachedCell(value);
  const [ref, clipped] = useClipped(text ?? "");

  if (text === null || text === "") {
    return <span className="font-mono text-[12px] text-muted-foreground/60" data-testid={testId}>-</span>;
  }

  const repeats = !strong && seen.includes(text);
  const span = (
    <span
      ref={ref}
      className={cn(
        "inline-block max-w-full overflow-hidden text-ellipsis whitespace-nowrap align-bottom font-mono text-[12px]",
        strong && "font-medium text-foreground",
        repeats && "text-muted-foreground",
      )}
      style={{ maxWidth: fill ? "100%" : `${maxWidth}px` }}
      data-testid={testId}
      data-repeats={repeats ? "true" : undefined}
    >
      {valueSegments(text, search).map((segment, index) => {
        if (segment.match) {
          return (
            <mark
              key={index}
              className={cn("rounded-[2px] bg-warning/25 text-foreground", segment.escape && "text-muted-foreground")}
            >
              {segment.text}
            </mark>
          );
        }

        return segment.escape
          ? <span key={index} className="text-muted-foreground/60">{segment.text}</span>
          : <span key={index}>{segment.text}</span>;
      })}
    </span>
  );

  return clipped ? <RichTooltip body={text} mono>{span}</RichTooltip> : span;
}
