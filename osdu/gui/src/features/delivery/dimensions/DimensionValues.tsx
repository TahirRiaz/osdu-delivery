import { useMemo, useState } from "react";
import { keepPreviousData, useInfiniteQuery } from "@tanstack/react-query";
import { Filter, TriangleAlert, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { DataTable, type Column } from "@/components/DataTable";
import { FilterBar } from "@/components/FilterBar";
import { RichTooltip } from "@/components/RichTooltip";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { deliveryApi, type DeliveryDimensionValue } from "../../../api/delivery";
import { ProblemView } from "../TemplateSheet";
import { counted } from "../assertions/assertionFormat";
import { MoreFooter } from "./DimensionBadges";
import { DimensionFilterSheet } from "./DimensionFilterSheet";
import { DimensionValueText } from "./DimensionValueText";
import { percent, type DimensionEntry } from "./dimensionFormat";

/** The values one page reads. */
const PAGE = 100;

/** How long a typed search rests before it is asked. */
const SEARCH_DELAY_MS = 300;

type ValueOrder = "records" | "value";

/**
 * A value's records as a bar and a number: the bar its share of the widest value in view, the number exact, or the sum of
 * its keys' counts marked approximate, since a record holding two of them counts twice there.
 */
function RecordsCell({ value, widest, withValue }: { value: DeliveryDimensionValue; widest: number; withValue: number | null }) {
  const share = withValue !== null && withValue > 0 && value.recordsExact ? value.records / withValue : null;
  const body = (
    <span className="flex items-center gap-2" data-testid="dimension-value-records">
      <span className="h-1.5 w-20 shrink-0 overflow-hidden rounded-full bg-muted" aria-hidden>
        <span className="block h-full rounded-full bg-chart-1" style={{ width: `${Math.max(2, (value.records / Math.max(1, widest)) * 100)}%` }} />
      </span>
      <span className="font-mono text-[12px] tabular-nums">
        {value.recordsExact ? "" : "~"}{value.records.toLocaleString("en-US")}
      </span>
      {share !== null && <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{percent(share)}</span>}
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
 * The keys most records hold of a value, each with its count, and how many more it stands for. A key is shown exactly as
 * the index holds it (an id, for a reference), with the label read for it on hover when that differs from the value.
 */
function KeysCell({ value, search }: { value: DeliveryDimensionValue; search: string }) {
  const more = value.keys - value.top.length;
  return (
    <span className="flex min-w-0 items-center gap-1.5 overflow-hidden" data-testid="dimension-value-keys">
      {value.top.map((key) => (
        <span key={key.key} className="inline-flex min-w-0 shrink items-baseline gap-1 rounded-sm bg-secondary/70 px-1.5 py-px dark:bg-input/40">
          <DimensionValueText value={key.key} search={search} maxWidth={170} />
          <span className="font-mono text-[10.5px] tabular-nums text-muted-foreground">{key.count.toLocaleString("en-US")}</span>
        </span>
      ))}
      {more > 0 && <span className="shrink-0 font-mono text-[11px] text-muted-foreground">+{more.toLocaleString("en-US")}</span>}
    </span>
  );
}

/** How a value's records are found: its filter to copy when one query holds it, the queries it takes, or that none can. */
function FilterCell({ value, onFilter }: { value: DeliveryDimensionValue; onFilter: () => void }) {
  const warning = value.unfilterable > 0 && (
    <RichTooltip
      title="Left out of the filter"
      body={`${counted(value.unfilterable, "key")} of this value cannot be carried in a search query, so its filter does not find the records holding ${value.unfilterable === 1 ? "it" : "them"}.`}
    >
      <TriangleAlert className="size-4 shrink-0 text-warning" aria-label="Some keys are left out of the filter" />
    </RichTooltip>
  );

  if (value.filterParts === 0) {
    return (
      <span className="flex items-center gap-1 text-[12px] text-muted-foreground">
        {warning}
        none
      </span>
    );
  }

  return (
    <span className="flex items-center gap-1" onClick={(event) => event.stopPropagation()}>
      {warning}
      {value.filter !== null
        ? (
          <TruncatedText text={value.filter} mono maxWidth={180} title="Search filter" copy copyTestId="dimension-value-copy-filter" />
        )
        : (
          <Button variant="ghost" size="xs" onClick={onFilter} data-testid="dimension-value-filter-parts">
            {value.filterParts} queries
          </Button>
        )}
    </span>
  );
}

/**
 * A dimension's values: each human-friendly value with a bar of the records holding it, the keys it stands for (the
 * commonest first, each exactly as the index holds it, with its count), and the search filter that finds its records. Read
 * with the most records first or in value order, a page at a time, found by the value or any of its keys. Values are picked
 * to write the search that finds the records of all of them together; a value opens in a sheet with everything the ledger
 * holds of it.
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
  // A new build rewrites the values, so its id keys every page: pages read before it are read again rather than mixed in.
  const built = entry.dimension.current?.buildId ?? null;
  const pages = useInfiniteQuery({
    queryKey: ["delivery", "dimensions", "values", dimensionId, built, search, order, removed],
    queryFn: ({ pageParam }) => deliveryApi.dimensionValues(dimensionId, { search, order, removed, after: pageParam, limit: PAGE }),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.next,
    placeholderData: keepPreviousData,
  });
  const rows = useMemo(() => pages.data?.pages.flatMap((page) => page.items), [pages.data]);
  const widest = useMemo(() => Math.max(1, ...(rows ?? []).map((row) => row.records)), [rows]);
  const withValue = entry.dimension.current?.withValue ?? null;
  const labelled = entry.dimension.label.length > 0;

  const columns: Column<DeliveryDimensionValue>[] = [
    {
      id: "value",
      header: "Value",
      render: (row) => (
        <span className="flex items-center gap-2">
          <DimensionValueText value={row.value} search={search} strong maxWidth={220} testId="dimension-value-value" />
          {row.removedUtc !== null && (
            <RichTooltip body="No build finds it any more; it keeps its id, so it is the same value if a later build finds it again.">
              <span className="rounded-sm border border-border px-1 text-[10.5px] text-muted-foreground">removed</span>
            </RichTooltip>
          )}
        </span>
      ),
    },
    {
      id: "records",
      header: "Records",
      render: (row) => <RecordsCell value={row} widest={widest} withValue={withValue} />,
    },
    {
      id: "keys",
      header: labelled ? "Keys (ids)" : "Keys",
      fill: true,
      floor: 220,
      render: (row) => <KeysCell value={row} search={search} />,
    },
    {
      id: "filter",
      header: "Filter",
      render: (row) => <FilterCell value={row} onFilter={() => setFiltering([row.valueId])} />,
    },
  ];

  const toolbar = picked.size > 0 && (
    <div className="flex flex-wrap items-center gap-2 border-b border-border bg-primary/5 px-3 py-1.5 text-[12.5px]" data-testid="dimension-values-picked">
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
          placeholder="Find a value by itself or any key"
          label="Find a value"
          className="sm:w-96"
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
          <ToggleGroupItem value="value" className="h-8 px-2.5 text-[13px]" data-testid="dimension-values-order-value">By value</ToggleGroupItem>
        </ToggleGroup>
        <div className="flex items-center gap-2">
          <Switch id={`removed-${dimensionId}`} checked={removed} onCheckedChange={setRemoved} data-testid="dimension-values-removed" />
          <Label htmlFor={`removed-${dimensionId}`} className="text-[13px] font-normal">Removed too</Label>
        </div>
      </FilterBar>

      {pages.isError
        ? <ProblemView error={pages.error} testId="dimension-values-error" />
        : (
          <DataTable
            columns={columns}
            rows={rows}
            rowKey={(row) => row.valueId}
            onRowClick={(row) => onValue(row.valueId)}
            selection={{ selected: picked, onChange: setPicked }}
            toolbar={toolbar || undefined}
            footer={(
              <MoreFooter
                shown={rows?.length ?? 0}
                noun="value"
                hasMore={pages.hasNextPage}
                loading={pages.isFetchingNextPage}
                onMore={() => void pages.fetchNextPage()}
                testId="dimension-values-footer"
              />
            )}
            emptyMessage={search !== "" ? "No value, nor any of its keys, holds the search." : "The dimension holds no value: its last build found no key."}
            data-testid="dimension-values-table"
          />
        )}

      <DimensionFilterSheet dimensionId={dimensionId} valueIds={filtering} onClose={() => setFiltering(null)} />
    </div>
  );
}
