import { useEffect, useMemo, useRef } from "react";
import { useQuery } from "@tanstack/react-query";
import { AlertTriangle, CalendarClock, CheckCircle2, CircleDashed, Link2, PauseCircle } from "lucide-react";
import { EmptyState } from "@/components/EmptyState";
import { ExplainMark } from "@/components/ExplainTip";
import { RelativeTime } from "@/components/RelativeTime";
import { Skeleton } from "@/components/ui/skeleton";
import { useOwnedPanel } from "@/layout/workbench/useOwnedPanel";
import { cn } from "@/lib/utils";
import {
  orderApi,
  type OrderSchedule,
  type OrderScheduleFirst,
  type OrderScheduleOrder,
  type OrderScheduleReason,
  type OrderTiming,
} from "../../../api/order";
import { ProblemView } from "../TemplateSheet";
import { useWindowFit } from "../useWindowFit";
import { OrderGraph, type OrderGraphLink, type OrderGraphNode } from "./OrderGraph";

/** What stays under the graph: the page's padding and the workbench's status bar. */
const BELOW = 24;
const MIN_GRAPH_HEIGHT = 320;
const PANEL = "order-schedule:";

/** When a schedule fires, in a few words. */
function cadence(schedule: OrderSchedule): string {
  if (!schedule.fires) {
    return "paused or disabled";
  }

  if (schedule.cron !== null) {
    return `cron ${schedule.cron}${schedule.timezone !== "UTC" ? ` (${schedule.timezone})` : ""}`;
  }

  if (schedule.intervalSeconds !== null) {
    const seconds = schedule.intervalSeconds;
    return seconds % 86400 === 0 ? `every ${seconds / 86400} d`
      : seconds % 3600 === 0 ? `every ${seconds / 3600} h`
        : seconds % 60 === 0 ? `every ${seconds / 60} min` : `every ${seconds} s`;
  }

  return "after other schedules";
}

const TIMING: Record<OrderTiming, { label: string; icon: typeof CheckCircle2; tone: "fine" | "warning" | "quiet" }> = {
  chained: { label: "Chained", icon: Link2, tone: "fine" },
  firesFirst: { label: "Fires first", icon: CheckCircle2, tone: "fine" },
  firesAfter: { label: "Fires after", icon: AlertTriangle, tone: "warning" },
  notFiring: { label: "Not firing", icon: PauseCircle, tone: "warning" },
  unknown: { label: "No next fire", icon: CircleDashed, tone: "quiet" },
};

/** Why a timing is what it is, for the schedule that runs best after `first.schedule`. */
function timingText(later: OrderSchedule, first: OrderScheduleFirst): string {
  const earlier = first.schedule;
  switch (first.timing) {
    case "chained":
      return `${later.name} fires after ${earlier.name}: it names it in after:, directly or through the schedules it chains behind.`;
    case "firesFirst":
      return `${earlier.name} fires next, before ${later.name} does, so what it delivers is there when ${later.name}'s flows run.`;
    case "firesAfter":
      return `${later.name} fires next, before ${earlier.name} does, so its flows run before the records they refer to are delivered. `
        + (first.sameRepository
          ? `Chain it behind ${earlier.name} with after: [${earlier.name}], or move its clock after ${earlier.name}'s.`
          : `${earlier.name} is another repository's schedule, which after: cannot name: move ${later.name}'s clock after ${earlier.name}'s.`);
    case "notFiring":
      return `${earlier.name} is paused or disabled, so it delivers nothing ${later.name}'s flows can rely on.`;
    default:
      return `One of them has no next fire to compare: it fires after other schedules, or the scheduler has not planned it yet.`;
  }
}

function TimingMark({ timing, testId }: { timing: OrderTiming; testId: string }) {
  const { label, icon: Icon, tone } = TIMING[timing];
  return (
    <span className="inline-flex items-center gap-1 rounded border px-1.5 py-px text-[11.5px]" data-testid={testId}>
      <Icon className={cn("size-3", tone === "warning" ? "text-warning" : tone === "fine" ? "text-muted-foreground" : "text-muted-foreground/60")} aria-hidden />
      {label}
    </span>
  );
}

function flowLabel(flow: string, face: string | null): string {
  return face === null ? flow : `${flow}/${face}`;
}

