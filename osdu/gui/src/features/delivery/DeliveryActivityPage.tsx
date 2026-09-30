import { useState } from "react";
import { Link as RouterLink } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import {
  CalendarClock, CheckCircle2, CircleDashed, CircleDot, CircleHelp, CircleSlash, Loader2, Server, Terminal, User, XCircle,
  type LucideIcon,
} from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Switch } from "@/components/ui/switch";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { cn } from "@/lib/utils";
import { deliveryApi, deliveryRecordRoute, type DeliveryActivity } from "../../api/delivery";
import { CodeView } from "@/components/CodeView";
import { FilterBar } from "@/components/FilterBar";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { PagedTable, type Column } from "@/components/PagedTable";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import { useActivePartition } from "./activePartition";
import { RecordRef, RunRef, SubmissionRef } from "./DeliveryRefs";
import { Fact, FactGrid, NoFact } from "./Facts";
import { prettyJson } from "./prettyJson";

const ALL = "all";
const KINDS = ["deliver", "intake", "drain", "verify", "probe", "sync", "release", "redeliver", "delete"];
const OUTCOMES = ["running", "completed", "failed", "cancelled"];

/** What the idle-runs switch says on hover: which runs it shows, and why they are left out otherwise. */
const IDLE_HINT = "Runs that completed having changed nothing: a schedule fired and found nothing new to plan, send or hold. "
  + "They stay in the ledger as the proof that it fired; the trail leaves them out unless you show them.";

/** What a run is, on hover of its link in the detail. */
const RUN_HINT = "The run: one execution of the flow, with its status, log and trace.";

/** What a submission is, on hover of its link in the detail. */
const SUBMISSION_HINT = "The submission: the plan the run delivered under, which rows it read and how each record ended up.";

interface Ending {
  icon: LucideIcon;
  tone: string;
  label: string;
}

/** How each ending reads at the head of a result: an icon in its tone, named on hover and to a screen reader. */
const ENDINGS: Record<DeliveryActivity["outcome"] | "idle", Ending> = {
  completed: { icon: CheckCircle2, tone: "text-success", label: "Completed" },
  failed: { icon: XCircle, tone: "text-destructive", label: "Failed" },
  cancelled: { icon: CircleSlash, tone: "text-warning", label: "Cancelled" },
  running: { icon: Loader2, tone: "animate-spin text-info", label: "Running" },
  idle: { icon: CircleDashed, tone: "text-muted-foreground", label: "Completed, changed nothing" },
};

function endingOf(row: DeliveryActivity): Ending {
  return row.idle ? ENDINGS.idle : ENDINGS[row.outcome] ?? { icon: CircleDot, tone: "text-muted-foreground", label: row.outcome };
}

/**
 * How the ending and the summary share one cell: the icon says how it ended, the text what it did or why it failed. An
 * idle run says only that it changed nothing, whatever its summary lists, and keeps the summary on hover: a run recorded
 * before summaries left out their zeros would otherwise fill the cell with them.
 */
function Result({ row }: { row: DeliveryActivity }) {
  const ending = endingOf(row);
  const Icon = ending.icon;
  return (
    <span className="flex min-w-0 items-center gap-1.5">
      <span className="inline-flex shrink-0" title={ending.label}>
        <Icon className={cn("size-3.5", ending.tone)} aria-hidden />
        <span className="sr-only">{ending.label}</span>
      </span>
      {row.idle
        ? <span className="truncate text-muted-foreground" title={row.summary ?? undefined}>changed nothing</span>
        : (
          <span className="min-w-0 flex-1">
            <TruncatedText
              text={row.summary ?? ending.label.toLowerCase()}
              maxWidth={1600}
              className={cn(row.outcome === "failed" && "text-destructive")}
            />
          </span>
        )}
    </span>
  );
}

/** What started an activity, by the prefix of its actor: the icon, and what it is called on hover. */
const STARTERS: { prefix: string; icon: LucideIcon; what: string }[] = [
  { prefix: "schedule:", icon: CalendarClock, what: "A schedule" },
  { prefix: "cli:", icon: Terminal, what: "The command line on a workstation" },
  { prefix: "service:", icon: Server, what: "A service of the control plane" },
  { prefix: "manual:", icon: User, what: "Started by hand" },
  { prefix: "user:", icon: User, what: "A user" },
  { prefix: "gui:", icon: User, what: "A user" },
];

/**
 * An actor as the trail shows it: what started it as an icon, then its name; the whole actor on hover. In a narrow table
 * the icon stands alone and the name is left to the hover and to a screen reader.
 */
