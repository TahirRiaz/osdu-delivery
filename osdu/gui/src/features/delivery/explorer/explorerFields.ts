import { explorerApi, type ExplorerFieldInfo, type ExplorerFields } from "../../../api/explorer";
import { ALL_KINDS, NAME_FIELDS, searchedIn, useExplorerRead } from "./explorerModel";

// The properties of the records in view (osdu/docs/reference/concepts/explorer.md, Conditions and properties): the
// record's own, those the kind's schema declares, and those its records hold beyond them. Group by, the conditions and
// the search box's "in" all offer them, read once for a kind and kept, since a kind's properties move slowly.

/** How long the properties of a kind stand before a part that shows them reads them again. */
const FIELDS_FRESH_MS = 10 * 60_000;

/** The properties of `kind`'s records, read when `enabled` and kept by kind, so every part asking for them shares one read. */
export function useExplorerFields(partition: string | null, kind: string | undefined, enabled: boolean) {
  const asked = kind ?? ALL_KINDS;
  return useExplorerRead<ExplorerFields>(["fields", partition, asked], enabled ? () => explorerApi.fields(partition, asked) : null, FIELDS_FRESH_MS);
}

/** Whether the place in view is every type, where only the record's own properties mean the same thing in every record. */
export function isEveryType(kind: string | undefined): boolean {
  return kind === undefined || kind === ALL_KINDS;
}

/**
 * The properties a place offers: across every type, the record's own and the record's name properties, which most kinds
 * hold and mean the same thing in; for a type, everything its schema declares and its records hold.
 */
export function offeredFields(fields: ExplorerFieldInfo[], kind: string | undefined): ExplorerFieldInfo[] {
  return isEveryType(kind) ? [...fields.filter((field) => !field.path.startsWith("data.")), ...NAME_FIELD_INFOS] : fields;
}

/** The record's name properties, as text: what most kinds hold, and what a search across every type offers to search in. */
export const NAME_FIELD_INFOS: ExplorerFieldInfo[] = NAME_FIELDS.map((path) => ({ path, index: "text", origin: "schema" }));

/** The entity type the properties searched in are remembered under: the type of the place, or every type. */
export function searchedInType(kind: string | undefined): string {
  if (isEveryType(kind)) {
    return "*";
  }

  const entityType = (kind ?? "").split(":")[2] ?? "";
  return entityType === "" ? "*" : entityType;
}

/**
 * The properties the search box offers to search typed text in, the likeliest first: those searched in lately for the type,
 * then the record's name properties, each once and only where the place holds it. Across every type, the name properties
 * alone, which most kinds hold; for a type whose properties are still being read, none yet.
 */
export function searchInChoices(fields: ExplorerFieldInfo[] | undefined, kind: string | undefined, entityType: string): ExplorerFieldInfo[] {
  if (!isEveryType(kind) && fields === undefined) {
    return [];
  }

  const held = new Map((isEveryType(kind) ? NAME_FIELD_INFOS : fields ?? []).map((field) => [field.path, field]));
  const choices: ExplorerFieldInfo[] = [];
  for (const path of [...searchedIn(entityType), ...NAME_FIELDS]) {
    const field = held.get(path);
    if (field !== undefined && !choices.includes(field)) {
      choices.push(field);
    }
  }

  return choices.slice(0, 6);
}

/** Where a property comes from, in a few words for a tooltip. */
export function originText(field: ExplorerFieldInfo, schemaKind: string | null | undefined): string {
  switch (field.origin) {
    case "record":
      return "A property of every record.";
    case "records":
      return schemaKind
        ? `Held by the records read, and not declared by the schema of ${schemaKind}: a property the index adds (an index augmentation), or one of another version.`
        : "Held by the records read; no schema was read for them.";
    default:
      return schemaKind ? `Declared by the schema of ${schemaKind}.` : "Declared by the kind's schema.";
  }
}

/** The nested list a property is reached through, as a reader says it: `GeoContexts` of `data.GeoContexts`. */
export function nestedLabel(nested: string | null | undefined): string | null {
  return nested ? nested.replace(/^data\./, "") : null;
}
