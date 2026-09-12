import type { DeliveryCacheDefinition, DeliveryCacheField } from "../../api/delivery";

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

/** Families in the order the type list shows them: what mappings look up first, then the master data they point at. */
const familyRank: Record<string, number> = { "Reference data": 0, "Master data": 1 };

export function compareFamilies(a: string, b: string): number {
  const rankA = familyRank[a] ?? 2;
  const rankB = familyRank[b] ?? 2;
  return rankA !== rankB ? rankA - rankB : a.localeCompare(b);
}

/** One cached type across every repository in scope: the same name declared in two repositories is one entry. */
export interface CachedTypeSummary {
  name: string;
  entityType: string;
  family: string;
  /** The records the version being read holds for the type, over every repository in scope. */
  items: number;
  /** The captured paths by the name they are cached under, in declaration order. */
  fields: DeliveryCacheField[];
  /** The retrieval flows that keep the type current. */
  flows: string[];
  /** How many repositories declare it. */
  repos: number;
  /** Whether a refresh of the type moves the pin, so deliveries read what it captured. */
  movesPin: boolean;
}

/** The declarations folded by type name and sorted the way the type list shows them: by family, then by name. */
export function summarizeTypes(definitions: DeliveryCacheDefinition[]): CachedTypeSummary[] {
  const byName = new Map<string, CachedTypeSummary & { repoIds: Set<string> }>();
  for (const definition of definitions) {
    const existing = byName.get(definition.name);
    if (existing === undefined) {
      byName.set(definition.name, {
        name: definition.name,
        entityType: definition.entityType,
        family: entityFamily(definition.entityType),
        items: definition.items,
        fields: [...definition.fields],
        flows: [definition.flowName],
        repos: 1,
        repoIds: new Set([definition.repoId]),
        movesPin: definition.makeCurrent,
      });
      continue;
    }

    existing.items += definition.items;
    existing.repoIds.add(definition.repoId);
    existing.repos = existing.repoIds.size;
    existing.movesPin = existing.movesPin || definition.makeCurrent;
    if (!existing.flows.includes(definition.flowName)) {
      existing.flows.push(definition.flowName);
    }

    for (const field of definition.fields) {
      if (!existing.fields.some((known) => known.as === field.as)) {
        existing.fields.push(field);
      }
    }
  }

  return [...byName.values()]
    .map(({ repoIds: _repoIds, ...summary }) => summary)
    .sort((a, b) => compareFamilies(a.family, b.family) || a.name.localeCompare(b.name));
}
