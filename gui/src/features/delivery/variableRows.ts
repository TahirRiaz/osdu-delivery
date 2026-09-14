import type {
  DeliveryTemplateDetail, DeliveryTemplateVariable, MappingDraft, MappingDraftEntry,
} from "../../api/delivery";
import { KEY_NAME } from "./mappingDraft";

/** One row of the variables list: a variable a mapping can fill, a key under an object with free keys, or an entry the template does not let a mapping fill. */
export interface VariableRow {
  key: string;
  variable: DeliveryTemplateVariable;
  entry: MappingDraftEntry | null;
  kind: "variable" | "key" | "outside";
  /** Why no mapping may fill the row's target, for an outside row. */
  outside: string | null;
}

/** The variable a key under an object with free keys is, the way the template resolves it. */
export function keyVariable(holder: DeliveryTemplateVariable, target: string): DeliveryTemplateVariable {
  const type = holder.keyValueType ?? "string";
  return {
    path: target,
    shape: type === "object" || type === "array" || type === "any" ? "Whole" : "Value",
    type,
    itemType: null,
    format: null,
    required: false,
    role: "Mapping",
    relationships: [],
    pattern: null,
    unitContext: null,
    title: null,
    description: holder.description,
    keyValueType: null,
    nested: false,
    cacheTypes: [],
  };
}

function isKeyOf(holder: string, target: string): boolean {
  return target.startsWith(`${holder}.`) && KEY_NAME.test(target.slice(holder.length + 1));
}

function unknownVariable(target: string): DeliveryTemplateVariable {
  return {
    path: target,
    shape: "Value",
    type: "any",
    itemType: null,
    format: null,
    required: false,
    role: "Mapping",
    relationships: [],
    pattern: null,
    unitContext: null,
    title: null,
    description: null,
    keyValueType: null,
    nested: false,
    cacheTypes: [],
  };
}

/**
 * The rows of the variables list, in template order: every variable a mapping can fill with its entry, each key entry
 * under the object with free keys it belongs to, and at the end every entry whose target the template does not let a
 * mapping fill, so nothing in the draft is out of sight.
 */
export function variableRows(detail: DeliveryTemplateDetail, draft: MappingDraft): VariableRow[] {
  const byTarget = new Map(draft.entries.map((entry) => [entry.target, entry]));
  const byPath = new Map(detail.variables.map((variable) => [variable.path, variable]));
  const shown = new Set<string>();
  const rows: VariableRow[] = [];

  for (const variable of detail.variables) {
    if (variable.role !== "Mapping" || variable.nested) {
      continue;
    }

    const entry = byTarget.get(variable.path) ?? null;
    shown.add(variable.path);
    rows.push({ key: variable.path, variable, entry, kind: "variable", outside: null });
    if (variable.keyValueType === null) {
      continue;
    }

    for (const candidate of draft.entries) {
      // A property the template names is its own row, even under an object that also takes free keys.
      if (!shown.has(candidate.target) && !byPath.has(candidate.target) && isKeyOf(variable.path, candidate.target)) {
        shown.add(candidate.target);
        rows.push({ key: candidate.target, variable: keyVariable(variable, candidate.target), entry: candidate, kind: "key", outside: null });
      }
    }
  }

  for (const entry of draft.entries) {
    if (shown.has(entry.target)) {
      continue;
    }

    shown.add(entry.target);
    const known = byPath.get(entry.target);
    let outside: string;
    if (known === undefined) {
      outside = `Template ${detail.kind} version ${detail.version} has no variable ${entry.target}.`;
    } else if (known.role === "Engine") {
      outside = `${entry.target} is written by OSDU Delivery, not by a mapping.`;
    } else if (known.role === "Osdu") {
      outside = `${entry.target} is set by OSDU when the record is stored, not by a mapping.`;
    } else {
      outside = `${entry.target} is a list inside a repeated item, which a mapping cannot fill.`;
    }

    rows.push({ key: entry.target, variable: known ?? unknownVariable(entry.target), entry, kind: "outside", outside });
  }

  return rows;
}
