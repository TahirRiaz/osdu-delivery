import { deliveryApi, type DeliveryCacheTypeChange, type DeliveryCacheVersion, type DeliveryCachedItem } from "../../api/delivery";
import { compareFamilies, entityFamily, type CachedTypeSummary } from "./cacheFormat";

/** How many records a type's section on the records tab shows before it points at the type's own table. */
export const SECTION_ROWS = 5;

/**
 * One type as the records tab browses it: what the version being read holds of it, and how the cache flows declare it
 * now, which is where its columns come from.
 */
export interface BrowsedType {
  name: string;
  entityType: string;
  family: string;
  /** For a lookup table, the name its rows' keys are kept under; null for OSDU records. */
  key: string | null;
  /** How many records the version being read holds of the type. */
  items: number;
  /** The type as the cache flows declare it now; null for a type only an older version holds. */
  declared: CachedTypeSummary | null;
  /**
   * How the version being read holds the type against the version before it, by the type's content hash; null before the
   * versions have loaded, and for a version written before types were hashed.
   */
  change: DeliveryCacheTypeChange | null;
  /** The version that last added or changed the type, whose content the version being read holds unchanged; null when it cannot be told. */
  since: string | null;
  /** The type's content hash in the version being read; null when it cannot be told. */
  hash: string | null;
}

/**
 * The types the records tab lists, in the order the type picker lists them (by family, then by name): every type the
 * version being read holds, with its count in that version, and every type the cache flows declare that the version holds
 * nothing of yet. Before the versions have loaded the declaration's own counts, which are the current version's, stand in.
 */
export function browsedTypes(summaries: CachedTypeSummary[], version: DeliveryCacheVersion | undefined): BrowsedType[] {
  const declared = new Map(summaries.map((summary) => [summary.name, summary]));
  const held = version === undefined
    ? summaries.map((summary): BrowsedType => ({
      name: summary.name,
      entityType: summary.entityType,
      family: summary.family,
      key: summary.key,
      items: summary.items,
      declared: summary,
      change: null,
      since: null,
      hash: null,
    }))
    : version.types.map((type): BrowsedType => ({
      name: type.name,
      entityType: type.entityType,
      family: entityFamily(type.entityType),
      key: type.key,
      items: type.items,
      declared: declared.get(type.name) ?? null,
      change: type.change,
      since: type.since,
      hash: type.hash,
    }));

  const names = new Set(held.map((type) => type.name));
  const waiting = summaries
    .filter((summary) => !names.has(summary.name))
    .map((summary): BrowsedType => ({
      name: summary.name,
      entityType: summary.entityType,
      family: summary.family,
      key: summary.key,
      items: 0,
      declared: summary,
      change: null,
      since: null,
      hash: null,
    }));

  return [...held, ...waiting].sort((a, b) => compareFamilies(a.family, b.family) || a.name.localeCompare(b.name));
}

/**
 * The names a type's table has a column for, in order: the names the cache flows declare for it, then any other name the
 * rows given carry (a type only an older version holds declares none, and an older version can hold a name no flow keeps
 * any more). A lookup table's key leads its rows as their identity, so it is not repeated as a value.
 */
export function columnNames(type: BrowsedType, rows: DeliveryCachedItem[] | undefined): string[] {
  const names = type.declared?.fields.map((field) => field.as) ?? [];
  const seen = new Set(names);
  for (const row of rows ?? []) {
    for (const name of Object.keys(row.fields)) {
      if (!seen.has(name)) {
        seen.add(name);
        names.push(name);
      }
    }
  }

  return names.filter((name) => name !== type.key);
}

/**
 * The first records of one type in one version, matching the search when there is one, with how many match: what a type's
 * section shows and what the type list counts while a search is typed. One query definition for both, so the list and the
 * section share one request per type.
 */
export function typeSampleQuery(scope: string, type: string, search: string, version: string | undefined) {
  return {
    queryKey: ["delivery", "cache", "items", "sample", scope, type, search, version ?? null] as const,
    queryFn: () => deliveryApi.cachedItems({
      page: 1, pageSize: SECTION_ROWS, scope, type, search: search === "" ? undefined : search, version,
    }),
  };
}
