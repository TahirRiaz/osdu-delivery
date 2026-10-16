import type { ComponentProps } from "react";
import { CheckCircle2, CircleDashed, CircleDot, CircleSlash, Loader2, XCircle, type LucideIcon } from "lucide-react";
import type { OutcomePill } from "@/components/StatusBadge";
import type { DeliveryActivity } from "../../api/delivery";

type Tone = ComponentProps<typeof OutcomePill>["tone"];

/** How an activity ended: its glyph and tone, the word its pill carries, and the sentence its hover and a screen reader say. */
export interface Ending {
  icon: LucideIcon;
  tone: Tone;
  word: string;
  label: string;
  /** The glyph turns where it stands alone (a spinner); a pill keeps it still. */
  spin?: boolean;
}

/** Each ending of the audit trail; an idle run is told apart from one that changed something. */
const ENDINGS: Record<DeliveryActivity["outcome"] | "idle", Ending> = {
  completed: { icon: CheckCircle2, tone: "success", word: "completed", label: "Completed" },
  failed: { icon: XCircle, tone: "destructive", word: "failed", label: "Failed" },
  cancelled: { icon: CircleSlash, tone: "warning", word: "cancelled", label: "Cancelled" },
  running: { icon: Loader2, tone: "info", word: "running", label: "Running", spin: true },
  idle: { icon: CircleDashed, tone: "muted", word: "changed nothing", label: "Completed, changed nothing" },
};

/** The text colour of each tone, for an ending's glyph standing alone in a cell. */
export const TONE_TEXT: Record<Tone, string> = {
  success: "text-success",
  destructive: "text-destructive",
  warning: "text-warning",
  info: "text-info",
  muted: "text-muted-foreground",
};

/** How an activity ended, as the trail and its sheet show it. */
export function endingOf(row: DeliveryActivity): Ending {
  return row.idle
    ? ENDINGS.idle
    : ENDINGS[row.outcome] ?? { icon: CircleDot, tone: "muted", word: row.outcome, label: row.outcome };
}
