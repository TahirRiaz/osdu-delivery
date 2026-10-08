import type { ReactNode } from "react";
import { CircleCheck, CircleX, Loader2 } from "lucide-react";
import { HoverCard, HoverCardContent, HoverCardTrigger } from "@/components/ui/hover-card";
import { CopyButton } from "@/components/CopyButton";
import { OutcomePill } from "@/components/StatusBadge";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import type { InventoryCount } from "../../../api/inventories";
import { STANDING_VISUALS, findingVisual, type InventoryStanding } from "./inventoryFormat";

/**
 * An explanation behind what it explains: what a finding means, why an id has it, what a run did. It opens on hover over its
 * trigger and stays while the pointer is in it, so its Copy button can be reached; the page itself keeps to one line per row.
 */
export function ExplainTip({ title, text, children, testId, side = "top" }: {
  title: string;
  text: string;
  children: ReactNode;
  testId: string;
  side?: "top" | "right" | "bottom" | "left";
}) {
  return (
    <HoverCard openDelay={350} closeDelay={150}>
      <HoverCardTrigger asChild>{children}</HoverCardTrigger>
      <HoverCardContent side={side} align="start" className="w-80 p-3" data-testid={`${testId}-tip`}>
        <div className="flex items-start justify-between gap-2">
          <span className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">{title}</span>
          <CopyButton iconOnly label={`Copy: ${title.toLowerCase()}`} text={text} testId={`${testId}-copy`} />
        </div>
        <p className="mt-1 whitespace-pre-wrap break-words text-[12.5px] leading-snug">{text}</p>
      </HoverCardContent>
    </HoverCard>
  );
}

/** A finding's glyph: in its tone when ids have it, muted when none does or it needs no look. */
export function FindingGlyph({ finding, quiet = false, className }: { finding: string; quiet?: boolean; className?: string }) {
  const visual = findingVisual(finding);
  const Icon = visual.icon;
  return <Icon className={cn("size-3.5 shrink-0", quiet ? "text-muted-foreground/60" : visual.tone, className)} aria-hidden />;
}

/**
 * The raised findings an inventory's last reconcile counted, each a glyph and a count on one line, the finding's name and
 * meaning on hover; a quiet word when there are none.
 */
export function RaisedCounts({ counts, testId }: { counts: readonly InventoryCount[] | undefined; testId: string }) {
  if (counts === undefined) {
    return <span className="text-[12px] text-muted-foreground" data-testid={testId}>not reconciled</span>;
  }

  const raised = counts.filter((count) => count.raised && count.count > 0);
  if (raised.length === 0) {
    return <span className="text-[12px] text-muted-foreground" data-testid={testId}>none raised</span>;
  }

  return (
    <span className="inline-flex items-center gap-x-2.5 whitespace-nowrap" data-testid={testId}>
      {raised.map((count) => (
        <RichTooltip key={count.finding} title={findingVisual(count.finding).label} body={findingVisual(count.finding).hint}>
          <span className="inline-flex items-center gap-1 text-[12px]" data-finding={count.finding} aria-label={`${count.count} ${findingVisual(count.finding).label.toLowerCase()}`}>
            <FindingGlyph finding={count.finding} />
            <span className="font-mono tabular-nums">{count.count.toLocaleString("en-US")}</span>
          </span>
        </RichTooltip>
      ))}
    </span>
  );
}

/** Where an inventory stands, as a glyph in its status colour with the words on hover. */
export function StandingGlyph({ standing, testId }: { standing: InventoryStanding; testId?: string }) {
  const visual = STANDING_VISUALS[standing];
  const Icon = visual.icon;
  return (
    <RichTooltip title={visual.label} body={visual.hint}>
      <span className="inline-flex shrink-0" data-testid={testId} data-standing={standing}>
        <Icon className={cn("size-4", visual.tone, visual.spin && "animate-spin")} aria-label={visual.label} />
      </span>
    </RichTooltip>
  );
}

/** What a build or reconcile came to, as the outcome pill every run status wears. */
export function RunStatusPill({ status, testId }: { status: string; testId: string }) {
  switch (status) {
    case "completed":
      return <OutcomePill tone="success" label="completed" icon={CircleCheck} testId={testId} />;
    case "running":
      return <OutcomePill tone="info" label="running" icon={Loader2} testId={testId} />;
    default:
      return <OutcomePill tone="destructive" label={status} icon={CircleX} testId={testId} />;
  }
}
