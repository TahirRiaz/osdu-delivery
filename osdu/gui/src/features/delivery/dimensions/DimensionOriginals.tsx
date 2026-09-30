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
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { deliveryApi, type DeliveryDimensionValue } from "../../../api/delivery";
import { ProblemView } from "../TemplateSheet";
import { MoreFooter } from "./DimensionBadges";
import { DimensionValueText } from "./DimensionValueText";
import { LEFT_OUT_TEXT } from "./dimensionFormat";

const PAGE = 100;

const SEARCH_DELAY_MS = 300;

/** Which originals the list reads: every one, or only those cleaning left out of every member. */
type Scope = "all" | "leftOut";

type OriginalOrder = "count" | "arrival";

/**
 * A dimension's originals, exactly as the index holds them: each with the member it was cleaned into (which opens it) or
 * why it belongs to none, how many records hold it, a note cleaning left, whether a query can carry it, and when it
 * arrived. Read with the most records first or in the order the builds found them, a page at a time; narrowed to those
 * under no member, which is where a clean step that needs a look shows.
 */
export function DimensionOriginals({ dimensionId, onMember }: { dimensionId: number; onMember: (memberId: number) => void }) {
  const [typed, setTyped] = useState("");
  const search = useDebouncedValue(typed.trim(), SEARCH_DELAY_MS);
  const [scope, setScope] = useState<Scope>("all");
  const [order, setOrder] = useState<OriginalOrder>("count");
  const [removed, setRemoved] = useState(false);
  const pages = useInfiniteQuery({
    queryKey: ["delivery", "dimensions", "values", dimensionId, search, scope, order, removed],
    queryFn: ({ pageParam }) => deliveryApi.dimensionValues(dimensionId, {
      search, order, removed, leftOut: scope === "leftOut" ? true : undefined, after: pageParam, limit: PAGE,
    }),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.next,
    placeholderData: keepPreviousData,
  });
  const rows = useMemo(() => pages.data?.pages.flatMap((page) => page.items), [pages.data]);

  const columns: Column<DeliveryDimensionValue>[] = [
    {
      id: "original",
      header: "Original",
      render: (row) => (
        <span className="flex items-center gap-2">
          <DimensionValueText value={row.original} search={search} strong maxWidth={280} testId="dimension-original-value" />
          {row.removedUtc !== null && <span className="rounded-sm border border-border px-1 text-[10.5px] text-muted-foreground">removed</span>}
        </span>
      ),
    },
    {
      id: "member",
      header: "Member",
      fill: true,
      floor: 180,
      render: (row) => (row.memberId !== null && row.member !== null
        ? (
          <button
            type="button"
            onClick={(event) => { event.stopPropagation(); onMember(row.memberId!); }}
            className="inline-flex min-w-0 items-center gap-1.5 rounded-sm px-1 outline-none hover:bg-accent/60 focus-visible:bg-accent/60"
            data-testid="dimension-original-member"
          >
            <ArrowRight className="size-3.5 shrink-0 text-muted-foreground" />
            <DimensionValueText value={row.member} maxWidth={240} />
          </button>
        )
        : (
          <RichTooltip body={`It belongs to no member: ${row.leftOut === null ? "no reason kept" : LEFT_OUT_TEXT[row.leftOut]}.`}>
            <span className="text-[12px] italic text-muted-foreground" data-testid="dimension-original-left-out">
              under no member{row.leftOut === null ? "" : `: ${LEFT_OUT_TEXT[row.leftOut]}`}
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
    {
      id: "note",
      header: "Note",
      align: "center",
      render: (row) => <NoteRef note={row.note} title="What cleaning said" testId="dimension-original-note" />,
    },
    {
      id: "filterable",
      header: "Query",
      align: "center",
      render: (row) => (row.filterable
        ? <span className="text-muted-foreground/50">-</span>
        : (
          <RichTooltip body="No search query can carry this original, so its member's filter does not find the records holding it.">
            <Ban className="inline size-4 text-warning" aria-label="Cannot be carried in a query" />
          </RichTooltip>
        )),
    },
    {
      id: "arrived",
      header: "Arrived",
      render: (row) => <span className="text-[12px] text-muted-foreground"><RelativeTime value={row.firstSeenUtc} absolute={false} /></span>,
    },
  ];

  return (
    <div className="flex flex-col gap-2" data-testid="dimension-originals">
      <FilterBar>
        <SearchInput value={typed} onChange={setTyped} placeholder="Find an original" label="Find an original" className="sm:w-80" testId="dimension-originals-search" />
        <ToggleGroup
          type="single"
          variant="outline"
          size="sm"
          value={scope}
          onValueChange={(value) => { if (value !== "") { setScope(value as Scope); } }}
          aria-label="Which originals"
        >
          <ToggleGroupItem value="all" className="h-8 px-2.5 text-[13px]" data-testid="dimension-originals-all">Every original</ToggleGroupItem>
          <ToggleGroupItem value="leftOut" className="h-8 px-2.5 text-[13px]" data-testid="dimension-originals-left-out">Under no member</ToggleGroupItem>
        </ToggleGroup>
        <ToggleGroup
          type="single"
          variant="outline"
          size="sm"
          value={order}
          onValueChange={(value) => { if (value !== "") { setOrder(value as OriginalOrder); } }}
          aria-label="Order of the originals"
        >
          <ToggleGroupItem value="count" className="h-8 px-2.5 text-[13px]" data-testid="dimension-originals-order-count">Most records</ToggleGroupItem>
          <ToggleGroupItem value="arrival" className="h-8 px-2.5 text-[13px]" data-testid="dimension-originals-order-arrival">As found</ToggleGroupItem>
        </ToggleGroup>
        <div className="flex items-center gap-2">
          <Switch id={`originals-removed-${dimensionId}`} checked={removed} onCheckedChange={setRemoved} data-testid="dimension-originals-removed" />
          <Label htmlFor={`originals-removed-${dimensionId}`} className="text-[13px] font-normal">Removed too</Label>
        </div>
      </FilterBar>

      {pages.isError
        ? <ProblemView error={pages.error} testId="dimension-originals-error" />
        : (
          <DataTable
            columns={columns}
            rows={rows}
            rowKey={(row) => row.valueId}
            footer={(
              <MoreFooter
                shown={rows?.length ?? 0}
                noun="original"
                hasMore={pages.hasNextPage}
                loading={pages.isFetchingNextPage}
                onMore={() => void pages.fetchNextPage()}
                testId="dimension-originals-footer"
              />
            )}
            emptyMessage={scope === "leftOut"
              ? "Cleaning gave every original a member."
              : search !== "" ? "No original holds the search." : "The dimension holds no original."}
            data-testid="dimension-originals-table"
          />
        )}
    </div>
  );
}
