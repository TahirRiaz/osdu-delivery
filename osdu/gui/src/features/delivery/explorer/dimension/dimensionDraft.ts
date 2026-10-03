import type { DimensionDraft, DimensionDraftAttribute, DimensionDraftCleanStep, DimensionSampleRecord } from "../../../../api/explorer";
import { isRecordReference, withoutVersion } from "../../osduDocument";

// What the dimension builder holds while a person picks a dimension from the records OSDU holds (osdu/docs/explorer.md,
// Building a dimension): the draft, kept in the page's address; where a pick in a record is (a location) and the path it
// becomes; the trails the builder reads; and the filters it suggests where a pick reaches more than one value. Every check
// of what a dimension may hold is the control plane's, which reads the draft back through the loader a flow is read by.

/** The most records a label or an attribute reads through, one path each: DimensionSpec.MaxLabelSteps. */
export const MAX_STEPS = 3;

/** The most attributes a dimension reads: DimensionSpec.MaxAttributes. */
export const MAX_ATTRIBUTES = 20;

/** The examples a page steps through: the commonest keys, or the first records of the kind. */
export const EXAMPLES = 25;

/** The longest attribute name: DimensionAttributeSpec.MaxNameLength. */
export const MAX_NAME_LENGTH = 64;

/** The clean steps written as their name alone, as the documentation names them. */
export const PLAIN_STEPS = ["trim", "collapseSpaces", "upper", "lower", "nfc", "nfkc", "foldSeparators"] as const;

/** Names an attribute cannot take: the columns every dimension's table has, and the words a key's own facts go by. */
const RESERVED = new Set(["value", "keys", "key", "key_id", "records", "filter", "label", "id", "partition"]);

/** A draft that has picked nothing but the kind it reads. */
export function emptyDraft(kind: string): DimensionDraft {
  return {
    name: "",
    description: null,
    kind,
    query: null,
    path: null,
    label: [],
    unlabelled: null,
    attributes: [],
    clean: [],
    keyColumn: null,
    valueColumn: null,
    countRecords: false,
    maxValues: null,
  };
}

const text = (value: unknown): string | null => (typeof value === "string" ? value : null);
const texts = (value: unknown): string[] => (Array.isArray(value) ? value.filter((v): v is string => typeof v === "string") : []);

/**
 * The draft an address holds, read defensively: an address is typed, pasted and kept, so whatever it holds that is not a
 * draft's part is passed over rather than trusted. An address holding no draft starts one for `kind`.
 */
export function draftFromParam(param: string | null, kind: string | null): DimensionDraft {
  const fresh = emptyDraft(kind ?? "");
  if (param === null || param === "") {
    return fresh;
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(param);
  } catch {
    return fresh;
  }

  if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
    return fresh;
  }

  const held = parsed as Record<string, unknown>;
  const attributes = Array.isArray(held.attributes)
    ? held.attributes
      .filter((a): a is Record<string, unknown> => a !== null && typeof a === "object" && !Array.isArray(a))
      .map((a): DimensionDraftAttribute => ({
        name: text(a.name) ?? "",
        steps: Array.isArray(a.steps) ? texts(a.steps) : null,
        collect: text(a.collect),
      }))
      .filter((a) => (a.steps !== null && a.steps.length > 0) || a.collect !== null)
    : [];
  const clean = Array.isArray(held.clean)
    ? held.clean
      .filter((c): c is Record<string, unknown> => c !== null && typeof c === "object" && typeof (c as Record<string, unknown>).step === "string")
      .map((c): DimensionDraftCleanStep => ({ step: c.step as string, pattern: text(c.pattern), with: text(c.with) }))
    : [];
  const maxValues = typeof held.maxValues === "number" && Number.isSafeInteger(held.maxValues) ? held.maxValues : null;
  return {
    name: text(held.name) ?? "",
    description: text(held.description),
    kind: text(held.kind) ?? fresh.kind,
    query: text(held.query),
    path: text(held.path),
    label: texts(held.label),
    unlabelled: text(held.unlabelled),
    attributes,
    clean,
    keyColumn: text(held.keyColumn),
    valueColumn: text(held.valueColumn),
    countRecords: held.countRecords === true,
    maxValues,
  };
}

