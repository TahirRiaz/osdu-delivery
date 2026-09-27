import { useMemo, useState, type ReactNode } from "react";
import { Link as RouterLink } from "react-router-dom";
import {
  ArchiveRestore, CheckCircle2, ChevronDown, ChevronRight, CircleDashed, Database, DatabaseZap, Eraser, FileInput, FilePen,
  FileQuestion, FileX2, Hourglass, PauseCircle, RefreshCw, RotateCcw, ScanSearch, Send, ShieldCheck, Trash2, Unlock,
  UserRoundCog, XCircle, type LucideIcon,
} from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { cn } from "@/lib/utils";
import { parseUtc } from "@/lib/time";
import { CodeView } from "@/components/CodeView";
import { EmptyState } from "@/components/EmptyState";
import { RelativeTime } from "@/components/RelativeTime";
import { SummaryStrip, type SummaryCell } from "@/components/SummaryStrip";
import { TruncatedText } from "@/components/TruncatedText";
import type {
  DeliveryActivity, DeliveryAttempt, DeliveryAttemptResult, DeliveryChainLanding, DeliveryChainRun, DeliveryRecord,
  DeliveryRecordChain, DeliverySourceChange, DeliverySourceChangeKind,
} from "../../api/delivery";
import { Fact, FactGrid, NoFact } from "./Facts";
import { prettyJson } from "./prettyJson";
import { RecordName } from "./RecordName";

type Tone = "success" | "destructive" | "warning" | "info" | "muted";

/**
 * Which of the record's stories an event belongs to, so the timeline can be narrowed to one of them: the changes of its
 * row in the ingestion table, what the ledger decided about it, what was done against OSDU, and who intervened.
 */
type Lane = "source" | "ledger" | "osdu" | "intervention";
type LaneFilter = "all" | "source" | "osdu" | "intervention";

/** One thing that happened to the record: when, what, the facts that place it, and the rest. */
interface JourneyEvent {
  id: string;
  at: string;
  title: string;
  /** The facts that place the event, kept to one line: the file and row, the run and submission it belongs to. */
  summary: ReactNode[];
  /** What went wrong, when something did; shown under the summary in the destructive colour. */
  error: string | null;
  /** A note the event carries that is not an error (what the target answered, why nothing was sent). */
  note: string | null;
  /** Everything else known about the event, revealed when the entry is opened. */
  more: ReactNode | null;
  tone: Tone;
  icon: LucideIcon;
  /** Events dated alike (a landing and the load it fed, an attempt's start and its completion) keep their order by this. */
  order: number;
  lane: Lane;
  testId: string;
}

const TONE_RING: Record<Tone, string> = {
  success: "border-success/50 bg-success/10 text-success",
  destructive: "border-destructive/50 bg-destructive/10 text-destructive",
  warning: "border-warning/50 bg-warning/10 text-warning",
  info: "border-info/50 bg-info/10 text-info",
  muted: "border-border bg-muted text-muted-foreground",
};

function Mono({ children }: { children: ReactNode }) {
  return <span className="font-mono text-[12px]">{children}</span>;
}

/**
 * A value with no length of its own (an OSDU id, a file name, a lease owner, a worker): clipped to the line it sits
 * on, revealed in full on hover, and handed over by its copy button, so a timeline entry stays one line whatever the
 * estate names things.
 */
function LongValue({ value, width = 320, copy = true, title }: { value: string; width?: number; copy?: boolean; title?: string }) {
  // The wrapper carries the cap as a definite width: the clipped span's own cap is relative to its container, which
  // counts for nothing while the line is being measured, so without the wrapper the line would be laid out as if
  // the whole value were showing and leave a blank stretch after the ellipsis. The line itself bounds the cap, so a
  // value in a cell narrower than the cap clips at the cell instead of running under its neighbour.
  return (
    <span className="inline-flex min-w-0 max-w-full" style={{ maxWidth: `min(100%, ${width}px)` }}>
      <TruncatedText text={value} mono maxWidth={width} copy={copy} title={title} />
    </span>
  );
}

function Origin({ file, row, inCell = false }: { file: string | null; row: number | null; inCell?: boolean }) {
  if (file === null) {
    return <span>the ingestion table (the file is not recorded)</span>;
  }

  if (inCell) {
    // Two tracks, the file's sized by the cell rather than by its content, so the file clips there and the row number
    // keeps its place after it: a content-sized wrapper would size to the whole file name and run out of the cell.
    return (
      <span className="grid max-w-full items-baseline gap-x-1 [grid-template-columns:minmax(0,max-content)_auto]">
        <TruncatedText text={file} mono maxWidth={360} copy title="File" />
        {row !== null && <span>{"row "}<Mono>{row}</Mono></span>}
      </span>
    );
  }

  return (
    <span className="inline-flex min-w-0 max-w-full items-baseline gap-1">
      <LongValue value={file} width={280} title="File" />
      {row !== null && <span className="shrink-0">{"row "}<Mono>{row}</Mono></span>}
    </span>
  );
}