function Actor({ actor }: { actor: string }) {
  const unknown = actor === "unknown";
  const starter = STARTERS.find((s) => actor.startsWith(s.prefix));
  const Icon = unknown ? CircleHelp : starter?.icon ?? User;
  const name = starter === undefined ? actor : actor.slice(starter.prefix.length);
  const hint = unknown ? "Recorded before the platform named who or what started a run." : `${starter?.what ?? "A user"}: ${actor}`;
  return (
    <span className="inline-flex max-w-full items-center gap-1.5" title={hint}>
      <Icon className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />
      <span className="min-w-0 @max-3xl/table:sr-only">
        {unknown
          ? <span className="text-[12px] text-muted-foreground">unknown</span>
          : <TruncatedText text={name === "" ? actor : name} mono maxWidth={120} />}
      </span>
    </span>
  );
}

/**
 * What an activity worked on, the most particular first: the record it acted on, else the submission it delivered under,
 * else the run it was. The detail names all three in full.
 */
function Target({ row }: { row: DeliveryActivity }) {
  if (row.deliveryKey !== null) {
    return <RecordRef flowId={row.flowId} deliveryKey={row.deliveryKey} />;
  }

  if (row.submissionId !== null) {
    return <SubmissionRef submissionId={row.submissionId} />;
  }

  return row.runId !== null ? <RunRef runId={row.runId} /> : <NoFact />;
}

/** A whole id in the detail, as a link to its page. */
function IdLink({ to, id, hint }: { to: string; id: string; hint: string }) {
  return (
    <RouterLink to={to} className="break-all font-mono text-[12px] text-primary hover:underline" title={hint}>
      {id.toLowerCase()}
    </RouterLink>
  );
}

/**
 * The audit trail across every delivery flow in the workbench's partition: who did what, when, and how it ended. The runs
 * that changed nothing are left out unless asked for, and counted. Each entry opens with the ids it worked on, its
 * recorded parameters and, for runs, the captured log.
 */