/** The draft as an address holds it: the parts it has, and nothing for the parts it leaves empty. */
export function draftParam(draft: DimensionDraft): string {
  const kept: Record<string, unknown> = {};
  for (const [name, value] of Object.entries(draft)) {
    if (value === null || value === "" || value === false || (Array.isArray(value) && value.length === 0)) {
      continue;
    }

    kept[name] = value;
  }

  return JSON.stringify(kept);
}

/** Where a pick is in a record: property names and array places, from the record's root. */
export type Location = (string | number)[];

/** How a filter compares a property of an object with its text: equal, containing it, or ending with it. */
export type FilterCompare = "=" | "*=" | "$=";

/** A filter on one segment of a path: of the objects the segment holds, those whose property compares with the text. */
export interface SegmentFilter {
  property: string;
  compare: FilterCompare;
  value: string;
}

/** The property names of a location, its array places left out, as a path names them. */
export function namesOf(location: Location): string[] {
  return location.filter((part): part is string => typeof part === "string");
}

/**
 * The path a location is read by: its property names joined by dots, an array met on the way stepped into, and a filter on
 * the segment it names (by its place among the names), written `[Property*=text]` after it.
 */
export function pathOf(location: Location, filters: ReadonlyMap<number, SegmentFilter> = new Map()): string {
  return namesOf(location)
    .map((name, index) => {
      const filter = filters.get(index);
      return filter === undefined ? name : `${name}[${filter.property}${filter.compare}${filter.value.trim()}]`;
    })
    .join(".");
}

/** A path without its filters: the property names it reads through. */
export function plainPath(path: string): string {
  return path.replace(/\[[^\]]*\]/g, "");
}

/** Why a filter cannot be written: a property that is no name, or a text that is empty or holds the `]` that would end it. */
export function filterProblem(filter: SegmentFilter): string | null {
  if (!/^[A-Za-z0-9_]+$/.test(filter.property)) {
    return "The property compared is a name of letters, digits and underscores.";
  }

  const value = filter.value.trim();
  if (value === "") {
    return "Give the text the property is compared with.";
  }

  return value.includes("]") ? "The text cannot hold ], which ends a filter." : null;
}

/** The property a path ends with, its filters set aside. */
export function lastProperty(path: string): string {
  const names = plainPath(path).split(".");
  return names[names.length - 1] ?? path;
}

/** Whether two names are the same name, as the loader compares them: ignoring case. */
const sameName = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

/**
 * A name for an attribute read at `path`: the property it ends with, made a name an attribute may take (a letter, then
 * letters, digits and underscores, at most 64), never a reserved word and never one of `taken` (compared ignoring case),
 * numbered when it would be.
 */
export function attributeNameFor(path: string, taken: readonly string[]): string {
  let base = lastProperty(path).replace(/[^A-Za-z0-9_]/g, "_");
  if (!/^[A-Za-z]/.test(base)) {
    base = `A${base}`;
  }

  base = base.slice(0, MAX_NAME_LENGTH);
  const free = (name: string) => !RESERVED.has(name.toLowerCase()) && !taken.some((t) => sameName(t, name));
  if (free(base)) {
    return base;
  }

  for (let n = 2; ; n++) {
    const numbered = `${base.slice(0, MAX_NAME_LENGTH - String(n).length)}${n}`;
    if (free(numbered)) {
      return numbered;
    }
  }
}

/**
 * A name for the dimension: the type of record its key names (a wellbore's id makes a `Wellbore` dimension), or the property
 * its key is read at, with whatever a dimension's name may not hold left out.
 */
export function dimensionNameFor(path: string, example: string | null): string {
  const named = example !== null && isRecordReference(example) ? entityTypeOf(example) : null;
  const base = named === null ? lastProperty(path) : (named.split("--")[1] ?? named);
  const cleaned = base.replace(/[^A-Za-z0-9._-]/g, "").replace(/^[^A-Za-z0-9]+/, "");
  return cleaned.slice(0, 100);
}

/** The entity type a record id names: its second segment (`master-data--Wellbore`). */
export function entityTypeOf(id: string): string {
  return withoutVersion(id).split(":")[1] ?? id;
}

