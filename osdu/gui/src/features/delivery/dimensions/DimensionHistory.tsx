import { useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { keepPreviousData, useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { ArrowRight, TriangleAlert } from "lucide-react";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { DataTable, type Column } from "@/components/DataTable";
import { DetailPair } from "@/components/DetailPair";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { TruncatedText } from "@/components/TruncatedText";
import { deliveryApi, type DeliveryDimensionBuild, type DeliveryDimensionChange, type DimensionChangeKind } from "../../../api/delivery";
import { KindText } from "../KindText";
import { ProblemView } from "../TemplateSheet";
import { counted } from "../assertions/assertionFormat";
import { BuildStatusPill, MoreFooter } from "./DimensionBadges";
import { DimensionValueText } from "./DimensionValueText";
import { CHANGE_TEXT, buildDuration, coverage, percent } from "./dimensionFormat";

/** The builds the Builds tab lists. */
const BUILDS = 50;

/** The changes one page of the log reads. */
const CHANGE_PAGE = 200;

/** What a build changed, as signed counts: arrived, left, moved, came back. */
function ChangeCounts({ build }: { build: DeliveryDimensionBuild }) {
  const { originalsAdded, originalsRemoved, originalsMoved, originalsRestored } = build.changes;
  if (originalsAdded + originalsRemoved + originalsMoved + originalsRestored === 0) {
    return <span className="text-[12px] text-muted-foreground">none</span>;
  }

  return (
    <RichTooltip
      title="Changes to originals"
      body={`${originalsAdded} arrived, ${originalsRemoved} left, ${originalsMoved} moved to another member, ${originalsRestored} came back.`}
    >
      <span className="flex items-center gap-2 font-mono text-[12px] tabular-nums">
        {originalsAdded > 0 && <span className="text-success">+{originalsAdded.toLocaleString("en-US")}</span>}
        {originalsRemoved > 0 && <span className="text-destructive">−{originalsRemoved.toLocaleString("en-US")}</span>}
        {originalsMoved > 0 && <span className="text-warning">⇄{originalsMoved.toLocaleString("en-US")}</span>}
        {originalsRestored > 0 && <span className="text-info">↺{originalsRestored.toLocaleString("en-US")}</span>}
      </span>
    </RichTooltip>
  );
}

/** How a build read the index, in a line: the aggregations, how many ranges it split and scanned, and the count searches. */
function readText(build: DeliveryDimensionBuild): string {
  const parts = [counted(build.aggregations, "aggregation")];
  if (build.splits > 0) {
    parts.push(counted(build.splits, "split"));
  }

  if (build.scanPages > 0) {
    parts.push(`${counted(build.scannedSlices, "range")} scanned in ${counted(build.scanPages, "page")}`);
  }

  if (build.countQueries > 0) {
    parts.push(counted(build.countQueries, "count search"));
  }

  return parts.join(", ");
}

/** One build whole, in a sheet: what it came to and why, what it found and read, the kinds it read, and what it noted. */
function BuildSheet({ build, onClose }: { build: DeliveryDimensionBuild | null; onClose: () => void }) {
  const covered = build === null ? null : coverage(build);
  return (
    <Sheet open={build !== null} onOpenChange={(open) => { if (!open) { onClose(); } }}>
      <SheetContent className="w-full gap-0 sm:max-w-2xl" data-testid="dimension-build-sheet">
        {build !== null && (
          <>
            <SheetHeader className="border-b border-border">
              <SheetTitle className="flex items-center gap-2">
                <span className="font-mono">Build #{build.dimensionRunId}</span>
                <BuildStatusPill status={build.status} />
              </SheetTitle>
              <SheetDescription>
                Started <RelativeTime value={build.startedUtc} absolute={false} /> by {build.actor}, took {buildDuration(build)}.
              </SheetDescription>
            </SheetHeader>
            <div className="flex flex-1 flex-col gap-5 overflow-y-auto p-4">
              {build.error !== null && <p className="break-words text-[13px] text-destructive" data-testid="dimension-build-error">{build.error}</p>}
              <div className="grid gap-3 [grid-template-columns:repeat(auto-fill,minmax(150px,1fr))]">
                <DetailPair label="Members"><span className="font-mono tabular-nums">{build.members.toLocaleString("en-US")}</span></DetailPair>
                <DetailPair label="Originals"><span className="font-mono tabular-nums">{build.originals.toLocaleString("en-US")}</span></DetailPair>
                <DetailPair label="Under no member"><span className="font-mono tabular-nums">{build.leftOut.toLocaleString("en-US")}</span></DetailPair>
                <DetailPair label="No query carries"><span className="font-mono tabular-nums">{build.unfilterable.toLocaleString("en-US")}</span></DetailPair>
                <DetailPair label="Records read">
                  <span className="font-mono tabular-nums">{build.records === null ? "not counted" : build.records.toLocaleString("en-US")}</span>
                </DetailPair>
                <DetailPair label="Hold a value">
                  <span className="font-mono tabular-nums">{covered === null ? "not counted" : `${build.withValue!.toLocaleString("en-US")} (${percent(covered.share)})`}</span>
                </DetailPair>
                <DetailPair label="Null values"><span className="font-mono tabular-nums">{build.nulls.toLocaleString("en-US")}</span></DetailPair>
                <DetailPair label="Too long to aggregate">
                  <span className="font-mono tabular-nums">{build.tooLong === null ? "not counted" : build.tooLong.toLocaleString("en-US")}</span>
                </DetailPair>
                <DetailPair label="Not of the field's type"><span className="font-mono tabular-nums">{build.unreadable.toLocaleString("en-US")}</span></DetailPair>
                <DetailPair label="Read"><span className="text-[12.5px]">{readText(build)}</span></DetailPair>
                <DetailPair label="Changes"><ChangeCounts build={build} /></DetailPair>
                {build.runId !== null && (
                  <DetailPair label="Run">
                    <Link to={`/runs/${build.runId}`} className="font-mono text-[12px] hover:underline">{build.runId.slice(0, 8)}</Link>
                  </DetailPair>
                )}
              </div>
              {build.aggregateBy !== null && (
                <DetailPair label="Read as"><span className="break-all font-mono text-[12px]">{build.aggregateBy}</span></DetailPair>
              )}
              {build.query !== null && <DetailPair label="Query"><span className="break-all font-mono text-[12px]">{build.query}</span></DetailPair>}
              {build.kinds.length > 0 && (
                <section className="flex flex-col gap-2">
                  <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Kinds read</h3>
                  <DataTable
                    columns={[
                      { id: "kind", header: "Kind", fill: true, floor: 200, render: (row) => <KindText kind={row.kind} /> },
                      { id: "records", header: "Records", align: "right", render: (row) => <span className="font-mono text-[12px] tabular-nums">{row.records.toLocaleString("en-US")}</span> },
                      { id: "template", header: "Template", render: (row) => <span className="font-mono text-[12px] text-muted-foreground">{row.template ?? "none saved"}</span> },
                    ]}
                    rows={build.kinds}
                    rowKey={(row) => row.kind}
                    emptyMessage="No kind was read."
                    data-testid="dimension-build-kinds"
                  />
                </section>
              )}
              {build.notes.length > 0 && (
                <section className="flex flex-col gap-1.5">
                  <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Notes</h3>
                  <ul className="flex flex-col gap-1 text-[12.5px]" data-testid="dimension-build-notes">
                    {build.notes.map((note) => (
                      <li key={note} className="flex items-start gap-2">
                        <TriangleAlert className="mt-0.5 size-3.5 shrink-0 text-muted-foreground" />
                        <span className="break-words">{note}</span>
                      </li>
                    ))}
                  </ul>
                </section>
              )}
            </div>
          </>
        )}
      </SheetContent>
    </Sheet>
  );
}

/**
 * A dimension's builds, newest first: what each came to and when, how long it took, what it found, how complete its
 * values are, how it read the index, what it changed and who ran it. A build opens whole in a sheet.
 */
export function DimensionBuilds({ dimensionId }: { dimensionId: number }) {
  const [open, setOpen] = useState<DeliveryDimensionBuild | null>(null);
  const builds = useQuery({
    queryKey: ["delivery", "dimensions", "builds", dimensionId],
    queryFn: () => deliveryApi.dimensionBuilds(dimensionId, BUILDS),
    refetchInterval: 15000,
  });

  const columns: Column<DeliveryDimensionBuild>[] = [
    { id: "build", header: "Build", render: (row) => <span className="font-mono text-[12px]">#{row.dimensionRunId}</span> },
    { id: "status", header: "Status", render: (row) => <BuildStatusPill status={row.status} /> },
    { id: "started", header: "Started", render: (row) => <span className="text-[12px]"><RelativeTime value={row.startedUtc} absolute={false} /></span> },
    { id: "took", header: "Took", align: "right", render: (row) => <span className="font-mono text-[12px] tabular-nums">{buildDuration(row)}</span> },
    { id: "members", header: "Members", align: "right", render: (row) => <span className="font-mono text-[12px] tabular-nums">{row.status === "completed" ? row.members.toLocaleString("en-US") : "-"}</span> },
    {
      id: "coverage",
      header: "Coverage",
      align: "right",
      render: (row) => {
        const covered = coverage(row);
        return covered === null
          ? <span className="text-muted-foreground">-</span>
          : <RichTooltip body={covered.text}><span className="font-mono text-[12px] tabular-nums">{percent(covered.share)}</span></RichTooltip>;
      },
    },
    { id: "changes", header: "Changes", render: (row) => <ChangeCounts build={row} /> },
    { id: "read", header: "Read", fill: true, floor: 160, render: (row) => <TruncatedText text={row.error ?? readText(row)} maxWidth={360} /> },
    { id: "by", header: "By", render: (row) => <TruncatedText text={row.actor} mono maxWidth={160} /> },
  ];

  return (
    <>
      {builds.isError
        ? <ProblemView error={builds.error} testId="dimension-builds-error" />
        : (
          <DataTable
            columns={columns}
            rows={builds.data}
            rowKey={(row) => row.dimensionRunId}
            onRowClick={setOpen}
            emptyMessage="No build yet."
            data-testid="dimension-builds-table"
          />
        )}
      <BuildSheet build={open} onClose={() => setOpen(null)} />
    </>
  );
}

const CHANGE_KINDS: readonly DimensionChangeKind[] = ["added", "removed", "moved", "restored"];

/** Where a change took an original: the member it left, the one it arrived under, or both for a move; each opens its member. */
function Movement({ change, onMember }: { change: DeliveryDimensionChange; onMember: (memberId: number) => void }) {
  const side = (memberId: number | null, value: string | null) => (memberId === null || value === null
    ? <span className="text-[12px] italic text-muted-foreground">no member</span>
    : (
      <button
        type="button"
        onClick={(event) => { event.stopPropagation(); onMember(memberId); }}
        className="rounded-sm px-1 outline-none hover:bg-accent/60 focus-visible:bg-accent/60"
      >
        <DimensionValueText value={value} maxWidth={200} />
      </button>
    ));

  switch (change.change) {
    case "moved":
      return (
        <span className="flex min-w-0 items-center gap-1.5">
          {side(change.fromMemberId, change.fromValue)}
          <ArrowRight className="size-3.5 shrink-0 text-muted-foreground" />
          {side(change.toMemberId, change.toValue)}
        </span>
      );
    case "removed":
      return <span className="flex items-center gap-1.5"><span className="text-[12px] text-muted-foreground">from</span>{side(change.fromMemberId, change.fromValue)}</span>;
    default:
      return <span className="flex items-center gap-1.5"><span className="text-[12px] text-muted-foreground">under</span>{side(change.toMemberId, change.toValue)}</span>;
  }
}

const CHANGE_TONE: Record<DimensionChangeKind, string> = {
  added: "text-success",
  removed: "text-destructive",
  moved: "text-warning",
  restored: "text-info",
};

/**
 * A dimension's change log, newest first and grouped by the build that made each change: every original that arrived,
 * left, came back or moved to another member, narrowed to one kind of change when asked. A dimension's first build logs
 * no arrivals: everything it found arrived with it.
 */
export function DimensionChanges({ dimensionId, onMember }: { dimensionId: number; onMember: (memberId: number) => void }) {
  const [kind, setKind] = useState<DimensionChangeKind | "all">("all");
  const pages = useInfiniteQuery({
    queryKey: ["delivery", "dimensions", "changes", dimensionId, kind],
    queryFn: ({ pageParam }) => deliveryApi.dimensionChanges(dimensionId, {
      change: kind === "all" ? undefined : kind, before: pageParam, limit: CHANGE_PAGE,
    }),
    initialPageParam: null as number | null,
    getNextPageParam: (last) => last.next,
    placeholderData: keepPreviousData,
  });
  const rows = useMemo(() => pages.data?.pages.flatMap((page) => page.items), [pages.data]);

  const columns: Column<DeliveryDimensionChange>[] = [
    {
      id: "change",
      header: "Change",
      render: (row) => <span className={`text-[12.5px] font-medium ${CHANGE_TONE[row.change]}`}>{CHANGE_TEXT[row.change].label}</span>,
    },
    { id: "original", header: "Original", render: (row) => <DimensionValueText value={row.original} strong maxWidth={260} testId="dimension-change-original" /> },
    { id: "movement", header: "Member", fill: true, floor: 200, render: (row) => <Movement change={row} onMember={onMember} /> },
    { id: "when", header: "When", render: (row) => <span className="text-[12px] text-muted-foreground"><RelativeTime value={row.changedUtc} absolute={false} /></span> },
  ];

  return (
    <div className="flex flex-col gap-2" data-testid="dimension-changes">
      <ToggleGroup
        type="single"
        variant="outline"
        size="sm"
        value={kind}
        onValueChange={(value) => { if (value !== "") { setKind(value as DimensionChangeKind | "all"); } }}
        aria-label="Which changes"
        className="self-start"
      >
        <ToggleGroupItem value="all" className="h-8 px-2.5 text-[13px]" data-testid="dimension-changes-all">Every change</ToggleGroupItem>
        {CHANGE_KINDS.map((option) => (
          <ToggleGroupItem key={option} value={option} className="h-8 px-2.5 text-[13px]" data-testid={`dimension-changes-${option}`}>
            {CHANGE_TEXT[option].label}
          </ToggleGroupItem>
        ))}
      </ToggleGroup>
      {pages.isError
        ? <ProblemView error={pages.error} testId="dimension-changes-error" />
        : (
          <DataTable
            columns={columns}
            rows={rows}
            rowKey={(row) => row.changeId}
            grouping={{
              levels: [{
                key: (row) => String(row.dimensionRunId),
                renderHeader: (group) => (
                  <span className="flex items-center gap-2 text-[12px]">
                    <span className="font-mono font-medium">Build #{group[0].dimensionRunId}</span>
                    <span className="text-muted-foreground"><RelativeTime value={group[0].changedUtc} absolute={false} /></span>
                    <span className="font-mono tabular-nums text-muted-foreground">{counted(group.length, "change")}</span>
                  </span>
                ),
              }],
            }}
            footer={(
              <MoreFooter
                shown={rows?.length ?? 0}
                noun="change"
                hasMore={pages.hasNextPage}
                loading={pages.isFetchingNextPage}
                onMore={() => void pages.fetchNextPage()}
                testId="dimension-changes-footer"
              />
            )}
            emptyMessage={kind === "all"
              ? "No build has changed the dimension since its first, which logs no arrivals: everything it found arrived with it."
              : `No build has logged a change of this kind.`}
            data-testid="dimension-changes-table"
          />
        )}
    </div>
  );
}
