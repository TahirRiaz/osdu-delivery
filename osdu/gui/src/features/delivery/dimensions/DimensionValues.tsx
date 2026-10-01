import { useMemo, useState } from "react";
import { keepPreviousData, useInfiniteQuery } from "@tanstack/react-query";
import { Filter, TriangleAlert, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { CopyButton } from "@/components/CopyButton";
import { DataTable, type Column } from "@/components/DataTable";
import { FilterBar } from "@/components/FilterBar";
import { RichTooltip } from "@/components/RichTooltip";
import { SearchInput } from "@/components/SearchInput";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { deliveryApi, type DeliveryDimensionValue, type DimensionAttributeCondition } from "../../../api/delivery";
import { ProblemView } from "../TemplateSheet";
import { counted } from "../assertions/assertionFormat";
import { DimensionAttributeFilter, ValueAttributeCell } from "./DimensionAttributeFilter";
import { attributeColumnId } from "./dimensionColumns";
import { DimensionFilterSheet } from "./DimensionFilterSheet";
import { DimensionGrid, GridFooter, GridViewMenu } from "./DimensionGrid";
import { useHiddenColumns, type GridColumnChoice } from "./dimensionGridState";
import { DimensionValueText } from "./DimensionValueText";
import { keyTail, type DimensionEntry } from "./dimensionFormat";

/** The values one page reads. */
const PAGE = 100;

/** How long a typed search rests before it is asked. */
const SEARCH_DELAY_MS = 300;

type ValueOrder = "records" | "value";

/**
 * A value's records as a number beside a bar of its share of the widest value in view: exact, or the sum of its keys'
 * counts marked approximate, since a record holding two of them counts twice there.
 */
function RecordsCell({ value, widest }: { value: DeliveryDimensionValue; widest: number }) {
  const body = (
    <span className="flex items-center justify-end gap-2" data-testid="dimension-value-records">
      <span className="h-1.5 w-12 shrink-0 overflow-hidden rounded-full bg-muted" aria-hidden>
        <span className="block h-full rounded-full bg-chart-1" style={{ width: `${Math.max(3, (value.records / Math.max(1, widest)) * 100)}%` }} />
      </span>
      <span className="min-w-[3.5rem] text-right font-mono text-[12px] tabular-nums">
        {value.recordsExact ? "" : "~"}{value.records.toLocaleString("en-US")}
      </span>
    </span>
  );

  return value.recordsExact
    ? body
    : (
      <RichTooltip
        title="Approximate"
        body="The sum of its keys' counts: a record holding more than one of them counts once for each. A dimension that sets countRecords: true counts each value's records exactly, with one search per value."
      >
        {body}
      </RichTooltip>
    );
}

/**
 * The keys a value stands for. Where keys are ids read for a name, the name is the value already, so the cell says how
 * many there are, with the ids on hover; where keys are spellings cleaned into the value, they are what the reader came
 * for, so the commonest are drawn with their counts.
 */
function KeysCell({ value, search, labelled }: { value: DeliveryDimensionValue; search: string; labelled: boolean }) {
  const more = value.keys - value.top.length;
  if (labelled) {
    return (
      <RichTooltip
        title={counted(value.keys, "key")}
        body={`${value.top.map((key) => `${key.key}  ${key.count.toLocaleString("en-US")}`).join("\n")}${more > 0 ? `\nand ${more.toLocaleString("en-US")} more` : ""}`}
        mono
      >
        <span className="font-mono text-[12px] tabular-nums text-muted-foreground" data-testid="dimension-value-keys">{value.keys.toLocaleString("en-US")}</span>
      </RichTooltip>
    );
  }

  return (
    <span className="flex min-w-0 items-center gap-1.5 overflow-hidden" data-testid="dimension-value-keys">
      {value.top.map((key) => (
        <span key={key.key} className="inline-flex min-w-0 shrink items-baseline gap-1 rounded-sm bg-secondary/70 px-1.5 py-px dark:bg-input/40">
          <DimensionValueText value={key.key} search={search} maxWidth={170} tail={keyTail(key.key)} />
          <span className="font-mono text-[10.5px] tabular-nums text-muted-foreground">{key.count.toLocaleString("en-US")}</span>
        </span>
      ))}
      {more > 0 && <span className="shrink-0 font-mono text-[11px] text-muted-foreground">+{more.toLocaleString("en-US")}</span>}
    </span>
  );
}

/** How a value's records are found: its search to copy when one query holds it, the queries it takes, or that none can. */
function SearchCell({ value, onFilter }: { value: DeliveryDimensionValue; onFilter: () => void }) {
  const warning = value.unfilterable > 0 && (
    <RichTooltip
      title="Left out of the search"
      body={`${counted(value.unfilterable, "key")} of this value cannot be carried in a search query, so its search does not find the records holding ${value.unfilterable === 1 ? "it" : "them"}.`}
    >
      <TriangleAlert className="size-4 shrink-0 text-warning" aria-label="Some keys are left out of the search" />
    </RichTooltip>
  );

  if (value.filterParts === 0) {
    return (
      <span className="flex items-center justify-end gap-1 text-[12px] text-muted-foreground">
        {warning}
        none
      </span>
    );
  }

  return (
    <span className="flex items-center justify-end gap-1" onClick={(event) => event.stopPropagation()}>
      {warning}
      {value.filter !== null
        ? <CopyButton iconOnly label="Copy the search that finds its records" text={value.filter} testId="dimension-value-copy-filter" />
        : (
          <Button variant="ghost" size="xs" onClick={onFilter} data-testid="dimension-value-filter-parts">
            {value.filterParts} queries
          </Button>
        )}
    </span>
  );
}

/**
 * A dimension's values as a grid: each human-friendly value with the records holding it, the keys it stands for, a column
 * per attribute its keys hold, and the search that finds its records, to copy. Read with the most records first or in
 * value order, more as the grid is scrolled, found by the value or any of its keys, and narrowed by attributes. The
 * columns a reader has no use for are left out under View. Values are picked to write the search that finds the records
 * of all of them together; a value opens in a sheet with everything the ledger holds of it.
 */
export function DimensionValues({ entry, dimensionId, onValue }: {
  entry: DimensionEntry;
  dimensionId: number;
  onValue: (valueId: number) => void;
}) {
  const [typed, setTyped] = useState("");
  const search = useDebouncedValue(typed.trim(), SEARCH_DELAY_MS);
  const [order, setOrder] = useState<ValueOrder>("records");
  const [removed, setRemoved] = useState(false);
  const [picked, setPicked] = useState<ReadonlySet<string>>(new Set());
  const [filtering, setFiltering] = useState<number[] | null>(null);
  const [conditions, setConditions] = useState<DimensionAttributeCondition[]>([]);
  const [hidden, toggleColumn] = useHiddenColumns(`osdu.dimensions.values.columns.${dimensionId}`);
  const { dimension } = entry;
  const attributes = dimension.attributes;
  // A new build rewrites the values, so its id keys every page: pages read before it are read again rather than mixed in.
  const built = dimension.current?.buildId ?? null;
  const pages = useInfiniteQuery({
    queryKey: ["delivery", "dimensions", "values", dimensionId, built, search, order, removed, conditions],
    queryFn: ({ pageParam }) => deliveryApi.dimensionValues(dimensionId, { search, order, removed, after: pageParam, limit: PAGE, attributes: conditions }),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.next,
    placeholderData: keepPreviousData,
  });
  const rows = useMemo(() => pages.data?.pages.flatMap((page) => page.items), [pages.data]);
  const widest = useMemo(() => Math.max(1, ...(rows ?? []).map((row) => row.records)), [rows]);
  const labelled = dimension.label.length > 0;
  const narrowed = search !== "" || removed || conditions.length > 0;
  const more = () => {
    if (pages.hasNextPage && !pages.isFetchingNextPage) {
      void pages.fetchNextPage();
    }
  };

  const choices: GridColumnChoice[] = [
    { id: "keys", label: "Keys" },
    ...attributes.map((attribute) => ({ id: attributeColumnId(attribute.name), label: attribute.name })),
    { id: "search", label: "Search" },
  ];
  const all: Column<DeliveryDimensionValue>[] = [
    {
      id: "value",
      header: "Value",
      // Where the keys are spellings, they take the room that is left; where they are ids, the value does.
      fill: labelled,
      floor: 180,
      render: (row) => (
        <span className="flex min-w-0 items-center gap-2">
          <DimensionValueText
            value={row.value}
            search={search}
            strong
            maxWidth={labelled ? "100%" : 240}
            className={row.value === dimension.unlabelled ? "text-muted-foreground" : undefined}
            testId="dimension-value-value"
          />
          {row.removedUtc !== null && (
            <RichTooltip body="No build finds it any more; it keeps its id, so it is the same value if a later build finds it again.">
              <span className="shrink-0 rounded-sm border border-border px-1 text-[10.5px] text-muted-foreground">removed</span>
            </RichTooltip>
          )}
        </span>
      ),
    },
    { id: "records", header: "Records", align: "right", render: (row) => <RecordsCell value={row} widest={widest} /> },
    {
      id: "keys",
      header: "Keys",
      align: labelled ? "right" : "left",
      fill: !labelled,
      floor: 220,
      render: (row) => <KeysCell value={row} search={search} labelled={labelled} />,
    },
    ...attributes.map((attribute): Column<DeliveryDimensionValue> => ({
      id: attributeColumnId(attribute.name),
      header: attribute.name,
      render: (row) => (
        <ValueAttributeCell name={attribute.name} values={row.attributes.filter((a) => a.name === attribute.name).map((a) => a.value)} faint={dimension.unlabelled} />
      ),
    })),
    { id: "search", header: "Search", align: "right", render: (row) => <SearchCell value={row} onFilter={() => setFiltering([row.valueId])} /> },
  ];
  const columns = all.filter((column) => !hidden.has(column.id));

  const toolbar = picked.size > 0 && (
    <div className="flex flex-wrap items-center gap-2 border-b border-border bg-primary/5 px-3 py-1 text-[12.5px]" data-testid="dimension-values-picked">
      <span className="font-medium">{counted(picked.size, "value")} picked</span>
      <Button size="xs" onClick={() => setFiltering([...picked].map(Number))} data-testid="dimension-values-write-filter">
        <Filter />
        Write their search
      </Button>
      <Button size="xs" variant="ghost" onClick={() => setPicked(new Set())} data-testid="dimension-values-clear">
        <X />
        Clear
      </Button>
    </div>
  );

  return (
    <div className="flex flex-col gap-2" data-testid="dimension-values">
      <FilterBar>
        <SearchInput
          value={typed}
          onChange={setTyped}
          placeholder={labelled ? "Find a value, or the id of a key" : "Find a value by itself or any key"}
          label="Find a value"
          className="sm:w-72"
          testId="dimension-values-search"
        />
        <ToggleGroup
          type="single"
          variant="outline"
          size="sm"
          value={order}
          onValueChange={(value) => { if (value !== "") { setOrder(value as ValueOrder); } }}
          aria-label="Order of the values"
        >
          <ToggleGroupItem value="records" className="h-8 px-2.5 text-[13px]" data-testid="dimension-values-order-records">Most records</ToggleGroupItem>
          <ToggleGroupItem value="value" className="h-8 px-2.5 text-[13px]" data-testid="dimension-values-order-value">A to Z</ToggleGroupItem>
        </ToggleGroup>
        <DimensionAttributeFilter dimensionId={dimensionId} attributes={attributes} conditions={conditions} onChange={setConditions} testId="dimension-values-attributes" />
        <div className="sm:ml-auto">
          <GridViewMenu columns={choices} hidden={hidden} onToggle={toggleColumn} removed={removed} onRemoved={setRemoved} testId="dimension-values-view" />
        </div>
      </FilterBar>

      {pages.isError
        ? <ProblemView error={pages.error} testId="dimension-values-error" />
        : (
          <DimensionGrid onNearEnd={more}>
            <DataTable
              columns={columns}
              rows={rows}
              rowKey={(row) => row.valueId}
              onRowClick={(row) => onValue(row.valueId)}
              selection={{ selected: picked, onChange: setPicked }}
              toolbar={toolbar || undefined}
              footer={(
                <GridFooter
                  shown={rows?.length ?? 0}
                  total={narrowed ? null : dimension.values}
                  noun="value"
                  hasMore={pages.hasNextPage}
                  loading={pages.isFetchingNextPage}
                  onMore={more}
                  testId="dimension-values-footer"
                />
              )}
              emptyMessage={conditions.length > 0
                ? "No value has a key holding every attribute picked."
                : search !== "" ? "No value, nor any of its keys, holds the search." : "The dimension holds no value: its last build found no key."}
              data-testid="dimension-values-table"
            />
          </DimensionGrid>
        )}

      <DimensionFilterSheet dimensionId={dimensionId} valueIds={filtering} onClose={() => setFiltering(null)} />
    </div>
  );
}