function RunRef({ runId }: { runId: string | null }) {
  return runId === null
    ? null
    : <RouterLink to={`/runs/${runId}`} className="font-mono text-[12px] text-primary hover:underline" onClick={(event) => event.stopPropagation()}>run {runId.slice(0, 8)}</RouterLink>;
}

function SubmissionRef({ submissionId }: { submissionId: string | null }) {
  return submissionId === null
    ? null
    : <RouterLink to={`/delivery/submissions/${submissionId}`} className="font-mono text-[12px] text-primary hover:underline" onClick={(event) => event.stopPropagation()}>submission {submissionId.slice(0, 8)}</RouterLink>;
}

function FlowRef({ run }: { run: DeliveryChainRun }) {
  return <RouterLink to={`/pipelines/${run.pipelineId}`} className="text-primary hover:underline" onClick={(event) => event.stopPropagation()}>{run.flowName}</RouterLink>;
}

/** The pieces of an event's summary line, separated so a missing one leaves no dangling separator. */
function Summary({ parts }: { parts: ReactNode[] }) {
  const shown = parts.filter((part) => part !== null && part !== undefined && part !== false);
  if (shown.length === 0) {
    return null;
  }

  return (
    <span className="flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[12px] text-muted-foreground">
      {/* Each part is a flex row of its own, so a clipped value inside it counts at its clipped width: as a plain
          inline the part would size to the whole unclipped string and leave a blank stretch after the ellipsis. */}
      {shown.map((part, index) => <span key={index} className="inline-flex max-w-full flex-wrap items-baseline gap-x-1">{part}</span>)}
    </span>
  );
}

/** The steps of one try, compactly: name, status, duration, and whether an earlier try had completed it. */
export function AttemptSteps({ result }: { result: DeliveryAttemptResult | null }) {
  const steps = result?.steps ?? [];
  if (steps.length === 0) {
    return <span className="text-muted-foreground">-</span>;
  }

  return (
    <div className="flex flex-wrap gap-1">
      {steps.map((step, index) => (
        <Badge
          key={`${step.name}-${index}`}
          variant="outline"
          className={step.error !== undefined ? "text-destructive" : undefined}
          title={step.returned !== undefined ? JSON.stringify(step.returned) : undefined}
        >
          {step.name}
          {step.status !== undefined ? ` ${step.status}` : ""}
          {step.ms !== undefined ? ` ${step.ms}ms` : ""}
          {step.resumed === true ? " (resumed)" : ""}
        </Badge>
      ))}
    </div>
  );
}

/** How long something took, from two moments the ledger or the catalog recorded. */
function between(from: string | null, to: string): string | null {
  if (from === null) {
    return null;
  }

  const ms = parseUtc(to).getTime() - parseUtc(from).getTime();
  if (!Number.isFinite(ms) || ms < 0) {
    return null;
  }

  return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(1)} s`;
}

/** How long a run took, for a run's line. */
function runDuration(seconds: number | null): string | null {
  if (seconds === null || !Number.isFinite(seconds)) {
    return null;
  }

  return seconds < 1 ? `${Math.round(seconds * 1000)} ms` : seconds < 90 ? `${seconds.toFixed(1)} s` : `${(seconds / 60).toFixed(1)} min`;
}

function fileSize(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`;
  }

  if (bytes < 1024 * 1024) {
    return `${(bytes / 1024).toFixed(1)} KB`;
  }

  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

/** JSON the ledger stored as text, pretty-printed when it parses and shown as it is when it does not. */
function storedJson(text: string): string {
  try {
    return prettyJson(text);
  } catch {
    return text;
  }
}

/** The ingestion table as an operator names it: its schema and table, without brackets or the database. */
function ingestionTableName(sourceTable: string | null): string {
  if (sourceTable === null) {
    return "the ingestion table";
  }

  const parts = sourceTable.split(".").map((part) => part.trim().replace(/^\[/, "").replace(/\]$/, "")).filter((part) => part !== "");
  return parts.length === 0 ? sourceTable : parts.slice(-2).join(".");
}

/** Whether a change is the row's arrival as far as the ledger knows it: its insert, a reinsert, or its earliest version. */
function isArrival(change: DeliverySourceChange): boolean {
  return change.kind === "loaded" || change.kind === "reloaded" || change.kind === "earliest";
}

