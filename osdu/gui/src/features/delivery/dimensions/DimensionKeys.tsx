import { useMemo, useState } from "react";
import { keepPreviousData, useInfiniteQuery } from "@tanstack/react-query";
import { ArrowRight, Ban } from "lucide-react";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { DataTable, type Column } from "@/components/DataTable";
import { FilterBar } from "@/components/FilterBar";
import { NoteRef } from "@/components/NoteRef";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { deliveryApi, type DeliveryDimensionKey } from "../../../api/delivery";
import { ProblemView } from "../TemplateSheet";
import { MoreFooter } from "./DimensionBadges";
import { DimensionValueText } from "./DimensionValueText";
import { LEFT_OUT_TEXT } from "./dimensionFormat";

const PAGE = 100;

const SEARCH_DELAY_MS = 300;

/** Which keys the list reads: every one, or only those cleaning left out of every value. */
type Scope = "all" | "leftOut";

type KeyOrder = "count" | "arrival";

/** The label read for a key, with the record it was read from on hover; a dash for a key of a dimension that reads none. */
export function LabelCell({ label, from }: { label: string | null; from: string | null }) {
  if (label === null) {
    return from === null
      ? <span className="text-muted-foreground/50">-</span>
      : (
        <RichTooltip title="No label" body={`Record ${from} holds nothing where the label is read, so the key is its own value.`} mono>
          <span className="text-[12px] italic text-muted-foreground">none</span>
        </RichTooltip>
      );
  }

  return (
    <RichTooltip title="Read from" body={from ?? "the record the key names"} mono>
      <span className="inline-flex min-w-0" data-testid="dimension-key-label">
        <DimensionValueText value={label} maxWidth={160} />
      </span>
    </RichTooltip>
  );
}

/** The search filter finding exactly the records that hold a key, to copy; a warning when no query can carry the key. */
export function KeyFilterCell({ row }: { row: DeliveryDimensionKey }) {
  if (!row.filterable || row.filter === null) {
    return (
      <RichTooltip body="No search query can carry this key, so no filter finds the records holding it, and its value's filter leaves it out.">
        <Ban className="inline size-4 text-warning" aria-label="Cannot be carried in a query" />
      </RichTooltip>
    );
  }

  return (
    <span onClick={(event) => event.stopPropagation()}>
      <TruncatedText text={row.filter} mono maxWidth={170} title="Search filter" copy copyTestId="dimension-key-copy-filter" />
    </span>
  );
}

/**
 * A dimension's keys, exactly as the index holds them (an id, for a reference): each with the label read for it from the
 * record it names, the value it was cleaned into (which opens it) or why it belongs to none, how many records hold it, the
 * search filter that finds exactly those records, a note cleaning left, and when it arrived. Read with the most records
 * first or in the order the builds found them, a page at a time, found by the key or its label; narrowed to those of no
 * value, which is where a clean step that needs a look shows.
 */
