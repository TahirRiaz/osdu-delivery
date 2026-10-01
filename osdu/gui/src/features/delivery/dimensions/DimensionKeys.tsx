import { useMemo, useState } from "react";
import { keepPreviousData, useInfiniteQuery } from "@tanstack/react-query";
import { ArrowRight, Ban } from "lucide-react";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { CopyButton } from "@/components/CopyButton";
import { DataTable, type Column } from "@/components/DataTable";
import { FilterBar } from "@/components/FilterBar";
import { NoteRef } from "@/components/NoteRef";
import { RichTooltip } from "@/components/RichTooltip";
import { SearchInput } from "@/components/SearchInput";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { deliveryApi, type DeliveryDimensionAttributeSpec, type DeliveryDimensionKey, type DimensionAttributeCondition } from "../../../api/delivery";
import { ProblemView } from "../TemplateSheet";
import { DimensionAttributeFilter } from "./DimensionAttributeFilter";
import { attributeColumnId, keyAttributeColumns } from "./dimensionColumns";
import { DimensionGrid, GridFooter, GridViewMenu } from "./DimensionGrid";
import { useHiddenColumns, type GridColumnChoice } from "./dimensionGridState";
import { DimensionValueText } from "./DimensionValueText";
import { LEFT_OUT_TEXT, keyTail } from "./dimensionFormat";

const PAGE = 100;

const SEARCH_DELAY_MS = 300;

/** Which keys the list reads: every one, or only those cleaning left out of every value. */
type Scope = "all" | "leftOut";

type KeyOrder = "count" | "arrival";

/**
 * A key exactly as the index holds it, with the end that tells it from its neighbours kept in view when it has to be cut
 * short, and what cleaning said of it beside it when it said anything.
 */
export function KeyCell({ row, search = "" }: { row: DeliveryDimensionKey; search?: string }) {
  return (
    <span className="flex min-w-0 items-center gap-2">
      <DimensionValueText value={row.key} search={search} strong maxWidth="100%" tail={keyTail(row.key)} testId="dimension-key-value" />
      {row.removedUtc !== null && <span className="shrink-0 rounded-sm border border-border px-1 text-[10.5px] text-muted-foreground">removed</span>}
      {row.note !== null && row.note !== "" && <span className="shrink-0"><NoteRef note={row.note} title="What cleaning said" testId="dimension-key-note" /></span>}
    </span>
  );
}

/**
 * The value a key belongs to, which opens it, with the label read for the key and the record it was read from on hover; or
 * why the key belongs to none.
 */
export function KeyValueCell({ row, faint, onValue }: { row: DeliveryDimensionKey; faint: string | null; onValue: (valueId: number) => void }) {
  if (row.valueId === null || row.value === null) {
    return (
      <RichTooltip body={`It belongs to no value: ${row.leftOut === null ? "no reason kept" : LEFT_OUT_TEXT[row.leftOut]}.`}>
        <span className="text-[12px] italic text-muted-foreground" data-testid="dimension-key-left-out">
          of no value{row.leftOut === null ? "" : `: ${LEFT_OUT_TEXT[row.leftOut]}`}
        </span>
      </RichTooltip>
    );
  }

  const valueId = row.valueId;
  const read = row.label === null
    ? row.labelFrom === null ? null : `No label was read: record ${row.labelFrom} holds nothing where the label is read.`
    : `Label "${row.label}"${row.labelFrom === null ? "" : `, read from ${row.labelFrom}`}.`;
  const button = (
    <button
      type="button"
      onClick={(event) => { event.stopPropagation(); onValue(valueId); }}
      className="inline-flex min-w-0 max-w-full items-center gap-1.5 rounded-sm px-1 outline-none hover:bg-accent/60 focus-visible:bg-accent/60"
      data-testid="dimension-key-to-value"
    >
      <ArrowRight className="size-3.5 shrink-0 text-muted-foreground" />
      <DimensionValueText value={row.value} maxWidth={180} className={row.value === faint ? "text-muted-foreground/70" : undefined} />
    </button>
  );

  return read === null ? button : <RichTooltip title="Its value" body={read}>{button}</RichTooltip>;
}

/** The search finding exactly the records that hold a key, to copy; a warning when no query can carry the key. */
export function KeySearchCell({ row }: { row: DeliveryDimensionKey }) {
  if (!row.filterable || row.filter === null) {
    return (
      <RichTooltip body="No search query can carry this key, so no search finds the records holding it, and its value's search leaves it out.">
        <Ban className="inline size-4 text-warning" aria-label="Cannot be carried in a query" />
      </RichTooltip>
    );
  }

  return (
    <span className="inline-flex" onClick={(event) => event.stopPropagation()}>
      <CopyButton iconOnly label="Copy the search that finds its records" text={row.filter} testId="dimension-key-copy-filter" />
    </span>
  );
}

/**
 * A dimension's keys as a grid, exactly as the index holds them (an id, for a reference): each with the value it belongs
 * to (which opens it; the label read for the key is on hover) or why it belongs to none, how many records hold it, a
 * column per attribute, and the search that finds exactly its records, to copy. Read with the most records first or in the
 * order the builds found them, more as the grid is scrolled, found by the key or its label and narrowed by attributes or to
 * those of no value, which is where a clean step that needs a look shows. The key and its value are headed as the
 * dimension calls them. Columns are left out under View.
 */