const CHANGE_SHAPES: Record<DeliverySourceChangeKind, { title: (table: string) => string; what: (table: string) => string; icon: LucideIcon; tone: Tone }> = {
  loaded: {
    title: (table) => `Loaded into ${table}`,
    what: (table) => `The ingestion flow inserted the row into ${table} (its InsertedDate_DW): the record's arrival. A later run that loads the row unchanged leaves it as it is, and is no part of this history.`,
    icon: DatabaseZap,
    tone: "info",
  },
  reloaded: {
    title: (table) => `Loaded into ${table} again`,
    what: (table) => `The ingestion flow inserted the row into ${table} again after earlier versions of it: the row was deleted from the table and loaded anew.`,
    icon: ArchiveRestore,
    tone: "info",
  },
  earliest: {
    title: (table) => `In ${table}: the earliest version the ledger holds`,
    what: () => "The ledger does not know when the row first reached the table: the table carries no InsertedDate_DW, or no plan has read the row since the ledger began keeping it. This is the earliest version of the row it recorded; whether it was the row's insert or a later change cannot be told.",
    icon: Database,
    tone: "muted",
  },
  changed: {
    title: (table) => `Changed in ${table}`,
    what: () => "The ingestion flow restamped the row (its UpdatedDate_DW): the row's data changed. A run that loads the row unchanged does not restamp it, so every entry like this one is a change.",
    icon: FilePen,
    tone: "info",
  },
  deleted: {
    title: (table) => `Deleted from ${table}`,
    what: () => "The ingestion flow's key match found the row gone from the source and marked it deleted (its DeletedDate_DW). A deleted row is never delivered: what OSDU holds is removed only by a removal someone asks for.",
    icon: FileX2,
    tone: "destructive",
  },
};

function RunFacts({ run, rowsLabel }: { run: DeliveryChainRun; rowsLabel: string }) {
  const duration = runDuration(run.durationSeconds);
  return (
    <span className="flex min-w-0 flex-wrap items-baseline gap-x-2">
      <FlowRef run={run} />
      <RunRef runId={run.runId} />
      <span className="text-muted-foreground">
        <Mono>{run.status}</Mono>
        {run.rows > 0 && `, ${run.rows.toLocaleString()} ${rowsLabel}`}
        {duration !== null && `, took ${duration}`}
      </span>
    </span>
  );
}

/**
 * One change of the record's row, as the ledger recorded it: its arrival, a change, or its deletion. The runs are the
 * evidence of the change, not events of their own: the ingestion run that wrote it and, for a change from a file, the
 * landing that brought the file in. A run that reloaded the row unchanged made no change, and so is not here.
 */
function changeEvent(change: DeliverySourceChange, table: string): JourneyEvent {
  const shape = CHANGE_SHAPES[change.kind];
  const arrival = isArrival(change);
  const deleted = change.kind === "deleted";
  return {
    id: `change-${change.kind}-${change.atUtc}`,
    at: change.atUtc,
    title: shape.title(table),
    summary: [
      change.fileName !== null
        ? <Origin key="o" file={change.fileName} row={change.rowNumber} />
        : arrival && change.kind !== "earliest" && "from a file the ledger never saw: the row changed before a plan read it",
      change.loading !== null && <span key="l">{deleted ? "marked by" : "written by"} <RunRef runId={change.loading.runId} /></span>,
      !arrival && change.landing !== null && <span key="f">landed by <RunRef runId={change.landing.run.runId} /></span>,
    ],
    error: change.loading !== null && !change.loading.success ? `The ingestion run ended ${change.loading.status}.` : null,
    note: null,
    more: (
      <FactGrid>
        <Fact label="What happened" wide>{shape.what(table)}</Fact>
        <Fact label={deleted ? "Marked by" : "Written by"} wide>
          {change.loading !== null
            ? <RunFacts run={change.loading} rowsLabel="rows loaded" />
            : <span className="text-muted-foreground">{`No recorded run of a flow that writes ${table} was executing at this moment: the run was pruned, ran before this estate recorded runs, or the lineage does not name the table's writers.`}</span>}
        </Fact>
        {!deleted && (
          <Fact label="File landed by" wide>
            {change.landing !== null
              ? <RunFacts run={change.landing.run} rowsLabel="rows in the file" />
              : <span className="text-muted-foreground">{change.fileName === null ? "The ledger holds no file for this version." : "No successful run recorded processing a file of this name before the row was written, so the landing is not named."}</span>}
          </Fact>
        )}
        {change.landing?.filePath != null && (
          <Fact label="Path" wide>
            <TruncatedText text={change.landing.filePath} mono maxWidth={720} copy title="Path" />
            {change.landing.sizeBytes > 0 && <span className="ml-2 text-muted-foreground"><Mono>{fileSize(change.landing.sizeBytes)}</Mono></span>}
          </Fact>
        )}
      </FactGrid>
    ),
    tone: shape.tone,
    icon: shape.icon,
    order: 1,
    lane: "source",
    testId: `journey-source-${change.kind}`,
  };
}

/**
 * The first time the record came in from a file: the landing that brought in the file its arrival was loaded from. A
 * landing of the same file later is no change of the record, so only the arrival's landing is an entry of its own; a
 * later change names its landing as its evidence.
 */
