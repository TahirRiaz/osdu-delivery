// What the views of a verdict say in words, apart from how they draw it: the word of an outcome, the counts of the records
// a record refers to, the problems a check found at an element of a record, and what a mapping's assertions found.

import type { AssertionAction, AssertionFailure, AssertionFindings, AssertionStage, ValidationFinding, ValidationOutcome, ValidationVerdict } from "../../api/validation";

const OUTCOME_WORDS: Record<ValidationOutcome, string> = {
  valid: "valid",
  invalid: "invalid",
  unverified: "unverified",
  notValidated: "not validated",
};

/** The word an outcome is said with. */
export function outcomeWord(outcome: ValidationOutcome): string {
  return OUTCOME_WORDS[outcome];
}

/** The counts of the records a verdict's record refers to, by where they were found; zeros are left unsaid. */
export function referenceLine(verdict: ValidationVerdict): string | null {
  const refs = verdict.references;
  if (refs.total === 0) {
    return null;
  }

  const parts = [
    refs.osdu > 0 ? `${refs.osdu.toLocaleString("en-US")} in OSDU` : null,
    refs.ledger > 0 ? `${refs.ledger.toLocaleString("en-US")} in the ledger` : null,
    refs.cache > 0 ? `${refs.cache.toLocaleString("en-US")} in the cache` : null,
    refs.missing > 0 ? `${refs.missing.toLocaleString("en-US")} missing` : null,
    refs.notChecked > 0 ? `${refs.notChecked.toLocaleString("en-US")} not looked up` : null,
  ].filter((part): part is string => part !== null);
  const total = `${refs.total.toLocaleString("en-US")} reference${refs.total === 1 ? "" : "s"}${refs.cut ? " (the first ones)" : ""}`;
  return parts.length === 0 ? total : `${total}: ${parts.join(", ")}`;
}

/** The problems a check found at an element of the record, or inside it for a section, by the element's path. */
export function problemsAt(problems: readonly ValidationFinding[], path: string, section: boolean): ValidationFinding[] {
  return problems.filter((p) => p.path === path || (section && (p.path.startsWith(`${path}.`) || p.path.startsWith(`${path}[`))));
}

const ACTION_WORDS: Record<AssertionAction, string> = {
  hold: "holds the record",
  report: "sent and reported",
  omit: "value left out",
};

const STAGE_WORDS: Record<AssertionStage, string> = {
  record: "the value the record carries",
  incoming: "the value the row gives",
};

/** What a failure of an assertion does, in words. */
export function assertionActionWord(action: AssertionAction): string {
  return ACTION_WORDS[action] ?? action;
}

/** Which value an assertion judges, in words. */
export function assertionStageWord(stage: AssertionStage): string {
  return STAGE_WORDS[stage] ?? stage;
}

/** One line saying what a mapping's assertions found: every judgement met, or how many failed and what their failures do. */
export function assertionLine(findings: AssertionFindings): string {
  const judged = `${findings.checked.toLocaleString("en-US")} judgement${findings.checked === 1 ? "" : "s"} of ${findings.mapping}`;
  if (findings.failed === 0) {
    return `${judged}, all met`;
  }

  const parts = [
    findings.held > 0 ? `${findings.held.toLocaleString("en-US")} holding the record` : null,
    findings.reported > 0 ? `${findings.reported.toLocaleString("en-US")} reported` : null,
    findings.omitted > 0 ? `${findings.omitted.toLocaleString("en-US")} leaving the value out` : null,
  ].filter((part): part is string => part !== null);
  return `${findings.failed.toLocaleString("en-US")} of ${judged} failed: ${parts.join(", ")}`;
}

/** The failures of a mapping's assertions at an element of the record, or inside it for a section, by the element's path. */
export function assertionFailuresAt(failures: readonly AssertionFailure[], path: string, section: boolean): AssertionFailure[] {
  return failures.filter((f) => f.stage === "record"
    && (f.path === path || (section && (f.path.startsWith(`${path}.`) || f.path.startsWith(`${path}[`)))));
}
