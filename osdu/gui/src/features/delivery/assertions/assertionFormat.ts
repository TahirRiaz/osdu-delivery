import {
  CircleCheck, CircleDashed, CircleX, OctagonAlert, SkipForward, TriangleAlert, type LucideIcon,
} from "lucide-react";
import type {
  AssertionReportFormat, AssertionRunStatus, AssertionSeverity, DeliveryAssertionTest, TestOutcome,
} from "../../../api/delivery";
import { deliveryApi } from "../../../api/delivery";

export type OutcomeTone = "success" | "destructive" | "info" | "warning" | "muted";

/** Where a test stands on a board: an outcome of its latest run, or that it has not run in the partition. */
export type TestStanding = TestOutcome | "notRun" | "elsewhere";

interface OutcomeVisual {
  tone: OutcomeTone;
  icon: LucideIcon;
  label: string;
}

/**
 * How a test's outcome is shown (DESIGN.md 7.3): a glyph and a word in a status tone, so the colour never carries it alone.
 * A failure and an error share the destructive tone and differ in glyph: a failed test found what it asserts untrue, an
 * errored one could not find out.
 */
export const OUTCOME_VISUALS: Record<TestStanding, OutcomeVisual> = {
  passed: { tone: "success", icon: CircleCheck, label: "passed" },
  failed: { tone: "destructive", icon: CircleX, label: "failed" },
  warned: { tone: "warning", icon: TriangleAlert, label: "warned" },
  errored: { tone: "destructive", icon: OctagonAlert, label: "errored" },
  skipped: { tone: "muted", icon: SkipForward, label: "skipped" },
  notRun: { tone: "muted", icon: CircleDashed, label: "not run" },
  elsewhere: { tone: "muted", icon: SkipForward, label: "not in partition" },
};

/** The order outcomes are listed in: what needs a look first. */
export const OUTCOME_ORDER: readonly TestStanding[] = ["failed", "errored", "warned", "notRun", "passed", "skipped", "elsewhere"];

/** Where a board's test stands: its latest outcome, or not run, or not in the board's partition at all. */
export function standingOf(test: DeliveryAssertionTest): TestStanding {
  if (!test.runsHere) {
    return "elsewhere";
  }

  return test.latest?.outcome ?? "notRun";
}

/** How an assertion run's status is shown, in the same families as a test's outcome. */
export function runStatusVisual(status: AssertionRunStatus): OutcomeVisual {
  switch (status) {
    case "passed":
      return OUTCOME_VISUALS.passed;
    case "failed":
      return OUTCOME_VISUALS.failed;
    case "errored":
      return OUTCOME_VISUALS.errored;
    case "cancelled":
      return { tone: "muted", icon: CircleDashed, label: "cancelled" };
    default:
      return { tone: "info", icon: CircleDashed, label: "running" };
  }
}

/** The tone a severity chip wears: the weight its failure carries. */
export const SEVERITY_TONES: Record<AssertionSeverity, string> = {
  error: "text-destructive ring-destructive/40",
  warning: "text-warning ring-warning/45",
  info: "text-info ring-info/40",
};

/** The entity type's group of a kind (`master-data`, `work-product-component`), which a board groups tests by. */
export function kindGroup(kind: string): string {
  const entity = kind.split(":")[2] ?? kind;
  const at = entity.indexOf("--");
  return at > 0 ? entity.slice(0, at) : entity.includes("*") || entity === "" ? "any kind" : entity;
}

/** The entity type of a kind without its group (`Wellbore`), for a compact heading. */
export function kindEntity(kind: string): string {
  const entity = kind.split(":")[2] ?? kind;
  const at = entity.indexOf("--");
  return at > 0 ? entity.slice(at + 2) : entity;
}

/** A count with its noun, pluralized: "1 test", "3 tests". */
export function counted(count: number, noun: string): string {
  return `${count.toLocaleString("en-US")} ${noun}${count === 1 ? "" : "s"}`;
}

/** A duration in milliseconds as a reader reads it. */
export function duration(milliseconds: number | null | undefined): string {
  if (milliseconds === null || milliseconds === undefined) {
    return "-";
  }

  if (milliseconds < 1000) {
    return `${milliseconds} ms`;
  }

  const seconds = milliseconds / 1000;
  return seconds < 60 ? `${seconds.toFixed(1)} s` : `${Math.floor(seconds / 60)} min ${Math.round(seconds % 60)} s`;
}

/** The tests and tags a run was asked for, as its row names them; every test when it names none. */
export function selectionText(selection: string | null): string {
  if (selection === null) {
    return "every test";
  }

  try {
    const parsed = JSON.parse(selection) as { tests?: string[]; tags?: string[] };
    const parts = [
      ...(parsed.tests ?? []).map((name) => name),
      ...(parsed.tags ?? []).map((tag) => `#${tag}`),
    ];
    return parts.length === 0 ? "every test" : parts.join(", ");
  } catch {
    return selection;
  }
}

const EXTENSIONS: Record<AssertionReportFormat, { extension: string; type: string }> = {
  html: { extension: "html", type: "text/html;charset=utf-8" },
  md: { extension: "md", type: "text/markdown;charset=utf-8" },
  json: { extension: "json", type: "application/json;charset=utf-8" },
  junit: { extension: "xml", type: "application/xml;charset=utf-8" },
};

/** The file name a run's report downloads as: the flow, the partition and the report number. */
export function reportFileName(flow: string, partition: string | null, assertionRunId: number, format: AssertionReportFormat): string {
  const safe = `${flow}-${partition ?? "partition"}-report-${assertionRunId}`.replace(/[^A-Za-z0-9._-]+/g, "_");
  return `${safe}.${EXTENSIONS[format].extension}`;
}

/** Fetches a run's report in a format and offers it as a file to save. */
export async function downloadReport(flow: string, partition: string | null, assertionRunId: number, format: AssertionReportFormat): Promise<void> {
  const text = await deliveryApi.assertionReport(assertionRunId, format);
  const url = URL.createObjectURL(new Blob([text], { type: EXTENSIONS[format].type }));
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = reportFileName(flow, partition, assertionRunId, format);
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}

/** Opens a run's HTML report in a tab of its own, to read or print. */
export async function openHtmlReport(assertionRunId: number): Promise<void> {
  const text = await deliveryApi.assertionReport(assertionRunId, "html");
  const url = URL.createObjectURL(new Blob([text], { type: EXTENSIONS.html.type }));
  // The report is a page with no script, so the tab it opens in has nothing to do with the workbench that opened it; the
  // URL is released once the tab has had time to load it, and a blocked pop-up saves the file instead.
  const opened = window.open(url, "_blank");
  if (opened === null) {
    URL.revokeObjectURL(url);
    throw new Error("The browser blocked the report's tab; download it instead, or allow pop-ups for the workbench.");
  }

  window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

/** The payload that runs a selection of an assertion flow's tests: by name and by tag. */
export function selectionPayload(tests: readonly string[], tags: readonly string[]): Record<string, unknown> {
  const payload: Record<string, unknown> = {};
  if (tests.length > 0) {
    payload.tests = [...tests];
  }

  if (tags.length > 0) {
    payload.tags = [...tags];
  }

  return payload;
}