function landingEvent(change: DeliverySourceChange, landing: DeliveryChainLanding): JourneyEvent {
  const duration = runDuration(landing.run.durationSeconds);
  return {
    id: `landing-${landing.run.runId}`,
    at: landing.run.ranUtc,
    title: `Ingested from file by ${landing.run.flowName}`,
    summary: [
      <Origin key="o" file={landing.fileName} row={change.rowNumber} />,
      landing.rows > 0 && `${landing.rows.toLocaleString()} row${landing.rows === 1 ? "" : "s"} in the file`,
      duration !== null && `took ${duration}`,
      <RunRef key="r" runId={landing.run.runId} />,
    ],
    error: landing.run.success ? null : landing.run.error,
    note: null,
    more: (
      <FactGrid>
        <Fact label="Flow"><FlowRef run={landing.run} /></Fact>
        <Fact label="Run status"><Mono>{landing.run.status}</Mono>{" "}<span className="text-muted-foreground">wave</span>{" "}<Mono>{landing.run.wave}</Mono></Fact>
        <Fact label="File"><TruncatedText text={landing.fileName} mono maxWidth={360} copy title="File" /></Fact>
        {landing.filePath !== null && <Fact label="Path"><TruncatedText text={landing.filePath} mono maxWidth={360} copy title="Path" /></Fact>}
        {landing.sizeBytes > 0 && <Fact label="Size"><Mono>{fileSize(landing.sizeBytes)}</Mono></Fact>}
        {landing.fileModifiedUtc !== null && <Fact label="File modified"><RelativeTime value={landing.fileModifiedUtc} /></Fact>}
      </FactGrid>
    ),
    tone: landing.run.success ? "success" : "destructive",
    icon: FileInput,
    order: 0,
    lane: "source",
    testId: "journey-source-landing",
  };
}

/** The record's row through its ingestion table: each change of it, and the landing its arrival came in by. */
function sourceEvents(chain: DeliveryRecordChain | undefined): JourneyEvent[] {
  if (chain === undefined) {
    return [];
  }

  const table = ingestionTableName(chain.sourceTable);
  return chain.changes.flatMap((change) =>
    isArrival(change) && change.landing !== null ? [changeEvent(change, table), landingEvent(change, change.landing)] : [changeEvent(change, table)]);
}

/**
 * Whether an attempt was an operation against OSDU: a delivery, a removal, a purge, a try that failed, or any try that
 * took a step against the target. What the intake decided (a hold of a row it could not build, a change that rendered
 * what OSDU holds, an older version) and the final hash check that found nothing to send are the ledger's decisions.
 */
function isOsduOperation(attempt: DeliveryAttempt): boolean {
  return attempt.outcome === "delivered"
    || attempt.outcome === "failed"
    || attempt.outcome === "deleted"
    || attempt.outcome === "historypurged"
    || (attempt.result?.steps?.length ?? 0) > 0;
}

function attemptShape(attempt: DeliveryAttempt): { title: string; tone: Tone; icon: LucideIcon } {
  switch (attempt.outcome) {
    case "delivered":
      return { title: attempt.targetVersion !== null ? `Delivered to OSDU as version ${attempt.targetVersion}` : "Delivered to OSDU", tone: "success", icon: CheckCircle2 };
    case "failed":
      return { title: "Delivery to OSDU failed", tone: "destructive", icon: XCircle };
    case "held":
      return attempt.phase === "source-deleted"
        ? { title: "Held back: a deleted row is never delivered", tone: "warning", icon: PauseCircle }
        : { title: isOsduOperation(attempt) ? "Held back: OSDU refused it" : "Held back", tone: "warning", icon: PauseCircle };
    case "skipped":
      return attempt.phase === "source-missing"
        ? { title: "Not found in the ingestion table: the row is gone, and the record keeps its status", tone: "warning", icon: FileQuestion }
        : attempt.phase === "identical"
        ? { title: "Nothing to send: the changed row renders what OSDU holds", tone: "muted", icon: CircleDashed }
        : attempt.phase === "unchanged"
          ? { title: "Nothing to send: OSDU already holds this version", tone: "muted", icon: CircleDashed }
          : attempt.phase === "stale"
            ? { title: "Skipped: the source carried an older version", tone: "muted", icon: CircleDashed }
            : { title: "Skipped", tone: "muted", icon: CircleDashed };
    case "deleted":
      return { title: "Removed from OSDU", tone: "muted", icon: Trash2 };
    case "historypurged":
      return { title: "Earlier versions purged in OSDU", tone: "warning", icon: Eraser };
    default:
      return { title: attempt.outcome, tone: "muted", icon: CircleDashed };
  }
}

const ACTIVITY_TITLES: Record<string, { title: string; icon: LucideIcon }> = {
  release: { title: "Released back to pending", icon: Unlock },
  redeliver: { title: "Redelivery asked for", icon: RotateCcw },
  verify: { title: "Verify against OSDU", icon: ShieldCheck },
  sync: { title: "Synced from source", icon: RefreshCw },
  delete: { title: "Removal asked for", icon: Trash2 },
};

