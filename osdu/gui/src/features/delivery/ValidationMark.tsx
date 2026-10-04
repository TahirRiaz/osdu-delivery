import type { ComponentProps, ReactNode } from "react";
import { CircleAlert, CircleCheck, CircleDashed, CircleHelp, type LucideIcon } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";
import type { ValidationOutcome } from "../../api/validation";
import { outcomeWord } from "./validationModel";

/**
 * How a validation outcome is drawn: a glyph in its tone, never a tinted pill (DESIGN.md 7.3). A list of records checked
 * against their schemas reads by its glyphs, so a few invalid records stand out of many valid ones without the whole list
 * turning into a block of color.
 */
const marks: Record<ValidationOutcome, { icon: LucideIcon; tone: string }> = {
  valid: { icon: CircleCheck, tone: "text-success" },
  invalid: { icon: CircleAlert, tone: "text-destructive" },
  unverified: { icon: CircleHelp, tone: "text-warning" },
  notValidated: { icon: CircleDashed, tone: "text-muted-foreground" },
};

/** The glyph of an outcome, in its tone. */
export function ValidationGlyph({ outcome, className }: { outcome: ValidationOutcome; className?: string }) {
  const mark = marks[outcome];
  const Icon = mark.icon;
  return <Icon className={cn("size-3.5 shrink-0", mark.tone, className)} aria-hidden />;
}

/** A neutral chip led by the glyph of an outcome, saying it in a word unless given what to say. */
export function ValidationBadge({
  outcome, children, className, ...props
}: { outcome: ValidationOutcome; children?: ReactNode } & Omit<ComponentProps<typeof Badge>, "variant" | "children">) {
  return (
    <Badge variant="secondary" className={cn("rounded-md border-border bg-muted/60 text-foreground", className)} data-outcome={outcome} {...props}>
      <ValidationGlyph outcome={outcome} />
      {children ?? outcomeWord(outcome)}
    </Badge>
  );
}

/** How many records came to one outcome: the glyph, the count as data, and the word quietly after it; a zero is quiet too. */
export function ValidationCount({ outcome, count, className }: { outcome: ValidationOutcome; count: number; className?: string }) {
  return (
    <span className={cn("inline-flex items-center gap-1 whitespace-nowrap text-[12px]", count === 0 && "opacity-60", className)} data-outcome={outcome}>
      <ValidationGlyph outcome={outcome} className={count === 0 ? "text-muted-foreground" : undefined} />
      <span className="font-mono tabular-nums">{count.toLocaleString("en-US")}</span>
      {/* A space a flex row does not draw, so the count reads "3 invalid" as text. */}
      {" "}
      <span className="text-muted-foreground">{outcomeWord(outcome)}</span>
    </span>
  );
}
