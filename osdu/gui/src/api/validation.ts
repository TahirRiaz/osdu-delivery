// What a check of a record against its schema came to (osdu/docs/validation-plan.md, The verdict), as an attempt's result
// carries it under `validation` and as the explorer answers it: the one shape every view of a verdict reads.

/** What a check came to. */
export type ValidationOutcome = "valid" | "invalid" | "unverified" | "notValidated";

/** One way a record breaks its schema, or one part of it no rule could be checked on. */
export interface ValidationFinding {
  /** Where, by the path every value of the property shares: `data.VerticalMeasurements[].VerticalCRSID`; empty for the record itself. */
  at: string;
  /** Exactly where: `data.VerticalMeasurements[2].VerticalCRSID`. */
  path: string;
  /** The JSON Schema keyword, `relationship`, `reference`, or for a part not checked, why. */
  rule: string;
  message: string;
  value: string;
}

/** An id the record refers to whose record was not found, and what said so. */
export interface ValidationMissingReference {
  id: string;
  at: string;
  path: string;
  detail: string;
}

/** What was found of the records a record refers to, counted by where. */
export interface ValidationReferences {
  total: number;
  ledger: number;
  cache: number;
  osdu: number;
  missing: number;
  notChecked: number;
  cut: boolean;
  missingIds: ValidationMissingReference[];
}

export interface ValidationVerdict {
  outcome: ValidationOutcome;
  /** The schema checked against; absent when none could be had. `source` is `template` (a saved one) or `schema-service`. */
  schema?: { kind: string; version: string; source: "template" | "schema-service" };
  /** How many rules were applied. */
  rules: number;
  problemCount: number;
  problems: ValidationFinding[];
  unverifiedCount: number;
  unverified: ValidationFinding[];
  references: ValidationReferences;
  /** What the schema states that no check asserts, and why nothing was checked when nothing was. */
  notes: string[];
  /** Whether a release accepted the verdict for the document, so it was sent whatever it says. */
  accepted?: boolean;
  rulesVersion: string;
  checkedUtc: string;
  /** Whether the listings were shortened to fit an attempt's result; the counts are whole. */
  shortened?: boolean;
}

/** The verdict an attempt's result carries, or null for an attempt that carries none. */
export function verdictOf(resultJson: string | null | undefined): ValidationVerdict | null {
  if (resultJson === null || resultJson === undefined || resultJson === "") {
    return null;
  }

  try {
    const parsed: unknown = JSON.parse(resultJson);
    if (typeof parsed !== "object" || parsed === null) {
      return null;
    }

    const validation = (parsed as { validation?: unknown }).validation;
    return isVerdict(validation) ? validation : null;
  } catch {
    // A result that is not JSON is not the shape anything wrote; it carries no verdict.
    return null;
  }
}

function isVerdict(value: unknown): value is ValidationVerdict {
  if (typeof value !== "object" || value === null) {
    return false;
  }

  const outcome = (value as { outcome?: unknown }).outcome;
  return outcome === "valid" || outcome === "invalid" || outcome === "unverified" || outcome === "notValidated";
}

/** How a finding names where it is: its exact path, or the record itself. */
export function whereOf(finding: { at: string; path: string }): string {
  return finding.path !== "" ? finding.path : finding.at !== "" ? finding.at : "the record";
}

/** How a source of a schema reads to a person. */
export function schemaSourceName(source: string | undefined): string {
  return source === "schema-service" ? "the Schema service" : source === "template" ? "a saved template" : "no schema";
}
