import Chip from "@mui/material/Chip";
import CircularProgress from "@mui/material/CircularProgress";
import type { RunStatus } from "../api/types";

/** One mapping from run status to color/affordance, used everywhere a run status renders. */
export function RunStatusBadge({ status }: { status: RunStatus | string }) {
  switch (status) {
    case "queued":
      return <Chip size="small" label="queued" color="info" variant="outlined" data-testid="status-badge" />;
    case "running":
      return (
        <Chip
          size="small"
          label="running"
          color="primary"
          icon={<CircularProgress size={12} color="inherit" />}
          data-testid="status-badge"
        />
      );
    case "succeeded":
      return <Chip size="small" label="succeeded" color="success" data-testid="status-badge" />;
    case "failed":
      return <Chip size="small" label="failed" color="error" data-testid="status-badge" />;
    case "cancelled":
      return <Chip size="small" label="cancelled" color="warning" variant="outlined" data-testid="status-badge" />;
    case "skipped":
      return <Chip size="small" label="skipped" color="default" variant="outlined" data-testid="status-badge" />;
    default:
      return <Chip size="small" label={status} data-testid="status-badge" />;
  }
}

export function OnlineBadge({ online }: { online: boolean }) {
  return online
    ? <Chip size="small" label="online" color="success" data-testid="online-badge" />
    : <Chip size="small" label="offline" color="default" variant="outlined" data-testid="online-badge" />;
}

export function ActiveBadge({ active }: { active: boolean }) {
  return active
    ? <Chip size="small" label="active" color="success" variant="outlined" data-testid="active-badge" />
    : <Chip size="small" label="inactive" color="default" variant="outlined" data-testid="active-badge" />;
}

export function ScheduleStateBadge({ enabled, paused }: { enabled: boolean; paused: boolean }) {
  if (!enabled) {
    return <Chip size="small" label="disabled" color="default" variant="outlined" data-testid="schedule-badge" />;
  }

  return paused
    ? <Chip size="small" label="paused" color="warning" data-testid="schedule-badge" />
    : <Chip size="small" label="enabled" color="success" variant="outlined" data-testid="schedule-badge" />;
}
