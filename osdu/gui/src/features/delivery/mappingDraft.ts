// The mapping builder's draft logic that needs no server: which inputs a variable takes, where an entry goes in the draft,
// how a static value is edited, how an entry reads as text, and the column names a draft already knows. The server writes
// the YAML and checks it.

import type {
  DeliveryTemplateVariable, MappingDraft, MappingDraftCondition, MappingDraftEntry, MappingDraftInput, MappingDraftModifier,
  MappingDraftModifierKind,
} from "../../api/delivery";

/** The access and legal variables every record carries, which a mapping gives as static, non-empty lists of strings. */
export const ENVELOPE_TARGETS: readonly string[] = [
  "osdu.acl.owners",
  "osdu.acl.viewers",
  "osdu.legal.legaltags",
  "osdu.legal.otherRelevantDataCountries",
];

/** A dataset column or child dataset name, as the mapping format takes it. */
export const COLUMN_NAME = /^[A-Za-z0-9_-]+$/;

/** A column as an entry reads it: `column`, or `child.column` inside a repeater. */
export const DATASET_COLUMN = /^[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)?$/;

/** A key under an object with free keys (a tag name): anything but dots, brackets and whitespace. */
export const KEY_NAME = /^[^.[\]\s]+$/;

export const MODIFIER_KINDS: readonly MappingDraftModifierKind[] = ["trim", "upper", "lower", "split", "replace", "equals", "date", "number"];

/** The separators a number modifier reads before the decimals. */
export const DECIMAL_SEPARATORS: readonly { value: string; label: string }[] = [
  { value: ".", label: "point ." },
  { value: ",", label: "comma ," },
];

/** The group separator choice that stands for none; a select item cannot carry an empty value. */
export const NO_GROUP = "none";

/** The separators a number modifier reads between groups of three digits; a space also stands for a no-break or thin space. */
export const GROUP_SEPARATORS: readonly { value: string; label: string }[] = [
  { value: NO_GROUP, label: "none" },
  { value: ",", label: "comma ," },
  { value: ".", label: "point ." },
  { value: " ", label: "space" },
  { value: "'", label: "apostrophe '" },
];

/** An entry with no settings beyond its target and input. */
export function emptyEntry(target: string, input: MappingDraftInput): MappingDraftEntry {
  return {
    target,
    input,
    column: null,
    child: null,
    cacheType: null,
    cacheField: null,
    findBy: [],
    modifiers: [],
    appliesWhen: null,
    required: true,
    ignoreSeparators: false,
    static: null,
    description: null,
    prefilled: false,
  };
}

/** A new modifier of a kind, with the settings that kind asks for left for the person to give. */
export function newModifier(kind: MappingDraftModifierKind): MappingDraftModifier {
  switch (kind) {
    case "split":
      return { kind, separator: "", part: 1, replacements: null, text: null, decimalSeparator: null, groupSeparator: null };
    case "replace":
      return { kind, separator: null, part: null, replacements: [{ from: "", to: "" }], text: null, decimalSeparator: null, groupSeparator: null };
    case "equals":
      return { kind, separator: null, part: null, replacements: null, text: "", decimalSeparator: null, groupSeparator: null };
    case "number":
      return { kind, separator: null, part: null, replacements: null, text: null, decimalSeparator: ".", groupSeparator: null };
    case "trim":
    case "upper":
    case "lower":
    case "date":
      return { kind, separator: null, part: null, replacements: null, text: null, decimalSeparator: null, groupSeparator: null };
  }
}

/**
 * The inputs a variable takes, as the preflight allows them: a value or a list of values from a dataset column, the cache
 * or a static value; a list of objects from a repeater or a static list; an object only from a static value. The four
 * envelope variables are static lists.
 */
export function inputsFor(variable: Pick<DeliveryTemplateVariable, "shape" | "path">): MappingDraftInput[] {
  if (ENVELOPE_TARGETS.includes(variable.path)) {
    return ["Static"];
  }

  switch (variable.shape) {
    case "Value":
    case "ValueList":
      return ["Dataset", "Cache", "Static"];
    case "GroupList":
      return ["Repeat", "Static"];
    case "Group":
    case "Whole":
      return ["Static"];
  }
}

/** The list of objects a target inside a repeated item belongs to: osdu.data.Curves for osdu.data.Curves[].CurveID. */
export function repeaterOf(target: string): string | null {
  const at = target.indexOf("[]");
  return at < 0 ? null : target.slice(0, at);
}

/**
 * Where a target sorts in the template: its variable's position in schema order. A key under an object with free keys
 * sorts right after that object, and a target the template does not have sorts last.
 */
export function targetOrder(target: string, order: ReadonlyMap<string, number>): number {
  const own = order.get(target);
  if (own !== undefined) {
    return own;
  }

  const at = target.lastIndexOf(".");
  const holder = at < 0 ? undefined : order.get(target.slice(0, at));
  return holder !== undefined ? holder + 0.5 : Number.MAX_SAFE_INTEGER;
}

/**
 * The entries with one entry put in its place: replaced where it stands when its target already has an entry, and
 * otherwise inserted after the last entry that sorts before it (or with it) in template order.
 */
export function putEntry(
  entries: readonly MappingDraftEntry[], entry: MappingDraftEntry, order: ReadonlyMap<string, number>,
): MappingDraftEntry[] {
  const existing = entries.findIndex((candidate) => candidate.target === entry.target);
  if (existing >= 0) {
    return entries.map((candidate, index) => (index === existing ? entry : candidate));
  }

  const position = targetOrder(entry.target, order);
  let after = -1;
  entries.forEach((candidate, index) => {
    if (targetOrder(candidate.target, order) <= position) {
      after = index;
    }
  });

  return [...entries.slice(0, after + 1), entry, ...entries.slice(after + 1)];
}

