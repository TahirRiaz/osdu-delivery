import { useQuery } from "@tanstack/react-query";
import { Info, TriangleAlert } from "lucide-react";
import { Skeleton } from "@/components/ui/skeleton";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { CodeView } from "@/components/CodeView";
import { CopyButton } from "@/components/CopyButton";
import { DetailPair } from "@/components/DetailPair";
import { deliveryApi, type DeliveryDimensionFilter } from "../../../api/delivery";
import { KindText } from "../KindText";
import { ProblemView } from "../TemplateSheet";
import { counted } from "../assertions/assertionFormat";
import { DimensionValueText } from "./DimensionValueText";
import { osduSearchRequest } from "./dimensionFormat";

/** One search of a filter: the query text, whole and wrapped, with a way to copy it and the request body that sends it. */
function SearchBlock({ index, count, query, kind }: { index: number; count: number; query: string; kind: string }) {
  return (
    <div className="flex flex-col gap-1.5 rounded-md border border-border bg-card p-2" data-testid="dimension-filter-search">
      <div className="flex items-center gap-2 text-[12px] text-muted-foreground">
        <span>{count > 1 ? `Search ${index + 1} of ${count}` : "The search"}</span>
        <span className="ml-auto flex items-center gap-1">
          <CopyButton label="Copy the query" text={query} testId="dimension-filter-copy-query" />
          <CopyButton label="Copy the request" text={() => osduSearchRequest(kind, query)} testId="dimension-filter-copy-request" />
        </span>
      </div>
      <p className="max-h-40 overflow-y-auto whitespace-pre-wrap break-all font-mono text-[12px]" data-testid="dimension-filter-query">{query}</p>
    </div>
  );
}

/**
 * A filter as a reader uses it: the kind it searches and the field it compares, each search to send (a query, or the whole
 * request body the search service takes), and what it leaves out: originals no query can carry, members no build finds any
 * more, and names that are no member. A filter over more originals than one query holds is several searches, whose
 * records together are the members'.
 */
export function DimensionFilterView({ filter }: { filter: DeliveryDimensionFilter }) {
  return (
    <div className="flex flex-col gap-4" data-testid="dimension-filter">
      <div className="grid gap-3 [grid-template-columns:repeat(auto-fill,minmax(180px,1fr))]">
        <DetailPair label="Kind"><KindText kind={filter.kind} /></DetailPair>
        <DetailPair label="Compares"><span className="break-all font-mono text-[12px]">{filter.aggregateBy}</span></DetailPair>
        <DetailPair label="Finds">
          <span className="font-mono text-[12px] tabular-nums">{counted(filter.originals, "original")}</span>
        </DetailPair>
      </div>

      {filter.searches.length === 0
        ? (
          <p className="flex items-center gap-2 text-[13px] text-muted-foreground" data-testid="dimension-filter-none">
            <Info className="size-4 shrink-0" />
            No original of these members can be carried in a search query, so there is no search to send.
          </p>
        )
        : (
          <section className="flex flex-col gap-2">
            <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
              {filter.searches.length === 1 ? "Search" : `${filter.searches.length} searches`}
            </h3>
            {filter.searches.length > 1 && (
              <p className="text-[12px] text-muted-foreground">
                One query holds at most 500 originals, so the members' records are those the searches find together.
              </p>
            )}
            {filter.searches.map((query, index) => (
              <SearchBlock key={query} index={index} count={filter.searches.length} query={query} kind={filter.kind} />
            ))}
            <h3 className="mt-2 text-[11px] font-medium uppercase tracking-wider text-muted-foreground">As a search request</h3>
            <CodeView
              value={osduSearchRequest(filter.kind, filter.searches[0])}
              language="json"
              height={140}
              data-testid="dimension-filter-request"
            />
          </section>
        )}

      {(filter.unfilterable > 0 || filter.removed.length > 0 || filter.missing.length > 0) && (
        <section className="flex flex-col gap-1.5 text-[12.5px]" data-testid="dimension-filter-left-out">
          <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Left out</h3>
          {filter.unfilterable > 0 && (
            <p className="flex items-start gap-2">
              <TriangleAlert className="mt-0.5 size-4 shrink-0 text-warning" />
              <span>
                {counted(filter.unfilterable, "original")} cannot be carried in a query:{" "}
                {filter.unfilterableNamed.map((original, index) => (
                  <span key={original}>{index > 0 && ", "}<DimensionValueText value={original} maxWidth={200} /></span>
                ))}
                {filter.unfilterable > filter.unfilterableNamed.length && " and more"}.
              </span>
            </p>
          )}
          {filter.removed.length > 0 && (
            <p className="text-muted-foreground">No build finds these any more: {filter.removed.join(", ")}.</p>
          )}
          {filter.missing.length > 0 && (
            <p className="text-muted-foreground">No member of the dimension: {filter.missing.join(", ")}.</p>
          )}
        </section>
      )}
    </div>
  );
}

/** The search that finds the records of the members picked, in a sheet beside the list they were picked from. */
export function DimensionFilterSheet({ dimensionId, memberIds, onClose }: {
  dimensionId: number;
  /** The members to write the search for; null closes the sheet. */
  memberIds: number[] | null;
  onClose: () => void;
}) {
  const filter = useQuery({
    queryKey: ["delivery", "dimensions", "filter", dimensionId, memberIds],
    queryFn: () => deliveryApi.dimensionFilter(dimensionId, { memberIds: memberIds ?? [] }),
    enabled: memberIds !== null && memberIds.length > 0,
  });

  return (
    <Sheet open={memberIds !== null} onOpenChange={(open) => { if (!open) { onClose(); } }}>
      <SheetContent className="w-full gap-0 sm:max-w-2xl" data-testid="dimension-filter-sheet">
        <SheetHeader className="border-b border-border">
          <SheetTitle>Search for {counted(memberIds?.length ?? 0, "member")}</SheetTitle>
          <SheetDescription>
            The OSDU search that finds every record holding one of their originals, joined with the dimension's own query.
          </SheetDescription>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-4 overflow-y-auto p-4">
          {filter.isError
            ? <ProblemView error={filter.error} testId="dimension-filter-error" />
            : filter.data === undefined
              ? <Skeleton className="h-48 w-full" />
              : (
                <>
                  <DimensionFilterView filter={filter.data} />
                  <section className="flex flex-col gap-1.5">
                    <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Members</h3>
                    <div className="flex flex-wrap gap-1.5" data-testid="dimension-filter-members">
                      {filter.data.members.map((member) => (
                        <span key={member.memberId} className="inline-flex items-baseline gap-1 rounded-sm bg-secondary/70 px-1.5 py-px dark:bg-input/40">
                          <DimensionValueText value={member.value} maxWidth={220} />
                          <span className="font-mono text-[10.5px] tabular-nums text-muted-foreground">
                            {member.recordsExact ? "" : "~"}{member.records.toLocaleString("en-US")}
                          </span>
                        </span>
                      ))}
                    </div>
                  </section>
                </>
              )}
        </div>
      </SheetContent>
    </Sheet>
  );
}
