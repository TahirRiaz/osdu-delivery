import { useMemo, useState } from "react";
import { format } from "date-fns";
import { History, Pin } from "lucide-react";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { deliveryApi, type DeliveryCacheVersion, type DeliveryCachedItem } from "../../api/delivery";
import { CodeView } from "@/components/CodeView";
import { CopyButton } from "@/components/CopyButton";
import { DetailPair } from "@/components/DetailPair";
import { PagedTable, type Column } from "@/components/PagedTable";
import { RichTooltip } from "@/components/RichTooltip";
import { SearchInput } from "@/components/SearchInput";
import { StatePill } from "@/components/StatusBadge";
import { TruncatedText } from "@/components/TruncatedText";
import { useClipped } from "@/components/useClipped";
import { parseUtc } from "@/lib/time";
import { RecordMappingReference } from "./CacheMappingReference";
import { cachedCell, splitRecordId } from "./cacheFormat";

/** The picker's value for "whichever version is current", which is what the records open on. */
export const CURRENT = "current";

/**
 * An OSDU record id with its repeated part stepped back: the partition and entity type in muted text, the part that
 * tells records apart in full. The prefix gives way first when the cell runs out of room, so a column of ids stays
 * readable by its tails, and the whole id is revealed on hover only when something is actually hidden.
 */
export function RecordId({ id, maxWidth = 360, tailOnly = false }: {
  id: string;
  maxWidth?: number;
  /** Shows only the part that tells records apart, for a row that already names the type; the whole id is on hover. */
  tailOnly?: boolean;
}) {
  const { prefix, tail } = splitRecordId(id);
  const [prefixRef, prefixClipped] = useClipped(prefix);
  const [tailRef, tailClipped] = useClipped(tail);

  if (tailOnly) {
    return (
      <RichTooltip body={id} mono>
        <span className="inline-block truncate align-bottom font-mono text-[12px]" style={{ maxWidth }}>{tail}</span>
      </RichTooltip>
    );
  }

  const body = (
    <span className="inline-flex max-w-full items-baseline align-bottom font-mono text-[12px]" style={{ maxWidth }}>
      {prefix !== "" && (
        <span ref={prefixRef} className="min-w-0 shrink-[1000] truncate text-muted-foreground">{prefix}</span>
      )}
      <span ref={tailRef} className="min-w-0 truncate">{tail}</span>
    </span>
  );

  return prefixClipped || tailClipped ? <RichTooltip body={id} mono>{body}</RichTooltip> : body;
}

/** The captured values of one record, name by name, for the view over every type where the columns cannot be fixed. */
function FieldPairs({ fields }: { fields: Record<string, unknown> }) {
  const entries = Object.entries(fields);
  if (entries.length === 0) {
    return <span className="text-muted-foreground">-</span>;
  }

  // The pairs share the width the table leaves; the last ones clip, and the record's sheet lists every value.
  return (
    <span className="flex min-w-0 items-baseline gap-3 overflow-hidden">
      {entries.map(([name, value]) => (
        <span key={name} className="inline-flex shrink-0 items-baseline gap-1">
          <span className="text-[11px] text-muted-foreground">{name}</span>
          <TruncatedText text={cachedCell(value)} mono maxWidth={160} />
        </span>
      ))}
    </span>
  );
}

