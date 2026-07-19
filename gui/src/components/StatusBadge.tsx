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

/** One mapping from run status to tone/affordance, used everywhere a run status renders. */
export function RunStatusBadge({ status }: { status: RunStatus | string }) {
  switch (status) {
    case "queued":
      return <Pill tone="warning" label="queued" icon={Clock3} testId="status-badge" />;
    case "running":
      return <Pill tone="info" label="running" icon={Loader2} spin testId="status-badge" />;
    case "succeeded":
      return <Pill tone="success" label="succeeded" icon={CircleCheck} testId="status-badge" />;
    case "failed":
      return <Pill tone="destructive" label="failed" icon={CircleX} testId="status-badge" />;
    case "cancelled":
      return <Pill tone="muted" label="cancelled" icon={Ban} testId="status-badge" />;
    case "skipped":
      return <Pill tone="muted" label="skipped" icon={SkipForward} testId="status-badge" />;
    default:
      return <Pill tone="muted" label={status} icon={CircleMinus} testId="status-badge" />;
  }
}

export function OnlineBadge({ online }: { online: boolean }) {
  return online
    ? <Pill tone="success" label="online" icon={Wifi} testId="online-badge" />
    : <Pill tone="muted" label="offline" icon={WifiOff} testId="online-badge" />;
}

export function ActiveBadge({ active }: { active: boolean }) {
  return active
    ? <Pill tone="success" label="active" icon={CircleCheck} testId="active-badge" />
    : <Pill tone="muted" label="inactive" icon={CircleMinus} testId="active-badge" />;
}

export function ScheduleStateBadge({ enabled, paused }: { enabled: boolean; paused: boolean }) {
  if (!enabled) {
    return <Pill tone="muted" label="disabled" icon={Power} testId="schedule-badge" />;
  }

  return paused
    ? <Pill tone="warning" label="paused" icon={Pause} testId="schedule-badge" />
    : <Pill tone="success" label="enabled" icon={CircleCheck} testId="schedule-badge" />;
}
