import { useMemo, useState, type ReactNode } from "react";
import { Link as RouterLink } from "react-router-dom";
import {
  CheckCircle2, ChevronDown, ChevronRight, CircleDashed, Database, Eraser, FileInput, Hourglass, Inbox, PauseCircle,
  RotateCcw, ScanSearch, Send, ShieldCheck, Trash2, Unlock, UserRoundCog, XCircle, type LucideIcon,
} from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { cn } from "@/lib/utils";
import { CodeView } from "@/components/CodeView";
import { DetailPair } from "@/components/DetailPair";
import { EmptyState } from "@/components/EmptyState";
import { RelativeTime } from "@/components/RelativeTime";
import { SummaryStrip, type SummaryCell } from "@/components/SummaryStrip";
import { TruncatedText } from "@/components/TruncatedText";
import type {
  DeliveryActivity, DeliveryAttempt, DeliveryAttemptResult, DeliveryChainStage, DeliveryRecord, DeliveryRecordChain,
} from "../../api/delivery";
import { prettyJson } from "./prettyJson";

type Tone = "success" | "destructive" | "warning" | "info" | "muted";

/** Which of the record's stories an event belongs to, so the timeline can be narrowed to one of them. */
type Lane = "chain" | "dispatch" | "intervention" | "state";
type LaneFilter = "all" | "dispatch" | "intervention";

