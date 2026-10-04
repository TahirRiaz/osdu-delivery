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
  /** Records left waiting for a record they refer to; null when the run reports none. */
  waiting: number | null;
}

/** The record counts a run reported: a deliver run's own, or a verify run's read as the same headline numbers. */
export function runRecordCounts(result: Record<string, unknown> | null): RunRecordCounts {
  if (result === null) {
    return { planned: null, delivered: null, held: null, failed: null, unchanged: null, waiting: null };
  }

  const drifted = count(result, "drifted");
  const missing = count(result, "missing");
  return {
    planned: count(result, "planned") ?? count(result, "checked"),
    delivered: count(result, "delivered") ?? count(result, "matched"),
    held: count(result, "held") ?? (drifted === null && missing === null ? null : (drifted ?? 0) + (missing ?? 0)),
    failed: count(result, "failed") ?? count(result, "errors"),
    unchanged: count(result, "skippedUnchanged"),
    waiting: count(result, "waiting"),
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
  /** Whether the run brings records up to date: renders them again and sends only what renders differently. */
  rerender: boolean;
  /** The key slices of the submission a fan-out intake member plans. */
  slices: number[];
  /** The tests an assertion run was asked to run, by name and by tag; both empty when it ran every test. */
  tests: string[];
  tags: string[];
}

function names(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((name): name is string => typeof name === "string") : [];
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
    rerender: payload.rerender === true,
    slices: Array.isArray(payload.slices)
      ? payload.slices.filter((slice): slice is number => typeof slice === "number" && Number.isInteger(slice))
      : [],
    tests: names(payload.tests),
    tags: names(payload.tags),
  };
}

/** One record a plan would send, as its outcome names it. */
export interface PlanSample {
  key: string;
  label: string | null;
  sourceKey: string;
  action: string;
  reason: string;
  metadata: boolean;
  payload: boolean;
}

/** What a plan run said a delivery would do: how many it read, would send (the record, its payload), leave, hold, and the first it would send. */
export interface PlanPreview {
  records: number;
  deliveries: number;
  metadata: number;
  payload: number;
  unchanged: number;
  held: number;
  blocked: number;
  other: number;
  sample: PlanSample[];
}

/** The outcome of a plan run, read from its result; null before it finished, or for a run that is not a plan. */
export function planPreview(run: RunDetail): PlanPreview | null {
  const result = runResult(run);
  if (result === null || result.operation !== "plan") {
    return null;
  }

  const number = (field: string) => count(result, field) ?? 0;
  const sample = Array.isArray(result.sample) ? result.sample.filter(isRecord).map((entry): PlanSample => ({
    key: typeof entry.key === "string" ? entry.key : "",
    label: typeof entry.label === "string" ? entry.label : null,
    sourceKey: typeof entry.sourceKey === "string" ? entry.sourceKey : "",
    action: typeof entry.action === "string" ? entry.action : "",
    reason: typeof entry.reason === "string" ? entry.reason : "",
    metadata: entry.metadata === true,
    payload: entry.payload === true,
  })).filter((entry) => entry.key !== "") : [];
  return {
    records: number("records"),
    deliveries: number("deliveries"),
    metadata: number("metadata"),
    payload: number("payload"),
    unchanged: number("skips"),
    held: number("holds"),
    blocked: number("blocked"),
    other: number("awaitingApproval") + number("stale") + number("untracked"),
    sample,
  };
}
