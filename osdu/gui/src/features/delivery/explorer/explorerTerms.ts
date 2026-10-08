import { useQuery } from "@tanstack/react-query";
import type { ExplorerCondition, ExplorerFieldInfo, ExplorerFilter, ExplorerIndex } from "../../../api/explorer";
import { searchedRoute, searchTermsApi, type SearchRouteView, type SearchTermView } from "../../../api/searchTerms";
import { fieldLabel, kindParts, searchedIn } from "./explorerModel";

// The search terms of the records in view (osdu/docs/search-terms.md): the columns of the source systems the mappings of
// active delivery flows read, offered beside the record's own properties. A condition on one names it by its id, its values
// are the source's own, and the control plane turns it into the condition on the property its route fills. The terms are
// read once for a type and kept; a change made on the Search terms page reads them again.

/** How long the terms of a type stand before a part that shows them reads them again. */
const TERMS_FRESH_MS = 5 * 60_000;

/** The entity type whose terms a place offers: the one its kind names; none across every type or a group, whose records are of many types. */
export function termsEntityType(kind: string | undefined): string | null {
  if (kind === undefined) {
    return null;
  }

  const { entityType } = kindParts(kind);
  return entityType === "" || entityType.includes("*") ? null : entityType;
}

/**
 * The search terms of the type `kind` names, read when `enabled` and kept by kind, so every part asking for them shares one
 * read; each searched through its route for that kind (the version of the mapping that renders it, where versions write a
 * column differently). A place of many types offers none; with `anyType`, the terms of every type are read there instead,
 * so a condition on a term carried to such a place is still named. Kept under the Search terms page's keys, so a term
 * renamed there is read again here.
 */
export function useSearchTerms(kind: string | undefined, enabled = true, anyType = false) {
  const entityType = termsEntityType(kind);
  const asked = entityType === null ? (anyType ? "*" : null) : kind ?? null;
  return useQuery({
    queryKey: ["delivery", "search-terms", "explorer", asked],
    queryFn: () => searchTermsApi.list(asked === "*" ? {} : { kind: asked ?? undefined }),
    enabled: enabled && asked !== null,
    staleTime: TERMS_FRESH_MS,
    gcTime: 10 * 60_000,
    retry: false,
    refetchOnWindowFocus: false,
  });
}

/** A term the explorer searches by, with the route it is searched through and the property that route fills. */
export interface OfferedTerm {
  term: SearchTermView;
  route: SearchRouteView;
  /** The property the route fills, as the platform indexes it. */
  field: ExplorerFieldInfo;
}

/** The term `term` as offered: with its route and that route's property; null for one left out or that cannot be searched. */
export function offeredTerm(term: SearchTermView): OfferedTerm | null {
  const route = term.orphan ? null : searchedRoute(term);
  if (route === null || route.index === null || route.conditions.length === 0) {
    return null;
  }

  return { term, route, field: { path: route.path, index: route.index, nested: route.nested } };
}

/** The terms a place offers, by name: each searchable one, none left out. */
export function offeredTerms(terms: SearchTermView[] | undefined): OfferedTerm[] {
  return (terms ?? [])
    .map(offeredTerm)
    .filter((term): term is OfferedTerm => term !== null)
    .sort((a, b) => a.term.name.localeCompare(b.term.name, "en", { sensitivity: "base" }));
}

/** The term a condition names, among those read; undefined while they are read, or for a condition on a property. */
export function termOf(filter: ExplorerFilter, terms: SearchTermView[] | undefined): SearchTermView | undefined {
  return filter.term === undefined ? undefined : terms?.find((term) => term.id === filter.term);
}

/** The source column a term is, in full: `OsduData.arc.WellLog.wellbore_uwi`. */
export function termSource(term: SearchTermView): string {
  return term.source === "" ? term.columnLabel : `${term.source}.${term.column}`;
}

/** The versions of a type a route's kinds name, as a reader says them: `1.4.0 and 1.5.0`. */
export function routeVersions(kinds: string[]): string {
  return kinds.map((kind) => kindParts(kind).version).filter((version) => version !== "").join(" and ");
}

/** What a term is and how it is searched, in lines for a tooltip. */
export function termTitle(offered: OfferedTerm): string {
  const { term, route } = offered;
  const versions = routeVersions(route.kinds);
  return [
    term.name,
    `The column ${termSource(term)}${term.renamed ? `, named ${term.name} here` : ""}.`,
    `Searched in ${fieldLabel(route.path)}, ${route.how}${versions === "" ? "" : `, as ${kindParts(route.kinds[0]).type} ${versions} write it`}.`,
    term.note,
  ].filter(Boolean).join("\n");
}

/**
 * How a value of a term is typed: as the property it fills, for a route that writes the source's value there (a copy, or
 * steps that keep it); as plain text for one that finds or makes what it writes, since the value is the source's own.
 */
export function termValueIndex(route: SearchRouteView): ExplorerIndex {
  return (route.kind === "copy" || route.kind === "steps") && route.index !== null ? route.index : "keyword";
}

/**
 * The condition text typed in the search box becomes when it is searched in a term: its words or its start where the route
 * writes the source's text, as for a property; its whole value where the route finds or makes what it writes.
 */
export function termSearchInCondition(route: SearchRouteView): ExplorerCondition {
  if (route.kind === "copy" || route.kind === "steps") {
    if (route.conditions.includes("contains")) {
      return "contains";
    }

    if (route.conditions.includes("startsWith")) {
      return "startsWith";
    }
  }

  return "is";
}

/** The condition on `offered` the search box asks for the text typed there. */
export function termCondition(offered: OfferedTerm, value: string): ExplorerFilter {
  const condition = termSearchInCondition(offered.route);
  return {
    path: offered.field.path,
    index: offered.field.index,
    ...(offered.field.nested ? { nested: offered.field.nested } : {}),
    ...(condition !== "is" ? { condition } : {}),
    value,
    term: offered.term.id,
  };
}

/** The prefix a term searched in is remembered by among the properties searched in lately for a type. */
const TERM_MEMORY_PREFIX = "term:";

/** A term as the properties searched in lately remember it. */
export function termMemory(term: SearchTermView): string {
  return `${TERM_MEMORY_PREFIX}${term.id}`;
}

/** The most source columns the search box offers to search typed text in. */
const SEARCH_IN_TERMS = 3;

/** The terms the search box offers to search typed text in: those searched in lately for the type, newest first. */
export function searchInTerms(offered: OfferedTerm[], entityType: string): OfferedTerm[] {
  const byMemory = new Map(offered.map((term) => [termMemory(term.term), term]));
  return searchedIn(entityType)
    .map((remembered) => byMemory.get(remembered))
    .filter((term): term is OfferedTerm => term !== undefined)
    .slice(0, SEARCH_IN_TERMS);
}
