import { BookOpen } from "lucide-react";
import { Card } from "@/components/ui/card";
import type { DeliveryCachedItem } from "../../api/delivery";
import { CopyButton } from "@/components/CopyButton";
import { TruncatedText } from "@/components/TruncatedText";
import {
  CACHE_ID_FIELD, cacheEntryExample, cacheReference, cachedCell, isLookupEntityType, lookupEntryExample, lookupReplaceExample,
  type CachedTypeSummary,
} from "./cacheFormat";

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
  if (isLookupEntityType(item.entityType)) {
    return <LookupRowReference item={item} names={names} />;
  }

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
 * How a mapping reads one row of a lookup table: each of its values by name, and an entry that finds the row by its key. A
 * lookup row is not an OSDU record, so there is no id to read or copy; its key is the value it is found by.
 */
function LookupRowReference({ item, names }: { item: DeliveryCachedItem; names: string[] }) {
  const keyName = names.find((name) => item.fields[name] === item.recordId) ?? names[0] ?? null;
  const valueName = names.find((name) => name !== keyName) ?? null;
  const rows = names.map((name) => ({ name, value: cachedCell(item.fields[name]) }));
  return (
    <section className="flex flex-col gap-2" data-testid="delivery-cache-item-mapping">
      <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">In a mapping</h3>
      <div className="overflow-x-auto rounded-md border border-border">
        <table className="w-full text-[12px]">
          <thead className="bg-muted/40 text-left text-[11px] text-muted-foreground">
            <tr>
              <th className="px-3 py-1.5 font-medium">Source</th>
              <th className="px-3 py-1.5 font-medium">Reads, for this row</th>
              <th className="w-8" aria-label="Copy" />
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => {
              const reference = cacheReference(item.typeName, row.name);
              return (
                <tr key={row.name} className="border-t border-border" data-testid="delivery-cache-item-reference">
                  <td className="whitespace-nowrap px-3 py-1.5 font-mono">
                    {reference}
                    {row.name === keyName && <span className="ml-1.5 font-sans text-[11px] text-muted-foreground">the key</span>}
                  </td>
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
      {keyName !== null && <Snippet text={lookupEntryExample(item.typeName, keyName, valueName)} testId="delivery-cache-item-entry" />}
      <p className="text-[12px] text-muted-foreground">
        This row belongs to a lookup table, filled from an ingestion table or a dictionary: it is not an OSDU record and has no
        id to write. A row whose <span className="font-mono">dataset.&lt;column&gt;</span> holds{" "}
        <span className="font-mono text-foreground">{item.recordId}</span> selects it
        {valueName !== null && (
          <>
            , and the entry renders <span className="break-all font-mono text-foreground">{cachedCell(item.fields[valueName]) ?? "no value"}</span>
          </>
        )}
        .
        {valueName !== null && (
          <>
            {" "}Under <span className="font-mono">replace: cache.{item.typeName}</span>, the same value becomes it on its way to any
            entry.
          </>
        )}
      </p>
    </section>
  );
}

/**
 * How a mapping reads the partition's cache, for the Definition tab: the source and findBy forms, with a working entry for
 * an OSDU type and one for a lookup table when the cache holds either, since every name the table lists is readable as
 * cache.<Type>.<name>.
 */
export function CacheMappingGuide({ types, scope }: { types: CachedTypeSummary[]; scope: string }) {
  const records = types.filter((type) => type.key === null);
  const lookups = types.filter((type) => type.key !== null);
  const example = records.find((type) => type.fields.length > 0) ?? records[0] ?? null;
  const table = lookups[0] ?? null;
  if (example === null && table === null) {
    return null;
  }

  return (
    <Card className="flex flex-col gap-2 rounded-lg p-4" data-testid="delivery-cache-mapping-guide">
      <h3 className="flex items-center gap-1.5 text-[13px] font-medium">
        <BookOpen className="size-4 text-muted-foreground" />
        Reading the cache in a mapping
      </h3>
      {example !== null && (
        <>
          <p className="text-[12.5px] text-muted-foreground">
            A mapping entry reads a cached OSDU record with <span className="font-mono text-foreground">cache.&lt;Type&gt;.id</span> (its
            OSDU id, as a relationship) or <span className="font-mono text-foreground">cache.&lt;Type&gt;.&lt;name&gt;</span> (a
            value it keeps, by the names below), and says which record with <span className="font-mono text-foreground">findBy</span>:
            lines comparing a kept value with a dataset column or a quoted text, tried in order.
          </p>
          <Snippet text={cacheEntryExample(example.name, example.fields[0]?.as ?? null)} testId="delivery-cache-mapping-guide-entry" />
        </>
      )}
      {table !== null && (
        <>
          <p className="text-[12.5px] text-muted-foreground" data-testid="delivery-cache-mapping-guide-lookups">
            A lookup table, filled from an ingestion table or a dictionary, holds rows that are not OSDU records, so it has no
            id to write. A mapping reads one of a row's values with{" "}
            <span className="font-mono text-foreground">cache.&lt;Type&gt;.&lt;name&gt;</span>, found by the row's key (its{" "}
            <span className="font-mono text-foreground">{table.key}</span>) matching a dataset column.
          </p>
          <Snippet
            text={lookupEntryExample(table.name, table.key!, table.fields.find((field) => field.as !== table.key)?.as ?? null)}
            testId="delivery-cache-mapping-guide-lookup-entry"
          />
          <p className="text-[12.5px] text-muted-foreground" data-testid="delivery-cache-mapping-guide-replace">
            It also translates a dataset value on its way to any entry, as a replace modifier: the value is matched on the key
            and becomes the row's value. A value the table does not list stays as it is, unless the replace's{" "}
            <span className="font-mono text-foreground">otherwise</span> says what it becomes; a row with no value gives none.
          </p>
          <Snippet
            text={lookupReplaceExample(table.name, table.key!, table.fields.map((field) => field.as))}
            testId="delivery-cache-mapping-guide-replace-entry"
          />
        </>
      )}
      <p className="text-[12.5px] text-muted-foreground">
        The mapping never names a cache: every delivery flow delivering to <span className="font-mono text-foreground">{scope}</span>{" "}
        reads this one. Open a record under Records to see the values each reference reads.
      </p>
    </Card>
  );
}