/** The type of an entity type, its group aside: `Wellbore` of `master-data--Wellbore`. */
export function typeName(entityType: string): string {
  const at = entityType.indexOf("--");
  return at < 0 ? entityType : entityType.slice(at + 2);
}

/** One chain of a draft: its value (the label) or an attribute, read through `steps` from the record a key names. */
export interface DraftChain {
  id: string;
  role: "label" | "attribute";
  name: string | null;
  steps: string[];
}

/** The chains a draft reads through the records its keys name: the label first, then each attribute that is read, not collected. */
export function chainsOf(draft: DimensionDraft): DraftChain[] {
  const chains: DraftChain[] = [];
  if (draft.label.length > 0) {
    chains.push({ id: "label", role: "label", name: null, steps: draft.label });
  }

  draft.attributes.forEach((attribute, index) => {
    if (attribute.collect === null && attribute.steps !== null && attribute.steps.length > 0) {
      chains.push({ id: `attribute:${index}`, role: "attribute", name: attribute.name, steps: attribute.steps });
    }
  });
  return chains;
}

/** A trail as a key of a map: its steps, in order. */
export const trailKey = (trail: readonly string[]) => JSON.stringify(trail);

/**
 * The trails the builder reads from the record the example key names: that record itself, every part of every chain a
 * further step is read from (a chain of three paths reads its second in the records the first names, and its third in
 * those the second names), and the trails a person opened to look at before reading anything there. Each once, shortest
 * first, at most `max`.
 */
export function trailsOf(draft: DimensionDraft, opened: readonly string[][], max = 16): string[][] {
  const trails: string[][] = [[]];
  const seen = new Set([trailKey([])]);
  const add = (trail: string[]) => {
    const key = trailKey(trail);
    if (!seen.has(key) && trail.length < MAX_STEPS) {
      seen.add(key);
      trails.push(trail);
    }
  };

  for (const chain of chainsOf(draft)) {
    for (let length = 1; length < chain.steps.length; length++) {
      add(chain.steps.slice(0, length));
    }
  }

  opened.forEach(add);
  return trails.sort((a, b) => a.length - b.length).slice(0, max);
}

/** Whether `trail` begins with `prefix`, step for step. */
export function startsWith(trail: readonly string[], prefix: readonly string[]): boolean {
  return prefix.length <= trail.length && prefix.every((step, index) => trail[index] === step);
}

/** A use of a property in the records a trail reaches: the chain reading it there, and whether that is its last step. */
export interface PropertyUse {
  chain: DraftChain;
  last: boolean;
  step: string;
}

/** What reads the property at `path` (its filters aside) in the records `trail` reaches. */
export function usesAt(draft: DimensionDraft, trail: readonly string[], path: string): PropertyUse[] {
  const uses: PropertyUse[] = [];
  for (const chain of chainsOf(draft)) {
    if (chain.steps.length > trail.length && startsWith(chain.steps, trail) && plainPath(chain.steps[trail.length]) === path) {
      uses.push({ chain, last: chain.steps.length === trail.length + 1, step: chain.steps[trail.length] });
    }
  }

  return uses;
}

/** A property a filter can compare, with the values the objects hold there. */
export interface FilterCandidate {
  property: string;
  values: string[];
}

/** The text a value is compared as: a text, a number or a boolean as written; nothing for anything else. */
function comparable(value: unknown): string | null {
  if (typeof value === "string") {
    return value;
  }

  return typeof value === "number" || typeof value === "boolean" ? String(value) : null;
}

/** The texts an object holds at `property`: its value, or each value of a list of them. */
function heldAt(object: Record<string, unknown>, property: string): string[] {
  const value = object[property];
  const values = Array.isArray(value) ? value : [value];
  return values.map(comparable).filter((v): v is string => v !== null && v.trim() !== "");
}

/**
 * The filter a pick of `chosen` among `objects` is read by, when several objects answer: the properties the chosen object
 * holds a value of (its own `leaf` aside), those that tell the objects apart first, and a type, a kind or a role before
 * anything else, since that is what a person picks one object of a list by; with the filter keeping the chosen object
 * suggested. A record id is compared by containing its type and code (`GeoPoliticalEntityType:Country:`), so the filter
 * reads alike in every partition and in every group the type is kept in; any other text equal, exactly.
 */