function attemptEvent(attempt: DeliveryAttempt): JourneyEvent {
  const shape = attemptShape(attempt);
  const osdu = isOsduOperation(attempt);
  const duration = between(attempt.startedUtc, attempt.completedUtc);
  const steps = attempt.result?.steps ?? [];
  const correlationId = attempt.result?.correlationId;
  return {
    id: `attempt-${attempt.attemptId}`,
    at: attempt.startedUtc,
    title: shape.title,
    summary: osdu
      ? [
        attempt.phase !== "" && <span key="p">phase <Mono>{attempt.phase}</Mono></span>,
        duration !== null && `took ${duration}`,
        steps.length > 0 && `${steps.length} step${steps.length === 1 ? "" : "s"}`,
        <RunRef key="r" runId={attempt.runId} />,
        <SubmissionRef key="s" submissionId={attempt.submissionId} />,
      ]
      : [
        attempt.sourceFileName !== null && <Origin key="o" file={attempt.sourceFileName} row={attempt.sourceRowNumber} />,
        <RunRef key="r" runId={attempt.runId} />,
        <SubmissionRef key="s" submissionId={attempt.submissionId} />,
      ],
    error: attempt.error,
    note: attempt.result?.detail ?? null,
    more: (
      <FactGrid>
        <Fact label="Worker"><TruncatedText text={attempt.worker} mono maxWidth={360} title="Worker" /></Fact>
        <Fact label="Correlation id">{correlationId !== undefined ? <TruncatedText text={correlationId} mono maxWidth={360} copy title="Correlation id" /> : <NoFact />}</Fact>
        <Fact label="Work batch">{attempt.workBatch !== null ? <Mono>{attempt.workBatch}</Mono> : <NoFact />}</Fact>
        <Fact label="OSDU version">{attempt.targetVersion !== null ? <Mono>{attempt.targetVersion}</Mono> : <NoFact />}</Fact>
        <Fact label="Completed"><RelativeTime value={attempt.completedUtc} /></Fact>
        <Fact label="Built from"><Origin file={attempt.sourceFileName} row={attempt.sourceRowNumber} inCell /></Fact>
        <Fact label="Row stamped"><RelativeTime value={attempt.sourceUpdatedUtc} /></Fact>
        {attempt.sourceDeletedUtc != null && <Fact label="Row deleted"><RelativeTime value={attempt.sourceDeletedUtc} /></Fact>}
        <Fact label="Metadata hash">{attempt.metadataHash !== null ? <TruncatedText text={attempt.metadataHash} mono maxWidth={360} copy title="Metadata hash" /> : <NoFact />}</Fact>
        <Fact label="Payload hash">{attempt.payloadHash !== null ? <TruncatedText text={attempt.payloadHash} mono maxWidth={360} copy title="Payload hash" /> : <NoFact />}</Fact>
        {steps.length > 0 && <Fact label="Steps" wide><AttemptSteps result={attempt.result} /></Fact>}
      </FactGrid>
    ),
    tone: shape.tone,
    icon: shape.icon,
    order: 3,
    // A row a sync did not find is a fact of the source, like the row's other changes.
    lane: osdu ? "osdu" : attempt.phase === "source-missing" ? "source" : "ledger",
    testId: `journey-attempt-${attempt.outcome}`,
  };
}

function activityEvent(activity: DeliveryActivity): JourneyEvent {
  const shape = ACTIVITY_TITLES[activity.kind] ?? { title: activity.kind, icon: UserRoundCog };
  const tone: Tone = activity.outcome === "failed" ? "destructive" : activity.outcome === "running" ? "info" : "muted";
  const hasMore = activity.parametersJson !== null || activity.completedUtc !== null;
  return {
    id: `activity-${activity.activityId}`,
    at: activity.startedUtc,
    title: `${shape.title} by ${activity.actor}`,
    summary: [
      activity.outcome,
      activity.outcome !== "failed" && activity.summary,
      <RunRef key="r" runId={activity.runId} />,
      <SubmissionRef key="s" submissionId={activity.submissionId} />,
    ],
    error: activity.outcome === "failed" ? activity.summary : null,
    note: null,
    more: hasMore
      ? (
        <FactGrid>
          <Fact label="Ended">{activity.completedUtc !== null ? <RelativeTime value={activity.completedUtc} /> : "still running"}</Fact>
          <Fact label="Flow">{activity.flowName}</Fact>
          {activity.parametersJson !== null && (
            <Fact label="Parameters" wide>
              <CodeView value={storedJson(activity.parametersJson)} language="json" height={120} />
            </Fact>
          )}
        </FactGrid>
      )
      : null,
    tone,
    icon: shape.icon,
    order: 4,
    lane: "intervention",
    testId: `journey-intervention-${activity.kind}`,
  };
}