export function DimensionKeys({ dimensionId, keyColumn, valueColumn, labelled, unlabelled, attributes, total, onValue }: {
  dimensionId: number;
  /** What the dimension calls its key and its value: the two columns' headings. */
  keyColumn: string;
  valueColumn: string;
  /** The dimension reads a label for its keys, so a key is found by its label too. */
  labelled: boolean;
  /** The value the dimension gives what it could not read, drawn faint; null when it gives none. */
  unlabelled: string | null;
  /** The attributes the dimension reads of its keys, a column and a filter each. */
  attributes: DeliveryDimensionAttributeSpec[];
  /** The keys the dimension holds now. */
  total: number;
  onValue: (valueId: number) => void;
}) {
  const [typed, setTyped] = useState("");
  const search = useDebouncedValue(typed.trim(), SEARCH_DELAY_MS);
  const [scope, setScope] = useState<Scope>("all");
  const [order, setOrder] = useState<KeyOrder>("count");
  const [removed, setRemoved] = useState(false);
  const [conditions, setConditions] = useState<DimensionAttributeCondition[]>([]);
  const [hidden, toggleColumn] = useHiddenColumns(`osdu.dimensions.keys.columns.${dimensionId}`);
  const pages = useInfiniteQuery({
    queryKey: ["delivery", "dimensions", "keys", dimensionId, search, scope, order, removed, conditions],
    queryFn: ({ pageParam }) => deliveryApi.dimensionKeys(dimensionId, {
      search, order, removed, leftOut: scope === "leftOut" ? true : undefined, after: pageParam, limit: PAGE, attributes: conditions,
    }),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.next,
    placeholderData: keepPreviousData,
  });
  const rows = useMemo(() => pages.data?.pages.flatMap((page) => page.items), [pages.data]);
  const narrowed = search !== "" || removed || scope !== "all" || conditions.length > 0;
  const more = () => {
    if (pages.hasNextPage && !pages.isFetchingNextPage) {
      void pages.fetchNextPage();
    }
  };

  const choices: GridColumnChoice[] = [
    { id: "value", label: valueColumn },
    ...attributes.map((attribute) => ({ id: attributeColumnId(attribute.name), label: attribute.name })),
    { id: "search", label: "Search" },
  ];
  const all: Column<DeliveryDimensionKey>[] = [
    { id: "key", header: keyColumn, fill: true, floor: 200, render: (row) => <KeyCell row={row} search={search} /> },
    { id: "value", header: valueColumn, render: (row) => <KeyValueCell row={row} faint={unlabelled} onValue={onValue} /> },
    {
      id: "count",
      header: "Records",
      align: "right",
      render: (row) => <span className="font-mono text-[12px] tabular-nums">{row.count.toLocaleString("en-US")}</span>,
    },
    ...keyAttributeColumns(attributes, unlabelled),
    { id: "search", header: "Search", align: "right", render: (row) => <KeySearchCell row={row} /> },
  ];
  const columns = all.filter((column) => !hidden.has(column.id));

  return (
    <div className="flex flex-col gap-2" data-testid="dimension-keys">
      <FilterBar>
        <SearchInput
          value={typed}
          onChange={setTyped}
          placeholder={labelled ? "Find a key by itself or its label" : "Find a key"}
          label="Find a key"
          className="sm:w-72"
          testId="dimension-keys-search"
        />
        <ToggleGroup
          type="single"
          variant="outline"
          size="sm"
          value={scope}
          onValueChange={(value) => { if (value !== "") { setScope(value as Scope); } }}
          aria-label="Which keys"
        >
          <ToggleGroupItem value="all" className="h-8 px-2.5 text-[13px]" data-testid="dimension-keys-all">Every key</ToggleGroupItem>
          <ToggleGroupItem value="leftOut" className="h-8 px-2.5 text-[13px]" data-testid="dimension-keys-left-out">Of no value</ToggleGroupItem>
        </ToggleGroup>
        <ToggleGroup
          type="single"
          variant="outline"
          size="sm"
          value={order}
          onValueChange={(value) => { if (value !== "") { setOrder(value as KeyOrder); } }}
          aria-label="Order of the keys"
        >
          <ToggleGroupItem value="count" className="h-8 px-2.5 text-[13px]" data-testid="dimension-keys-order-count">Most records</ToggleGroupItem>
          <ToggleGroupItem value="arrival" className="h-8 px-2.5 text-[13px]" data-testid="dimension-keys-order-arrival">As found</ToggleGroupItem>
        </ToggleGroup>
        <DimensionAttributeFilter dimensionId={dimensionId} attributes={attributes} conditions={conditions} onChange={setConditions} testId="dimension-keys-attributes" />
        <div className="sm:ml-auto">
          <GridViewMenu columns={choices} hidden={hidden} onToggle={toggleColumn} removed={removed} onRemoved={setRemoved} testId="dimension-keys-view" />
        </div>
      </FilterBar>

      {pages.isError
        ? <ProblemView error={pages.error} testId="dimension-keys-error" />
        : (
          <DimensionGrid onNearEnd={more}>
            <DataTable
              columns={columns}
              rows={rows}
              rowKey={(row) => row.keyId}
              footer={(
                <GridFooter
                  shown={rows?.length ?? 0}
                  total={narrowed ? null : total}
                  noun="key"
                  hasMore={pages.hasNextPage}
                  loading={pages.isFetchingNextPage}
                  onMore={more}
                  testId="dimension-keys-footer"
                />
              )}
              emptyMessage={scope === "leftOut"
                ? "Cleaning gave every key a value."
                : conditions.length > 0
                  ? "No key holds every attribute picked."
                  : search !== "" ? "No key, nor any label, holds the search." : "The dimension holds no key."}
              data-testid="dimension-keys-table"
            />
          </DimensionGrid>
        )}
    </div>
  );
}
