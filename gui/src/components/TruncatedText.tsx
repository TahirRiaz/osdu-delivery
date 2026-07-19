import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";

interface TruncatedTextProps {
  /** The full value: clipped with an ellipsis and shown in full on hover. */
  text: string | null | undefined;
  /**
   * Hard cap on the rendered width in pixels; the cell never grows past this no matter how long the value is,
   * which is what keeps one long path or URL from stretching a whole table column. Short values render at their
   * natural width and are not truncated.
   */
  maxWidth?: number;
  /** Render in monospace, for paths, ids, keys, and other code-like values. */
  mono?: boolean;
  /** Shown when `text` is null/empty (matches the tables' "-" placeholder). */
  placeholder?: string;
}

/**
 * A single-line value that clips with an ellipsis at a fixed pixel cap and reveals the full text in a tooltip.
 * The full string stays in the DOM (only visually clipped), so it remains selectable and copyable. This is the
 * one canonical way to render potentially long free-text values (paths, URLs, object keys, SQL, errors) in a
 * table cell or detail row, so a long value can never blow out the surrounding layout.
 */
export function TruncatedText({ text, maxWidth = 360, mono = false, placeholder = "-" }: TruncatedTextProps) {
  if (text === null || text === undefined || text === "") {
    return <>{placeholder}</>;
  }

  return (
    <Tooltip delayDuration={400}>
      <TooltipTrigger asChild>
        <span
          className={cn(
            // align-bottom keeps the clipped inline-block on the baseline of adjacent text in the same cell
            "inline-block max-w-full overflow-hidden text-ellipsis whitespace-nowrap align-bottom",
            mono && "font-mono text-[12px]",
          )}
          style={{ maxWidth }}
        >
          {text}
        </span>
      </TooltipTrigger>
      <TooltipContent side="top" align="start" className="max-w-lg break-all">
        {text}
      </TooltipContent>
    </Tooltip>
  );
}
