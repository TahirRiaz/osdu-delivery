import {
  AlertTriangle, Ban, CheckCircle2, CircleDashed, Loader2, PauseCircle, Trash2, XCircle, type LucideIcon,
} from "lucide-react";
import { Badge } from "@/components/ui/badge";
import type { DeliveryRecordStatus, DeliverySubmissionStatus, DeliveryVerifyOutcome } from "../../api/delivery";
import { StatePill } from "../../components/StatusBadge";

type Tone = "success" | "destructive" | "info" | "warning" | "muted";

const recordTones: Record<DeliveryRecordStatus, { tone: Tone; icon: LucideIcon }> = {
  pending: { tone: "info", icon: CircleDashed },
  delivering: { tone: "info", icon: Loader2 },
  delivered: { tone: "success", icon: CheckCircle2 },
  held: { tone: "warning", icon: PauseCircle },
  failed: { tone: "destructive", icon: XCircle },
  deleted: { tone: "muted", icon: Trash2 },
};

/** A record's custody state, in the same pill family the run board uses. */
export function RecordStatusBadge({ status, testId = "record-status" }: { status: DeliveryRecordStatus; testId?: string }) {
  const { tone, icon } = recordTones[status] ?? { tone: "muted" as Tone, icon: CircleDashed };
  return <StatePill tone={tone} label={status} icon={icon} testId={testId} />;
}

const submissionTones: Record<DeliverySubmissionStatus, Tone> = {
  received: "info",
  planned: "info",
  running: "info",
  completed: "success",
  failed: "destructive",
};

export function SubmissionStatusBadge({ status, testId = "submission-status" }: { status: DeliverySubmissionStatus; testId?: string }) {
  const icon: LucideIcon = status === "completed" ? CheckCircle2 : status === "failed" ? XCircle : status === "running" ? Loader2 : CircleDashed;
  return <StatePill tone={submissionTones[status] ?? "muted"} label={status} icon={icon} testId={testId} />;
}

/** What the last drift check found, when one ran. */
export function VerifyOutcomeBadge({ outcome }: { outcome: DeliveryVerifyOutcome | null }) {
  if (outcome === null) {
    return <span className="text-muted-foreground">-</span>;
  }

  const className = outcome === "match"
    ? "bg-success/15 text-success"
    : outcome === "error"
      ? "bg-muted text-muted-foreground"
      : "bg-warning/15 text-warning";
  return (
    <Badge variant="secondary" className={className} data-testid="verify-outcome">
      {outcome !== "match" && outcome !== "error" && <AlertTriangle className="size-3" />}
      {outcome}
    </Badge>
  );
}

export function BlockedBadge() {
  return (
    <Badge variant="secondary" className="bg-warning/15 text-warning" data-testid="record-blocked">
      <Ban className="size-3" />
      blocked
    </Badge>
  );
}