/**
 * The record's own story, newest first, and nothing else: each change of its row in the ingestion table (its arrival
 * with the landing that brought its file in, every later change, its deletion), when it entered the ledger and what
 * the ledger decided, every operation against OSDU with its outcome, every intervention with who asked for it, and
 * where it stands now. A pipeline run appears only as the evidence of a change it made to this row: a run that loaded
 * the row again unchanged is a fact about the pipeline, which its own page holds, and not about this record.
 */
function buildEvents(
  record: DeliveryRecord, attempts: DeliveryAttempt[], activities: DeliveryActivity[], chain: DeliveryRecordChain | undefined,
): JourneyEvent[] {
  const events: JourneyEvent[] = sourceEvents(chain);

  events.push({
    id: "planned",
    at: record.createdUtc,
    title: "Planned: the record entered the ledger",
    summary: [
      record.targetId !== null ? <span key="t">claimed the OSDU id <RecordName id={record.targetId} copy className="max-w-[320px]" /></span> : "no OSDU id claimed yet",
      `mapping ${record.mappingName}`,
    ],
    error: null,
    note: null,
    more: null,
    tone: "info",
    icon: ScanSearch,
    order: 2,
    lane: "ledger",
    testId: "journey-planned",
  });

  for (const attempt of attempts) {
    events.push(attemptEvent(attempt));
  }

  for (const activity of activities) {
    events.push(activityEvent(activity));
  }

  if (record.lastVerifiedUtc !== null) {
    const outcome = record.lastVerifyOutcome;
    events.push({
      id: "verified",
      at: record.lastVerifiedUtc,
      title: outcome === "match"
        ? "Verified: OSDU holds what the ledger holds"
        : outcome === "drifted"
          ? "Verified: OSDU has drifted from what was delivered"
          : outcome === "missing"
            ? "Verified: OSDU no longer holds the record"
            : "Verify could not read the record",
      summary: [outcome !== null && `outcome ${outcome}`, record.targetVersion !== null && `ledger version ${record.targetVersion}`],
      error: null,
      note: null,
      more: null,
      tone: outcome === "match" ? "success" : outcome === "error" ? "muted" : "warning",
      icon: ShieldCheck,
      order: 5,
      lane: "osdu",
      testId: "journey-verified",
    });
  }

  if (record.status === "waiting") {
    events.push({
      id: "waiting",
      at: record.updatedUtc,
      title: "Waiting for a record it refers to",
      summary: [record.waitingFor !== null && <RecordName key="w" id={record.waitingFor} copy className="max-w-[320px]" />],
      error: null,
      note: record.lastError,
      more: null,
      tone: "info",
      icon: Hourglass,
      order: 6,
      lane: "ledger",
      testId: "journey-waiting",
    });
  }

  if (record.hasPendingDocument && record.status === "pending") {
    events.push({
      id: "queued",
      at: record.updatedUtc,
      title: "Queued: a rendered document waits to be sent to OSDU",
      summary: [
        record.nextAttemptUtc !== null && <span key="n">next try <RelativeTime value={record.nextAttemptUtc} /></span>,
        record.workBatch !== null && `work batch ${record.workBatch}`,
        <SubmissionRef key="s" submissionId={record.lastSubmissionId} />,
      ],
      error: null,
      note: null,
      more: null,
      tone: "info",
      icon: Send,
      order: 7,
      lane: "ledger",
      testId: "journey-queued",
    });
  }

  // Newest first: what happened last is what an operator came for, and it sits at the top of the bounded list. The
  // API writes some moments without their zone; every moment is UTC by contract, so each is read as UTC.
  return events.sort((a, b) => {
    const byTime = parseUtc(b.at).getTime() - parseUtc(a.at).getTime();
    return byTime !== 0 && Number.isFinite(byTime) ? byTime : b.order - a.order;
  });
}

/**
 * The answers an operator came for, in one strip, in the order a record moves: the file it came in from, when its row
 * reached the ingestion table, when the row last changed, when it landed in OSDU, when OSDU was last verified, and
 * whether it was removed.
 */
