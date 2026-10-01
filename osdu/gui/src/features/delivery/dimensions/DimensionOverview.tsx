import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Play } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import { deliveryApi, type DeliveryDimensionFlow } from "../../../api/delivery";
import { KindText } from "../KindText";
import { useNearViewport } from "../useNearViewport";
import { counted } from "../assertions/assertionFormat";
import { StandingGlyph } from "./DimensionBadges";
import { DimensionValueText } from "./DimensionValueText";
import type { DimensionLaunch } from "./DimensionBuildDialog";
import { percent, type DimensionEntry } from "./dimensionFormat";

/** The values a card shows: the ones most records hold. */
const CARD_VALUES = 5;

/**
 * The values most records hold, each with a bar of its share of the records the dimension's values hold together, so a
 * card says at a glance whether a dimension is a handful of values or a long tail. Fetched as the card nears the viewport.
 */
function TopValues({ entry }: { entry: DimensionEntry }) {
  const [ref, near] = useNearViewport<HTMLDivElement>();
  const dimensionId = entry.dimension.dimensionId;
  const top = useQuery({
    queryKey: ["delivery", "dimensions", "top", dimensionId, entry.dimension.current?.buildId ?? null],
    queryFn: () => deliveryApi.dimensionValues(dimensionId!, { order: "records", limit: CARD_VALUES }),
    enabled: near && dimensionId !== null && entry.dimension.values > 0,
    staleTime: 60_000,
  });
  const total = entry.dimension.current?.withValue ?? null;
  const rows = top.data?.items ?? [];
  const widest = Math.max(1, ...rows.map((value) => value.records));

  return (
    <div ref={ref} className="flex flex-col gap-1" data-testid="dimension-card-top">
      {entry.dimension.values === 0
        ? <p className="py-2 text-[12px] text-muted-foreground">{entry.dimension.current === null ? "No build has read it yet." : "Its last build found no key."}</p>
        : top.isPending
          ? Array.from({ length: Math.min(CARD_VALUES, entry.dimension.values) }, (_, index) => <Skeleton key={index} className="h-4 w-full" />)
          : rows.map((value) => (
            <div key={value.valueId} className="grid grid-cols-[minmax(0,1fr)_4.5rem] items-center gap-2">
              <div className="relative h-5 min-w-0 overflow-hidden rounded-sm">
                <div className="absolute inset-y-0 left-0 rounded-sm bg-chart-1/20" style={{ width: `${(value.records / widest) * 100}%` }} />
                <span className="relative flex h-5 items-center px-1.5">
                  <DimensionValueText value={value.value} maxWidth={220} />
                </span>
              </div>
              <span className="text-right font-mono text-[11px] tabular-nums text-muted-foreground">
                {value.recordsExact ? "" : "~"}{value.records.toLocaleString("en-US")}
                {total !== null && total > 0 && value.recordsExact && value.records / total >= 0.01 && (
                  <span className="ml-1 text-muted-foreground/60">{percent(value.records / total)}</span>
                )}
              </span>
            </div>
          ))}
      {entry.dimension.values > CARD_VALUES && rows.length > 0 && (
        <span className="text-[11px] text-muted-foreground">and {counted(entry.dimension.values - rows.length, "more value")}</span>
      )}
    </div>
  );
}

/** One dimension as a card: where it stands, what it reads, its commonest values, and when it was built. */
function DimensionCard({ entry, onOpen }: { entry: DimensionEntry; onOpen: () => void }) {
  const { dimension } = entry;
  const latest = dimension.latest;
  return (
    <Card
      role="button"
      tabIndex={0}
      onClick={onOpen}
      onKeyDown={(event) => { if (event.key === "Enter") { onOpen(); } }}
      className={cn(
        "flex cursor-pointer flex-col gap-2 rounded-lg p-3 outline-none transition-colors duration-120 hover:bg-accent/40 focus-visible:bg-accent/40",
        entry.standing === "undeclared" && "opacity-70",
      )}
      data-testid="dimension-card"
      data-dimension={dimension.name}
    >
      <div className="flex min-w-0 items-center gap-2">
        <StandingGlyph standing={entry.standing} />
        <span className="min-w-0 flex-1 truncate text-[13px] font-medium">{dimension.name}</span>
        <span className="shrink-0 font-mono text-[12px] tabular-nums text-muted-foreground">{counted(dimension.values, "value")}</span>
      </div>
      <div className="flex min-w-0 flex-col gap-0.5 text-muted-foreground">
        <KindText kind={dimension.kind} />
        <span className="truncate font-mono text-[11px]" title={dimension.path}>
          {dimension.path}{dimension.label.length > 0 && <span className="text-muted-foreground/70"> labelled by {dimension.label.at(-1)}</span>}
        </span>
      </div>
      <TopValues entry={entry} />
      <div className="mt-auto flex min-w-0 items-center gap-1.5 border-t border-border pt-2 text-[11px] text-muted-foreground">
        {latest !== null && latest.status === "failed"
          ? <span className="truncate text-destructive" title={latest.error ?? undefined}>Newest build failed: {latest.error ?? "no reason kept"}</span>
          : dimension.lastBuiltUtc === null
            ? <span>Not built yet</span>
            : (
              <>
                <span className="font-mono tabular-nums">{counted(dimension.keys, "key")}</span>
                <span className="text-muted-foreground/60">·</span>
                <span>built <RelativeTime value={dimension.lastBuiltUtc} absolute={false} /></span>
              </>
            )}
      </div>
    </Card>
  );
}

