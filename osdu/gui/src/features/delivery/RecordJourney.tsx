import { useMemo, useState, type ReactNode } from "react";
import { Link as RouterLink } from "react-router-dom";
import {
  CheckCircle2, CircleDashed, Database, Eraser, FileInput, Hourglass, Inbox, PauseCircle, RotateCcw, ScanSearch, Send,
  ShieldCheck, Trash2, Unlock, UserRoundCog, XCircle, type LucideIcon,
} from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import { RelativeTime } from "@/components/RelativeTime";
import { SummaryStrip, type SummaryCell } from "@/components/SummaryStrip";
import type { DeliveryActivity, DeliveryAttempt, DeliveryChainStage, DeliveryRecord, DeliveryRecordChain } from "../../api/delivery";

type Tone = "success" | "destructive" | "warning" | "info" | "muted";

/** One thing that happened to the record, as the ledger holds it: when, what, and where to read more. */
interface JourneyEvent {
  id: string;
  at: string;
  title: string;
  detail: ReactNode;
  tone: Tone;
  icon: LucideIcon;
  /** Events the ledger dates alike (an attempt's start and the completion it wrote) keep their order by this. */
  order: number;
  testId: string;
}

const TONE_RING: Record<Tone, string> = {
  success: "border-success/50 bg-success/10 text-success",
  destructive: "border-destructive/50 bg-destructive/10 text-destructive",
  warning: "border-warning/50 bg-warning/10 text-warning",
  info: "border-info/50 bg-info/10 text-info",
  muted: "border-border bg-muted text-muted-foreground",
};

/** How many events show before the middle folds away, and how many of the latest stay in view when it does. */
const FOLD_ABOVE = 14;
const FOLD_HEAD = 3;
const FOLD_TAIL = 8;

function Mono({ children }: { children: ReactNode }) {
  return <span className="font-mono text-[12px]">{children}</span>;
}

