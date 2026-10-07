import { useMemo, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { format } from "date-fns";
import { Clock3, Timer } from "lucide-react";
import { isApiError } from "@/api/client";
import { Badge } from "@/components/ui/badge";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { CopyButton } from "@/components/CopyButton";
import { IdChip } from "@/components/IdChip";
import { RelativeTime } from "@/components/RelativeTime";
import { OutcomePill } from "@/components/StatusBadge";
import { TraceLog } from "@/components/TraceLog";
import { downloadFileName } from "@/lib/download";
import { formatDurationSeconds, parseUtc } from "@/lib/time";
import { cn } from "@/lib/utils";
import { deliveryApi, deliveryRecordRoute, ledgerLabel, type DeliveryActivity, type DeliveryFlowScope, type ReversalSource } from "../../api/delivery";
import { Actor } from "./ActivityActor";
import { endingOf } from "./activityEnding";
import { prettyJson } from "./prettyJson";
import { ReversalCard } from "./ReversalCard";
import { ReverseButton } from "./ReverseButton";
import { DELIVERING_OPERATIONS } from "./runOutcome";
import { runLogLines } from "./runLogLines";

/** What a run is, on hover of its chip. */
const RUN_HINT = "The run: one execution of the flow, with its status, log and trace.";

/** What a submission is, on hover of its chip. */
const SUBMISSION_HINT = "The submission: the plan the run delivered under, which rows it read and how each record ended up.";

/** The caption over each part of the sheet, in the chrome caption voice. */
const CAPTION = "text-[11px] font-medium uppercase tracking-wider text-muted-foreground";

/** How many items of a list parameter show before the rest are counted; the copy carries every one. */
const LIST_SHOWN = 3;

/** One setting of an activity's parameters: its path through the recorded object, and its value. */
interface Setting {
  name: string;
  value: unknown;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** The settings of an object, nested objects opened into dotted paths, in the order they were recorded. */
function settingsOf(value: Record<string, unknown>, path: string, into: Setting[]): Setting[] {
  for (const [key, inner] of Object.entries(value)) {
    const name = path === "" ? key : `${path}.${key}`;
    if (isObject(inner) && Object.keys(inner).length > 0) {
      settingsOf(inner, name, into);
    } else {
      into.push({ name, value: inner });
    }
  }

  return into;
}

/** The recorded parameters as settings; null when they are not a JSON object, which is then shown as it was stored. */
function parseSettings(json: string): Setting[] | null {
  try {
    const parsed: unknown = JSON.parse(json);
    return isObject(parsed) ? settingsOf(parsed, "", []) : null;
  } catch {
    return null;
  }
}

/** A value with no content of its own, in the quiet voice. */
function Quiet({ children }: { children: ReactNode }) {
  return <span className="text-muted-foreground">{children}</span>;
}

/** One setting's value: a scalar as it is, a list as its first items and how many more, anything deeper as compact JSON. */
function SettingValue({ value }: { value: unknown }) {
  if (value === null) {
    return <Quiet>null</Quiet>;
  }

  if (typeof value === "string") {
    return value === "" ? <Quiet>empty</Quiet> : <>{value}</>;
  }

  if (Array.isArray(value)) {
    if (value.length === 0) {
      return <Quiet>none</Quiet>;
    }

    const shown = value.slice(0, LIST_SHOWN).map((item) => (typeof item === "string" ? item : JSON.stringify(item)));
    const more = value.length - shown.length;
    return (
      <>
        {shown.join(", ")}
        {more > 0 && <Quiet>{` and ${more.toLocaleString("en-US")} more`}</Quiet>}
      </>
    );
  }

  return isObject(value) ? <Quiet>{"{}"}</Quiet> : <>{JSON.stringify(value)}</>;
}

/**
 * The parameters the activity was started with, one setting a line with its value beside it: the few a run takes read at
 * a glance, where a JSON editor gave five lines of braces a box of their own. A long list (the record keys of a redeliver)
 * shows its first items and counts the rest; the copy button carries the whole document as JSON.
 */
function ActivityParameters({ json }: { json: string | null }) {
  const settings = json === null ? [] : parseSettings(json);
  return (
    <section className="flex shrink-0 flex-col gap-1.5" data-testid="delivery-activity-parameters">
      <div className="flex h-5 items-center justify-between">
        <h3 className={CAPTION}>Parameters</h3>
        {json !== null && (
          <CopyButton iconOnly label="Copy the parameters as JSON" text={() => prettyJson(json)} testId="delivery-activity-parameters-copy" />
        )}
      </div>
      {settings === null && (
        <pre className="max-h-48 overflow-auto rounded-md border border-border bg-card px-3 py-2 font-mono text-[12px] leading-5 whitespace-pre-wrap break-all">
          {json}
        </pre>
      )}
      {settings !== null && settings.length === 0 && <p className="text-[12px] text-muted-foreground">None recorded.</p>}
      {settings !== null && settings.length > 0 && (
        <dl className="grid max-h-48 grid-cols-[minmax(0,max-content)_minmax(0,1fr)] gap-x-6 gap-y-1 overflow-auto rounded-md border border-border bg-card px-3 py-2 font-mono text-[12px] leading-5">
          {settings.map((setting) => (
            <div key={setting.name} className="contents">
              <dt className="truncate text-muted-foreground" title={setting.name}>{setting.name}</dt>
              <dd className="min-w-0 break-all">
                <SettingValue value={setting.value} />
              </dd>
            </div>
          ))}
        </dl>
      )}
    </section>
  );
}

/**
 * The run log the activity captured, through the workbench's trace view: a line per event with its time and step, the
 * whole line and a copy behind each, and the whole log to copy or download as it was captured. It takes the height it
 * needs and no more, up to what the sheet leaves, then scrolls inside itself. A running activity has no log yet: the
 * ledger keeps it when the run ends, and the run's own trace streams it meanwhile.
 */
function ActivityLog({ activity }: { activity: DeliveryActivity }) {
  const log = activity.log;
  const lines = useMemo(() => (log === null ? [] : runLogLines(log)), [log]);

  if (log === null) {
    const none = activity.outcome === "running"
      ? "The log is kept when the run ends; its run's trace streams it until then."
      : activity.runId === null
        ? "This action captured no log."
        : "No log was captured for this activity; its run's trace has the full account.";
    return (
      <section className="flex flex-col gap-1.5">
        <h3 className={CAPTION}>Log</h3>
        <p className="text-[12px] text-muted-foreground" data-testid="delivery-activity-no-log">{none}</p>
      </section>
    );
  }

  return (
    <section className="flex min-h-0 flex-col gap-1.5">
      <h3 className={CAPTION}>Log</h3>
      <div className="flex min-h-0 flex-col overflow-hidden rounded-md border border-border bg-card" data-testid="delivery-activity-log">
        <TraceLog
          lines={lines}
          connected={false}
          ended
          failed={activity.outcome === "failed"}
          text={() => log}
          fileName={downloadFileName([activity.flowName, activity.kind, "activity", String(activity.activityId)], "log")}
          emptyEnded="The captured log is empty."
        />
      </div>
    </section>
  );
}

/** The ledger an activity was done to, as a request about its flow names it: the interface and the partition the entry names. */
function scopeOf(activity: DeliveryActivity): DeliveryFlowScope {
  return { interfaceName: activity.interface ?? null, partition: activity.namedPartition ?? null };
}

/** The source a reverse run reversed, as its recorded parameters name it; null for parameters that name none. */
function reversedBy(activity: DeliveryActivity): ReversalSource | null {
  if (activity.kind !== "reverse" || activity.parametersJson === null) {
    return null;
  }

  try {
    const parsed: unknown = JSON.parse(activity.parametersJson);
    if (isObject(parsed) && (parsed.source === "run" || parsed.source === "submission") && typeof parsed.sourceId === "string") {
      return { kind: parsed.source, id: parsed.sourceId };
    }
  } catch {
    return null;
  }

  return null;
}

/**
 * The reversal a reverse run worked on, as the ledger keeps it: its state, its records by outcome and its runs, followed live
 * while a run works on it. Kept to a part of the sheet and scrolled inside, so the log keeps the rest of the height.
 */
function ActivityReversal({ activity, pipelineId, source }: { activity: DeliveryActivity; pipelineId: string; source: ReversalSource }) {
  const scope = scopeOf(activity);
  const found = useQuery({
    queryKey: ["delivery", "reversals", pipelineId, scope.interfaceName, scope.partition, source.kind, source.id],
    queryFn: () => deliveryApi.reversals(pipelineId, scope, source),
  });
  const reversalId = found.data?.[0]?.reversalId ?? null;
  if (reversalId === null) {
    return null;
  }

  return (
    <section className="flex max-h-[45%] shrink-0 flex-col gap-1.5" data-testid="delivery-activity-reversal">
      <h3 className={CAPTION}>Reversal</h3>
      <div className="min-h-0 overflow-y-auto">
        <ReversalCard reversalId={reversalId} />
      </div>
    </section>
  );
}

/** How long an activity took, with when it completed on hover; a running one says so. */
function Took({ activity }: { activity: DeliveryActivity }) {
  if (activity.completedUtc === null) {
    return <span>running</span>;
  }

  const seconds = Math.max(0, (parseUtc(activity.completedUtc).getTime() - parseUtc(activity.startedUtc).getTime()) / 1000);
  return (
    <span className="inline-flex items-center gap-1.5" title={`Completed ${format(parseUtc(activity.completedUtc), "MMM d, HH:mm:ss")}`}>
      <Timer className="size-3.5 shrink-0" aria-hidden />
      <span className="font-mono tabular-nums">{formatDurationSeconds(seconds)}</span>
    </span>
  );
}

/**
 * The head of the sheet: how it ended and what it was, the summary it recorded (the error, when it failed), who started
 * it, when and for how long, and the ids it worked on, each a chip that opens it and copies it whole. A run that sends
 * records offers its reversal beside them once it has ended, when it left anything to reverse (docs/reversal-plan.md).
 */
function ActivityHeader({ activity }: { activity: DeliveryActivity }) {
  const ending = endingOf(activity);
  const pipelineId = typeof activity.pipelineId === "string" ? activity.pipelineId : null;
  const reversible = activity.runId !== null && pipelineId !== null && activity.outcome !== "running" && DELIVERING_OPERATIONS.includes(activity.kind);
  const scope = scopeOf(activity);
  return (
    <SheetHeader className="gap-2 border-b border-border px-5 pt-4 pr-12 pb-3">
      <div className="flex min-w-0 items-center gap-2.5">
        <OutcomePill tone={ending.tone} label={ending.word} icon={ending.icon} testId="delivery-activity-outcome" />
        <SheetTitle className="flex min-w-0 items-baseline gap-1.5 text-[15px]">
          <span className="shrink-0 capitalize">{activity.kind}</span>
          <span className="shrink-0 font-normal text-muted-foreground">on</span>
          <span className="truncate font-mono text-[13px]" title={activity.flowName}>{activity.flowName}</span>
        </SheetTitle>
      </div>
      <SheetDescription
        className={cn("text-[13px] break-words", activity.outcome === "failed" ? "text-destructive" : "text-foreground")}
        data-testid="delivery-activity-summary"
      >
        {activity.summary ?? ending.label}
      </SheetDescription>
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-[12px] text-muted-foreground" data-testid="delivery-activity-facts">
        <Actor actor={activity.actor} nameWidth={320} />
        <span className="inline-flex items-center gap-1.5">
          <Clock3 className="size-3.5 shrink-0" aria-hidden />
          <RelativeTime value={activity.startedUtc} />
        </span>
        <Took activity={activity} />
        {activity.partition && (
          <Badge variant="secondary" className="font-mono text-[10px]" title="The OSDU partition this activity's ledger is kept under">
            {activity.partition}
          </Badge>
        )}
      </div>
      {(activity.deliveryKey !== null || activity.submissionId !== null || activity.runId !== null) && (
        <div className="flex flex-wrap items-center gap-1.5 pt-0.5" data-testid="delivery-activity-ids">
          {activity.deliveryKey !== null && (
            <IdChip
              label="record"
              value={activity.deliveryKey}
              display={activity.deliveryKey.toLowerCase()}
              to={deliveryRecordRoute({ flowId: activity.flowId, deliveryKey: activity.deliveryKey })}
              testId="delivery-activity-record"
              copyTestId="delivery-activity-record-copy"
            />
          )}
          {activity.submissionId !== null && (
            <span className="inline-flex max-w-full" title={SUBMISSION_HINT}>
              <IdChip
                label="submission"
                value={activity.submissionId}
                display={activity.submissionId.toLowerCase()}
                to={`/delivery/submissions/${activity.submissionId}`}
                testId="delivery-activity-submission"
                copyTestId="delivery-activity-submission-copy"
              />
            </span>
          )}
          {activity.runId !== null && (
            <span className="inline-flex max-w-full" title={RUN_HINT}>
              <IdChip
                label="run"
                value={activity.runId}
                display={activity.runId.toLowerCase()}
                to={`/runs/${activity.runId}`}
                testId="delivery-activity-run"
                copyTestId="delivery-activity-run-copy"
              />
            </span>
          )}
          {reversible && (
            <span className="ml-auto">
              <ReverseButton
                pipelineId={pipelineId}
                flowScope={scope}
                flowName={ledgerLabel(activity.flowName, scope)}
                source={{ kind: "run", id: activity.runId! }}
                label="Reverse this run"
                testId="delivery-activity-reverse"
              />
            </span>
          )}
        </div>
      )}
    </SheetHeader>
  );
}

/**
 * One entry of the audit trail, whole, in a sheet as wide as the window leaves once the workbench's menu is kept clear
 * (the activity bar and the side bar at its widest), never narrower than 48rem: the head says what it was and how it
 * ended, the parameters sit in a compact list, and the captured log takes the rest of the height.
 */
export function DeliveryActivitySheet({ activityId, onClose }: { activityId: number | null; onClose: () => void }) {
  const detail = useQuery({
    queryKey: ["delivery", "activity", activityId],
    queryFn: () => deliveryApi.activity(activityId!),
    enabled: activityId !== null,
    // A running activity's ending and log arrive when it completes.
    refetchInterval: (query) => (query.state.data?.outcome === "running" ? 5000 : false),
  });
  const data = detail.data;
  const reversed = data === undefined ? null : reversedBy(data);
  const problem = detail.error === null
    ? null
    : isApiError(detail.error) ? detail.error.detail ?? detail.error.title : String(detail.error);

  return (
    <Sheet open={activityId !== null} onOpenChange={(open) => { if (!open) { onClose(); } }}>
      <SheetContent className="w-full gap-0 sm:max-w-[max(48rem,calc(100vw_-_468px))]" data-testid="delivery-activity-detail">
        {data === undefined
          ? (
            <SheetHeader className="border-b border-border px-5 pt-4 pr-12 pb-3">
              <SheetTitle>Activity</SheetTitle>
              <SheetDescription className={cn(problem !== null && "text-destructive")}>
                {problem === null ? "Loading." : `The activity could not be read: ${problem}`}
              </SheetDescription>
            </SheetHeader>
          )
          : (
            <>
              <ActivityHeader activity={data} />
              <div className="flex min-h-0 flex-1 flex-col gap-4 px-5 pt-3 pb-5">
                <ActivityParameters json={data.parametersJson} />
                {reversed !== null && typeof data.pipelineId === "string" && (
                  <ActivityReversal activity={data} pipelineId={data.pipelineId} source={reversed} />
                )}
                <ActivityLog activity={data} />
              </div>
            </>
          )}
      </SheetContent>
    </Sheet>
  );
}
