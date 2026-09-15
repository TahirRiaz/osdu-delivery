import { RichTooltip } from "../../components/RichTooltip";
import { useClipped } from "../../components/useClipped";
import { cn } from "@/lib/utils";

interface HeadClippedTextProps {
  /** What leads to the part that matters (osdu:wks:master-data--, osdu.data.Curves[].); gives way first. */
  head: string;
  /** The part that tells values apart (Wellbore, CurveUnit); always in view. */
  body: string;
  /** What follows it and stays in view too (:1.3.0); optional. */
  tail?: string;
  /** Caption for the hover panel naming what the value is. */
  title: string;
  /** Classes for the head, which reads muted by default. */
  headClassName?: string;
  /** Classes for the line (font, size, color). */
  className?: string;
}

/**
 * A code-like value on one line in whatever width it is given, where the end tells values apart and the start repeats
 * down a column: an OSDU kind, a variable path. The body and tail stay in view and the head clips from the left. The text
 * is the value verbatim, so it selects and copies whole, and the whole value is on hover whenever any of it is hidden.
 */
export function HeadClippedText({ head, body, tail = "", title, headClassName = "text-muted-foreground", className }: HeadClippedTextProps) {
  const full = `${head}${body}${tail}`;
  const [lineRef, lineClipped] = useClipped(full);
  const [headRef, headClipped] = useClipped(full);

  const line = (
    <span ref={lineRef} className={cn("flex min-w-0 max-w-full items-baseline overflow-hidden", className)}>
      {/* Right-to-left direction puts the ellipsis at the start; the bdi keeps the head itself reading left to right. */}
      <span ref={headRef} className={cn("min-w-0 overflow-hidden text-ellipsis whitespace-nowrap [direction:rtl]", headClassName)}>
        <bdi>{head}</bdi>
      </span>
      <span className="shrink-0 font-medium">{body}</span>
      {tail !== "" && <span className="shrink-0 text-muted-foreground">{tail}</span>}
    </span>
  );

  return lineClipped || headClipped ? <RichTooltip body={full} title={title} mono>{line}</RichTooltip> : line;
}