export function DimensionKeys({ dimensionId, labelled, onValue }: {
  dimensionId: number;
  /** The dimension reads a label for its keys, so the Label column says what each was read as. */
  labelled: boolean;
  onValue: (valueId: number) => void;
}) {
  const [typed, setTyped] = useState("");
  const search = useDebouncedValue(typed.trim(), SEARCH_DELAY_MS);
  const [scope, setScope] = useState<Scope>("all");
  const [order, setOrder] = useState<KeyOrder>("count");
  const [removed, setRemoved] = useState(false);
  const pages = useInfiniteQuery({
    queryKey: ["delivery", "dimensions", "keys", dimensionId, search, scope, order, removed],
    queryFn: ({ pageParam }) => deliveryApi.dimensionKeys(dimensionId, {
      search, order, removed, leftOut: scope === "leftOut" ? true : undefined, after: pageParam, limit: PAGE,
    }),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.next,
    placeholderData: keepPreviousData,
  });
  const rows = useMemo(() => pages.data?.pages.flatMap((page) => page.items), [pages.data]);

  const columns: Column<DeliveryDimensionKey>[] = [
    {
      id: "key",
      header: "Key",
      render: (row) => (
        <span className="flex items-center gap-2">
          <DimensionValueText value={row.key} search={search} strong maxWidth={250} testId="dimension-key-value" />
          {row.removedUtc !== null && <span className="rounded-sm border border-border px-1 text-[10.5px] text-muted-foreground">removed</span>}
        </span>
      ),
    },
    ...(labelled
      ? [{ id: "label", header: "Label", render: (row: DeliveryDimensionKey) => <LabelCell label={row.label} from={row.labelFrom} /> }]
      : []),
    {
      id: "value",
      header: "Value",
      fill: true,
      floor: 160,
      render: (row) => (row.valueId !== null && row.value !== null
        ? (
          <button
            type="button"
            onClick={(event) => { event.stopPropagation(); onValue(row.valueId!); }}
            className="inline-flex min-w-0 items-center gap-1.5 rounded-sm px-1 outline-none hover:bg-accent/60 focus-visible:bg-accent/60"
            data-testid="dimension-key-to-value"
          >
            <ArrowRight className="size-3.5 shrink-0 text-muted-foreground" />
            <DimensionValueText value={row.value} maxWidth={180} />
          </button>
        )
        : (
          <RichTooltip body={`It belongs to no value: ${row.leftOut === null ? "no reason kept" : LEFT_OUT_TEXT[row.leftOut]}.`}>
            <span className="text-[12px] italic text-muted-foreground" data-testid="dimension-key-left-out">
              of no value{row.leftOut === null ? "" : `: ${LEFT_OUT_TEXT[row.leftOut]}`}
            </span>
          </RichTooltip>
        )),
    },
    {
      id: "count",
      header: "Count",
      align: "right",
      render: (row) => <span className="font-mono text-[12px] tabular-nums">{row.count.toLocaleString("en-US")}</span>,
    },
    { id: "filter", header: "Filter", render: (row) => <KeyFilterCell row={row} /> },
    {
      id: "note",
      header: "Note",
      align: "center",
      render: (row) => <NoteRef note={row.note} title="What cleaning said" testId="dimension-key-note" />,
    },
    {
      id: "arrived",
      header: "Arrived",
      render: (row) => <span className="text-[12px] text-muted-foreground"><RelativeTime value={row.firstSeenUtc} absolute={false} /></span>,
    },
  ];

  return (
    <div className="flex flex-col gap-2" data-testid="dimension-keys">
      <FilterBar>
        <SearchInput
          value={typed}
          onChange={setTyped}
          placeholder={labelled ? "Find a key by itself or its label" : "Find a key"}
          label="Find a key"
          className="sm:w-80"
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
        <div className="flex items-center gap-2">
          <Switch id={`keys-removed-${dimensionId}`} checked={removed} onCheckedChange={setRemoved} data-testid="dimension-keys-removed" />
          <Label htmlFor={`keys-removed-${dimensionId}`} className="text-[13px] font-normal">Removed too</Label>
        </div>
      </FilterBar>

      {pages.isError
        ? <ProblemView error={pages.error} testId="dimension-keys-error" />
        : (
          <DataTable
            columns={columns}
            rows={rows}
            rowKey={(row) => row.keyId}
            footer={(
              <MoreFooter
                shown={rows?.length ?? 0}
                noun="key"
                hasMore={pages.hasNextPage}
                loading={pages.isFetchingNextPage}
                onMore={() => void pages.fetchNextPage()}
                testId="dimension-keys-footer"
              />
            )}
            emptyMessage={scope === "leftOut"
              ? "Cleaning gave every key a value."
              : search !== "" ? "No key, nor any label, holds the search." : "The dimension holds no key."}
            data-testid="dimension-keys-table"
          />
        )}
    </div>
  );
}