export function suggestFilter(objects: readonly Record<string, unknown>[], chosen: number, leaf: string | null): {
  candidates: FilterCandidate[];
  suggested: SegmentFilter | null;
} {
  const picked = objects[chosen];
  if (picked === undefined) {
    return { candidates: [], suggested: null };
  }

  const candidates: (FilterCandidate & { telling: boolean; typed: boolean })[] = [];
  for (const property of Object.keys(picked)) {
    if (property === leaf || !/^[A-Za-z0-9_]+$/.test(property) || heldAt(picked, property).length === 0) {
      continue;
    }

    const values = [...new Set(objects.flatMap((object) => heldAt(object, property)))];
    const mine = new Set(heldAt(picked, property));
    const telling = objects.some((object, index) => index !== chosen && !heldAt(object, property).some((v) => mine.has(v)));
    candidates.push({ property, values, telling, typed: /type|kind|role|category|class/i.test(property) });
  }

  candidates.sort((a, b) => Number(b.typed) - Number(a.typed) || Number(b.telling) - Number(a.telling) || a.property.localeCompare(b.property));
  const best = candidates.find((candidate) => candidate.telling) ?? null;
  return {
    candidates: candidates.map(({ property, values }) => ({ property, values })),
    suggested: best === null ? null : filterFor(best.property, heldAt(picked, best.property)[0]),
  };
}

/** The filter keeping the objects holding `value` at `property`, written as `suggestFilter` says; null when no filter can be written of it. */
export function filterFor(property: string, value: string): SegmentFilter | null {
  const held: unknown = value;
  if (isRecordReference(held)) {
    // The type without its group and the code, as the documentation writes it: GeoPoliticalEntityType:Country:.
    const [, entityType = "", ...code] = withoutVersion(value.trim()).split(":");
    const filter: SegmentFilter = { property, compare: "*=", value: `${typeName(entityType)}:${code.join(":")}:` };
    return filterProblem(filter) === null ? filter : null;
  }

  const filter: SegmentFilter = { property, compare: "=", value: value.trim() };
  return filterProblem(filter) === null ? filter : null;
}

/** Whether `object` is one a filter keeps, as a build compares: equal exactly, or containing or ending with the text ignoring case. */
export function keeps(object: Record<string, unknown>, filter: SegmentFilter): boolean {
  const wanted = filter.value.trim();
  return heldAt(object, filter.property).some((value) => {
    switch (filter.compare) {
      case "*=":
        return value.toLowerCase().includes(wanted.toLowerCase());
      case "$=":
        return value.toLowerCase().endsWith(wanted.toLowerCase());
      default:
        return value === wanted;
    }
  });
}

/** The value at `location` in `record`, or undefined where it holds nothing. */
export function valueAt(record: unknown, location: Location): unknown {
  let at: unknown = record;
  for (const part of location) {
    if (at === null || typeof at !== "object") {
      return undefined;
    }

    at = (at as Record<string | number, unknown>)[part];
  }

  return at;
}

/** A list of objects a pick passes through: the segment holding it (its place among the location's names), its objects, the one picked, and where the rest of the location starts. */
export interface ListOnTheWay {
  segment: number;
  objects: Record<string, unknown>[];
  chosen: number;
  /** The location within each object: what follows the list's place. */
  within: Location;
}

/**
 * Where a pick at `location` reaches more than one value, so a build would keep the first it finds: the nearest list of
 * objects on the way, by the place of the segment holding it among the location's names, with the objects it holds and the
 * one picked. Null where the location meets no such list.
 */
export function listOnTheWay(record: unknown, location: Location): ListOnTheWay | null {
  let found: ListOnTheWay | null = null;
  let names = -1;
  for (let i = 0; i < location.length; i++) {
    const part = location[i];
    if (typeof part === "string") {
      names++;
      continue;
    }

    const list = valueAt(record, location.slice(0, i));
    if (Array.isArray(list) && list.length > 1 && list.every((item) => item !== null && typeof item === "object" && !Array.isArray(item))) {
      found = { segment: names, objects: list as Record<string, unknown>[], chosen: part, within: location.slice(i + 1) };
    }
  }

  return found;
}