function milestones(record: DeliveryRecord, attempts: DeliveryAttempt[], chain: DeliveryRecordChain | undefined): SummaryCell[] {
  const dispatched = attempts.filter((a) => a.outcome === "delivered" || a.outcome === "failed" || (a.outcome === "held" && isOsduOperation(a)));
  const failed = dispatched.filter((a) => a.outcome === "failed").length;
  const removed = attempts.find((a) => a.outcome === "deleted");
  const changes = chain?.changes ?? [];
  const arrival = [...changes].reverse().find(isArrival);
  const since = arrival === undefined ? changes : changes.filter((c) => parseUtc(c.atUtc).getTime() > parseUtc(arrival.atUtc).getTime());
  const latest = since.at(0);
  const unknown = chain === undefined ? "-" : "not recorded";

  const cells: SummaryCell[] = [
    {
      label: "From file",
      value: arrival?.landing != null ? <RelativeTime value={arrival.landing.run.ranUtc} /> : unknown,
      caption: arrival === undefined
        ? undefined
        : arrival.fileName ?? "the file is not recorded",
      tone: arrival?.landing != null && !arrival.landing.run.success ? "destructive" : undefined,
      testId: "milestone-file",
    },
    {
      label: "Loaded",
      value: arrival !== undefined ? <RelativeTime value={arrival.atUtc} /> : unknown,
      caption: arrival === undefined
        ? undefined
        : arrival.kind === "earliest"
          ? `earliest version held, ${ingestionTableName(chain?.sourceTable ?? null)}`
          : ingestionTableName(chain?.sourceTable ?? null),
      testId: "milestone-loaded",
    },
    {
      label: "Last change",
      value: latest !== undefined ? <RelativeTime value={latest.atUtc} /> : arrival !== undefined ? "none" : unknown,
      caption: latest === undefined
        ? (arrival !== undefined ? "unchanged since it was loaded" : undefined)
        : latest.kind === "deleted"
          ? "the row was deleted"
          : `${since.length} change${since.length === 1 ? "" : "s"} since it was loaded`,
      tone: latest?.kind === "deleted" ? "destructive" : undefined,
      testId: "milestone-changed",
    },
    {
      label: "In OSDU",
      value: record.lastDeliveredUtc === null ? "not yet" : <RelativeTime value={record.lastDeliveredUtc} />,
      caption: record.lastDeliveredUtc === null
        ? (dispatched.length === 0
          ? (record.status === "pending" || record.status === "delivering" ? "queued" : record.status === "waiting" ? "waiting" : undefined)
          : `${dispatched.length} ${dispatched.length === 1 ? "try" : "tries"}${failed > 0 ? `, ${failed} failed` : ""}`)
        : record.targetVersion !== null ? `version ${record.targetVersion}` : undefined,
      tone: record.lastDeliveredUtc === null ? undefined : "success",
      testId: "milestone-landed",
    },
    {
      label: "Verified",
      value: record.lastVerifiedUtc === null ? "never" : <RelativeTime value={record.lastVerifiedUtc} />,
      caption: record.lastVerifyOutcome ?? undefined,
      tone: record.lastVerifyOutcome === "drifted" || record.lastVerifyOutcome === "missing" ? "warning" : record.lastVerifyOutcome === "match" ? "success" : undefined,
      testId: "milestone-verified",
    },
  ];
  if (record.status === "deleted" || removed !== undefined) {
    cells.push({
      label: "Removed",
      value: removed === undefined ? "yes" : <RelativeTime value={removed.startedUtc} />,
      caption: record.status === "deleted" ? "blocked until released" : "delivered again since",
      tone: "destructive",
      testId: "milestone-removed",
    });
  }

  return cells;
}

/**
 * How far the record has come, as one strip in the order a record moves: the file it came in from, when its row
 * reached the ingestion table and last changed there, when it landed in OSDU and as which version, when it was last
 * verified, and whether it was removed. The page's spine: the timeline under it is the evidence.
 */
export function RecordMilestones({ record, attempts, chain }: {
  record: DeliveryRecord;
  attempts: DeliveryAttempt[] | undefined;
  /** The changes of the record's row in its ingestion table; undefined while they are being read. */
  chain: DeliveryRecordChain | undefined;
}) {
  if (attempts === undefined) {
    return <Skeleton className="h-16 w-full rounded-lg" />;
  }

  // Five cells (six for a removed record) at the strip's default floor need more width than the workbench's content
  // measure gives them and strand the last on a line of its own; a time and a short caption fit in 160px.
  return <SummaryStrip cells={milestones(record, attempts, chain)} minCellWidth={160} data-testid="record-milestones" />;
}

function laneMatches(event: JourneyEvent, filter: LaneFilter): boolean {
  return filter === "all" || event.lane === filter;
}

const EMPTY: Record<Exclude<LaneFilter, "all">, { title: string; description: string }> = {
  source: { title: "No change recorded", description: "The ledger holds no version of this record's row in its ingestion table." },
  osdu: { title: "Nothing done against OSDU yet", description: "The record has not been sent to, removed from or verified against OSDU." },
  intervention: { title: "No intervention on this record", description: "Nobody has released, redelivered, verified or removed it." },
};

/**
 * The record's story, newest first, from the ledger and the changes of its row: how its row arrived and changed in the
 * ingestion table, what the ledger decided, every operation against OSDU and what OSDU answered, and every
 * intervention with who asked for it. Each entry is one line with the facts that place it; the rest (the runs a change
 * came from, the steps a try took, an intervention's parameters) opens under the entry. The list scrolls inside a
 * bounded box, and narrows to the row's changes, to what was done against OSDU, or to the interventions alone.
 */
