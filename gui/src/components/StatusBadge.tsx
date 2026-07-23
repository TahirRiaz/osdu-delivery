import {
  Ban,
  CircleCheck,
  CircleMinus,
  CircleX,
  Clock3,
  Loader2,
  Pause,
  Power,
  SkipForward,
  Wifi,
  WifiOff,
  type LucideIcon,
} from "lucide-react";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import type { RunStatus } from "../api/types";

type Tone = "success" | "destructive" | "info" | "warning" | "muted";

const toneClasses: Record<Tone, string> = {
  success: "bg-success/12 text-success",
  destructive: "bg-destructive/12 text-destructive",
  info: "bg-info/12 text-info",
  warning: "bg-warning/15 text-warning",
  muted: "bg-muted text-muted-foreground",
};

/**
 * The one status pill (DESIGN.md 7.3): tinted background, solid text, and an icon so color never
 * carries the state alone. Every domain badge below renders through this.
 */
function Pill({
  tone,
  label,
  icon: Icon,
  spin = false,
  testId,
}: {
  tone: Tone;
  label: string;
  icon: LucideIcon;
  spin?: boolean;
  testId: string;
}) {
  return (
    <span
      data-testid={testId}
      className={cn(
        "inline-flex items-center gap-1 whitespace-nowrap rounded-full px-2 py-0.5 text-[11px] font-medium leading-4",
        toneClasses[tone],
      )}
    >
      <Icon className={cn("size-3 shrink-0", spin && "animate-spin")} />
      {label}
    </span>
  );
}

/** The tone and icon a run status renders with; the label doubles as the tooltip text and the accessible name. */
function runStatusVisual(status: RunStatus | string): { tone: Tone; label: string; icon: LucideIcon; spin?: boolean } {
  switch (status) {
    case "queued":
      return { tone: "warning", label: "queued", icon: Clock3 };
    case "running":
      return { tone: "info", label: "running", icon: Loader2, spin: true };
    case "succeeded":
      return { tone: "success", label: "succeeded", icon: CircleCheck };
    case "failed":
      return { tone: "destructive", label: "failed", icon: CircleX };
    case "cancelled":
      return { tone: "muted", label: "cancelled", icon: Ban };
    case "skipped":
      return { tone: "muted", label: "skipped", icon: SkipForward };
    default:
      return { tone: "muted", label: status, icon: CircleMinus };
  }
}

/**
 * A status rendered as the tinted icon alone: the word is redundant next to the icon (and next to a column
 * header that already names the state), so it moves to a hover tooltip and a visually hidden label. Icon shape
 * plus tone still carry the state without color alone (DESIGN.md 7.3), and the hidden label keeps the word
 * queryable in tests and available to assistive tech.
 */
function IconBadge({
  tone,
  label,
  icon: Icon,
  spin = false,
  testId,
}: {
  tone: Tone;
  label: string;
  icon: LucideIcon;
  spin?: boolean;
  testId: string;
}) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span
          data-testid={testId}
          className={cn(
            "inline-flex size-5 shrink-0 items-center justify-center rounded-full",
            toneClasses[tone],
          )}
        >
          <Icon className={cn("size-3 shrink-0", spin && "animate-spin")} />
          <span className="sr-only">{label}</span>
        </span>
      </TooltipTrigger>
      <TooltipContent>{label}</TooltipContent>
    </Tooltip>
  );
}

/**
 * One mapping from run status to tone/affordance, used everywhere a run status renders. Renders through
 * {@link IconBadge}: only the tinted status icon, with the word on hover and for assistive tech.
 */
export function RunStatusBadge({ status, testId = "status-badge" }: { status: RunStatus | string; testId?: string }) {
  const { tone, label, icon: Icon, spin } = runStatusVisual(status);
  return <IconBadge tone={tone} label={label} icon={Icon} spin={spin} testId={testId} />;
}

/**
 * Worst-wins rollup of a group's run statuses into one headline status, so a collapsed group can show green at a
 * glance only when nothing failed and nothing is still pending. Precedence, most to least alarming:
 * failed > running > queued > succeeded > cancelled > skipped. A group with only benign trailing states
 * (skipped/cancelled) alongside successes still rolls up green, since nothing needs attention.
 */
export function rollupStatus(statuses: readonly (RunStatus | string)[]): RunStatus {
  const has = (s: RunStatus) => statuses.includes(s);
  if (has("failed")) {
    return "failed";
  }

  if (has("running")) {
    return "running";
  }

  if (has("queued")) {
    return "queued";
  }

  if (has("succeeded")) {
    return "succeeded";
  }

  return has("cancelled") ? "cancelled" : "skipped";
}

export function OnlineBadge({ online }: { online: boolean }) {
  return online
    ? <Pill tone="success" label="online" icon={Wifi} testId="online-badge" />
    : <Pill tone="muted" label="offline" icon={WifiOff} testId="online-badge" />;
}

export function ActiveBadge({ active }: { active: boolean }) {
  return active
    ? <IconBadge tone="success" label="active" icon={CircleCheck} testId="active-badge" />
    : <IconBadge tone="muted" label="inactive" icon={CircleMinus} testId="active-badge" />;
}

export function ScheduleStateBadge({ enabled, paused }: { enabled: boolean; paused: boolean }) {
  if (!enabled) {
    return <Pill tone="muted" label="disabled" icon={Power} testId="schedule-badge" />;
  }

  return paused
    ? <Pill tone="warning" label="paused" icon={Pause} testId="schedule-badge" />
    : <Pill tone="success" label="enabled" icon={CircleCheck} testId="schedule-badge" />;
}