function ReasonTable({ reasons, testId }: { reasons: OrderScheduleReason[]; testId: string }) {
  return (
    <table className="w-full table-fixed text-[12px]" data-testid={testId}>
      <thead className="text-left text-[11px] uppercase tracking-wider text-muted-foreground">
        <tr>
          <th className="w-[26%] py-1 pr-2 font-medium">Flow</th>
          <th className="w-[26%] py-1 pr-2 font-medium">Refers to</th>
          <th className="py-1 pr-2 font-medium">Delivered by</th>
          <th className="w-[16%] py-1 font-medium">Partitions</th>
        </tr>
      </thead>
      <tbody>
        {reasons.map((reason) => (
          <tr key={`${reason.flow}|${reason.interface ?? ""}|${reason.refersTo}|${reason.providerRepository}|${reason.providerFlow}|${reason.providerInterface ?? ""}`} className="border-t align-top">
            <td className="truncate py-1 pr-2 font-mono" title={`${flowLabel(reason.flow, reason.interface)} delivers ${reason.entityType}`}>
              {flowLabel(reason.flow, reason.interface)}
            </td>
            <td className="truncate py-1 pr-2" title={reason.refersTo}>{reason.refersTo}</td>
            <td className="truncate py-1 pr-2 font-mono" title={`${reason.providerRepository}: ${flowLabel(reason.providerFlow, reason.providerInterface)}`}>
              <span className="text-muted-foreground">{reason.providerRepository}: </span>
              {flowLabel(reason.providerFlow, reason.providerInterface)}
            </td>
            <td className="truncate py-1" title={reason.partitions.join(", ")}>
              {reason.partitions.map((p) => (p === "*" ? "every" : p)).join(", ")}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/** One schedule in the bottom panel: when it fires, the schedules that run best before it and why, and what no schedule delivers. */
function ScheduleDetail({ order, circle }: { order: OrderScheduleOrder; circle: OrderSchedule[] | null }) {
  const schedule = order.schedule;
  return (
    <div className="flex flex-col gap-4 p-3" data-testid="order-schedule-detail">
      <div className="flex flex-wrap items-baseline gap-x-4 gap-y-1 text-[12.5px]">
        <span className="font-medium">{schedule.name}</span>
        <span className="text-muted-foreground">{schedule.repository}</span>
        <span><span className="text-muted-foreground">Fires </span>{cadence(schedule)}</span>
        <span><span className="text-muted-foreground">Next </span><RelativeTime value={schedule.nextFireUtc} /></span>
        <span><span className="text-muted-foreground">Last </span><RelativeTime value={schedule.lastFireUtc} /></span>
      </div>

      {circle !== null && (
        <p className="flex items-start gap-1.5 text-[12.5px]" data-testid="order-schedule-circle">
          <AlertTriangle className="mt-0.5 size-3.5 shrink-0 text-warning" aria-hidden />
          {`${circle.map((s) => s.name).join(", ")} each deliver records another of them refers to, so whichever fires first runs some flows before what they refer to is delivered. Moving a flow to another schedule breaks the circle.`}
        </p>
      )}

      <section className="flex flex-col gap-2">
        <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Runs best after</h3>
        {order.runFirst.length === 0 && (
          <p className="text-[12.5px] text-muted-foreground">No other schedule delivers what its flows refer to.</p>
        )}
        {order.runFirst.map((first) => (
          <div key={first.schedule.id} className="flex flex-col gap-1.5 rounded-md border p-2" data-testid={`order-first-${first.schedule.name}`}>
            <div className="flex flex-wrap items-center gap-2 text-[12.5px]">
              <span className="font-medium">{first.schedule.name}</span>
              {!first.sameRepository && <span className="text-muted-foreground">{first.schedule.repository}</span>}
              <TimingMark timing={first.timing} testId={`order-first-${first.schedule.name}-timing`} />
              <span className="text-muted-foreground">next <RelativeTime value={first.schedule.nextFireUtc} /></span>
            </div>
            <p className="text-[12px] text-muted-foreground">{timingText(schedule, first)}</p>
            <ReasonTable reasons={first.because} testId={`order-first-${first.schedule.name}-reasons`} />
          </div>
        ))}
      </section>

      {order.unscheduled.length > 0 && (
        <section className="flex flex-col gap-2">
          <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Refers to deliveries no schedule runs</h3>
          <ReasonTable reasons={order.unscheduled} testId="order-unscheduled-reasons" />
        </section>
      )}
    </div>
  );
}

/**
 * The schedules that run deliveries, with an arrow from each schedule to the ones whose flows refer to what it delivers.
 * Nothing here orders a run: inside a schedule lineage's waves run every flow after the flows it refers to, and across
 * schedules this shows whether their fires fall that way. A dashed arrow is an order the timing does not keep.
 */
export function ScheduleOrderTab() {
  const overview = useQuery({ queryKey: ["delivery", "order", "schedules"], queryFn: orderApi.schedules });
  const { ownedId, show, close } = useOwnedPanel(PANEL);
  const frame = useRef<HTMLDivElement>(null);
  useWindowFit(frame, BELOW, MIN_GRAPH_HEIGHT, undefined, "height");

  const data = overview.data;
  const circleOf = useMemo(() => {
    const map = new Map<string, OrderSchedule[]>();
    for (const circle of data?.circles ?? []) {
      for (const schedule of circle) {
        map.set(schedule.id, circle);
      }
    }

    return map;
  }, [data]);

  const { nodes, links } = useMemo(() => {
    const graphNodes: OrderGraphNode[] = (data?.schedules ?? []).map((order) => ({
      id: order.schedule.id,
      title: order.schedule.name,
      subtitle: `${order.schedule.repository} · ${cadence(order.schedule)}`,
      warning: circleOf.has(order.schedule.id)
        ? "Refers to records another schedule delivers, which refers to records this one delivers."
        : order.outOfOrder ? "Fires before a schedule that delivers what its flows refer to, or after one that does not fire." : undefined,
    }));
    const graphLinks: OrderGraphLink[] = (data?.schedules ?? []).flatMap((order) => order.runFirst.map((first) => ({
      id: `${first.schedule.id}>${order.schedule.id}`,
      from: first.schedule.id,
      to: order.schedule.id,
      tone: TIMING[first.timing].tone === "warning" ? "warning" as const : first.timing === "unknown" ? "quiet" as const : "neutral" as const,
      title: `${first.schedule.name} runs best before ${order.schedule.name}: ${TIMING[first.timing].label}`,
    })));
    return { nodes: graphNodes, links: graphLinks };
  }, [data, circleOf]);

  // The panel shows the schedule picked in the graph, read again whenever the overview is.
  const selected = ownedId;
  useEffect(() => {
    if (selected === null || data === undefined) {
      return;
    }

    const order = data.schedules.find((o) => o.schedule.id === selected);
    if (order === undefined) {
      close();
      return;
    }

    show(selected, `Schedule · ${order.schedule.name}`, <ScheduleDetail order={order} circle={circleOf.get(selected) ?? null} />);
  }, [data, selected, circleOf, show, close]);

  const pick = (id: string | null) => {
    if (id === null || data === undefined) {
      close();
      return;
    }

    const order = data.schedules.find((o) => o.schedule.id === id);
    if (order !== undefined) {
      show(id, `Schedule · ${order.schedule.name}`, <ScheduleDetail order={order} circle={circleOf.get(id) ?? null} />);
    }
  };

  if (overview.isError) {
    return <ProblemView error={overview.error} testId="order-schedules-error" />;
  }

  const outOfOrder = data?.schedules.filter((o) => o.outOfOrder).length ?? 0;
  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-[12.5px]" data-testid="order-schedules-facts">
        {data === undefined ? <Skeleton className="h-4 w-64" /> : (
          <>
            <span><span className="font-mono tabular-nums">{data.schedules.length}</span> schedule{data.schedules.length === 1 ? "" : "s"} running deliveries</span>
            <span className={cn(outOfOrder === 0 && "text-muted-foreground")}>
              <span className="font-mono tabular-nums">{outOfOrder}</span> out of order
            </span>
            <span className={cn(data.circles.length === 0 && "text-muted-foreground")}>
              <span className="font-mono tabular-nums">{data.circles.length}</span> circle{data.circles.length === 1 ? "" : "s"}
            </span>
            <ExplainMark
              title="Schedules"
              testId="order-schedules-about"
              text={"An arrow goes from a schedule to one whose flows refer to records it delivers: that one runs best after it. "
                + "Inside a schedule, lineage's waves already run every flow after the flows it refers to; across schedules nothing waits, "
                + "so this shows whether their fires fall that way. A dashed arrow is an order the timing does not keep: the later schedule "
                + "fires next, or the earlier one does not fire. A schedule of the same repository can be chained with after:; "
                + "another repository's can only be timed."}
            />
          </>
        )}
      </div>
      <div ref={frame} className="h-80 min-h-0">
        {data !== undefined && data.schedules.length === 0 ? (
          <EmptyState
            icon={<CalendarClock />}
            title="No schedule runs a delivery"
            description="A delivery flow joins a schedule with schedule: in its document; once a repository with one is synced, its schedules show here."
            data-testid="order-schedules-empty"
          />
        ) : (
          <OrderGraph nodes={nodes} links={links} selected={selected} onSelect={pick} testId="order-schedules-graph" />
        )}
      </div>
    </div>
  );
}