function Origin({ file, row }: { file: string | null; row: number | null }) {
  if (file === null) {
    return <span>the ingestion table (the file is not recorded)</span>;
  }

  return (
    <span>
      <Mono>{file}</Mono>
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

/** The pieces of an event's detail line, separated so a missing one leaves no dangling separator. */
function Detail({ parts }: { parts: ReactNode[] }) {
  const shown = parts.filter((part) => part !== null && part !== undefined && part !== false);
  return (
    <span className="flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[12px] text-muted-foreground">
      {shown.map((part, index) => <span key={index}>{part}</span>)}
    </span>
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

/** How long a run took, for a chain stage's detail line. */
function runDuration(seconds: number | null): string | null {
  if (seconds === null || !Number.isFinite(seconds)) {
    return null;
  }

  return seconds < 1 ? `${Math.round(seconds * 1000)} ms` : seconds < 90 ? `${seconds.toFixed(1)} s` : `${(seconds / 60).toFixed(1)} min`;
}

/** The stage's icon and tone: where in the chain it is, and whether that run ended well. */
function chainShape(stage: DeliveryChainStage): { icon: LucideIcon; tone: Tone } {
  const tone: Tone = stage.success ? "success" : stage.status === "running" || stage.status === "queued" ? "info" : "destructive";
  return { icon: stage.stage === "ingestion" ? Database : FileInput, tone };
}

/**
 * The runs that carried the record's file through the estate before it reached the delivery ledger: pre-ingestion
 * landing the file, ingestion loading it into the table the delivery flow reads. They are facts from the platform's
 * own record of processed files, so they take their place in the same timeline as everything else.
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
        ? `Ingested: ${stage.flowName} loaded the file into its table`
        : stage.stage === "pre-ingestion"
          ? `Landed: ${stage.flowName} took the file in`
          : `${stage.flowName} handled the file`,
      detail: (
        <div className="flex flex-col gap-0.5">
          <Detail parts={[
            <span key="f"><Mono>{stage.fileName}</Mono></span>,
            stage.rows > 0 && `${stage.rows.toLocaleString()} row${stage.rows === 1 ? "" : "s"}`,
            duration !== null && `took ${duration}`,
            !stage.success && `ended ${stage.status}`,
            <RunRef key="r" runId={stage.runId} />,
            <RouterLink key="p" to={`/pipelines/${stage.pipelineId}`} className="text-[12px] text-primary hover:underline">the flow</RouterLink>,
          ]} />
          {stage.error !== null && <span className="text-[12px] text-destructive">{stage.error}</span>}
        </div>
      ),
      tone,
      icon,
      order: 0,
      testId: `journey-chain-${stage.stage}`,
    } satisfies JourneyEvent;
  });
}

/**
 * Every dated fact the ledger holds about the record, as one ordered story: when the row was received, when the record
 * entered the ledger, every dispatch with its outcome, every intervention with who asked for it, and what OSDU holds now.
 */
function buildEvents(
  record: DeliveryRecord, attempts: DeliveryAttempt[], activities: DeliveryActivity[], chain: DeliveryRecordChain | undefined,
): JourneyEvent[] {
  const events: JourneyEvent[] = chainEvents(chain);

  if (record.sourceUpdatedUtc !== null) {
    events.push({
      id: "received",
      at: record.sourceUpdatedUtc,
      title: "Received: the row reached the ingestion table",
      detail: <Detail parts={[<Origin key="o" file={record.sourceFileName} row={record.sourceRowNumber} />, "the version the delivered document was built from"]} />,
      tone: "info",
      icon: Inbox,
      order: 0,
      testId: "journey-received",
    });
  }

  if (record.pendingSourceUpdatedUtc !== null && record.pendingSourceUpdatedUtc !== record.sourceUpdatedUtc) {
    events.push({
      id: "received-pending",
      at: record.pendingSourceUpdatedUtc,
      title: "Received: a newer row reached the ingestion table",
      detail: <Detail parts={[<Origin key="o" file={record.pendingSourceFileName} row={record.pendingSourceRowNumber} />, "the version the waiting document is built from"]} />,
      tone: "info",
      icon: FileInput,
      order: 0,
      testId: "journey-received-pending",
    });
  }

  events.push({
    id: "planned",
    at: record.createdUtc,
    title: "Planned: the record entered the ledger",
    detail: <Detail parts={[
      record.targetId !== null ? <span key="t">claimed the OSDU id <Mono>{record.targetId}</Mono></span> : "no OSDU id claimed yet",
      `mapping ${record.mappingName}`,
    ]} />,
    tone: "info",
    icon: ScanSearch,
    order: 1,
    testId: "journey-planned",
  });

  for (const attempt of attempts) {
    const shape = ATTEMPT_TITLES[attempt.outcome] ?? { title: () => attempt.outcome, tone: "muted" as Tone, icon: CircleDashed };
    const duration = attemptDuration(attempt);
    const steps = attempt.result?.steps ?? [];
    events.push({
      id: `attempt-${attempt.attemptId}`,
      at: attempt.startedUtc,
      title: shape.title(attempt),
      detail: (
        <div className="flex flex-col gap-0.5">
          <Detail parts={[
            attempt.phase !== "" && <span key="p">phase <Mono>{attempt.phase}</Mono></span>,
            duration !== null && `took ${duration}`,
            steps.length > 0 && `${steps.length} step${steps.length === 1 ? "" : "s"}`,
            attempt.result?.correlationId && <span key="c">correlation <Mono>{attempt.result.correlationId}</Mono></span>,
            <span key="w">by <Mono>{attempt.worker}</Mono></span>,
            <RunRef key="r" runId={attempt.runId} />,
            <SubmissionRef key="s" submissionId={attempt.submissionId} />,
            attempt.sourceFileName !== null && <span key="o">from <Origin file={attempt.sourceFileName} row={attempt.sourceRowNumber} /></span>,
          ]} />
          {attempt.error !== null && <span className="text-[12px] text-destructive" data-testid="journey-attempt-error">{attempt.error}</span>}
          {attempt.result?.detail && <span className="text-[12px] text-muted-foreground">{attempt.result.detail}</span>}
        </div>
      ),
      tone: shape.tone,
      icon: shape.icon,
      order: 2,
      testId: `journey-attempt-${attempt.outcome}`,
    });
  }

  for (const activity of activities) {
    const shape = ACTIVITY_TITLES[activity.kind] ?? { title: activity.kind, icon: UserRoundCog };
    const tone: Tone = activity.outcome === "failed" ? "destructive" : activity.outcome === "running" ? "info" : "muted";
    events.push({
      id: `activity-${activity.activityId}`,
      at: activity.startedUtc,
      title: `${shape.title} by ${activity.actor}`,
      detail: <Detail parts={[
        activity.outcome,
        activity.summary,
        <RunRef key="r" runId={activity.runId} />,
        <SubmissionRef key="s" submissionId={activity.submissionId} />,
      ]} />,
      tone,
      icon: shape.icon,
      order: 3,
      testId: `journey-intervention-${activity.kind}`,
    });
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
      detail: <Detail parts={[outcome !== null && `outcome ${outcome}`, record.targetVersion !== null && `ledger version ${record.targetVersion}`]} />,
      tone: outcome === "match" ? "success" : outcome === "error" ? "muted" : "warning",
      icon: ShieldCheck,
      order: 4,
      testId: "journey-verified",
    });
  }

  if (record.status === "waiting") {
    events.push({
      id: "waiting",
      at: record.updatedUtc,
      title: "Waiting for a record it refers to",
      detail: <Detail parts={[record.waitingFor !== null && <Mono key="w">{record.waitingFor}</Mono>, record.lastError]} />,
      tone: "info",
      icon: Hourglass,
      order: 5,
      testId: "journey-waiting",
    });
  }

  if (record.hasPendingDocument && record.status === "pending") {
    events.push({
      id: "queued",
      at: record.updatedUtc,
      title: "Queued: a rendered document waits to be dispatched",
      detail: <Detail parts={[
        record.nextAttemptUtc !== null && <span key="n">next try <RelativeTime value={record.nextAttemptUtc} /></span>,
        record.workBatch !== null && `work batch ${record.workBatch}`,
        <SubmissionRef key="s" submissionId={record.lastSubmissionId} />,
      ]} />,
      tone: "info",
      icon: Send,
      order: 6,
      testId: "journey-queued",
    });
  }

  return events.sort((a, b) => {
    const byTime = Date.parse(a.at) - Date.parse(b.at);
    return byTime !== 0 && Number.isFinite(byTime) ? byTime : a.order - b.order;
  });
}

/**
 * The answers an operator came for, in one strip, in the order the estate moves a row: landed in pre-ingestion, loaded
 * by ingestion, planned into the ledger, dispatched, landed in OSDU, verified, removed.
 */
function milestones(record: DeliveryRecord, attempts: DeliveryAttempt[], chain: DeliveryRecordChain | undefined): SummaryCell[] {
  const dispatched = attempts.filter((a) => a.outcome === "delivered" || a.outcome === "failed" || a.outcome === "held");
  const failed = dispatched.filter((a) => a.outcome === "failed").length;
  const removed = attempts.find((a) => a.outcome === "deleted");
  const pre = chain?.stages.find((s) => s.stage === "pre-ingestion");
  const ing = chain?.stages.find((s) => s.stage === "ingestion");
  const cells: SummaryCell[] = [
    {
      label: "Pre-ingestion",
      value: pre === undefined ? (chain === undefined ? "-" : "not recorded") : <RelativeTime value={pre.ranUtc} />,
      caption: pre === undefined
        ? (chain?.fileName ?? "no file recorded")
        : `${pre.flowName}${pre.success ? "" : ` (${pre.status})`}`,
      tone: pre !== undefined && !pre.success ? "destructive" : undefined,
      testId: "milestone-pre",
    },
    {
      label: "Ingestion",
      value: ing === undefined ? (chain === undefined ? "-" : "not recorded") : <RelativeTime value={ing.ranUtc} />,
      caption: ing === undefined ? "no ingestion run recorded for this file" : `${ing.flowName}${ing.success ? "" : ` (${ing.status})`}`,
      tone: ing !== undefined && !ing.success ? "destructive" : undefined,
      testId: "milestone-ing",
    },
    {
      label: "Received",
      value: <RelativeTime value={record.sourceUpdatedUtc ?? record.pendingSourceUpdatedUtc} />,
      caption: record.sourceFileName ?? record.pendingSourceFileName ?? "file not recorded",
      testId: "milestone-received",
    },
    {
      label: "Planned",
      value: <RelativeTime value={record.createdUtc} />,
      caption: record.lastSubmissionId ? `last by ${record.lastSubmissionId.slice(0, 8)}` : undefined,
      testId: "milestone-planned",
    },
    {
      label: "Dispatched",
      value: dispatched.length === 0 ? "not yet" : `${dispatched.length} time${dispatched.length === 1 ? "" : "s"}`,
      caption: dispatched.length === 0
        ? (record.status === "pending" || record.status === "delivering" ? "queued" : record.status === "waiting" ? "waiting" : undefined)
        : failed > 0 ? `${failed} failed` : "every try landed",
      tone: failed > 0 ? "warning" : undefined,
      testId: "milestone-dispatched",
    },
    {
      label: "Landed",
      value: record.lastDeliveredUtc === null ? "not yet" : <RelativeTime value={record.lastDeliveredUtc} />,
      caption: record.lastDeliveredUtc === null ? undefined : record.targetVersion !== null ? `version ${record.targetVersion}` : undefined,
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
 * The record's story from the ledger alone: when it was received, when it was planned, every dispatch and what OSDU
 * answered, every intervention and who asked for it, and where it stands. The strip on top answers the questions an
 * operator arrives with; the timeline under it is the evidence, oldest first, folded in the middle when it is long.
 */
export function RecordJourney({ record, attempts, activities, chain }: {
  record: DeliveryRecord;
  attempts: DeliveryAttempt[] | undefined;
  activities: DeliveryActivity[] | undefined;
  /** The runs that carried the record's file through the estate; undefined while they are being read. */
  chain: DeliveryRecordChain | undefined;
}) {
  const [unfolded, setUnfolded] = useState(false);
  const loaded = attempts !== undefined && activities !== undefined;
  const events = useMemo(
    () => (loaded ? buildEvents(record, attempts, activities, chain) : []),
    [loaded, record, attempts, activities, chain]);

  const folded = !unfolded && events.length > FOLD_ABOVE;
  const shown = folded ? [...events.slice(0, FOLD_HEAD), ...events.slice(events.length - FOLD_TAIL)] : events;
  const hidden = folded ? events.length - FOLD_HEAD - FOLD_TAIL : 0;

  return (
    <Card className="gap-3 rounded-lg p-3" data-testid="record-journey">
      <div className="flex flex-wrap items-baseline gap-2">
        <h2 className="text-[13px] font-medium">Journey</h2>
        <span className="text-[12px] text-muted-foreground">
          where this record has been: the file that arrived, the runs that carried it through pre-ingestion and
          ingestion, and everything the delivery ledger then did with it
        </span>
      </div>
      {!loaded
        ? <Skeleton className="h-24 w-full rounded-md" />
        : (
          <>
            <SummaryStrip cells={milestones(record, attempts, chain)} data-testid="record-milestones" />
            <ol className="flex flex-col" data-testid="record-journey-events">
              {shown.map((event, index) => {
                const Icon = event.icon;
                const last = index === shown.length - 1;
                const foldHere = folded && index === FOLD_HEAD - 1;
                return (
                  <li key={event.id} className="relative flex gap-3" data-testid={event.testId}>
                    <div className="flex flex-col items-center">
                      <span className={cn("flex size-6 shrink-0 items-center justify-center rounded-full border", TONE_RING[event.tone])}>
                        <Icon className="size-3.5" />
                      </span>
                      {!last && <span className={cn("w-px flex-1 bg-border", foldHere && "border-l border-dashed border-border bg-transparent")} />}
                    </div>
                    <div className={cn("flex min-w-0 flex-1 flex-col gap-0.5 pb-3", last && "pb-0")}>
                      <div className="flex flex-wrap items-baseline gap-x-2">
                        <span className="text-[13px] font-medium">{event.title}</span>
                        <span className="text-[12px] text-muted-foreground"><RelativeTime value={event.at} absolute /></span>
                      </div>
                      {event.detail}
                      {foldHere && (
                        <div className="pt-2">
                          <Button variant="outline" size="sm" className="h-7" onClick={() => setUnfolded(true)} data-testid="record-journey-unfold">
                            {`Show ${hidden} more event${hidden === 1 ? "" : "s"}`}
                          </Button>
                        </div>
                      )}
                    </div>
                  </li>
                );
              })}
            </ol>
            {events.length > FOLD_ABOVE && unfolded && (
              <div>
                <Button variant="ghost" size="sm" className="h-7" onClick={() => setUnfolded(false)}>Fold the middle away</Button>
              </div>
            )}
            {chain !== undefined && !chain.fileKnown && chain.note !== null && (
              <p className="text-[12px] text-muted-foreground" data-testid="record-chain-note">{chain.note}</p>
            )}
            {record.blocked && (
              <Badge variant="secondary" className="w-fit bg-warning/15 text-warning">
                blocked: nothing is sent until the source changes or the record is released
              </Badge>
            )}
          </>
        )}
    </Card>
  );
}
