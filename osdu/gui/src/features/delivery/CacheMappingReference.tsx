import { BookOpen } from "lucide-react";
import { Card } from "@/components/ui/card";
import type { DeliveryCachedItem } from "../../api/delivery";
import { CopyButton } from "@/components/CopyButton";
import { TruncatedText } from "@/components/TruncatedText";
import { CACHE_ID_FIELD, cacheEntryExample, cacheReference, cachedCell, type CachedTypeSummary } from "./cacheFormat";

/** What cache.<Type>.id renders: the record's OSDU id with the trailing colon an OSDU relationship carries. */
function relationshipId(recordId: string): string {
  return recordId.endsWith(":") ? recordId : `${recordId}:`;
}

/** A captured value a findBy line can match on: a text or number, or the first of a set, since a set matches on any one. */
function matchValue(value: unknown): string | null {
  const first = Array.isArray(value) ? value[0] : value;
  if (typeof first === "string" && first.trim() !== "") {
    return first;
  }

  return typeof first === "number" || typeof first === "boolean" ? String(first) : null;
}

/** A snippet of mapping YAML with its copy button. */
function Snippet({ text, testId }: { text: string; testId: string }) {
  return (
    <div className="flex items-start gap-1 rounded-md border border-border bg-muted/40 px-3 py-2">
      <pre className="min-w-0 flex-1 overflow-x-auto font-mono text-[12px] leading-5" data-testid={testId}>{text}</pre>
      <CopyButton iconOnly label="Copy the YAML" text={text} testId={`${testId}-copy`} />
    </div>
  );
}

/**
 * How a mapping reads one cached record, with the values it would get: each reference (cache.<Type>.id and one per captured
 * name) next to what it renders for this record, and an entry that finds the record by one of its values. Every delivery
 * flow delivering to the partition reads the same cache, so the references carry no cache or partition name.
 */
export function RecordMappingReference({ item, names }: {
  item: DeliveryCachedItem;
  /** The captured names in the order the sheet lists them. */
  names: string[];
}) {
  const rows = [
    { name: CACHE_ID_FIELD, value: relationshipId(item.recordId) },
    ...names.map((name) => ({ name, value: cachedCell(item.fields[name]) })),
  ];
  const lookup = names.map((name) => ({ name, value: matchValue(item.fields[name]) })).find((candidate) => candidate.value !== null) ?? null;

  return (
    <section className="flex flex-col gap-2" data-testid="delivery-cache-item-mapping">
      <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">In a mapping</h3>
      <div className="overflow-x-auto rounded-md border border-border">
        <table className="w-full text-[12px]">
          <thead className="bg-muted/40 text-left text-[11px] text-muted-foreground">
            <tr>
              <th className="px-3 py-1.5 font-medium">Source</th>
              <th className="px-3 py-1.5 font-medium">Reads, for this record</th>
              <th className="w-8" aria-label="Copy" />
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => {
              const reference = cacheReference(item.typeName, row.name);
              return (
                <tr key={row.name} className="border-t border-border" data-testid="delivery-cache-item-reference">
                  <td className="whitespace-nowrap px-3 py-1.5 font-mono">{reference}</td>
                  <td className="px-3 py-1.5">
                    <TruncatedText text={row.value} mono maxWidth={320} />
                  </td>
                  <td className="px-1 py-1 text-right">
                    <CopyButton iconOnly label={`Copy ${reference}`} text={reference} testId="delivery-cache-item-reference-copy" />
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <Snippet text={cacheEntryExample(item.typeName, lookup?.name ?? null)} testId="delivery-cache-item-entry" />
      <p className="text-[12px] text-muted-foreground">
        {lookup === null
          ? "The record holds no captured value a findBy line can match on, so a mapping cannot select it by value."
          : (
            <>
              A row whose <span className="font-mono">dataset.&lt;column&gt;</span> holds{" "}
              <span className="font-mono text-foreground">{lookup.value}</span> selects this record, and the entry renders{" "}
              <span className="break-all font-mono text-foreground">{relationshipId(item.recordId)}</span>. Any delivery flow
              delivering to the partition reads it; the mapping never names the cache.
            </>
          )}
      </p>
    </section>
  );
}

/**
 * How a mapping reads the partition's cache, for the Definition tab: the source and findBy forms, with a working entry for
 * the type in scope (or the first type), since every name the table lists is readable as cache.<Type>.<name>.
 */
export function CacheMappingGuide({ types, scope }: { types: CachedTypeSummary[]; scope: string }) {
  const example = types.find((type) => type.fields.length > 0) ?? types[0] ?? null;
  if (example === null) {
    return null;
  }

  const lookup = example.fields[0]?.as ?? null;
  return (
    <Card className="flex flex-col gap-2 rounded-lg p-4" data-testid="delivery-cache-mapping-guide">
      <h3 className="flex items-center gap-1.5 text-[13px] font-medium">
        <BookOpen className="size-4 text-muted-foreground" />
        Reading the cache in a mapping
      </h3>
      <p className="text-[12.5px] text-muted-foreground">
        A mapping entry reads a cached record with <span className="font-mono text-foreground">cache.&lt;Type&gt;.id</span> (its
        OSDU id, as a relationship) or <span className="font-mono text-foreground">cache.&lt;Type&gt;.&lt;name&gt;</span> (a
        value it keeps, by the names below), and says which record with <span className="font-mono text-foreground">findBy</span>:
        lines comparing a kept value with a dataset column or a quoted text, tried in order. The mapping never names a cache:
        every delivery flow delivering to <span className="font-mono text-foreground">{scope}</span> reads this one. Open a
        record under Records to see the values each reference reads.
      </p>
      <Snippet text={cacheEntryExample(example.name, lookup)} testId="delivery-cache-mapping-guide-entry" />
    </Card>
  );
}
