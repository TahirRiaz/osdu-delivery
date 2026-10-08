import type { ExplorerCondition, ExplorerFieldInfo, ExplorerFilter, ExplorerSearchRequest } from "../../../api/explorer";
import { heldFor } from "./explorerHeld";
import { fieldLabel, searchInCondition } from "./explorerModel";
import { termCondition, termMemory, termSearchInCondition, termTitle, type OfferedTerm } from "./explorerTerms";

// The attribute a place's search field searches in, picked at its start (osdu/docs/explorer.md, Searching a property):
// every property, as the field always searches, or one source column or one property the reader picked. Text searched in
// one attribute is a condition of the list, and searching the attribute again replaces it.

/** One attribute the search field searches in: a source column (a search term), or a property of the records. */
export type SearchScopeTarget = { kind: "term"; term: OfferedTerm } | { kind: "field"; field: ExplorerFieldInfo };

/** The attribute as the page keeps it while the place is in view: a term by its id, so a term renamed since is read anew. */
export type SearchScopeChoice = { kind: "term"; termId: string } | { kind: "field"; field: ExplorerFieldInfo };

/** The attribute a choice names among the terms offered; null for a term no longer offered. */
export function scopeTargetOf(choice: SearchScopeChoice, terms: OfferedTerm[]): SearchScopeTarget | null {
  if (choice.kind === "field") {
    return choice;
  }

  const term = terms.find((offered) => offered.term.id === choice.termId);
  return term === undefined ? null : { kind: "term", term };
}

/** The choice that keeps `target`. */
export function scopeChoiceOf(target: SearchScopeTarget): SearchScopeChoice {
  return target.kind === "term" ? { kind: "term", termId: target.term.term.id } : target;
}

/** The attribute as a reader names it: the term's name, or the property's path without `data.`. */
export function scopeName(target: SearchScopeTarget): string {
  return target.kind === "term" ? target.term.term.name : fieldLabel(target.field.path);
}

/** What the attribute is, in lines for a tooltip. */
export function scopeTitle(target: SearchScopeTarget): string {
  return target.kind === "term"
    ? termTitle(target.term)
    : [target.field.path, target.field.title, target.field.description].filter(Boolean).join("\n");
}

/** How a list of attributes marks the one picked: `term:<id>`, or `field:<path>`. */
export function scopeKey(target: SearchScopeTarget): string {
  return target.kind === "term" ? `term:${target.term.term.id}` : `field:${target.field.path}`;
}

/** The condition text typed is searched by in the attribute: its words, its start or its whole value, as the attribute allows. */
export function scopeTypedCondition(target: SearchScopeTarget): ExplorerCondition {
  return target.kind === "term" ? termSearchInCondition(target.term.route) : searchInCondition(target.field);
}

/** The condition on the attribute for `value`: as typed (its words, its start, or its whole value), or `exact`, a value picked. */
export function scopeCondition(target: SearchScopeTarget, value: string, exact: boolean): ExplorerFilter {
  if (target.kind === "term") {
    if (!exact) {
      return termCondition(target.term, value);
    }

    const { field } = target.term;
    return { path: field.path, index: field.index, ...(field.nested ? { nested: field.nested } : {}), value, term: target.term.term.id };
  }

  const { field } = target;
  const condition = exact ? "is" : searchInCondition(field);
  return {
    path: field.path,
    index: field.index,
    ...(field.nested ? { nested: field.nested } : {}),
    ...(condition !== "is" ? { condition } : {}),
    value,
  };
}

/** Whether `filter` is a condition on the attribute, which a search of the attribute replaces. */
export function scopeReplaces(filter: ExplorerFilter, target: SearchScopeTarget): boolean {
  return target.kind === "term"
    ? filter.term === target.term.term.id
    : filter.term === undefined && filter.path === target.field.path;
}

/**
 * Where the values of the attribute are read from, within `base`: the property itself, or for a term the property its
 * values are listed from (that of the records its route finds, for a lookup or a search); null for a term that lists none
 * (a key), whose values are made, not held.
 */
export function scopeHeld(target: SearchScopeTarget, base: ExplorerSearchRequest): { base: ExplorerSearchRequest; field: ExplorerFieldInfo; heading?: string } | null {
  if (target.kind === "field") {
    return { base, field: target.field };
  }

  return target.term.term.suggest === null ? null : heldFor(base, target.term.field, target.term);
}

/** How the page remembers the attribute among those searched in lately for the type. */
export function scopeMemory(target: SearchScopeTarget): string {
  return target.kind === "term" ? termMemory(target.term.term) : target.field.path;
}