/** The values each of `records` holds at `location`, its lists stepped into: what a build reads there, record by record. */
export function valuesAcross(records: readonly unknown[], location: Location): string[][] {
  const names = namesOf(location);
  const read = (node: unknown, at: number): string[] => {
    if (Array.isArray(node)) {
      return node.flatMap((item) => read(item, at));
    }

    if (at === names.length) {
      const value = comparable(node);
      return value === null ? [] : [value];
    }

    return node !== null && typeof node === "object" ? read((node as Record<string, unknown>)[names[at]], at + 1) : [];
  };

  return records.map((record) => read(record, 0));
}

/** Whether a value is one a dimension reads: a text, a number or a boolean, or a list of them. */
export function isReadable(value: unknown): boolean {
  const one = (v: unknown) => typeof v === "string" || typeof v === "number" || typeof v === "boolean";
  return one(value) || (Array.isArray(value) && value.length > 0 && value.every(one));
}

/** Whether a value names a record: a record id, or a list of them. */
export function namesRecords(value: unknown): boolean {
  return isRecordReference(value) || (Array.isArray(value) && value.length > 0 && value.every(isRecordReference));
}

/** What a record is called in a list of them: its name where it holds one, else the code its id ends with. */
export function recordName(record: DimensionSampleRecord): string {
  const data = record.record.data;
  if (data !== null && typeof data === "object" && !Array.isArray(data)) {
    const held = data as Record<string, unknown>;
    for (const name of ["Name", "FacilityName", "FieldName", "GeoPoliticalEntityName", "ProjectName", "Code"]) {
      if (typeof held[name] === "string" && held[name] !== "") {
        return held[name] as string;
      }
    }

    const named = Object.entries(held).find(([property, value]) => property.endsWith("Name") && typeof value === "string" && value !== "");
    if (named !== undefined) {
      return named[1] as string;
    }
  }

  const parts = record.id.split(":");
  return parts[parts.length - 1] || parts[parts.length - 2] || record.id;
}

/** What a person can make of a property of a record. */
export type PickAction = "key" | "collect" | "value" | "attribute" | "follow";

/** A pick in a record: what to make of it, the trail of the records it is in (null for the dimension's own), where it is, and the records shown beside it. */
export interface PropertyPick {
  action: PickAction;
  trail: string[] | null;
  location: Location;
  records: DimensionSampleRecord[];
  chosen: number;
}

/** A pick that reaches several values, waiting on how it is read: the objects answering it, the one picked, what each holds there, and the segment a filter goes on. */
export interface AmbiguousPick {
  /** What is being picked, as a person reads it. */
  what: string;
  /** Records reached, or the items of a list on the way. */
  kind: "records" | "items";
  objects: Record<string, unknown>[];
  /** What each object is called, for the list. */
  names: string[];
  /** What each object holds where the pick reads, in order; the first non-empty is what a build keeps without a filter. */
  values: string[][];
  chosen: number;
  /** The property the pick reads in each object, which a filter does not compare. */
  leaf: string | null;
  /** True when every value is followed rather than the first kept: a step naming the next records. */
  follows: boolean;
  /** The segment a filter goes on, by its place among the location's names. */
  segment: number;
}

const isObject = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === "object" && !Array.isArray(value);

/** Whether the objects answer a pick differently: another object holds a value too where a step follows them all, or the values are not one. */
function differs(values: string[][], chosen: number, follows: boolean): boolean {
  if (follows) {
    return values.some((held, index) => index !== chosen && held.length > 0);
  }

  return new Set(values.flat().filter((v) => v.trim() !== "")).size > 1;
}

/** What an item of a list is called: its place, and the type or kind it says it is, where it says one. */
function itemName(object: Record<string, unknown>, index: number): string {
  const typed = Object.entries(object).find(([property, value]) => /type|kind/i.test(property) && typeof value === "string" && value !== "");
  if (typed === undefined) {
    return `item ${index + 1}`;
  }

  const value = typed[1] as string;
  const shown = isRecordReference(value) ? withoutVersion(value).split(":").filter((part) => part !== "").pop() ?? value : value;
  return `item ${index + 1}: ${shown}`;
}