export default function DeliveryActivityPage() {
  const [active] = useActivePartition();
  const [actor, setActor] = useLocalStorageState("sqlflow.filters.delivery-activity.actor", "");
  const [kind, setKind] = useLocalStorageState("sqlflow.filters.delivery-activity.kind", ALL);
  const [outcome, setOutcome] = useLocalStorageState("sqlflow.filters.delivery-activity.outcome", ALL);
  const [showIdle, setShowIdle] = useLocalStorageState<boolean>("sqlflow.filters.delivery-activity.idle", false);
  const [selected, setSelected] = useState<number | null>(null);

  const filters = {
    partition: active ?? undefined,
    actor: actor.trim() === "" ? undefined : actor.trim(),
    kind: kind === ALL ? undefined : kind,
    outcome: outcome === ALL ? undefined : outcome,
  };

  // How many idle runs match the other filters: what the switch says it shows, or leaves out.
  const idle = useQuery({
    queryKey: ["delivery", "activities", "idle", actor, kind, outcome, active],
    queryFn: () => deliveryApi.activities({ ...filters, idle: true, page: 1, pageSize: 1 }),
    refetchInterval: 10000,
  });
  const idleCount = idle.data?.total ?? 0;

  const detail = useQuery({
    queryKey: ["delivery", "activity", selected],
    queryFn: () => deliveryApi.activity(selected!),
    enabled: selected !== null,
  });

  const columns: Column<DeliveryActivity>[] = [
    { id: "started", header: "When", render: (row) => <RelativeTime value={row.startedUtc} absolute /> },
    // The page never scrolls sideways: the flow keeps to a width that steps down as the table narrows, the result takes
    // what the other columns leave, and both clip with the whole value on hover.
    {
      id: "flow",
      header: "Flow",
      render: (row) => (
        <span className="flex items-center gap-1.5">
          {/* Every row is the workbench partition's when one is picked; with none, each says which it is. */}
          {active === null && row.partition && (
            <Badge variant="secondary" className="shrink-0 font-mono text-[10px]" title="The OSDU partition this activity's ledger is kept under">{row.partition}</Badge>
          )}
          <span className="block max-w-[260px] @max-4xl/table:max-w-[150px] @max-3xl/table:max-w-[110px]">
            <TruncatedText text={row.flowName} mono maxWidth={1600} className="font-medium" />
          </span>
        </span>
      ),
    },
    { id: "kind", header: "Action", render: (row) => row.kind },
    { id: "actor", header: "By", render: (row) => <Actor actor={row.actor} /> },
    { id: "result", header: "Result", fill: true, floor: 120, render: (row) => <Result row={row} /> },
    { id: "target", header: "Target", render: (row) => <Target row={row} /> },
  ];

  const emptyMessage = !showIdle && idleCount > 0
    ? `Only idle runs match: ${idleCount} run(s) that changed nothing. Show idle runs to see them.`
    : "No delivery activity recorded yet.";
  const data = detail.data;

  return (
    <Page data-testid="page-delivery-activity">
      <PageHeader
        title="Delivery audit trail"
        subtitle={`Every run and intervention on every delivery flow${active === null ? "" : ` in ${active}`}, newest first.`}
      />
      <FilterBar>
        <SearchInput value={actor} onChange={setActor} placeholder="Actor, e.g. admin or schedule:" label="Filter by actor" testId="delivery-activity-actor" className="sm:w-64" />
        <Select value={kind} onValueChange={setKind}>
          <SelectTrigger size="sm" className="h-8 w-40" data-testid="delivery-activity-kind"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>All actions</SelectItem>
            {KINDS.map((value) => <SelectItem key={value} value={value}>{value}</SelectItem>)}
          </SelectContent>
        </Select>
        <Select value={outcome} onValueChange={setOutcome}>
          <SelectTrigger size="sm" className="h-8 w-40" data-testid="delivery-activity-outcome"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>All outcomes</SelectItem>
            {OUTCOMES.map((value) => <SelectItem key={value} value={value}>{value}</SelectItem>)}
          </SelectContent>
        </Select>
        <Tooltip>
          <TooltipTrigger asChild>
            <Label className="flex items-center gap-2 text-[13px] font-normal">
              <Switch checked={showIdle} onCheckedChange={setShowIdle} data-testid="delivery-activity-idle" />
              Show idle runs
              <span className="font-mono text-[12px] tabular-nums text-muted-foreground" data-testid="delivery-activity-idle-count">
                {idle.data === undefined ? "" : idleCount}
              </span>
            </Label>
          </TooltipTrigger>
          <TooltipContent className="max-w-80">{IDLE_HINT}</TooltipContent>
        </Tooltip>
      </FilterBar>
      <PagedTable
        queryKey={["delivery", "activities", actor, kind, outcome, active, showIdle]}
        fetchPage={(page, pageSize) => deliveryApi.activities({ ...filters, idle: showIdle ? undefined : false, page, pageSize })}
        columns={columns}
        rowKey={(row) => row.activityId}
        onRowClick={(row) => setSelected(row.activityId)}
        rowSx={(row) => (row.idle ? { opacity: 0.7 } : undefined)}
        pollMs={10000}
        emptyMessage={emptyMessage}
        data-testid="delivery-activity-table"
      />

      <Sheet open={selected !== null} onOpenChange={(open) => { if (!open) { setSelected(null); } }}>
        <SheetContent className="w-full gap-0 sm:max-w-2xl" data-testid="delivery-activity-detail">
          <SheetHeader>
            <SheetTitle>{data ? `${data.kind} on ${data.flowName}` : "Activity"}</SheetTitle>
            <SheetDescription>
              {data ? `${endingOf(data).label}${data.partition ? `, in ${data.partition}` : ""}.` : "Loading."}
            </SheetDescription>
          </SheetHeader>
          {data && (
            <div className="flex flex-1 flex-col gap-3 overflow-y-auto px-4 pb-4">
              <FactGrid data-testid="delivery-activity-facts">
                <Fact label="By"><span className="font-mono text-[12px]">{data.actor}</span></Fact>
                <Fact label="Started"><RelativeTime value={data.startedUtc} absolute /></Fact>
                <Fact label="Completed">{data.completedUtc ? <RelativeTime value={data.completedUtc} absolute /> : <NoFact />}</Fact>
                {data.deliveryKey !== null && (
                  <Fact label="Record" wide>
                    <IdLink to={deliveryRecordRoute({ flowId: data.flowId, deliveryKey: data.deliveryKey })} id={data.deliveryKey} hint="The record this action was on." />
                  </Fact>
                )}
                {data.submissionId !== null && (
                  <Fact label="Submission" wide>
                    <IdLink to={`/delivery/submissions/${data.submissionId}`} id={data.submissionId} hint={SUBMISSION_HINT} />
                  </Fact>
                )}
                {data.runId !== null && (
                  <Fact label="Run" wide>
                    <IdLink to={`/runs/${data.runId}`} id={data.runId} hint={RUN_HINT} />
                  </Fact>
                )}
              </FactGrid>
              {data.summary && <p className="text-[13px]">{data.summary}</p>}
              <div>
                <div className="mb-1 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Parameters</div>
                <CodeView value={prettyJson(data.parametersJson ?? "{}")} language="json" height={160} data-testid="delivery-activity-parameters" />
              </div>
              <div>
                <div className="mb-1 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Log</div>
                {data.log
                  ? <CodeView value={data.log} language="plaintext" height={360} data-testid="delivery-activity-log" />
                  : <p className="text-[13px] text-muted-foreground">No captured log for this activity{data.runId ? "; the run trace has the full account." : "."}</p>}
              </div>
            </div>
          )}
        </SheetContent>
      </Sheet>
    </Page>
  );
}
