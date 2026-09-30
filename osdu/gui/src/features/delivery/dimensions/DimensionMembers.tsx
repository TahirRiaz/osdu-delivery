import { useMemo, useState } from "react";
import { keepPreviousData, useInfiniteQuery } from "@tanstack/react-query";
import { Filter, TriangleAlert, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { CopyButton } from "@/components/CopyButton";
import { DataTable, type Column } from "@/components/DataTable";
import { FilterBar } from "@/components/FilterBar";
import { RichTooltip } from "@/components/RichTooltip";
import { SearchInput } from "@/components/SearchInput";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { deliveryApi, type DeliveryDimensionMember } from "../../../api/delivery";
import { ProblemView } from "../TemplateSheet";
import { counted } from "../assertions/assertionFormat";
import { MoreFooter } from "./DimensionBadges";
import { DimensionFilterSheet } from "./DimensionFilterSheet";
import { DimensionValueText } from "./DimensionValueText";
import { percent, type DimensionEntry } from "./dimensionFormat";

/** The members one page reads. */
const PAGE = 100;

/** How long a typed search rests before it is asked. */
const SEARCH_DELAY_MS = 300;

type MemberOrder = "records" | "value";

/**
 * A member's records as a bar and a number: the bar its share of the widest member in view, the number exact, or the sum
 * of its originals' counts marked approximate, since a record holding two of them counts twice there.
 */
function RecordsCell({ member, widest, withValue }: { member: DeliveryDimensionMember; widest: number; withValue: number | null }) {
  const share = withValue !== null && withValue > 0 && member.recordsExact ? member.records / withValue : null;
  const body = (
    <span className="flex items-center gap-2" data-testid="dimension-member-records">
      <span className="h-1.5 w-20 shrink-0 overflow-hidden rounded-full bg-muted" aria-hidden>
        <span className="block h-full rounded-full bg-chart-1" style={{ width: `${Math.max(2, (member.records / Math.max(1, widest)) * 100)}%` }} />
      </span>
      <span className="font-mono text-[12px] tabular-nums">
        {member.recordsExact ? "" : "~"}{member.records.toLocaleString("en-US")}
      </span>
      {share !== null && <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{percent(share)}</span>}
    </span>
  );

  return member.recordsExact
    ? body
    : (
      <RichTooltip
        title="Approximate"
        body="The sum of its originals' counts: a record holding more than one of them counts once for each. A dimension that sets countRecords: true counts each member's records exactly, with one search per member."
      >
        {body}
      </RichTooltip>
    );
}

/** The originals most records hold of a member, each with its count, and how many more it gathers. */
function OriginalsCell({ member, search }: { member: DeliveryDimensionMember; search: string }) {
  const more = member.originals - member.top.length;
  return (
    <span className="flex min-w-0 items-center gap-1.5 overflow-hidden" data-testid="dimension-member-originals">
      {member.top.map((original) => (
        <span key={original.original} className="inline-flex min-w-0 shrink items-baseline gap-1 rounded-sm bg-secondary/70 px-1.5 py-px dark:bg-input/40">
          <DimensionValueText value={original.original} search={search} maxWidth={160} />
          <span className="font-mono text-[10.5px] tabular-nums text-muted-foreground">{original.count.toLocaleString("en-US")}</span>
        </span>
      ))}
      {more > 0 && <span className="shrink-0 font-mono text-[11px] text-muted-foreground">+{more.toLocaleString("en-US")}</span>}
    </span>
  );
}

/** How a member's records are found: its filter to copy when one query holds it, the queries it takes, or that none can. */
function FilterCell({ member, onFilter }: { member: DeliveryDimensionMember; onFilter: () => void }) {
  const warning = member.unfilterable > 0 && (
    <RichTooltip
      title="Left out of the filter"
      body={`${counted(member.unfilterable, "original")} of this member cannot be carried in a search query, so its filter does not find the records holding ${member.unfilterable === 1 ? "it" : "them"}.`}
    >
      <TriangleAlert className="size-4 shrink-0 text-warning" aria-label="Some originals are left out of the filter" />
    </RichTooltip>
  );

  if (member.filterParts === 0) {
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
      {member.filter !== null
        ? <CopyButton iconOnly label="Copy its search filter" text={member.filter} testId="dimension-member-copy-filter" />
        : (
          <Button variant="ghost" size="xs" onClick={onFilter} data-testid="dimension-member-filter-parts">
            {member.filterParts} queries
          </Button>
        )}
    </span>
  );
}

/**
 * A dimension's members: each clean value with a bar of the records holding it, the originals it gathers (the commonest
 * first, each with its count), and its search filter. Read with the most records first or in value order, a page at a
 * time, found by the member's value or any of its originals. Members are picked to write the search that finds the records
 * of all of them together; a member opens in a sheet with everything the ledger holds of it.
 */
export function DimensionMembers({ entry, dimensionId, onMember }: {
  entry: DimensionEntry;
  dimensionId: number;
  onMember: (memberId: number) => void;
}) {
  const [typed, setTyped] = useState("");
  const search = useDebouncedValue(typed.trim(), SEARCH_DELAY_MS);
  const [order, setOrder] = useState<MemberOrder>("records");
  const [removed, setRemoved] = useState(false);
  const [picked, setPicked] = useState<ReadonlySet<string>>(new Set());
  const [filtering, setFiltering] = useState<number[] | null>(null);
  // A new build rewrites the members, so its id keys every page: pages read before it are read again rather than mixed in.
  const built = entry.dimension.current?.dimensionRunId ?? null;
  const pages = useInfiniteQuery({
    queryKey: ["delivery", "dimensions", "members", dimensionId, built, search, order, removed],
    queryFn: ({ pageParam }) => deliveryApi.dimensionMembers(dimensionId, { search, order, removed, after: pageParam, limit: PAGE }),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.next,
    placeholderData: keepPreviousData,
  });
  const rows = useMemo(() => pages.data?.pages.flatMap((page) => page.items), [pages.data]);
  const widest = useMemo(() => Math.max(1, ...(rows ?? []).map((row) => row.records)), [rows]);
  const withValue = entry.dimension.current?.withValue ?? null;

  const columns: Column<DeliveryDimensionMember>[] = [
    {
      id: "value",
      header: "Member",
      render: (row) => (
        <span className="flex items-center gap-2">
          <DimensionValueText value={row.value} search={search} strong maxWidth={260} testId="dimension-member-value" />
          {row.removedUtc !== null && (
            <RichTooltip body="No build finds it any more; it keeps its id, so it is the same member if a later build finds it again.">
              <span className="rounded-sm border border-border px-1 text-[10.5px] text-muted-foreground">removed</span>
            </RichTooltip>
          )}
        </span>
      ),
    },
    {
      id: "records",
      header: "Records",
      render: (row) => <RecordsCell member={row} widest={widest} withValue={withValue} />,
    },
    {
      id: "originals",
      header: "Originals",
      fill: true,
      floor: 220,
      render: (row) => <OriginalsCell member={row} search={search} />,
    },
    {
      id: "filter",
      header: "Filter",
      align: "right",
      render: (row) => <FilterCell member={row} onFilter={() => setFiltering([row.memberId])} />,
    },
  ];

  const toolbar = picked.size > 0 && (
    <div className="flex flex-wrap items-center gap-2 border-b border-border bg-primary/5 px-3 py-1.5 text-[12.5px]" data-testid="dimension-members-picked">
      <span className="font-medium">{counted(picked.size, "member")} picked</span>
      <Button size="xs" onClick={() => setFiltering([...picked].map(Number))} data-testid="dimension-members-write-filter">
        <Filter />
        Write their search
      </Button>
      <Button size="xs" variant="ghost" onClick={() => setPicked(new Set())} data-testid="dimension-members-clear">
        <X />
        Clear
      </Button>
    </div>
  );

  return (
    <div className="flex flex-col gap-2" data-testid="dimension-members">
      <FilterBar>
        <SearchInput
          value={typed}
          onChange={setTyped}
          placeholder="Find a member by its value or any original"
          label="Find a member"
          className="sm:w-96"
          testId="dimension-members-search"
        />
        <ToggleGroup
          type="single"
          variant="outline"
          size="sm"
          value={order}
          onValueChange={(value) => { if (value !== "") { setOrder(value as MemberOrder); } }}
          aria-label="Order of the members"
        >
          <ToggleGroupItem value="records" className="h-8 px-2.5 text-[13px]" data-testid="dimension-members-order-records">Most records</ToggleGroupItem>
          <ToggleGroupItem value="value" className="h-8 px-2.5 text-[13px]" data-testid="dimension-members-order-value">By value</ToggleGroupItem>
        </ToggleGroup>
        <div className="flex items-center gap-2">
          <Switch id={`removed-${dimensionId}`} checked={removed} onCheckedChange={setRemoved} data-testid="dimension-members-removed" />
          <Label htmlFor={`removed-${dimensionId}`} className="text-[13px] font-normal">Removed too</Label>
        </div>
      </FilterBar>

      {pages.isError
        ? <ProblemView error={pages.error} testId="dimension-members-error" />
        : (
          <DataTable
            columns={columns}
            rows={rows}
            rowKey={(row) => row.memberId}
            onRowClick={(row) => onMember(row.memberId)}
            selection={{ selected: picked, onChange: setPicked }}
            toolbar={toolbar || undefined}
            footer={(
              <MoreFooter
                shown={rows?.length ?? 0}
                noun="member"
                hasMore={pages.hasNextPage}
                loading={pages.isFetchingNextPage}
                onMore={() => void pages.fetchNextPage()}
                testId="dimension-members-footer"
              />
            )}
            emptyMessage={search !== "" ? "No member's value or original holds the search." : "The dimension holds no member: its last build found no value."}
            data-testid="dimension-members-table"
          />
        )}

      <DimensionFilterSheet dimensionId={dimensionId} memberIds={filtering} onClose={() => setFiltering(null)} />
    </div>
  );
}
