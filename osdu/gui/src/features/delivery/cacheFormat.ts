import type { DeliveryCache, DeliveryCacheField, DeliveryCacheOrigin, DeliveryCacheSchedule, DeliveryCacheTypeSource } from "../../api/delivery";

/** The entity type prefix of a lookup table: a type filled from an ingestion table or a dictionary, holding no OSDU records. */
export const LOOKUP_ENTITY_PREFIX = "lookup--";

/** Whether a cached type is a lookup table, whose rows are kept under their keys rather than OSDU ids. */
export function isLookupEntityType(entityType: string): boolean {
  return entityType.startsWith(LOOKUP_ENTITY_PREFIX);
}

/** What a cached record is identified by: a lookup row by its key, an OSDU record by its id. */
export function recordIdLabel(entityType: string): string {
  return isLookupEntityType(entityType) ? "Key" : "OSDU id";
}

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
 * How a mapping reads a cached value: cache.<type>.<name>, where the name is what the cache flow caches a path under, or
 * id for the record's OSDU id. The partition is never named: a delivery flow reads the cache of the partition it delivers to.
 */
export function cacheReference(typeName: string, name: string): string {
  return `cache.${typeName}.${name}`;
}

/** The name a mapping reads a cached record's OSDU id under. */
export const CACHE_ID_FIELD = "id";

/**
 * A mapping entry that fills a variable with a cached record's OSDU id, found by one of its captured values matching a
 * dataset column: what a mapping author starts from for a type. The column is a placeholder the author renames.
 */
export function cacheEntryExample(typeName: string, lookupField: string | null): string {
  const lines = [`source: ${cacheReference(typeName, CACHE_ID_FIELD)}`];
  if (lookupField !== null) {
    lines.push(`findBy: ${cacheReference(typeName, lookupField)} = dataset.<column>`);
  }

  return lines.join("\n");
}

/**
 * A mapping entry that reads a value of a lookup table's row, found by its key matching a dataset column. A lookup row has
 * no OSDU id, so the entry reads one of its values; the column is a placeholder the author renames.
 */
export function lookupEntryExample(typeName: string, keyField: string, valueField: string | null): string {
  return [
    `source: ${cacheReference(typeName, valueField ?? keyField)}`,
    `findBy: ${cacheReference(typeName, keyField)} = dataset.<column>`,
  ].join("\n");
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
  if (isLookupEntityType(entityType)) {
    return "Lookup tables";
  }

  const at = entityType.indexOf("--");
  if (at <= 0) {
    return "Other";
  }

  const words = entityType.slice(0, at).replace(/-/g, " ");
  return words.charAt(0).toUpperCase() + words.slice(1);
}

/** Families in the order the type picker lists them: what mappings look up first, then the master data they point at. */
const familyRank: Record<string, number> = { "Reference data": 0, "Master data": 1, "Lookup tables": 2 };

export function compareFamilies(a: string, b: string): number {
  const rankA = familyRank[a] ?? 3;
  const rankB = familyRank[b] ?? 3;
  return rankA !== rankB ? rankA - rankB : a.localeCompare(b);
}

/** One type of the partition's cache, as the Definition tab and the type picker show it. */
export interface CachedTypeSummary {
  name: string;
  entityType: string;
  family: string;
  /** Every cache flow's declaration of the type: where it takes the records from. */
  sources: DeliveryCacheTypeSource[];
  /** Where the records come from: searched on OSDU, read from an ingestion table, or held from a dictionary. */
  origin: DeliveryCacheOrigin;
  /** For a lookup table, the name each row's key is kept under; null for OSDU records. */
  key: string | null;
  /** The records the current version holds of the type. */
  items: number;
  /** Every path the cache keeps for the type, by the name it is cached under, with the flows that declare it. */
  fields: DeliveryCacheField[];
  /** approve or auto: what a changed value does to the records built from it. */
  onChange: string;
}

/** The types a partition's cache holds, sorted the way the type picker lists them: by family, then by name. */
export function summarizeTypes(cache: DeliveryCache | null): CachedTypeSummary[] {
  return (cache?.types ?? [])
    .map((type) => ({
      name: type.name,
      entityType: type.entityType,
      family: entityFamily(type.entityType),
      sources: type.sources,
      origin: type.origin,
      key: type.key,
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