/** One thing that happened to the record, as the ledger holds it: when, what, the facts that place it, and the rest. */
interface JourneyEvent {
  id: string;
  at: string;
  title: string;
  /** The facts that place the event, kept to one line: phase, duration, the run and submission it belongs to. */
  summary: ReactNode[];
  /** What went wrong, when something did; shown under the summary in the destructive colour. */
  error: string | null;
  /** A note the event carries that is not an error (what the target answered, a stage's own remark). */
  note: string | null;
  /** Everything else the ledger holds about the event, revealed when the entry is opened. */
  more: ReactNode | null;
  tone: Tone;
  icon: LucideIcon;
  /** Events the ledger dates alike (an attempt's start and the completion it wrote) keep their order by this. */
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
function LongValue({ value, width = 320, copy = true }: { value: string; width?: number; copy?: boolean }) {
  // The wrapper carries the cap as a definite width: the clipped span's own cap is relative to its container, which
  // counts for nothing while the line is being measured, so without the wrapper the line would be laid out as if
  // the whole value were showing and leave a blank stretch after the ellipsis.
  return (
    <span className="inline-flex min-w-0 max-w-full" style={{ maxWidth: width }}>
      <TruncatedText text={value} mono maxWidth={width} copy={copy} />
    </span>
  );
}

function Origin({ file, row }: { file: string | null; row: number | null }) {
  if (file === null) {
    return <span>the ingestion table (the file is not recorded)</span>;
  }

  return (
    <span>
      <LongValue value={file} width={280} />
      {row !== null && <span>{" row "}<Mono>{row}</Mono></span>}
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

/** The facts an opened entry reveals, on the same caption-over-value grid the detail headers use. */
function Facts({ children }: { children: ReactNode }) {
  return (
    <div className="grid gap-3 [grid-template-columns:repeat(auto-fill,minmax(170px,1fr))]">
      {children}
    </div>
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

const ATTEMPT_TITLES: Record<DeliveryAttempt["outcome"], { title: (a: DeliveryAttempt) => string; tone: Tone; icon: LucideIcon }> = {
  delivered: {
    title: (a) => (a.targetVersion !== null ? `Dispatched and landed as version ${a.targetVersion}` : "Dispatched and landed"),
    tone: "success",
    icon: CheckCircle2,
  },
  failed: { title: () => "Dispatched and failed", tone: "destructive", icon: XCircle },
  held: { title: () => "Held back", tone: "warning", icon: PauseCircle },
  skipped: {
    title: (a) => (a.phase === "unchanged" ? "Nothing to send: OSDU already holds this version" : a.phase === "stale" ? "Skipped: the source carried an older version" : "Skipped"),
    tone: "muted",
    icon: CircleDashed,
  },
  deleted: { title: () => "Removed from OSDU", tone: "muted", icon: Trash2 },
  historypurged: { title: () => "Earlier versions purged in OSDU", tone: "warning", icon: Eraser },
};

const ACTIVITY_TITLES: Record<string, { title: string; icon: LucideIcon }> = {
  release: { title: "Released back to pending", icon: Unlock },
  redeliver: { title: "Redelivery asked for", icon: RotateCcw },
  verify: { title: "Verify against OSDU", icon: ShieldCheck },
  delete: { title: "Removal asked for", icon: Trash2 },
};

function attemptDuration(attempt: DeliveryAttempt): string | null {
  const ms = Date.parse(attempt.completedUtc) - Date.parse(attempt.startedUtc);
  if (!Number.isFinite(ms) || ms < 0) {
    return null;
  }

  return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(1)} s`;
}

/** How long a run took, for a chain stage's summary line. */
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

/** The stage's icon and tone: where in the chain it is, and whether that run ended well. */
function chainShape(stage: DeliveryChainStage): { icon: LucideIcon; tone: Tone } {
  const tone: Tone = stage.success ? "success" : stage.status === "running" || stage.status === "queued" ? "info" : "destructive";
  return { icon: stage.stage === "ingestion" ? Database : FileInput, tone };
}

/** JSON the ledger stored as text, pretty-printed when it parses and shown as it is when it does not. */
function storedJson(text: string): string {
  try {
    return prettyJson(text);
  } catch {
    return text;
  }
}

/**
 * The runs that carried the record through the estate before it reached the delivery ledger: pre-ingestion landing the
 * file, ingestion loading its row into the table the delivery flow reads. Each is a fact the platform recorded, found
 * by the file it processed or by the table it was writing when the row was stamped, so they take their place in the
 * same timeline as everything else.
 */
function chainEvents(chain: DeliveryRecordChain | undefined): JourneyEvent[] {
  if (chain === undefined) {
    return [];
  }

  return chain.stages.map((stage) => {
    const { icon, tone } = chainShape(stage);
    const duration = runDuration(stage.durationSeconds);
    return {
      id: `chain-${stage.runId}-${stage.stage}`,
      at: stage.ranUtc,
      title: stage.stage === "ingestion"
        ? `Ingested: ${stage.flowName} loaded the row into its table`
        : stage.stage === "pre-ingestion"
          ? `Landed: ${stage.flowName} took the file in`
          : `${stage.flowName} handled the file`,
      summary: [
        <span key="f"><LongValue value={stage.matchedBy === "table" ? (stage.objectName ?? "its table") : stage.fileName} width={280} /></span>,
        stage.rows > 0 && `${stage.rows.toLocaleString()} row${stage.rows === 1 ? "" : "s"}`,
        duration !== null && `took ${duration}`,
        !stage.success && `ended ${stage.status}`,
        <RunRef key="r" runId={stage.runId} />,
      ],
      error: stage.error,
      note: null,
      more: (
        <Facts>
          <DetailPair label="Flow"><RouterLink to={`/pipelines/${stage.pipelineId}`} className="text-primary hover:underline">{stage.flowName}</RouterLink></DetailPair>
          <DetailPair label="Run status"><Mono>{stage.status}</Mono></DetailPair>
          <DetailPair label="Wave"><Mono>{stage.wave}</Mono></DetailPair>
          <DetailPair label="Found by">{stage.matchedBy === "table" ? "the table it was writing when the row was stamped" : "the file it processed"}</DetailPair>
          <DetailPair label="File"><LongValue value={stage.fileName} width={260} /></DetailPair>
          {stage.filePath !== null && <DetailPair label="Path"><LongValue value={stage.filePath} width={260} /></DetailPair>}
          {stage.sizeBytes > 0 && <DetailPair label="Size"><Mono>{fileSize(stage.sizeBytes)}</Mono></DetailPair>}
          {stage.fileModifiedUtc !== null && <DetailPair label="File modified"><RelativeTime value={stage.fileModifiedUtc} /></DetailPair>}
        </Facts>
      ),
      tone,
      icon,
      order: 0,
      lane: "chain",
      testId: `journey-chain-${stage.stage}`,
    } satisfies JourneyEvent;
  });
}

function attemptEvent(attempt: DeliveryAttempt): JourneyEvent {
  const shape = ATTEMPT_TITLES[attempt.outcome] ?? { title: () => attempt.outcome, tone: "muted" as Tone, icon: CircleDashed };
  const duration = attemptDuration(attempt);
  const steps = attempt.result?.steps ?? [];
  const correlationId = attempt.result?.correlationId;
  return {
    id: `attempt-${attempt.attemptId}`,
    at: attempt.startedUtc,
    title: shape.title(attempt),
    summary: [
      attempt.phase !== "" && <span key="p">phase <Mono>{attempt.phase}</Mono></span>,
      duration !== null && `took ${duration}`,
      steps.length > 0 && `${steps.length} step${steps.length === 1 ? "" : "s"}`,
      <RunRef key="r" runId={attempt.runId} />,
      <SubmissionRef key="s" submissionId={attempt.submissionId} />,
    ],
    error: attempt.error,
    note: attempt.result?.detail ?? null,
    more: (
      <div className="flex flex-col gap-3">
        <Facts>
          <DetailPair label="Worker"><LongValue value={attempt.worker} width={220} copy={false} /></DetailPair>
          <DetailPair label="Correlation id">{correlationId !== undefined ? <LongValue value={correlationId} width={220} /> : "-"}</DetailPair>
          <DetailPair label="Work batch"><Mono>{attempt.workBatch ?? "-"}</Mono></DetailPair>
          <DetailPair label="OSDU version"><Mono>{attempt.targetVersion ?? "-"}</Mono></DetailPair>
          <DetailPair label="Completed"><RelativeTime value={attempt.completedUtc} /></DetailPair>
          <DetailPair label="Built from"><Origin file={attempt.sourceFileName} row={attempt.sourceRowNumber} /></DetailPair>
          <DetailPair label="Row received"><RelativeTime value={attempt.sourceUpdatedUtc} /></DetailPair>
          <DetailPair label="Metadata hash">{attempt.metadataHash !== null ? <LongValue value={attempt.metadataHash} width={170} /> : "-"}</DetailPair>
          <DetailPair label="Payload hash">{attempt.payloadHash !== null ? <LongValue value={attempt.payloadHash} width={170} /> : "-"}</DetailPair>
        </Facts>
        {steps.length > 0 && (
          <div className="flex flex-col gap-1">
            <div className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Steps</div>
            <AttemptSteps result={attempt.result} />
          </div>
        )}
      </div>
    ),
    tone: shape.tone,
    icon: shape.icon,
    order: 2,
    lane: "dispatch",
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
        <div className="flex flex-col gap-3">
          <Facts>
            <DetailPair label="Ended">{activity.completedUtc !== null ? <RelativeTime value={activity.completedUtc} /> : "still running"}</DetailPair>
            <DetailPair label="Flow">{activity.flowName}</DetailPair>
          </Facts>
          {activity.parametersJson !== null && (
            <div className="flex flex-col gap-1">
              <div className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Parameters</div>
              <CodeView value={storedJson(activity.parametersJson)} language="json" height={140} />
            </div>
          )}
        </div>
      )
      : null,
    tone,
    icon: shape.icon,
    order: 3,
    lane: "intervention",
    testId: `journey-intervention-${activity.kind}`,
  };
}

/**
 * Every dated fact the ledger holds about the record, as one ordered story: when the row was received, when the record
 * entered the ledger, every dispatch with its outcome, every intervention with who asked for it, and what OSDU holds now.
 */
function buildEvents(
  record: DeliveryRecord, attempts: DeliveryAttempt[], activities: DeliveryActivity[], chain: DeliveryRecordChain | undefined,
): JourneyEvent[] {
  const events: JourneyEvent[] = chainEvents(chain);

  // A record that has not been delivered has only the pending row, which is then simply the row it was received as;
  // a newer row is a fact only once there is a delivered row for it to be newer than.
  const received = record.sourceUpdatedUtc ?? record.pendingSourceUpdatedUtc;
  if (received !== null) {
    const delivered = record.sourceUpdatedUtc !== null;
    events.push({
      id: "received",
      at: received,
      title: "Received: the row reached the ingestion table",
      summary: [
        <Origin key="o" file={delivered ? record.sourceFileName : record.pendingSourceFileName} row={delivered ? record.sourceRowNumber : record.pendingSourceRowNumber} />,
        delivered ? "the version the delivered document was built from" : "the version the document is built from",
      ],
      error: null,
      note: null,
      more: null,
      tone: "info",
      icon: Inbox,
      order: 0,
      lane: "chain",
      testId: "journey-received",
    });
  }

  if (record.sourceUpdatedUtc !== null && record.pendingSourceUpdatedUtc !== null && record.pendingSourceUpdatedUtc !== record.sourceUpdatedUtc) {
    events.push({
      id: "received-pending",
      at: record.pendingSourceUpdatedUtc,
      title: "Received: a newer row reached the ingestion table",
      summary: [<Origin key="o" file={record.pendingSourceFileName} row={record.pendingSourceRowNumber} />, "the version the waiting document is built from"],
      error: null,
      note: null,
      more: null,
      tone: "info",
      icon: FileInput,
      order: 0,
      lane: "chain",
      testId: "journey-received-pending",
    });
  }

  events.push({
    id: "planned",
    at: record.createdUtc,
    title: "Planned: the record entered the ledger",
    summary: [
      record.targetId !== null ? <span key="t">claimed the OSDU id <LongValue value={record.targetId} /></span> : "no OSDU id claimed yet",
      `mapping ${record.mappingName}`,
    ],
    error: null,
    note: null,
    more: null,
    tone: "info",
    icon: ScanSearch,
    order: 1,
    lane: "chain",
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
      order: 4,
      lane: "state",
      testId: "journey-verified",
    });
  }

  if (record.status === "waiting") {
    events.push({
      id: "waiting",
      at: record.updatedUtc,
      title: "Waiting for a record it refers to",
      summary: [record.waitingFor !== null && <LongValue key="w" value={record.waitingFor} />],
      error: null,
      note: record.lastError,
      more: null,
      tone: "info",
      icon: Hourglass,
      order: 5,
      lane: "state",
      testId: "journey-waiting",
    });
  }

  if (record.hasPendingDocument && record.status === "pending") {
    events.push({
      id: "queued",
      at: record.updatedUtc,
      title: "Queued: a rendered document waits to be dispatched",
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
      order: 6,
      lane: "state",
      testId: "journey-queued",
    });
  }

  // Newest first: what happened last is what an operator came for, and it sits at the top of the bounded list.
  return events.sort((a, b) => {
    const byTime = Date.parse(b.at) - Date.parse(a.at);
    return byTime !== 0 && Number.isFinite(byTime) ? byTime : b.order - a.order;
  });
}

/**
 * The answers an operator came for, in one strip, in the order the estate moves a row: landed by pre-ingestion, loaded
 * into the table by ingestion, planned into the ledger, dispatched, landed in OSDU, verified, removed.
 */
function milestones(record: DeliveryRecord, attempts: DeliveryAttempt[], chain: DeliveryRecordChain | undefined): SummaryCell[] {
  const dispatched = attempts.filter((a) => a.outcome === "delivered" || a.outcome === "failed" || a.outcome === "held");
  const failed = dispatched.filter((a) => a.outcome === "failed").length;
  const removed = attempts.find((a) => a.outcome === "deleted");
  const pre = chain?.stages.find((s) => s.stage === "pre-ingestion");
  const ing = chain?.stages.find((s) => s.stage === "ingestion");

  // The ingestion cell answers WHEN the row reached the table, which the ledger always knows (that is where it read
  // the file and row from), and names the run that loaded it when the platform recorded one. A run that was not
  // recorded leaves the time in place and says only that: "no run recorded" beside a time that proves the row is
  // there would read as a contradiction.
  const received = record.sourceUpdatedUtc ?? record.pendingSourceUpdatedUtc;
  const receivedFrom = record.sourceFileName ?? record.pendingSourceFileName;
  const cells: SummaryCell[] = [
    {
      label: "Pre-ingestion",
      value: pre === undefined ? (chain === undefined ? "-" : "no run recorded") : <RelativeTime value={pre.ranUtc} />,
      caption: pre === undefined
        ? (chain?.fileName ?? receivedFrom ?? "no file recorded")
        : `${pre.flowName}${pre.success ? "" : ` (${pre.status})`}`,
      tone: pre !== undefined && !pre.success ? "destructive" : undefined,
      testId: "milestone-pre",
    },
    {
      label: "Ingestion",
      value: received !== null ? <RelativeTime value={received} /> : ing !== undefined ? <RelativeTime value={ing.ranUtc} /> : chain === undefined ? "-" : "no run recorded",
      caption: ing !== undefined
        ? `${ing.flowName}${ing.success ? "" : ` (${ing.status})`}`
        : chain === undefined
          ? (receivedFrom ?? undefined)
          : received !== null
            ? "the row is in the table; the run is not recorded"
            : "no ingestion run recorded for this row",
      tone: ing !== undefined && !ing.success ? "destructive" : undefined,
      testId: "milestone-ing",
    },
    {
      label: "Landed",
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
 * How far the record has come, as one strip in the order the estate moves a row: the run that landed its file, the
 * run that loaded its row, when it was planned, how often it was dispatched, when it landed and as which version, when
 * it was last verified, and whether it was removed. The page's spine: the timeline under it is the evidence.
 */
export function RecordMilestones({ record, attempts, chain }: {
  record: DeliveryRecord;
  attempts: DeliveryAttempt[] | undefined;
  /** The runs that carried the record's file through the estate; undefined while they are being read. */
  chain: DeliveryRecordChain | undefined;
}) {
  if (attempts === undefined) {
    return <Skeleton className="h-16 w-full rounded-lg" />;
  }

  // Six cells (seven for a removed record) at the strip's default floor need more width than the workbench's content
  // measure gives them and strand the last on a line of its own; a time and a short caption fit in 160px.
  return <SummaryStrip cells={milestones(record, attempts, chain)} minCellWidth={160} data-testid="record-milestones" />;
}

function laneMatches(event: JourneyEvent, filter: LaneFilter): boolean {
  return filter === "all" || event.lane === filter;
}

/**
 * The record's story from the ledger alone, newest first: where it stands, every intervention and who asked for it,
 * every dispatch and what OSDU answered, when it was planned and received, and the runs that carried its file. Each
 * entry is one line with the facts that place it; the rest of what the ledger holds about it (the steps a try took,
 * its worker and correlation id, an intervention's parameters) opens under the entry. The list scrolls inside a
 * bounded box, so a record with hundreds of tries does not push the rest of the page away; it narrows to the
 * dispatches or the interventions alone.
 */
export function RecordJourney({ record, attempts, activities, chain }: {
  record: DeliveryRecord;
  attempts: DeliveryAttempt[] | undefined;
  activities: DeliveryActivity[] | undefined;
  /** The runs that carried the record's file through the estate; undefined while they are being read. */
  chain: DeliveryRecordChain | undefined;
}) {
  const [filter, setFilter] = useState<LaneFilter>("all");
  const [opened, setOpened] = useState<ReadonlySet<string>>(() => new Set());
  const loaded = attempts !== undefined && activities !== undefined;
  const events = useMemo(
    () => (loaded ? buildEvents(record, attempts, activities, chain) : []),
    [loaded, record, attempts, activities, chain]);
  const listed = useMemo(() => events.filter((event) => laneMatches(event, filter)), [events, filter]);
  const dispatches = events.filter((event) => event.lane === "dispatch").length;
  const interventions = events.filter((event) => event.lane === "intervention").length;

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
    <Card className="gap-3 rounded-lg p-3" data-testid="record-journey">
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
          <ToggleGroupItem value="dispatch" className="px-2.5 text-xs">{`Dispatches (${dispatches})`}</ToggleGroupItem>
          <ToggleGroupItem value="intervention" className="px-2.5 text-xs">{`Interventions (${interventions})`}</ToggleGroupItem>
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
              title={filter === "dispatch" ? "No dispatch yet" : filter === "intervention" ? "No intervention on this record" : "Nothing recorded yet"}
              description={filter === "dispatch" ? "The record has not been sent to OSDU." : filter === "intervention" ? "Nobody has released, redelivered, verified or removed it." : undefined}
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
                              className="flex min-w-0 flex-wrap items-baseline gap-x-2 text-left hover:underline"
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
                        <div className="mt-2 rounded-md border bg-muted/30 p-3" data-testid="journey-event-detail">
                          {event.more}
                        </div>
                      )}
                    </div>
                  </li>
                );
              })}
            </ol>
          )}
      {loaded && chain !== undefined && !chain.fileKnown && chain.note !== null && (
        <p className="text-[12px] text-muted-foreground" data-testid="record-chain-note">{chain.note}</p>
      )}
    </Card>
  );
}
