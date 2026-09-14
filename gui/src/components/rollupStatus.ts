import type { RunStatus } from "../api/types";

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