/** The version picker: the current version by default, or any version of the cache by label, with when it was captured. */
export function CacheVersionPicker({ versions, value, onChange, className }: {
  versions: DeliveryCacheVersion[];
  value: string;
  onChange: (version: string) => void;
  className?: string;
}) {
  return (
    <Select value={value} onValueChange={onChange} disabled={versions.length === 0}>
      <SelectTrigger
        size="sm"
        // The trigger centres its text as any button does; a picker reads left to right like the search beside it.
        className={cn("h-8 text-left *:data-[slot=select-value]:flex-1 *:data-[slot=select-value]:justify-start", className)}
        active={value !== CURRENT}
        aria-label="Version to read"
        data-testid="delivery-cache-version"
      >
        <History />
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value={CURRENT}>Current version</SelectItem>
        {versions.map((option) => (
          <SelectItem key={option.version} value={option.version}>
            <span className="font-mono text-[12px]">{option.version}</span>
            <span className="text-[11px] text-muted-foreground">{format(parseUtc(option.capturedUtc), "MMM d, HH:mm")}</span>
            {option.current && <span className="text-[11px] font-medium text-primary">current</span>}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}

/**
 * The records of one cache, with the search and the version to read in the tab's own toolbar: they narrow these records
 * and nothing else on the page. The current version is read by default. With a type in scope the table has one column
 * per captured name, so a unit's code, name and id read down the page; over every type the values fold into one column,
 * since the names differ from type to type.
 */
export function DeliveryCacheRecords({ scope, type, fields, versions }: {
  /** The partition whose cache the records belong to. */
  scope: string;
  type: string | null;
  /** The names the type in scope caches its paths under, in declaration order; empty without a type in scope. */
  fields: string[];
  /** The cache's versions, newest first, for the version picker. */
  versions: DeliveryCacheVersion[];
}) {
  const [item, setItem] = useState<DeliveryCachedItem | null>(null);
  const [search, setSearch] = useState("");
  // The picker holds a version label; CURRENT follows whichever version is current rather than freezing on one.
  const [picked, setPicked] = useState<string>(CURRENT);

  // A label the cache no longer lists would read as an empty cache rather than a stale pick, so it falls back to current.
  const versionFilter = picked === CURRENT || versions.some((v) => v.version === picked) ? picked : CURRENT;
  const reading = versions.find((v) => (versionFilter === CURRENT ? v.current : v.version === versionFilter));
  const historic = reading !== undefined && !reading.current ? reading : null;
  const version = versionFilter === CURRENT ? undefined : versionFilter;

  const columns = useMemo<Column<DeliveryCachedItem>[]>(() => {
    if (type !== null && fields.length > 0) {
      // One column per captured name: the id takes the width the values leave, its repeated prefix giving way first.
      return [
        {
          id: "recordId",
          header: "OSDU id",
          fill: true,
          floor: 200,
          render: (row) => <RecordId id={row.recordId} maxWidth={1200} />,
        },
        ...fields.map((name): Column<DeliveryCachedItem> => ({
          id: `field:${name}`,
          header: name,
          render: (row) => <TruncatedText text={cachedCell(row.fields[name])} mono maxWidth={200} />,
        })),
      ];
    }

    // The values fold into one column, which takes the width the id leaves. Over every type the row already names the
    // type, so the id shows only the part that tells the records apart.
    const id: Column<DeliveryCachedItem> = {
      id: "recordId",
      header: type === null ? "Record" : "OSDU id",
      render: (row) => (type === null
        ? <RecordId id={row.recordId} maxWidth={220} tailOnly />
        : <RecordId id={row.recordId} maxWidth={240} />),
    };
    const values: Column<DeliveryCachedItem> = {
      id: "values", header: "Cached values", fill: true, floor: 200, render: (row) => <FieldPairs fields={row.fields} />,
    };
    return type === null
      ? [{ id: "type", header: "Type", render: (row) => <span className="font-mono text-[12px]">{row.typeName}</span> }, id, values]
      : [id, values];
  }, [type, fields]);

  // The sheet lists the declared names first, in their order, then anything else the record carries.
  const detailNames = item === null
    ? []
    : [...fields, ...Object.keys(item.fields).filter((name) => !fields.includes(name))];

  const emptyMessage = search !== ""
    ? "No cached record holds a value, id or alias matching the search."
    : type !== null
      ? `The version being read holds no ${type} records: the search the cache flow declares for the type matched none.`
      : "No cached records yet. Refresh the cache to capture its first version.";

  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
        <SearchInput
          value={search}
          onChange={setSearch}
          placeholder="Search values, ids and aliases"
          label="Search cached records"
          className="sm:w-80"
          testId="delivery-cache-search"
        />
        <CacheVersionPicker versions={versions} value={versionFilter} onChange={setPicked} className="w-full sm:w-64" />
      </div>

      {historic !== null && (
        <div
          role="status"
          className="flex flex-wrap items-center gap-2 rounded-md border border-warning/40 bg-warning/8 px-3 py-2 text-[12.5px]"
          data-testid="delivery-cache-historic"
        >
          <History className="size-4 shrink-0 text-warning" />
          <span>
            Reading the cache as it stood at <span className="font-mono">{historic.version}</span>. Deliveries read the
            current version.
          </span>
          <Button
            variant="outline"
            size="xs"
            className="ml-auto"
            onClick={() => setPicked(CURRENT)}
            data-testid="delivery-cache-back-to-current"
          >
            Back to current
          </Button>
        </div>
      )}

      <PagedTable
        queryKey={["delivery", "cache", "items", scope, type, search, version]}
        fetchPage={(page, pageSize) => deliveryApi.cachedItems({
          page, pageSize, scope, type: type ?? undefined, search: search || undefined, version,
        })}
        columns={columns}
        rowKey={(row) => row.itemId}
        onRowClick={setItem}
        emptyMessage={emptyMessage}
        data-testid="delivery-cache-items-table"
      />

      <Sheet open={item !== null} onOpenChange={(open) => { if (!open) { setItem(null); } }}>
        <SheetContent
          className="w-full gap-0 sm:max-w-2xl"
          // Focus lands on the sheet itself rather than its first button, whose tooltip would otherwise open with it.
          onOpenAutoFocus={(event) => { event.preventDefault(); (event.currentTarget as HTMLElement).focus(); }}
          data-testid="delivery-cache-item-detail"
        >
          {item !== null && (
            <>
              <SheetHeader className="border-b border-border">
                <SheetTitle className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5">
                  <span className="font-mono">{item.typeName}</span>
                  <span className="font-mono text-[12px] font-normal text-muted-foreground">{item.entityType}</span>
                </SheetTitle>
                <SheetDescription className="flex items-center gap-1 font-mono text-[12px] text-foreground">
                  <span className="break-all">{item.recordId}</span>
                  <CopyButton iconOnly label="Copy the OSDU id" text={item.recordId} testId="delivery-cache-item-copy-id" />
                </SheetDescription>
                <div className="flex flex-wrap items-center gap-2 text-[12px] text-muted-foreground" data-testid="delivery-cache-item-version">
                  <span>
                    Version <span className="font-mono text-foreground">{item.version}</span>
                  </span>
                  {historic === null
                    ? <StatePill tone="success" label="current" icon={Pin} testId="delivery-cache-item-state" />
                    : <StatePill tone="warning" label="historic" icon={History} testId="delivery-cache-item-state" />}
                </div>
              </SheetHeader>
              <div className="flex flex-1 flex-col gap-5 overflow-y-auto p-4">
                <section className="flex flex-col gap-2">
                  <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Captured values</h3>
                  {detailNames.length === 0
                    ? <p className="text-[13px] text-muted-foreground">The record carries no captured values.</p>
                    : (
                      <div className="grid gap-3 [grid-template-columns:repeat(auto-fill,minmax(160px,1fr))]">
                        {detailNames.map((name) => (
                          <DetailPair key={name} label={name}>
                            <span className="break-all font-mono text-[12px]">{cachedCell(item.fields[name]) ?? "-"}</span>
                          </DetailPair>
                        ))}
                      </div>
                    )}
                </section>
                <RecordMappingReference item={item} names={detailNames} />
                <section className="flex flex-col gap-2">
                  <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">As captured</h3>
                  <CodeView value={JSON.stringify(item.fields, null, 2)} language="json" height={360} data-testid="delivery-cache-item-json" />
                </section>
              </div>
            </>
          )}
        </SheetContent>
      </Sheet>
    </div>
  );
}
