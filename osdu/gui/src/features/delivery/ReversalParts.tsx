import { CheckCircle2, CircleDashed, Clock, Loader2, XCircle, type LucideIcon } from "lucide-react";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { StatePill } from "@/components/StatusBadge";
import { cn } from "@/lib/utils";
import type { DeliveryReversal } from "../../api/delivery";
import { REVERSAL_OUTCOMES } from "./reversalOutcomes";

/**
 * How many records of a reversal came out one way: the glyph in its tone, the count as data, and the word quietly after it,
 * with what it means in a tooltip. A neutral element: only the glyph carries the tone.
 */
export function ReversalOutcomeCount({ outcome, count, selected, onSelect, testId }: {
  outcome: string; count: number; selected?: boolean; onSelect?: () => void; testId?: string;
}) {
  const mark = REVERSAL_OUTCOMES[outcome] ?? { label: outcome, icon: CircleDashed, tone: "text-muted-foreground", meaning: outcome };
  const Icon = mark.icon;
  const content = (
    <>
      <Icon className={cn("size-3.5 shrink-0", mark.tone)} aria-hidden />
      <span className="font-mono tabular-nums">{count.toLocaleString()}</span>
      {" "}
      <span className="text-muted-foreground">{mark.label}</span>
    </>
  );
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        {onSelect === undefined
          ? <span className="inline-flex items-center gap-1 whitespace-nowrap text-[12px]" data-testid={testId}>{content}</span>
          : (
            <button
              type="button"
              onClick={onSelect}
              aria-pressed={selected === true}
              className={cn(
                "inline-flex items-center gap-1 whitespace-nowrap rounded-md border px-1.5 py-0.5 text-[12px] transition-colors",
                selected === true ? "border-primary bg-primary/5" : "border-transparent hover:bg-accent/50",
              )}
              data-testid={testId}
            >
              {content}
            </button>
          )}
      </TooltipTrigger>
      <TooltipContent className="max-w-xs">{mark.meaning}</TooltipContent>
    </Tooltip>
  );
}

const STATUS: Record<DeliveryReversal["status"], { tone: "success" | "destructive" | "info" | "warning" | "muted"; icon: LucideIcon; label: string }> = {
  capturing: { tone: "info", icon: Loader2, label: "listing" },
  reversing: { tone: "info", icon: Loader2, label: "reversing" },
  completed: { tone: "success", icon: CheckCircle2, label: "completed" },
  failed: { tone: "destructive", icon: XCircle, label: "stopped" },
  cancelled: { tone: "muted", icon: Clock, label: "cancelled" },
};

/** A reversal's state, in the same pill family the run board uses. */
export function ReversalStatusPill({ status, testId = "reversal-status" }: { status: DeliveryReversal["status"]; testId?: string }) {
  const { tone, icon, label } = STATUS[status] ?? { tone: "muted" as const, icon: CircleDashed, label: status };
  return <StatePill tone={tone} label={label} icon={icon} testId={testId} />;
}
