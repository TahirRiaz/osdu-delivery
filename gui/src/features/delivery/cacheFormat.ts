import type { DeliveryCache, DeliveryCacheField, DeliveryCacheSchedule } from "../../api/delivery";

/** A cached value on one line: a scalar as itself, a set as its values, an object as its JSON. */
export function cachedText(value: unknown): string {
  if (value === null || value === undefined) {
    return "-";
  }

  if (Array.isArray(value)) {
    return value.map((v) => cachedText(v)).join(", ");
  }

  return typeof value === "object" ? JSON.stringify(value) : String(value);
}

/** A cached value for a table cell: null where the record holds nothing under the name, so the cell shows its placeholder. */
export function cachedCell(value: unknown): string | null {
  return value === null || value === undefined ? null : cachedText(value);
}

/** Every captured value of a cached record on one line, name by name. */
export function cachedFieldsText(fields: Record<string, unknown> | null): string {
  return Object.entries(fields ?? {}).map(([name, value]) => `${name}: ${cachedText(value)}`).join("  ·  ") || "-";
}

/**
 * An OSDU record id in two parts: the partition and entity type that every id of a type repeats, and the part that
 * tells the records apart. A column of ids reads by its tails once the repeated prefix steps back.
 */
export function splitRecordId(id: string): { prefix: string; tail: string } {
  const at = id.lastIndexOf(":");
  if (at < 0 || at === id.length - 1) {
    return { prefix: "", tail: id };
  }

  return { prefix: id.slice(0, at + 1), tail: id.slice(at + 1) };
}

/** The family an entity type belongs to, as a group heading: reference-data--UnitOfMeasure is "Reference data". */
export function entityFamily(entityType: string): string {
  const at = entityType.indexOf("--");
  if (at <= 0) {
    return "Other";
  }

  const words = entityType.slice(0, at).replace(/-/g, " ");
  return words.charAt(0).toUpperCase() + words.slice(1);
}

/** Families in the order the type picker lists them: what mappings look up first, then the master data they point at. */
const familyRank: Record<string, number> = { "Reference data": 0, "Master data": 1 };

export function compareFamilies(a: string, b: string): number {
  const rankA = familyRank[a] ?? 2;
  const rankB = familyRank[b] ?? 2;
  return rankA !== rankB ? rankA - rankB : a.localeCompare(b);
}

/** One type of the cache in scope, as the Definition tab and the type picker show it. */
export interface CachedTypeSummary {
  name: string;
  entityType: string;
  family: string;
  kind: string;
  query: string | null;
  /** The records the current version holds of the type. */
  items: number;
  /** The captured paths by the name they are cached under, in declaration order. */
  fields: DeliveryCacheField[];
  /** approve or auto: what a changed value does to the records built from it. */
  onChange: string;
}

/** The types a cache declares, sorted the way the type picker lists them: by family, then by name. */
export function summarizeTypes(cache: DeliveryCache | null): CachedTypeSummary[] {
  return (cache?.types ?? [])
    .map((type) => ({
      name: type.name,
      entityType: type.entityType,
      family: entityFamily(type.entityType),
      kind: type.kind,
      query: type.query,
      items: type.items,
      fields: type.fields,
      onChange: type.onChange,
    }))
    .sort((a, b) => compareFamilies(a.family, b.family) || a.name.localeCompare(b.name));
}

/** A schedule's cadence in words: its cron, its interval, or that it fires behind other schedules. */
export function scheduleCadence(schedule: DeliveryCacheSchedule): string {
  if (schedule.cron !== null && schedule.cron.trim() !== "") {
    return `${schedule.name}: cron ${schedule.cron}`;
  }

  if (schedule.intervalSeconds !== null) {
    return `${schedule.name}: every ${schedule.intervalSeconds}s`;
  }

  return schedule.chained ? `${schedule.name}: after the schedules it follows` : schedule.name;
}
