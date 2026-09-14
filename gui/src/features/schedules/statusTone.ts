import type { RunStatus } from "../../api/types";

/** The status tones the timeline draws with (DESIGN.md 3.2): status colors are reserved for run states, and
 * cancelled/skipped/none read as muted. One mapping shared by the chart and the page's legend. */
export type StatusTone = "success" | "destructive" | "info" | "warning" | "muted";

export function statusTone(status: RunStatus | null): StatusTone {
  switch (status) {
    case "succeeded":
      return "success";
    case "failed":
      return "destructive";
    case "running":
      return "info";
    case "queued":
      return "warning";
    default:
      return "muted";
  }
}
