// What the views of a verdict say in words, apart from how they draw it: the word of an outcome, the counts of the records
// a record refers to, and the problems a check found at an element of a record.

import type { ValidationFinding, ValidationOutcome, ValidationVerdict } from "../../api/validation";

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