export function RecordJourney({ record, attempts, activities, chain }: {
  record: DeliveryRecord;
  attempts: DeliveryAttempt[] | undefined;
  activities: DeliveryActivity[] | undefined;
  /** The changes of the record's row in its ingestion table; undefined while they are being read. */
  chain: DeliveryRecordChain | undefined;
}) {
  const [filter, setFilter] = useState<LaneFilter>("all");
  const [opened, setOpened] = useState<ReadonlySet<string>>(() => new Set());
  const loaded = attempts !== undefined && activities !== undefined;
  const events = useMemo(
    () => (loaded ? buildEvents(record, attempts, activities, chain) : []),
    [loaded, record, attempts, activities, chain]);
  const listed = useMemo(() => events.filter((event) => laneMatches(event, filter)), [events, filter]);
  const count = (lane: Lane) => events.filter((event) => event.lane === lane).length;

  const toggle = (id: string) => setOpened((current) => {
    const next = new Set(current);
    if (next.has(id)) {
      next.delete(id);
    } else {
      next.add(id);
    }

    return next;
  });

  return (
    <Card className="gap-2 rounded-lg p-3" data-testid="record-journey">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <ToggleGroup
          type="single"
          value={filter}
          onValueChange={(value) => { if (value !== "") { setFilter(value as LaneFilter); } }}
          variant="outline"
          size="sm"
          data-testid="record-journey-filter"
        >
          <ToggleGroupItem value="all" className="px-2.5 text-xs">{`Everything (${events.length})`}</ToggleGroupItem>
          <ToggleGroupItem value="source" className="px-2.5 text-xs">{`Source changes (${count("source")})`}</ToggleGroupItem>
          <ToggleGroupItem value="osdu" className="px-2.5 text-xs">{`OSDU (${count("osdu")})`}</ToggleGroupItem>
          <ToggleGroupItem value="intervention" className="px-2.5 text-xs">{`Interventions (${count("intervention")})`}</ToggleGroupItem>
        </ToggleGroup>
        <span className="ml-auto text-[12px] text-muted-foreground">
          ledger row updated <RelativeTime value={record.updatedUtc} />
        </span>
      </div>
      {!loaded
        ? <Skeleton className="h-24 w-full rounded-md" />
        : listed.length === 0
          ? (
            <EmptyState
              title={filter === "all" ? "Nothing recorded yet" : EMPTY[filter].title}
              description={filter === "all" ? undefined : EMPTY[filter].description}
            />
          )
          : (
            <ol className="flex max-h-[520px] flex-col overflow-y-auto pr-1" data-testid="record-journey-events">
              {listed.map((event, index) => {
                const Icon = event.icon;
                const last = index === listed.length - 1;
                const open = opened.has(event.id);
                const Chevron = open ? ChevronDown : ChevronRight;
                return (
                  <li key={event.id} className="relative flex gap-3" data-testid={event.testId} data-state={open ? "open" : "closed"}>
                    <div className="flex flex-col items-center">
                      <span className={cn("flex size-6 shrink-0 items-center justify-center rounded-full border", TONE_RING[event.tone])}>
                        <Icon className="size-3.5" />
                      </span>
                      {!last && <span className="w-px flex-1 bg-border" />}
                    </div>
                    <div className={cn("flex min-w-0 flex-1 flex-col gap-0.5 pb-3", last && "pb-0")}>
                      <div className="flex min-w-0 items-baseline gap-x-2">
                        {event.more !== null
                          ? (
                            <button
                              type="button"
                              className="flex min-w-0 cursor-pointer flex-wrap items-baseline gap-x-2 text-left hover:underline"
                              onClick={() => toggle(event.id)}
                              aria-expanded={open}
                              data-testid="journey-event-toggle"
                            >
                              <Chevron className="size-3.5 shrink-0 self-center text-muted-foreground" />
                              <span className="text-[13px] font-medium">{event.title}</span>
                            </button>
                          )
                          : (
                            <span className="flex min-w-0 flex-wrap items-baseline gap-x-2">
                              <span className="size-3.5 shrink-0 self-center" aria-hidden="true" />
                              <span className="text-[13px] font-medium">{event.title}</span>
                            </span>
                          )}
                        <span className="shrink-0 text-[12px] text-muted-foreground"><RelativeTime value={event.at} absolute /></span>
                      </div>
                      <Summary parts={event.summary} />
                      {event.error !== null && <span className="text-[12px] text-destructive" data-testid="journey-attempt-error">{event.error}</span>}
                      {event.note !== null && <span className="text-[12px] text-muted-foreground">{event.note}</span>}
                      {open && event.more !== null && (
                        <div className="mt-1.5 rounded-md border bg-muted/30 px-3 py-2" data-testid="journey-event-detail">
                          {event.more}
                        </div>
                      )}
                    </div>
                  </li>
                );
              })}
            </ol>
          )}
      {loaded && chain !== undefined && (chain.note !== null || chain.truncated) && (
        <p className="text-[12px] text-muted-foreground" data-testid="record-chain-note">
          {chain.truncated && "The row changed more often than the timeline lists: its newest changes and its arrival are shown. "}
          {chain.note}
        </p>
      )}
    </Card>
  );
}