/** A flow's heading over its cards: its name (to its pipeline), what it is for, and a run of its pipeline, which builds every dimension it declares. */
function FlowHeading({ flow, onBuild }: { flow: DeliveryDimensionFlow; onBuild: () => void }) {
  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
      <RichTooltip body={flow.description ?? "A dimension flow."}>
        <Link to={`/pipelines/${flow.pipelineId}?tab=dimensions`} className="font-mono text-[13px] font-medium hover:underline" data-testid="dimension-flow-link">
          {flow.name}
        </Link>
      </RichTooltip>
      {flow.description !== null && <span className="min-w-0 truncate text-[12px] text-muted-foreground">{flow.description}</span>}
      <RichTooltip body="Runs the flow's pipeline, which builds every dimension it declares; the dialog can pick some of them.">
        <Button variant="outline" size="xs" className="ml-auto" onClick={onBuild} data-testid="dimension-flow-build">
          <Play />
          Run pipeline
        </Button>
      </RichTooltip>
    </div>
  );
}

/**
 * Every dimension of the partition, flow by flow, as cards: where each stands, what it reads, the values most records
 * hold with their share, and when it was last built. A card opens its dimension. Flows that do not build in the partition
 * are named at the foot, so an absent flow reads as elsewhere rather than missing.
 */
export function DimensionOverview({ entries, flows, onOpen, onLaunch }: {
  entries: DimensionEntry[];
  flows: DeliveryDimensionFlow[];
  onOpen: (ref: string) => void;
  onLaunch: (launch: DimensionLaunch) => void;
}) {
  const byFlow = flows.filter((flow) => flow.buildsPartition);
  const elsewhere = flows.filter((flow) => !flow.buildsPartition);
  return (
    <div className="flex flex-col gap-5" data-testid="dimension-overview">
      {byFlow.map((flow) => {
        const own = entries.filter((entry) => entry.flow.pipelineId === flow.pipelineId);
        return (
          <section key={flow.pipelineId} className="flex flex-col gap-2" data-testid="dimension-overview-flow">
            <FlowHeading flow={flow} onBuild={() => onLaunch({ pipelineId: flow.pipelineId, repoId: flow.repoId, flowName: flow.name, dimensions: [] })} />
            {own.length === 0
              ? <p className="text-[12px] text-muted-foreground">The flow declares no dimension.</p>
              : (
                <div className="grid gap-3 [grid-template-columns:repeat(auto-fill,minmax(19rem,1fr))]">
                  {own.map((entry) => <DimensionCard key={entry.ref} entry={entry} onOpen={() => onOpen(entry.ref)} />)}
                </div>
              )}
          </section>
        );
      })}
      {elsewhere.length > 0 && (
        <p className="text-[12px] text-muted-foreground" data-testid="dimension-flows-elsewhere">
          Not built in this partition:{" "}
          {elsewhere.map((flow, index) => (
            <span key={flow.pipelineId}>
              {index > 0 && ", "}
              <RichTooltip body={flow.problem ?? `It builds in ${flow.partitions.join(", ") || "the partition its data-partition-id header names"}.`}>
                <span className="font-mono underline decoration-dotted underline-offset-2">{flow.name}</span>
              </RichTooltip>
            </span>
          ))}
        </p>
      )}
    </div>
  );
}
