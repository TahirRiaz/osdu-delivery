import type { ComponentProps, ReactNode } from "react";
import { ListPlus, ListRestart, SearchCheck, SquareDot, SquareMinus, SquarePlus, Unlink, type LucideIcon } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";

/**
 * How a change to cached data is drawn: its glyph, and the tone the glyph takes. A change is not an outcome and not a
 * state, so it wears neither the tinted pill nor the status ring (DESIGN.md 7.3): the chip and the words stay in the
 * text tokens and only the glyph carries the tone, which keeps a table of several hundred changes from reading as one
 * block of green, and leaves the status colors meaning what they mean everywhere else.
 */
const marks: Record<string, { icon: LucideIcon; tone: string }> = {
  added: { icon: SquarePlus, tone: "text-success" },
  changed: { icon: SquareDot, tone: "text-info" },
  removed: { icon: SquareMinus, tone: "text-destructive" },
  unmatched: { icon: Unlink, tone: "text-warning" },
  listed: { icon: ListPlus, tone: "text-info" },
  relisted: { icon: ListRestart, tone: "text-info" },
  found: { icon: SearchCheck, tone: "text-success" },
};

/** The glyph of one kind of change, in its tone; nothing for a kind this page does not know. */
export function ChangeGlyph({ change, className }: { change: string; className?: string }) {
  const mark = marks[change];
  if (mark === undefined) {
    return null;
  }

  const Icon = mark.icon;
  return <Icon className={cn("size-3.5 shrink-0", mark.tone, className)} aria-hidden />;
}

/**
 * A neutral chip led by the glyph of a change. With no children it says the change in a word; given children (a type's
 * name) it says those, and the glyph alone tells how the thing moved.
 */
export function ChangeBadge({
  change, children, className, ...props
}: { change: string; children?: ReactNode } & Omit<ComponentProps<typeof Badge>, "variant" | "children">) {
  return (
    <Badge variant="secondary" className={cn("rounded-md border-border bg-muted/60 text-foreground", className)} data-change={change} {...props}>
      <ChangeGlyph change={change} />
      {children ?? change}
    </Badge>
  );
}

/** How many records changed one way: the glyph, the count as data, and the word quietly after it. */
export function ChangeCount({ change, count, className }: { change: string; count: number; className?: string }) {
  return (
    <span className={cn("inline-flex items-center gap-1 whitespace-nowrap text-[12px]", className)} data-change={change}>
      <ChangeGlyph change={change} />
      <span className="font-mono tabular-nums">{count.toLocaleString()}</span>
      <span className="text-muted-foreground">{change}</span>
    </span>
  );
}