/**
 * Whether a pick reaches several values, so how it is read is a person's to say: several records of the trail answering it
 * (a filter then goes on their data), or a list of objects on the way within the record picked in (a filter goes on that
 * list). A key and a collected path are read whole, so they never are.
 */
export function ambiguityOf(pick: PropertyPick): AmbiguousPick | null {
  if (pick.trail === null || pick.action === "key" || pick.action === "collect") {
    return null;
  }

  const follows = pick.action === "follow";
  const what = pick.action === "value" ? "the value of each key" : pick.action === "attribute" ? "the attribute" : "the records named next";
  const record = pick.records[pick.chosen]?.record;
  if (record === undefined) {
    return null;
  }

  if (pick.records.length > 1 && pick.location[0] === "data") {
    const objects = pick.records.map((r) => (isObject(r.record.data) ? r.record.data : {}));
    const values = valuesAcross(objects, pick.location.slice(1));
    if (differs(values, pick.chosen, follows)) {
      const leaf = pick.location[1];
      return {
        what, kind: "records", objects, names: pick.records.map(recordName), values, chosen: pick.chosen,
        leaf: typeof leaf === "string" ? leaf : null, follows, segment: 0,
      };
    }
  }

  const list = listOnTheWay(record, pick.location);
  if (list !== null) {
    const values = valuesAcross(list.objects, list.within);
    if (differs(values, list.chosen, follows)) {
      const leaf = list.within[0];
      return {
        what, kind: "items", objects: list.objects, names: list.objects.map(itemName), values, chosen: list.chosen,
        leaf: typeof leaf === "string" ? leaf : null, follows, segment: list.segment,
      };
    }
  }

  return null;
}

/**
 * The name an attribute read through `steps` starts from: what its filter keeps (`Country` of a filter on a country's type),
 * the type of the records its last step reads in when it reads through another record (`Field`), else the property it ends
 * with (`FacilityID`).
 */
export function chainNameBase(steps: readonly string[], filter: SegmentFilter | null, readIn: DimensionSampleRecord | undefined): string {
  if (filter !== null && filter.value.includes(":")) {
    const code = filter.value.split(":").filter((part) => part.trim() !== "").pop();
    if (code !== undefined && /^[A-Za-z][A-Za-z0-9_ -]*$/.test(code.trim())) {
      return code.trim().replace(/[ -]/g, "_");
    }
  }

  if (steps.length > 1 && readIn !== undefined) {
    return typeName(entityTypeOf(readIn.id));
  }

  return lastProperty(steps[steps.length - 1] ?? "");
}

/**
 * The draft and the opened trails once `pick` is made, read by `filter` on its segment where it reached several values: a
 * key replaces the key (naming the dimension when it is unnamed, and starting the trails afresh); a collected path replaces
 * the attribute collected; a value replaces the value read; an attribute is added under a name no column has; and a follow
 * opens the records the property names, for a further pick there. A pick the draft already holds changes nothing.
 * `columns` are the names of the key's and the value's columns, which no attribute may take.
 */
