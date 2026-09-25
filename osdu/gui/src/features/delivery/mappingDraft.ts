// The mapping builder's draft logic that needs no server: which inputs a variable takes, where an entry goes in the draft,
// how a static value is edited, how an entry reads as text, and the column names a draft already knows. The server writes
// the YAML and checks it.

import type {
  DeliveryCachedType, DeliveryTemplateVariable, MappingDraft, MappingDraftEntry, MappingDraftFind, MappingDraftInput,
  MappingDraftModifier, MappingDraftModifierKind,
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

export const MODIFIER_KINDS: readonly MappingDraftModifierKind[] = ["trim", "upper", "lower", "split", "replace", "equals", "date", "number", "id", "ref"];

/** The modifiers that build the id an entry writes, from a dataset value: always the last, and at most one of them. */
export const ID_MODIFIER_KINDS: readonly MappingDraftModifierKind[] = ["id", "ref"];

/** What a new id modifier starts from: a reference to a unit of measure whose code is the value. */
export const ID_TEMPLATE_EXAMPLE = "{$param.dataPartition}:reference-data--UnitOfMeasure:{$value}:";

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
    expression: null,
    when: null,
    where: null,
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
      return {
        kind, separator: null, part: null, replacements: [{ from: "", to: "" }], text: null, decimalSeparator: null, groupSeparator: null,
        otherwiseKind: "keep", otherwiseText: null,
      };
    case "equals":
      return { kind, separator: null, part: null, replacements: null, text: "", decimalSeparator: null, groupSeparator: null };
    case "id":
      return { kind, separator: null, part: null, replacements: null, text: ID_TEMPLATE_EXAMPLE, decimalSeparator: null, groupSeparator: null };
    case "number":
      return { kind, separator: null, part: null, replacements: null, text: null, decimalSeparator: ".", groupSeparator: null };
    case "trim":
    case "upper":
    case "lower":
    case "date":
    case "ref":
      return { kind, separator: null, part: null, replacements: null, text: null, decimalSeparator: null, groupSeparator: null };
  }
}

/**
 * The inputs a variable takes, as the preflight allows them: a value or a list of values from a dataset column, the cache,
 * a search of the platform or a static value; a list of objects from a repeater or a static list; an object only from a
 * static value. The four envelope variables are static lists.
 */
