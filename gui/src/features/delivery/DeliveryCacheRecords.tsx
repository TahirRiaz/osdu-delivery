import { useMemo, useState } from "react";
import { format } from "date-fns";
import { History, Pin } from "lucide-react";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { deliveryApi, type DeliveryCacheVersion, type DeliveryCachedItem } from "../../api/delivery";
import { CodeView } from "../../components/CodeView";
import { CopyButton } from "../../components/CopyButton";
import { DetailPair } from "../../components/DetailPair";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RichTooltip } from "../../components/RichTooltip";
import { StatePill } from "../../components/StatusBadge";
import { TruncatedText, useClipped } from "../../components/TruncatedText";
import { parseUtc } from "../../lib/time";
import { cachedCell, splitRecordId } from "./cacheFormat";

/** The picker's value for "whichever version is current", which is what the page opens on. */
export const CURRENT = "current";

/**
 * An OSDU record id with its repeated part stepped back: the partition and entity type in muted text, the part that
 * tells records apart in full. The prefix gives way first when the cell runs out of room, so a column of ids stays
 * readable by its tails, and the whole id is revealed on hover only when something is actually hidden.
 */
export function RecordId({ id, maxWidth = 360 }: { id: string; maxWidth?: number }) {
  const { prefix, tail } = splitRecordId(id);
  const [prefixRef, prefixClipped] = useClipped(prefix);
  const [tailRef, tailClipped] = useClipped(tail);

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

/** A version as the picker lists it, once per label: the same capture across repositories is one choice. */
interface VersionOption {
  version: string;
  capturedUtc: string | null;
  current: boolean;
  carried: boolean;
}

function versionOptions(versions: DeliveryCacheVersion[]): VersionOption[] {
  const byLabel = new Map<string, VersionOption>();
  for (const version of versions) {
    const known = byLabel.get(version.version);
    if (known === undefined) {
      byLabel.set(version.version, {
        version: version.version, capturedUtc: version.capturedUtc, current: version.current, carried: version.carried,
      });
      continue;
    }

    known.current = known.current || version.current;
    known.carried = known.carried || version.carried;
    known.capturedUtc = known.capturedUtc ?? version.capturedUtc;
  }

  return [...byLabel.values()];
}

/** The version picker: the current version by default, or any carried version by label, with when it was captured. */
export function CacheVersionPicker({ versions, value, onChange, className }: {
  versions: DeliveryCacheVersion[];
  value: string;
  onChange: (version: string) => void;
  className?: string;
}) {
  const options = useMemo(() => versionOptions(versions), [versions]);
  return (
    <Select value={value} onValueChange={onChange} disabled={options.length === 0}>
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
        {options.map((option) => (
          <SelectItem key={option.version} value={option.version} disabled={!option.carried}>
            <span className="font-mono text-[12px]">{option.version}</span>
            <span className="text-[11px] text-muted-foreground">
              {option.capturedUtc ? format(parseUtc(option.capturedUtc), "MMM d, HH:mm") : "never captured"}
            </span>
            {option.current && <span className="text-[11px] font-medium text-primary">current</span>}
            {!option.carried && <span className="text-[11px] text-muted-foreground">not carried</span>}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}

/**
 * The cached records at one snapshot version: the current one by default, or any carried version the page's picker
 * names. With a type in scope the table has one column per captured name, so a unit's code, name and id read down
 * the page; over every type the values fold into one column, since the names differ from type to type.
 */
export function DeliveryCacheRecords({ repoId, type, fields, search, version, historic, onBackToCurrent }: {
  repoId: string | undefined;
  type: string | null;
  /** The names the type in scope caches its paths under, in declaration order; empty without a type in scope. */
  fields: string[];
  /** The page's search term over every cached value, id and alias. */
  search: string;
  /** The version to read; undefined for the current one. */
  version: string | undefined;
  /** The version being read when it is not the current one. */
  historic: DeliveryCacheVersion | null;
  onBackToCurrent: () => void;
}) {
  const [item, setItem] = useState<DeliveryCachedItem | null>(null);

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

    // The values fold into one column, which takes the width the id leaves.
    const id: Column<DeliveryCachedItem> = {
      id: "recordId",
      header: "OSDU id",
      render: (row) => <RecordId id={row.recordId} maxWidth={type === null ? 260 : 240} />,
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
      ? `The version being read holds no ${type} records. Running the retrieval flow that declares the type captures them.`
      : "No cached records yet. Running the retrieval flow that declares the cache captures them, and syncing the repository lists them here.";

  return (
    <div className="flex flex-col gap-2">
      {historic !== null && (
        <div
          role="status"
          className="flex flex-wrap items-center gap-2 rounded-md border border-warning/40 bg-warning/8 px-3 py-2 text-[12.5px]"
          data-testid="delivery-cache-historic"
        >
          <History className="size-4 shrink-0 text-warning" />
          <span>
            Reading the cache as it stood at <span className="font-mono">{historic.version}</span>. Deliveries resolve
            against the current version, so nothing here is what a render would read today.
          </span>
          <Button
            variant="outline"
            size="xs"
            className="ml-auto"
            onClick={onBackToCurrent}
            data-testid="delivery-cache-back-to-current"
          >
            Back to current
          </Button>
        </div>
      )}

      <PagedTable
        queryKey={["delivery", "cache", "items", repoId, type, search, version]}
        fetchPage={(page, pageSize) => deliveryApi.cachedItems({
          page, pageSize, repoId, type: type ?? undefined, search: search || undefined, version,
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