/** The entry's input in one short line, as the YAML writes it. */
export function entrySummary(entry: MappingDraftEntry): string {
  switch (entry.input) {
    case "Dataset":
      return `dataset.${entry.column ?? ""}`;
    case "Repeat":
      return `rows of dataset.${entry.child ?? ""}`;
    case "Cache":
      return `cache.${entry.cacheType ?? ""}.${entry.cacheField ?? ""}`;
    case "Static":
      return `static ${entry.static ?? ""}`;
  }
}

/** Text a modifier or a findBy line quotes, so a separator that is a space or a comma is visible. */
function quoted(text: string): string {
  return text.includes("'") ? `"${text}"` : `'${text}'`;
}

/** One modifier in one short line, with the settings its kind carries: `split on ',', part 1`, `replace M to m`. */
export function modifierText(modifier: MappingDraftModifier): string {
  switch (modifier.kind) {
    case "split":
      return `split on ${quoted(modifier.separator ?? "")}, part ${modifier.part ?? 0}`;
    case "replace":
      return `replace ${(modifier.replacements ?? []).map((pair) => `${pair.from} to ${pair.to}`).join(", ")}`;
    case "equals":
      return `equals ${modifier.text ?? ""}`;
    case "date":
      return modifier.text === null || modifier.text === "" ? "date" : `date ${modifier.text}`;
    case "number": {
      const group = modifier.groupSeparator === null ? "" : `, group ${quoted(modifier.groupSeparator)}`;
      return `number, decimal ${quoted(modifier.decimalSeparator ?? ".")}${group}`;
    }
    case "trim":
    case "upper":
    case "lower":
      return modifier.kind;
  }
}

/**
 * The lookup an entry reads a cached record by, one line per findBy, as the YAML writes them:
 * `cache.UnitOfMeasure.Code = dataset.elev_meas_ref`. Empty for an entry that reads no cache.
 */
export function lookupLines(entry: MappingDraftEntry): string[] {
  if (entry.input !== "Cache") {
    return [];
  }

  return entry.findBy.map((find) => {
    const operand = find.literal !== null && find.literal.trim() !== "" ? quoted(find.literal) : `dataset.${find.column ?? ""}`;
    return `cache.${entry.cacheType ?? ""}.${find.field} = ${operand}`;
  });
}

/** An appliesWhen in one line, as the condition syntax reads it: `dataset.depth_coding is not empty`. */
export function conditionText(condition: MappingDraftCondition): string {
  const column = `dataset.${condition.column}`;
  switch (condition.operator) {
    case "isEmpty":
      return `${column} is empty`;
    case "isNotEmpty":
      return `${column} is not empty`;
    case "isNot":
      return `${column} is not ${condition.text ?? ""}`;
    case "is":
      return `${column} is ${condition.text ?? ""}`;
  }
}

/** The dataset columns and child datasets a draft already names: its key, label, entries, findBy lines, conditions and fixtures. */
export function knownColumns(draft: MappingDraft): { columns: string[]; children: string[] } {
  const columns = new Set<string>(draft.key);
  const children = new Set<string>();
  for (const entry of draft.entries) {
    if (entry.column !== null && entry.column !== "") {
      columns.add(entry.column);
    }

    if (entry.child !== null && entry.child !== "") {
      children.add(entry.child);
    }

    for (const find of entry.findBy) {
      if (find.column !== null && find.column !== "") {
        columns.add(find.column);
      }
    }

    if (entry.appliesWhen !== null && entry.appliesWhen.column !== "") {
      columns.add(entry.appliesWhen.column);
    }
  }

  for (const fixture of draft.fixtures) {
    Object.keys(fixture.record).forEach((column) => columns.add(column));
    for (const [child, rows] of Object.entries(fixture.datasets)) {
      children.add(child);
      rows.forEach((row) => Object.keys(row).forEach((column) => columns.add(`${child}.${column}`)));
    }
  }

  for (const match of (draft.label ?? "").matchAll(/\{dataset\.([A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)?)\}/g)) {
    columns.add(match[1]);
  }

  return { columns: [...columns].sort(), children: [...children].sort() };
}

/** How the entry editor offers a static value: a list of strings, one text, number or boolean, or JSON. */
export type StaticMode = "list" | "text" | "number" | "boolean" | "json";

/** JSON text parsed, or why it does not parse. Empty text is no value. */
export function parseJson(text: string | null): { ok: true; value: unknown } | { ok: false; error: string } {
  if (text === null || text.trim() === "") {
    return { ok: true, value: undefined };
  }

  try {
    return { ok: true, value: JSON.parse(text) as unknown };
  } catch (error) {
    return { ok: false, error: error instanceof Error ? error.message : String(error) };
  }
}

/**
 * The editor for a variable's static value: a list editor for a list of values, a typed field for one value, and JSON
 * for objects, lists of objects, and any value already written in a form those editors cannot show.
 */
export function staticModeFor(variable: Pick<DeliveryTemplateVariable, "shape" | "type">, json: string | null): StaticMode {
  const parsed = parseJson(json);
  if (!parsed.ok) {
    return "json";
  }

  const value = parsed.value;
  if (variable.shape === "ValueList") {
    return value === undefined || (Array.isArray(value) && value.every((item) => item === null || typeof item !== "object"))
      ? "list"
      : "json";
  }

  if (variable.shape !== "Value") {
    return "json";
  }

  switch (variable.type) {
    case "boolean":
      return value === undefined || typeof value === "boolean" ? "boolean" : "json";
    case "number":
    case "integer":
      return value === undefined || typeof value === "number" ? "number" : "json";
    default:
      return value === undefined || typeof value === "string" ? "text" : "json";
  }
}
