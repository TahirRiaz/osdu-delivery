import { useMemo, useRef, useState, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { AlertTriangle, CalendarClock, CheckCircle2, CircleDashed, Link2, PauseCircle } from "lucide-react";
import { EmptyState } from "@/components/EmptyState";
import { ExplainMark, ExplainTip } from "@/components/ExplainTip";
import { RelativeTime } from "@/components/RelativeTime";
import { Skeleton } from "@/components/ui/skeleton";
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
import { OrderGraph, OrderPanelSection as Section, type OrderGraphDetail, type OrderGraphLink, type OrderGraphNode } from "./OrderGraph";

/** What stays under the graph: the page's padding and the workbench's status bar. */
const BELOW = 24;
const MIN_GRAPH_HEIGHT = 320;

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

/** A timing as a neutral chip led by its glyph, with why it is what it is behind it. */
function TimingMark({ timing, text, testId }: { timing: OrderTiming; text: string; testId: string }) {
  const { label, icon: Icon, tone } = TIMING[timing];
  return (
    <ExplainTip title={label} text={text} testId={testId} side="right">
      <span className="inline-flex shrink-0 cursor-default items-center gap-1 rounded border px-1.5 py-px text-[11.5px]" data-testid={testId}>
        <Icon className={cn("size-3", tone === "warning" ? "text-warning" : tone === "fine" ? "text-muted-foreground" : "text-muted-foreground/60")} aria-hidden />
        {label}
      </span>
    </ExplainTip>
  );
}

function flowLabel(flow: string, face: string | null): string {
  return face === null ? flow : `${flow}/${face}`;
}

function partitionsLabel(partitions: string[]): string {
  return partitions.map((p) => (p === "*" ? "every" : p)).join(", ");
}

/** A schedule's name that selects it in the graph. */
function ScheduleLink({ schedule, onPick }: { schedule: OrderSchedule; onPick: (id: string) => void }) {
  return (
    <button
      type="button"
      className="min-w-0 truncate text-left text-[12.5px] font-medium hover:underline focus-visible:outline-1 focus-visible:outline-ring"
      onClick={() => onPick(schedule.id)}
      title={`Show ${schedule.name}`}
    >
      {schedule.name}
    </button>
  );
}

/** A label and its value on a line of a reason; a long name wraps under itself rather than being cut. */
function ReasonLine({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex min-w-0 items-baseline gap-2 text-[11.5px]">
      <span className="w-[5.5rem] shrink-0 text-muted-foreground">{label}</span>
      <span className="min-w-0 [overflow-wrap:anywhere]">{children}</span>
    </div>
  );
}

/**
 * The references behind an order, flow by flow: what each refers to and which delivery writes it, with the partitions it
 * holds in when the panel's references do not all hold in the same ones.
 */
function ReasonList({ reasons, partitions, testId }: { reasons: OrderScheduleReason[]; partitions: boolean; testId: string }) {
  const byFlow = new Map<string, OrderScheduleReason[]>();
  for (const reason of reasons) {
    const flow = flowLabel(reason.flow, reason.interface);
    byFlow.set(flow, [...(byFlow.get(flow) ?? []), reason]);
  }

  return (
    <ul className="flex flex-col gap-2 border-l-2 pl-2.5" data-testid={testId}>
      {[...byFlow].map(([flow, flowReasons]) => (
        <li key={flow} className="flex min-w-0 flex-col gap-1">
          <span className="font-mono text-[12px] [overflow-wrap:anywhere]" title={`${flow} delivers ${flowReasons[0].entityType}`}>{flow}</span>
          {flowReasons.map((reason) => {
            const provider = flowLabel(reason.providerFlow, reason.providerInterface);
            return (
              <div key={`${reason.refersTo}|${reason.providerRepository}|${provider}`} className="flex flex-col">
                <ReasonLine label="refers to">{reason.refersTo}</ReasonLine>
                <ReasonLine label="delivered by">
                  <span className="text-muted-foreground">{reason.providerRepository}: </span>
                  <span className="font-mono">{provider}</span>
                </ReasonLine>
                {partitions && <ReasonLine label="partitions">{partitionsLabel(reason.partitions)}</ReasonLine>}
              </div>
            );
          })}
        </li>
      ))}
    </ul>
  );
}

/**
 * One schedule in the graph's panel: when it fires, the schedules it runs best after and why, the schedules that run best
 * after it, and the references no schedule delivers. A schedule named here selects it in the graph.
 */
function ScheduleDetail({ order, later, circle, onPick }: {
  order: OrderScheduleOrder;
  /** Each schedule that runs best after this one, with what it says about this one. */
  later: { order: OrderScheduleOrder; first: OrderScheduleFirst }[];
  circle: OrderSchedule[] | null;
  onPick: (id: string) => void;
}) {
  const schedule = order.schedule;
  const others = circle?.filter((s) => s.id !== schedule.id) ?? [];
  // Partitions said once when every reference here holds in the same ones, as in an estate of one partition.
  const held = new Set([...order.runFirst.flatMap((f) => f.because), ...order.unscheduled].map((r) => partitionsLabel(r.partitions)));
  const oneSet = held.size === 1 ? [...held][0] : null;
  return (
    <div className="flex flex-col" data-testid="order-schedule-detail">
      <Section title="Fires">
        <dl className="grid grid-cols-[5.5rem_1fr] gap-x-2 gap-y-1 text-[12.5px]">
          <dt className="text-muted-foreground">When</dt>
          <dd className="min-w-0 [overflow-wrap:anywhere]">{cadence(schedule)}</dd>
          <dt className="text-muted-foreground">Next</dt>
          <dd><RelativeTime value={schedule.nextFireUtc} /></dd>
          <dt className="text-muted-foreground">Last</dt>
          <dd><RelativeTime value={schedule.lastFireUtc} /></dd>
          {oneSet !== null && (
            <>
              <dt className="text-muted-foreground">Partitions</dt>
              <dd className="min-w-0 [overflow-wrap:anywhere]">{oneSet}</dd>
            </>
          )}
        </dl>
        {circle !== null && (
          <div className="flex items-start gap-1.5 text-[12.5px]" data-testid="order-schedule-circle">
            <AlertTriangle className="mt-0.5 size-3.5 shrink-0 text-warning" aria-hidden />
            <span className="min-w-0">
              In a circle with{" "}
              {others.map((s, index) => (
                <span key={s.id}>
                  {index > 0 && ", "}
                  <ScheduleLink schedule={s} onPick={onPick} />
                </span>
              ))}
            </span>
            <ExplainMark
              title="A circle"
              testId="order-schedule-circle-about"
              text={`${circle.map((s) => s.name).join(", ")} each deliver records another of them refers to, so whichever fires first runs some flows before what they refer to is delivered. Moving a flow to another schedule breaks the circle.`}
            />
          </div>
        )}
      </Section>

      <Section title="Runs best after" note={order.runFirst.length}>
        {order.runFirst.length === 0 && (
          <p className="text-[12.5px] text-muted-foreground">No other schedule delivers what its flows refer to.</p>
        )}
        <ul className="flex flex-col gap-3">
          {order.runFirst.map((first) => (
            <li key={first.schedule.id} className="flex min-w-0 flex-col gap-1.5" data-testid={`order-first-${first.schedule.name}`}>
              <div className="flex min-w-0 items-center gap-2">
                <ScheduleLink schedule={first.schedule} onPick={onPick} />
                <TimingMark timing={first.timing} text={timingText(schedule, first)} testId={`order-first-${first.schedule.name}-timing`} />
              </div>
              <div className="flex min-w-0 gap-x-3 text-[11.5px] text-muted-foreground" title={cadence(first.schedule)}>
                {!first.sameRepository && <span className="truncate">{first.schedule.repository}</span>}
                <span className="shrink-0">next <RelativeTime value={first.schedule.nextFireUtc} /></span>
              </div>
              <ReasonList reasons={first.because} partitions={oneSet === null} testId={`order-first-${first.schedule.name}-reasons`} />
            </li>
          ))}
        </ul>
      </Section>

      <Section title="Runs best before" note={later.length}>
        {later.length === 0 && (
          <p className="text-[12.5px] text-muted-foreground">No other schedule's flows refer to what it delivers.</p>
        )}
        <ul className="flex flex-col gap-2">
          {later.map(({ order: after, first }) => {
            const types = [...new Set(first.because.map((r) => r.refersTo))];
            return (
              <li key={after.schedule.id} className="flex min-w-0 flex-col gap-0.5" data-testid={`order-later-${after.schedule.name}`}>
                <div className="flex min-w-0 items-center gap-2">
                  <ScheduleLink schedule={after.schedule} onPick={onPick} />
                  <TimingMark timing={first.timing} text={timingText(after.schedule, first)} testId={`order-later-${after.schedule.name}-timing`} />
                </div>
                <span className="text-[11.5px] text-muted-foreground [overflow-wrap:anywhere]">
                  {first.sameRepository ? "" : `${after.schedule.repository} · `}refers to {types.join(", ")}
                </span>
              </li>
            );
          })}
        </ul>
      </Section>

      {order.unscheduled.length > 0 && (
        <Section title="Refers to deliveries no schedule runs">
          <ReasonList reasons={order.unscheduled} partitions={oneSet === null} testId="order-unscheduled-reasons" />
        </Section>
      )}
    </div>
  );
}

/**
 * The schedules that run deliveries, with an arrow from each schedule to the ones whose flows refer to what it delivers.
 * Nothing here orders a run: inside a schedule lineage's waves run every flow after the flows it refers to, and across
 * schedules this shows whether their fires fall that way. A dashed arrow is an order the timing does not keep. A click on
 * a schedule slides its detail in from the left.
 */
export function ScheduleOrderTab() {
  const overview = useQuery({ queryKey: ["delivery", "order", "schedules"], queryFn: orderApi.schedules });
  const [selected, setSelected] = useState<string | null>(null);
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

  // The panel shows the schedule picked in the graph as the overview reads it now; one no longer there closes it.
  const order = selected === null ? undefined : data?.schedules.find((o) => o.schedule.id === selected);
  const detail: OrderGraphDetail | null = order === undefined || data === undefined ? null : {
    title: order.schedule.name,
    subtitle: order.schedule.repository,
    testId: "order-schedule-panel",
    body: (
      <ScheduleDetail
        order={order}
        later={data.schedules.flatMap((o) => o.runFirst.filter((f) => f.schedule.id === order.schedule.id).map((first) => ({ order: o, first })))}
        circle={circleOf.get(order.schedule.id) ?? null}
        onPick={setSelected}
      />
    ),
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
                + "another repository's can only be timed. A click on a schedule shows why it runs where it does."}
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
          <OrderGraph nodes={nodes} links={links} selected={detail === null ? null : selected} detail={detail} onSelect={setSelected} testId="order-schedules-graph" />
        )}
      </div>
    </div>
  );
}
