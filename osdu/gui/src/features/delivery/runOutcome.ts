import type { RunDetail } from "@/api/types";

// What a delivery, retrieval or cache run was asked to do and what it reported, read from the run page's generic fields:
// the kind-owned payload the trigger sent, and the bounded result JSON the run wrote when it finished. The field names
// are the ones the delivery kind's outcomes carry (a deliver run's planned and delivered counts, a verify run's checked,
// matched, drifted and missing ones).

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

/** The run's result object, or null: before the run finished, for a kind that reports none, or for text that is not an object. */
export function runResult(run: RunDetail): Record<string, unknown> | null {
  if (run.resultJson === null) {
    return null;
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(run.resultJson);
  } catch {
    // The result is bounded when it is stored, so an oversized one arrives cut off; the outcome card still shows the text.
    return null;
  }

  return isRecord(parsed) ? parsed : null;
}

function count(result: Record<string, unknown>, field: string): number | null {
  const value = result[field];
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

export interface RunRecordCounts {
  planned: number | null;
  delivered: number | null;
  held: number | null;
  failed: number | null;
  unchanged: number | null;
}

/** The record counts a run reported: a deliver run's own, or a verify run's read as the same headline numbers. */
export function runRecordCounts(result: Record<string, unknown> | null): RunRecordCounts {
  if (result === null) {
    return { planned: null, delivered: null, held: null, failed: null, unchanged: null };
  }

  const drifted = count(result, "drifted");
  const missing = count(result, "missing");
  return {
    planned: count(result, "planned") ?? count(result, "checked"),
    delivered: count(result, "delivered") ?? count(result, "matched"),
    held: count(result, "held") ?? (drifted === null && missing === null ? null : (drifted ?? 0) + (missing ?? 0)),
    failed: count(result, "failed") ?? count(result, "errors"),
    unchanged: count(result, "skippedUnchanged"),
  };
}

/** The submission a run worked on: the one its trigger named, else the one its result reports. */
export function runSubmissionId(run: RunDetail, result: Record<string, unknown> | null): string | null {
  const requested = run.payload?.submissionId;
  if (typeof requested === "string" && UUID.test(requested)) {
    return requested;
  }

  const reported = result?.submissionId;
  return typeof reported === "string" && UUID.test(reported) ? reported : null;
}

/** What a run's trigger asked of the kind, read from its payload; anything the payload does not carry reads as absent. */
export interface RunRequest {
  force: boolean;
  submissionId: string | null;
  recordKeys: string[];
  redeliver: string | null;
  replan: boolean;
  partitions: number[];
}

export function runRequest(run: RunDetail): RunRequest {
  const payload = run.payload ?? {};
  return {
    force: payload.force === true,
    submissionId: typeof payload.submissionId === "string" ? payload.submissionId : null,
    recordKeys: Array.isArray(payload.recordKeys)
      ? payload.recordKeys.filter((key): key is string => typeof key === "string")
      : [],
    redeliver: typeof payload.redeliver === "string" ? payload.redeliver : null,
    replan: payload.replan === true,
    partitions: Array.isArray(payload.partitions)
      ? payload.partitions.filter((slice): slice is number => typeof slice === "number" && Number.isInteger(slice))
      : [],
  };
}