export function inputsFor(variable: Pick<DeliveryTemplateVariable, "shape" | "path">): MappingDraftInput[] {
  if (ENVELOPE_TARGETS.includes(variable.path)) {
    return ["Static"];
  }

  switch (variable.shape) {
    case "Value":
    case "ValueList":
      return ["Dataset", "Expression", "Cache", "Search", "Static"];
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

/** The entry's input in one short line: the column, child rows, cached field or search it reads, or its fixed value. */
export function entrySummary(entry: MappingDraftEntry): string {
  switch (entry.input) {
    case "Dataset":
      return `dataset.${entry.column ?? ""}`;
    case "Repeat":
      return `rows of dataset.${entry.child ?? ""}`;
    case "Cache":
      return `cache.${entry.cacheType ?? ""}.${entry.cacheField ?? ""}`;
    case "Search":
      return `search.${entry.cacheType ?? ""}.${entry.cacheField ?? "id"}`;
    case "Expression":
      return entry.expression ?? "";
    case "Static":
      return `static ${entry.static ?? ""}`;
  }
}

/** Whether an entry finds a record by findBy lines: out of the cache, or by searching the platform. */
export function looksUp(entry: Pick<MappingDraftEntry, "input">): boolean {
  return entry.input === "Cache" || entry.input === "Search";
}

/**
 * An entry on one line, without the target it fills: where the value comes from, the lookup that finds it, what is done
 * to it, and when it applies. `dataset.facility_name | trim`, `search.Wellbore.id by data.FacilityName = dataset.wellbore_uwi`.
 */
export function entryText(entry: MappingDraftEntry): string {
  const lookup = lookupText(entry);
  return [
    entrySummary(entry),
    lookup === "" ? "" : `by ${lookup}`,
    entry.modifiers.length === 0 ? "" : `| ${entry.modifiers.map(modifierText).join(" | ")}`,
    entry.when === null ? "" : `when ${entry.when}`,
  ].filter((part) => part !== "").join(" ");
}

/** One property the mapping fills: where the value comes from, what is done to it, and the target it is written to. */
export interface PropertyRow {
  target: string;
  /** Where the value comes from, which decides what the rest of the entry means. */
  input: MappingDraftInput;
  /** The value's origin: `dataset.log_source`, `cache.UnitOfMeasure.id`, `search.Wellbore.id`, `rows of dataset.curves`, `static "MD"`. */
  source: string;
  /** The origin without the word that names its kind, for a view that says the kind itself. */
  sourceValue: string;
  /** The record's lookup as one phrase, `Code/Name = dataset.elev_meas_ref`; empty when no record is looked up. */
  lookup: string;
  /** One line per findBy, naming the record set and the field compared, for the property's own view. */
  lookupDetail: string[];
  /** The modifiers in order, one line each; empty for a value taken as it stands. */
  modifiers: string[];
  /** The modifier kinds in order, which is what a row has room for: `split, replace`. */
  modifierKinds: string;
  /** The entry's condition (`$when`), or null when it always applies. */
  condition: string | null;
  description: string | null;
  required: boolean;
  /** The whole line as text, with every modifier spelled out, which a hover panel shows and a filter matches. */
  detail: string;
  /** Everything the row holds, lowercased, which a filter matches against. */
  search: string;
}

/** One entry read as a row: every part of it worded once, so every view of a mapping says the same thing. */
export function propertyRow(entry: MappingDraftEntry): PropertyRow {
  const source = entrySummary(entry);
  const lookup = lookupText(entry);
  const modifiers = entry.modifiers.map(modifierText);
  const condition = entry.when;
  // The modifiers change the value a lookup compares, not the cached field, so they read after that value.
  const detail = [
    source,
    lookup === "" ? "" : `by ${lookup}`,
    modifiers.length === 0 ? "" : `| ${modifiers.join(" | ")}`,
    `-> ${entry.target}`,
    condition === null ? "" : `when ${condition}`,
  ].filter((part) => part !== "").join(" ");
  return {
    target: entry.target,
    input: entry.input,
    source,
    sourceValue: entry.input === "Repeat"
      ? `dataset.${entry.child ?? ""}${entry.where === null ? "" : ` where ${entry.where}`}`
      : entry.input === "Static" ? entry.static ?? "" : source,
    lookup,
    lookupDetail: lookupLines(entry),
    modifiers,
    modifierKinds: entry.modifiers.map((modifier) => (isCachedReplace(modifier) ? "replace from cache" : modifier.kind)).join(", "),
    condition,
    description: entry.description,
    required: entry.required,
    detail,
    search: [detail, entry.description ?? ""].join("\n").toLowerCase(),
  };
}

/** Text a modifier or a findBy line quotes, so a separator that is a space or a comma is visible. */
function quoted(text: string): string {
  return text.includes("'") ? `"${text}"` : `'${text}'`;
}

/** True for a replace that reads its table from the cache rather than listing its pairs. */
export function isCachedReplace(modifier: MappingDraftModifier): boolean {
  return modifier.kind === "replace" && (modifier.table ?? "").trim() !== "";
}

/**
 * The fields a replace reading a cached table matches on and replaces by, as a render settles them: those it names, or,
 * for a lookup table, its key and the one field it holds beside its key (`value` for a dictionary of pairs). Null where the
 * table cannot settle one: a type of OSDU records has no key and many fields, and a lookup table with several fields
 * beside its key does not say which replaces. `settled` says the field came from the table, not the replace.
 */
export function cachedReplaceFields(
  modifier: MappingDraftModifier,
  type: DeliveryCachedType | undefined,
): { match: string | null; field: string | null; matchSettled: boolean; fieldSettled: boolean } {
  const named = (text: string | null | undefined) => (text ?? "").trim() === "" ? null : (text ?? "").trim();
  const match = named(modifier.match);
  const field = named(modifier.field);
  const key = type?.key ?? null;
  const beside = type === undefined || key === null ? [] : type.fields.filter((name) => name.toLowerCase() !== key.toLowerCase());
  const fieldDefault = key === null ? null : beside.length === 1 ? beside[0] : beside.length === 0 ? "value" : null;
  return {
    match: match ?? key,
    field: field ?? fieldDefault,
    matchSettled: match === null && key !== null,
    fieldSettled: field === null && fieldDefault !== null,
  };
}

/** One modifier in one short line, with the settings its kind carries: `split on ',', part 1`, `replace M to m`. */
export function modifierText(modifier: MappingDraftModifier): string {
  switch (modifier.kind) {
    case "split":
      return `split on ${quoted(modifier.separator ?? "")}, part ${modifier.part ?? 0}`;
    case "replace": {
      if (isCachedReplace(modifier)) {
        const match = (modifier.match ?? "").trim();
        const field = (modifier.field ?? "").trim();
        const fields = match === "" && field === "" ? "" : ` (${match === "" ? "its key" : match} to ${field === "" ? "its value" : field})`;
        return `replace from $cache.${(modifier.table ?? "").trim()}${fields}${otherwiseText(modifier)}`;
      }

      const pairs = (modifier.replacements ?? []).map((pair) => `${pair.from} to ${pair.to === null ? "no value" : pair.to}`).join(", ");
      return `replace ${pairs}${otherwiseText(modifier)}`;
    }
    case "equals":
      return `equals ${modifier.text ?? ""}`;
    case "date":
      return modifier.text === null || modifier.text === "" ? "date" : `date ${modifier.text}`;
    case "number": {
      const group = modifier.groupSeparator === null ? "" : `, group ${quoted(modifier.groupSeparator)}`;
      return `number, decimal ${quoted(modifier.decimalSeparator ?? ".")}${group}`;
    }
    case "id":
      return `id ${modifier.text ?? ""}`;
    case "ref":
      return modifier.text === null || modifier.text.trim() === "" ? "ref" : `ref ${modifier.text.trim()}`;
    case "trim":
    case "upper":
    case "lower":
      return modifier.kind;
  }
}

/** What a replace says about the values its pairs do not list, when it says anything: `, otherwise no value`. */
function otherwiseText(modifier: MappingDraftModifier): string {
  switch (modifier.otherwiseKind) {
    case "empty":
      return ", otherwise no value";
    case "text":
      return `, otherwise ${modifier.otherwiseText ?? ""}`;
    default:
      return "";
  }
}

/** The value one findBy compares: a dataset column, or a fixed text. */
function operandOf(find: MappingDraftFind): string {
  return find.literal !== null && find.literal.trim() !== "" ? quoted(find.literal) : `dataset.${find.column ?? ""}`;
}

/**
 * The lookup an entry finds its record by, one line per findBy, each naming the record set, the field compared and the
 * value it must equal: `cache.UnitOfMeasure.Code = dataset.elev_meas_ref`,
 * `search.Wellbore.data.FacilityName = dataset.wellbore_uwi`. Empty for an entry that looks nothing up.
 */
export function lookupLines(entry: MappingDraftEntry): string[] {
  if (!looksUp(entry)) {
    return [];
  }

  const prefix = entry.input === "Search" ? "search" : "cache";
  return entry.findBy.map((find) => `${prefix}.${entry.cacheType ?? ""}.${find.field} = ${operandOf(find)}`);
}

/**
 * The lookup as one phrase, the way the renderer words it: the fields that compare the same value run together, and
 * the phrases after the first read as alternatives. `Code/Name/id = dataset.elev_meas_ref`, and empty for an entry
 * that looks nothing up. The cached type or the search is left out: the entry's source already names it.
 */
export function lookupText(entry: MappingDraftEntry): string {
  if (!looksUp(entry)) {
    return "";
  }

  const groups: { fields: string[]; operand: string }[] = [];
  for (const find of entry.findBy) {
    const operand = operandOf(find);
    const last = groups.at(-1);
    if (last !== undefined && last.operand === operand) {
      last.fields.push(find.field);
    } else {
      groups.push({ fields: [find.field], operand });
    }
  }

  return groups.map((group) => `${group.fields.join("/")} = ${group.operand}`).join(" or ");
}

/** The dataset columns and child datasets a draft already names: its key, label, entries, findBy lines and fixtures. */
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
