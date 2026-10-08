// The search terms (osdu/docs/search-terms.md): the columns of the source systems the mappings of active delivery flows
// read, extracted by the repository sync, each with the routes by which it reaches the records and what people made of it.
// The explorer searches by them; the Search terms page refines them.

import { del, get, put } from "@/api/client";
import type { ExplorerCondition, ExplorerIndex } from "./explorer";

/** How a route carries a source value to what the record holds. */
export type SearchRouteKind = "copy" | "steps" | "lookup" | "search" | "key" | "expression";

/** The records a lookup or a search finds the record by, how, and what it reads of it. */
export interface SearchRouteFind {
  name: string;
  kind: string;
  lines: { field: string }[];
  read: string;
}

/** One way a term's column reaches the record. */
export interface SearchRouteView {
  /** `osdu.data.WellboreID|Search`: the variable and how the value reaches it. */
  id: string;
  target: string;
  path: string;
  kind: SearchRouteKind;
  /** How the route reaches the record, in words. */
  how: string;
  steps: string[];
  mappings: string[];
  location: string | null;
  find: SearchRouteFind | null;
  keyColumns: string[] | null;
  when: string | null;
  description: string | null;
  /** How the platform indexes what is compared; null when the route cannot be searched. */
  index: ExplorerIndex | null;
  nested: string | null;
  /** The conditions the route takes, the likeliest first. */
  conditions: ExplorerCondition[];
  /** Why the route cannot be searched; null when it can. */
  problem: string | null;
}

/** Where the values to pick from are read for a term: a property, and for a route through other records their kind. */
export interface SearchTermSuggest {
  kind: string | null;
  path: string;
  index: ExplorerIndex;
  nested: string | null;
}

/** A search term as people see it. */
export interface SearchTermView {
  id: string;
  key: string;
  system: string;
  entityType: string;
  dataset: string | null;
  column: string;
  /** The column as the source names it: `curves.curve_unit`. */
  columnLabel: string;
  /** The name the term is searched by. */
  name: string;
  renamed: boolean;
  excluded: boolean;
  note: string | null;
  /** The route a person picked; null for the one the explorer prefers. */
  pickedRoute: string | null;
  /** The route the term is searched through; null when none can be. */
  route: string | null;
  routes: SearchRouteView[];
  flows: string[];
  mappings: string[];
  /** Why the term cannot be searched; null when it can. */
  problem: string | null;
  suggest: SearchTermSuggest | null;
  /** A refinement whose term no mapping of an active flow gives any longer. */
  orphan: boolean;
  updatedBy: string | null;
  updatedUtc: string | null;
}

export interface SearchTermsAnswer {
  entityType: string | null;
  terms: SearchTermView[];
}

export interface SearchTermType {
  entityType: string;
  terms: number;
}

/** What a person makes of a term: a name (empty for the column's own), whether it is left out, the route, a note. */
export interface SearchTermRefinement {
  name?: string | null;
  excluded?: boolean;
  route?: string | null;
  note?: string | null;
}

/** The route a term is searched through, when it can be searched and is not left out; null otherwise. */
export function searchedRoute(term: SearchTermView): SearchRouteView | null {
  if (term.excluded || term.route === null) {
    return null;
  }

  return term.routes.find((route) => route.id === term.route) ?? null;
}

/** The terms of a kind's entity type, by the kind the explorer is in. */
const termsPath = "/api/v1/delivery/search-terms";

export const searchTermsApi = {
  /** The terms of an entity type (`entityType`), or of the type a kind names (`kind`), or of every type; refinements without a term with `orphans`. */
  list: (query: { entityType?: string; kind?: string; orphans?: boolean }) =>
    get<SearchTermsAnswer>(termsPath, {
      ...(query.entityType ? { entityType: query.entityType } : {}),
      ...(query.kind ? { kind: query.kind } : {}),
      ...(query.orphans ? { orphans: "true" } : {}),
    }),
  entityTypes: () => get<SearchTermType[]>(`${termsPath}/entity-types`),
  get: (id: string) => get<SearchTermView>(`${termsPath}/${encodeURIComponent(id)}`),
  refine: (id: string, refinement: SearchTermRefinement) => put<SearchTermView>(`${termsPath}/${encodeURIComponent(id)}`, refinement),
  reset: (id: string) => del<void>(`${termsPath}/${encodeURIComponent(id)}/refinement`),
};
