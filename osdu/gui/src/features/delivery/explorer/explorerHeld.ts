import { useMemo } from "react";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { explorerApi, type ExplorerFieldInfo, type ExplorerPage, type ExplorerSearchRequest } from "../../../api/explorer";
import { fieldLabel, kindParts, useExplorerRead } from "./explorerModel";
import type { OfferedTerm } from "./explorerTerms";

// The values the records hold at a property, the commonest first, each with how many records hold it: what the condition
// editor and a search field searching one attribute offer to pick from (osdu/docs/reference/concepts/explorer.md,
// Conditions and properties).

/** How long typing rests before the values held are asked for again. */
const TYPING_DELAY_MS = 300;

/** The values held at a property, as they are being read. */
export interface HeldValues {
  /** What was typed, once typing rested: what the values listed are narrowed to. */
  settled: string;
  /** The values listed, the commonest first, each with how many records hold it. */
  listed: [string, number][];
  /** Whether a read is under way. */
  reading: boolean;
  /** Why the values could not be read; null when they were. */
  error: unknown;
  /** The search service's words, when it would not group by the property. */
  refusal: string | null;
}

/**
 * The values the records of `base` hold at `field`, the commonest first: one grouping of the search, and as a value is
 * typed, one more of the values that start with it (exact case, as the index keeps the whole value), with the values
 * listed before that hold the text anywhere, in any case. At most `limit` are listed; nothing is read until `enabled`.
 */
export function useHeldValues({ partition, base, field, typed, limit, enabled = true }: {
  partition: string | null;
  base: ExplorerSearchRequest;
  field: ExplorerFieldInfo;
  typed: string;
  limit: number;
  enabled?: boolean;
}): HeldValues {
  const settled = useDebouncedValue(typed.trim(), TYPING_DELAY_MS);
  const facet = { path: field.path, index: field.index, ...(field.nested ? { nested: field.nested } : {}) };
  const grouped = { ...base, offset: 0, limit: 1, columns: undefined, facet };
  const all = useExplorerRead<ExplorerPage>(["held", partition, grouped], enabled ? () => explorerApi.search(partition, grouped) : null);
  // The start typed is asked of the index where it keeps the whole value: text and keywords outside a nested list.
  const narrowable = enabled && settled !== "" && (field.index === "text" || field.index === "keyword") && !field.nested;
  const narrowed = { ...grouped, filters: [...(base.filters ?? []), { path: field.path, index: field.index, condition: "startsWith" as const, value: settled }] };
  const starting = useExplorerRead<ExplorerPage>(["held", partition, narrowed], narrowable ? () => explorerApi.search(partition, narrowed) : null);

  const listed = useMemo(() => {
    const wanted = settled.toLowerCase();
    const byValue = new Map<string, number>();
    for (const bucket of [...(starting.data?.answer.facet ?? []), ...(all.data?.answer.facet ?? [])]) {
      if (bucket.value != null && (wanted === "" || bucket.value.toLowerCase().includes(wanted)) && !byValue.has(bucket.value)) {
        byValue.set(bucket.value, bucket.count);
      }
    }

    return [...byValue.entries()].sort(([a, x], [b, y]) => y - x || a.localeCompare(b)).slice(0, limit);
  }, [all.data, starting.data, settled, limit]);

  return {
    settled,
    listed,
    reading: enabled && (all.isPending || (narrowable && starting.isFetching)),
    error: all.isError ? all.error : null,
    refusal: all.data?.answer.refusal ?? null,
  };
}

/**
 * Where the values to pick from are read: the property of the records in view, within the search without this condition;
 * for a term, the property its suggestions name, and for a route through other records, every record of their kind.
 */
export function heldFor(base: ExplorerSearchRequest, target: ExplorerFieldInfo, term: OfferedTerm | null): { base: ExplorerSearchRequest; field: ExplorerFieldInfo; heading?: string } {
  const suggest = term?.term.suggest;
  if (suggest === undefined || suggest === null) {
    return { base, field: target };
  }

  const field = { path: suggest.path, index: suggest.index, nested: suggest.nested };
  return suggest.kind === null
    ? { base, field }
    : { base: { kind: suggest.kind, filters: [] }, field, heading: `Values of ${kindParts(suggest.kind).type} ${fieldLabel(suggest.path)}` };
}
