// What an assertion of a mapping says, in the words its document writes it with (osdu/docs/reference/flow/mapping-assertions.md):
// the condition, the conditions it is judged under, and the one line a list of a property's assertions shows for each.

import type { MappingDraftAssertion, MappingDraftAssertionFilter } from "../../api/delivery";
import { assertionActionWord } from "./validationModel";

/** The condition as the document writes it after the operator, on one line: `between [0, 250]`, `in ['GR', 'SP']`. */
export function assertionConditionText(assertion: Pick<MappingDraftAssertion, "operator" | "operand" | "ignoreCase" | "tolerance">): string {
  const settings = [
    assertion.ignoreCase ? "ignoring case" : null,
    assertion.tolerance != null ? `within ${assertion.tolerance}` : null,
  ].filter((part): part is string => part !== null);
  const condition = `${assertion.operator.trim()} ${assertion.operand.trim()}`.trim();
  return settings.length === 0 ? condition : `${condition} (${settings.join(", ")})`;
}

/** One condition an assertion is judged under, as `where` reads: `data.Name exists true`, `column weight_unit equals 'KG'`. */
export function assertionFilterText(filter: MappingDraftAssertionFilter): string {
  const reads = filter.reads === "column" ? `column ${filter.path.trim()}` : filter.path.trim();
  return `${reads} ${assertionConditionText(filter)}`;
}

/** What an assertion asserts, under the conditions it is judged under: `between [0, 250] where data.Name exists true`. */
export function assertionText(assertion: MappingDraftAssertion): string {
  const where = assertion.where.map(assertionFilterText);
  const condition = assertionConditionText(assertion);
  return where.length === 0 ? condition : `${condition} where ${where.join(" and ")}`;
}

/** What an assertion is called wherever what it finds is shown: its name, or what it asserts. */
export function assertionLabel(assertion: MappingDraftAssertion): string {
  const name = assertion.name?.trim() ?? "";
  return name !== "" ? name : assertionText(assertion);
}

/**
 * One assertion on one line, for a hover listing a property's: the property it is written on when that is not `about`,
 * what it is called and what it asserts when that differs, the value it judges when not the record's, and what a failure
 * does.
 */
export function assertionLine(assertion: MappingDraftAssertion, target: string, about: string): string {
  const label = assertionLabel(assertion);
  const text = assertionText(assertion);
  const where = target === about ? "" : `${target}: `;
  const stage = assertion.stage === "incoming" ? ", on the value the row gives" : "";
  return `${where}${label}${label === text ? "" : ` (${text})`}${stage}, ${assertionActionWord(assertion.onFail)}`;
}