export function applyPick(
  draft: DimensionDraft, opened: string[][], pick: PropertyPick, filter: SegmentFilter | null, segment: number | null, columns: readonly string[],
): { draft: DimensionDraft; opened: string[][]; keyChanged: boolean } {
  const filters = new Map<number, SegmentFilter>();
  if (filter !== null && segment !== null) {
    filters.set(segment, filter);
  }

  const plain = pathOf(pick.location);
  const path = pathOf(pick.location, filters);
  const record = pick.records[pick.chosen];
  const unchanged = { draft, opened, keyChanged: false };
  const covered = (steps: string[]) => opened.filter((trail) => !startsWith(steps, trail));
  switch (pick.action) {
    case "key": {
      if (draft.path === plain) {
        return unchanged;
      }

      const held = valueAt(record?.record, pick.location);
      const example = typeof held === "string" ? held : Array.isArray(held) && typeof held[0] === "string" ? held[0] : null;
      return {
        draft: { ...draft, path: plain, name: draft.name.trim() === "" ? dimensionNameFor(plain, example) : draft.name },
        opened: [],
        keyChanged: true,
      };
    }

    case "collect": {
      const others = draft.attributes.filter((a) => a.collect === null);
      if (draft.attributes.some((a) => a.collect === plain)) {
        return unchanged;
      }

      const name = attributeNameFor(plain, [...others.map((a) => a.name), ...columns]);
      return { draft: { ...draft, attributes: [...others, { name, steps: null, collect: plain }] }, opened, keyChanged: false };
    }

    case "value": {
      const steps = [...(pick.trail ?? []), path];
      if (JSON.stringify(steps) === JSON.stringify(draft.label)) {
        return unchanged;
      }

      return { draft: { ...draft, label: steps }, opened: covered(steps), keyChanged: false };
    }

    case "attribute": {
      const steps = [...(pick.trail ?? []), path];
      if (draft.attributes.some((a) => a.collect === null && JSON.stringify(a.steps) === JSON.stringify(steps))) {
        return unchanged;
      }

      const name = attributeNameFor(chainNameBase(steps, filter, record), [...draft.attributes.map((a) => a.name), ...columns]);
      return { draft: { ...draft, attributes: [...draft.attributes, { name, steps, collect: null }] }, opened: covered(steps), keyChanged: false };
    }

    case "follow": {
      const trail = [...(pick.trail ?? []), path];
      if (trail.length >= MAX_STEPS || opened.some((o) => trailKey(o) === trailKey(trail))) {
        return unchanged;
      }

      return { draft, opened: [...opened, trail], keyChanged: false };
    }
  }
}

/** The trails a person opened, as the address holds them: lists of paths, read defensively, at most `MAX_STEPS - 1` paths each. */
export function openedFromParam(param: string | null): string[][] {
  if (param === null || param === "") {
    return [];
  }

  try {
    const parsed: unknown = JSON.parse(param);
    if (!Array.isArray(parsed)) {
      return [];
    }

    return parsed
      .filter((trail): trail is unknown[] => Array.isArray(trail))
      .map(texts)
      .filter((trail) => trail.length > 0 && trail.length < MAX_STEPS)
      .slice(0, 8);
  } catch {
    return [];
  }
}

/** Which example the address names, from 0: a whole number under `EXAMPLES`, else the first. */
export function exampleAt(param: string | null): number {
  const at = Number(param ?? "");
  return Number.isSafeInteger(at) && at >= 0 && at < EXAMPLES ? at : 0;
}

/** Where the builder goes back to: the explorer view it was opened from, or the explorer itself; never anywhere else. */
export function backOf(param: string | null): string {
  return param !== null && /^\/delivery\/explorer(?:[/?]|$)/.test(param) && !param.startsWith("/delivery/explorer/dimension") ? param : "/delivery/explorer";
}

/** A build as the builder holds it: the draft, the trails opened beside it, and which example fills it. */
export interface BuilderBuild {
  draft: DimensionDraft;
  opened: string[][];
  at: number;
}

/** The build an address holds: its draft (`d`, or a fresh one of the kind it names), its trails (`o`) and its example (`ex`). */
export function buildOf(params: URLSearchParams): BuilderBuild {
  return {
    draft: draftFromParam(params.get("d"), params.get("kind")),
    opened: openedFromParam(params.get("o")),
    at: exampleAt(params.get("ex")),
  };
}

/** The address holding `build`: `params` with the build's parts written over, and the rest (the way back) kept as it is. */
export function addressOf(params: URLSearchParams, build: BuilderBuild): URLSearchParams {
  const next = new URLSearchParams(params);
  const draft = draftParam(build.draft);
  if (draft === "{}") {
    next.delete("d");
  } else {
    next.set("d", draft);
  }

  next.delete("kind");
  if (build.opened.length === 0) {
    next.delete("o");
  } else {
    next.set("o", JSON.stringify(build.opened));
  }

  if (build.at === 0) {
    next.delete("ex");
  } else {
    next.set("ex", String(build.at));
  }

  return next;
}
