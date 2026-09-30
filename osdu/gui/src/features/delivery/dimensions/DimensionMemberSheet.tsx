import { useQuery } from "@tanstack/react-query";
import { ArrowRight, Ban, Info } from "lucide-react";
import { Skeleton } from "@/components/ui/skeleton";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { CopyButton } from "@/components/CopyButton";
import { DataTable, type Column } from "@/components/DataTable";
import { DetailPair } from "@/components/DetailPair";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { deliveryApi, type DeliveryDimensionChange, type DeliveryDimensionValue } from "../../../api/delivery";
import { ProblemView } from "../TemplateSheet";
import { counted } from "../assertions/assertionFormat";
import { DimensionFilterView } from "./DimensionFilterSheet";
import { DimensionValueText } from "./DimensionValueText";
import { CHANGE_TEXT } from "./dimensionFormat";

const ORIGINAL_COLUMNS: Column<DeliveryDimensionValue>[] = [
  {
    id: "original",
    header: "Original",
    fill: true,
    floor: 180,
    render: (row) => <DimensionValueText value={row.original} maxWidth={320} testId="dimension-member-original" />,
  },
  {
    id: "count",
    header: "Count",
    align: "right",
    render: (row) => <span className="font-mono text-[12px] tabular-nums">{row.count.toLocaleString("en-US")}</span>,
  },
  {
    id: "filterable",
    header: "Query",
    align: "center",
    render: (row) => (row.filterable
      ? <span className="text-muted-foreground/50">-</span>
      : (
        <RichTooltip body="No search query can carry this original, so the member's filter does not find the records holding it.">
          <Ban className="inline size-4 text-warning" aria-label="Cannot be carried in a query" />
        </RichTooltip>
      )),
  },
  {
    id: "since",
    header: "Arrived",
    render: (row) => <span className="text-[12px] text-muted-foreground"><RelativeTime value={row.firstSeenUtc} absolute={false} /></span>,
  },
];

/** A change to one of the member's originals, on one line: when, which build, the original, and where it went. */
function HistoryRow({ change, memberId }: { change: DeliveryDimensionChange; memberId: number }) {
  const left = change.fromMemberId === memberId;
  return (
    <li className="grid grid-cols-[7rem_4.5rem_minmax(0,1fr)] items-baseline gap-x-2 py-1 text-[12.5px]" data-testid="dimension-member-history-row">
      <span className="text-muted-foreground"><RelativeTime value={change.changedUtc} absolute={false} /></span>
      <span className="font-mono text-[11px] text-muted-foreground">build #{change.dimensionRunId}</span>
      <span className="flex min-w-0 flex-wrap items-baseline gap-1.5">
        <DimensionValueText value={change.original} maxWidth={200} />
        <span className={left ? "text-warning" : "text-success"}>{CHANGE_TEXT[change.change].verb}</span>
        {change.change === "moved" && (
          <span className="inline-flex items-baseline gap-1 text-muted-foreground">
            {change.fromValue === null ? "no member" : <DimensionValueText value={change.fromValue} maxWidth={140} />}
            <ArrowRight className="size-3.5 self-center" />
            {change.toValue === null ? "no member" : <DimensionValueText value={change.toValue} maxWidth={140} />}
          </span>
        )}
      </span>
    </li>
  );
}

/**
 * One member, whole, in a sheet: its clean value to copy, its records and originals, when it arrived and whether a build
 * still finds it; every original it gathers with its count, the most records first; the search that finds its records;
 * and the changes that brought originals to it or took them away.
 */
export function DimensionMemberSheet({ dimensionId, memberId, onClose }: {
  dimensionId: number;
  /** The member to show; null closes the sheet. */
  memberId: number | null;
  onClose: () => void;
}) {
  const detail = useQuery({
    queryKey: ["delivery", "dimensions", "member", dimensionId, memberId],
    queryFn: () => deliveryApi.dimensionMember(dimensionId, memberId!),
    enabled: memberId !== null,
  });
  const data = detail.data;

  return (
    <Sheet open={memberId !== null} onOpenChange={(open) => { if (!open) { onClose(); } }}>
      <SheetContent
        className="w-full gap-0 sm:max-w-2xl"
        onOpenAutoFocus={(event) => { event.preventDefault(); (event.currentTarget as HTMLElement).focus(); }}
        data-testid="dimension-member-sheet"
      >
        <SheetHeader className="border-b border-border">
          <SheetTitle className="flex min-w-0 items-center gap-2">
            {data === undefined
              ? <Skeleton className="h-5 w-40" />
              : (
                <>
                  <DimensionValueText value={data.member.value} strong maxWidth={440} className="text-[15px]" testId="dimension-member-title" />
                  <CopyButton iconOnly label="Copy the clean value" text={data.member.value} testId="dimension-member-copy-value" />
                </>
              )}
          </SheetTitle>
          <SheetDescription>A member of the dimension: one clean value, and the originals cleaning gathered into it.</SheetDescription>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-5 overflow-y-auto p-4">
          {detail.isError && <ProblemView error={detail.error} testId="dimension-member-error" />}
          {data === undefined && !detail.isError && <Skeleton className="h-64 w-full" />}
          {data !== undefined && (
            <>
              <div className="grid gap-3 [grid-template-columns:repeat(auto-fill,minmax(140px,1fr))]">
                <DetailPair label="Records">
                  <span className="font-mono tabular-nums" data-testid="dimension-member-detail-records">
                    {data.member.recordsExact ? "" : "~"}{data.member.records.toLocaleString("en-US")}
                  </span>
                </DetailPair>
                <DetailPair label="Originals"><span className="font-mono tabular-nums">{data.member.originals.toLocaleString("en-US")}</span></DetailPair>
                <DetailPair label="Arrived"><RelativeTime value={data.member.firstSeenUtc} absolute={false} /></DetailPair>
                <DetailPair label="Found now">
                  {data.member.removedUtc === null
                    ? <span>yes</span>
                    : <span className="text-warning">no, since <RelativeTime value={data.member.removedUtc} absolute={false} /></span>}
                </DetailPair>
              </div>

              <section className="flex flex-col gap-2">
                <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Originals</h3>
                <DataTable
                  columns={ORIGINAL_COLUMNS}
                  rows={data.originals}
                  rowKey={(row) => row.valueId}
                  emptyMessage="No build finds an original of this member now."
                  skeletonRows={3}
                  data-testid="dimension-member-originals-table"
                />
                {data.moreOriginals && (
                  <p className="flex items-center gap-1.5 text-[12px] text-muted-foreground">
                    <Info className="size-4 shrink-0" />
                    These are the {data.originals.length.toLocaleString("en-US")} most common of {counted(data.member.originals, "original")}; the Originals tab lists every one.
                  </p>
                )}
              </section>

              <section className="flex flex-col gap-2">
                <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Search</h3>
                {data.filter !== null
                  ? <DimensionFilterView filter={data.filter} />
                  : <p className="text-[13px] text-muted-foreground" data-testid="dimension-member-filter-problem">{data.filterProblem}</p>}
              </section>

              <section className="flex flex-col gap-1">
                <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">History</h3>
                {data.history.length === 0
                  ? <p className="text-[12.5px] text-muted-foreground">No build has moved an original to it or away from it since it arrived.</p>
                  : (
                    <ul className="divide-y divide-border" data-testid="dimension-member-history">
                      {data.history.map((change) => <HistoryRow key={change.changeId} change={change} memberId={data.member.memberId} />)}
                    </ul>
                  )}
              </section>
            </>
          )}
        </div>
      </SheetContent>
    </Sheet>
  );
}
