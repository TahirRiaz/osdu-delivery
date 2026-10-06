import { useCallback, useState, useSyncExternalStore, type ReactNode } from "react";
import { Link as RouterLink } from "react-router-dom";
import { Ban, CircleAlert, Hourglass, KeyRound, Loader2, Send, Trash2, Undo2, type LucideIcon } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import type { DeliveryRecord, DeliveryRecordLink } from "../../api/delivery";
import { shortId } from "./idTail";

type Tone = "destructive" | "warning" | "info" | "muted";

const TONE: Record<Tone, string> = {
  destructive: "",
  warning: "border-warning/40 text-warning *:data-[slot=alert-description]:text-warning/90",
  info: "border-info/40 *:data-[slot=alert-description]:text-foreground [&>svg]:text-info",
  muted: "*:data-[slot=alert-description]:text-muted-foreground [&>svg]:text-muted-foreground",
};

function Line({ tone, icon: Icon, spin = false, testId, children }: { tone: Tone; icon: LucideIcon; spin?: boolean; testId: string; children: ReactNode }) {
  return (
    <Alert variant={tone === "destructive" ? "destructive" : "default"} className={cn("py-2", TONE[tone])} data-testid={testId}>
      <Icon className={spin ? "animate-spin" : undefined} />
      {/* One block for the whole line: the description lays its children out as rows, which would put every
          clipped value and time of the sentence on a row of its own. */}
      <AlertDescription className="text-[13px]"><div className="min-w-0 break-words">{children}</div></AlertDescription>
    </Alert>
  );
}

/** A link to another record's page, named as the ledger names the record, with its flow on hover. */
export function RecordLink({ link, testId }: { link: DeliveryRecordLink; testId?: string }) {
  const flow = link.flowName === null ? undefined : link.interface ? `${link.flowName} / ${link.interface}` : link.flowName;
  return (
    <RouterLink to={`/delivery/records/${link.flowId}/${link.deliveryKey}`} className="text-primary hover:underline" title={flow} data-testid={testId}>
      {link.label ?? link.sourceKey}
    </RouterLink>
  );
}

/**
 * Whether an instant has passed, kept current while the page stays open: a lease that runs out while the operator is
 * looking at the record turns into the "ran out" line without a reload. The clock is read in an effect, never in
 * render, so a render is the same whenever it happens.
 */
function useHasPassed(instantUtc: string | null): boolean {
  const at = instantUtc === null ? Number.NaN : Date.parse(instantUtc);
  const subscribe = useCallback((onChange: () => void) => {
    const timer = window.setInterval(onChange, 15_000);
    return () => window.clearInterval(timer);
  }, []);
  return useSyncExternalStore(subscribe, () => Number.isFinite(at) && Date.now() >= at);
}

function tries(count: number): string {
  return `${count} ${count === 1 ? "try" : "tries"}`;
}

/** What is waiting to go: the metadata, the payload, or both. */
function pendingWhat(record: DeliveryRecord): string {
  return record.pendingMetadata && record.pendingPayload ? "metadata and payload" : record.pendingPayload ? "payload" : "metadata";
}

function NextTry({ record }: { record: DeliveryRecord }) {
  return record.nextAttemptUtc === null ? null : <span>{" Next try "}<RelativeTime value={record.nextAttemptUtc} />.</span>;
}

/**
 * The steps of the waiting delivery an earlier try completed (a file uploaded, a dataset registered) before it stopped,
 * which the next try resumes after rather than doing again; what each returned is on hover.
 */
function StepsDone({ record }: { record: DeliveryRecord }) {
  const steps = record.pendingSteps === null ? [] : Object.keys(record.pendingSteps);
  if (steps.length === 0) {
    return null;
  }

  return (
    <span title={JSON.stringify(record.pendingSteps, null, 2)} data-testid="record-steps-done">
      {" An earlier try completed "}
      <span className="font-mono">{steps.join(", ")}</span>
      {"; the next try resumes after them."}
    </span>
  );
}

/** The most records waiting for this one the record's page is given (the control plane's `MaxWaitersShown`). */
const MAX_WAITERS_SHOWN = 50;

/** How many of the records waiting for this one the line names before it offers the rest. */
const WAITERS_NAMED = 5;

/**
 * The records whose waiting document refers to this one: what this record's state costs beyond itself. A record that
 * failed, is held or is blocked keeps them waiting, so the line warns then. The first few are named, the rest one click
 * away, each a link to its own page with its flow on hover.
 */
function Waiters({ record, waiters }: { record: DeliveryRecord; waiters: DeliveryRecordLink[] }) {
  const [all, setAll] = useState(false);
  const named = all ? waiters : waiters.slice(0, WAITERS_NAMED);
  const stuck = record.blocked || record.status === "failed" || record.status === "held";
  const count = waiters.length >= MAX_WAITERS_SHOWN
    ? `${MAX_WAITERS_SHOWN} or more records wait`
    : waiters.length === 1 ? "1 record waits" : `${waiters.length} records wait`;
  return (
    <Line tone={stuck ? "warning" : "info"} icon={Hourglass} testId="record-waited-on-by">
      {record.status === "delivered" ? `${count} for this one still: ` : `${count} for this one to be delivered: `}
      {named.map((waiter, i) => (
        <span key={`${waiter.flowId}:${waiter.deliveryKey}`}>
          {i > 0 && ", "}
          <RecordLink link={waiter} testId="record-waiter-link" />
        </span>
      ))}
      {waiters.length > named.length
        ? (
          // Kept on one line: the button is a box of its own, and the full stop after it wrapped alone.
          <span className="whitespace-nowrap">
            {" and "}
            <Button variant="link" className="h-auto p-0 text-[13px]" onClick={() => setAll(true)} data-testid="record-waiters-more">
              {`${waiters.length - named.length} more`}
            </Button>
            .
          </span>
        )
        : "."}
    </Line>
  );
}

/**
 * Where the record stands right now and why, said once, in the lines its state calls for and in no others: blocked,
 * held or failed with its error, waiting for another record, mid-delivery under a lease (or a lease that ran out), a
 * rendered document waiting to go (with the steps an earlier try of it completed), removed, and the records waiting for
 * this one. A delivered record with nothing wrong has no situation and gets no lines: the status pill and the milestones
 * say everything there is.
 */
export function RecordSituation({ record, waitsOn, waitedOnBy }: {
  record: DeliveryRecord;
  waitsOn: DeliveryRecordLink | null | undefined;
  /** The records whose waiting document refers to this one, the first `MAX_WAITERS_SHOWN`. */
  waitedOnBy: DeliveryRecordLink[];
}) {
  const leaseExpired = useHasPassed(record.leaseExpiresUtc);
  const lines: ReactNode[] = [];

  if (record.blocked) {
    lines.push(
      <Line key="blocked" tone="warning" icon={Ban} testId="record-blocked-note">
        {record.status === "deleted"
          ? "Blocked: nothing is sent for this record until it is released."
          : "Blocked: nothing is sent until the source changes or the record is released."}
      </Line>,
    );
  }

  switch (record.status) {
    case "held":
    case "failed":
      lines.push(
        <Line key="error" tone="destructive" icon={CircleAlert} testId="record-error">
          <span className="font-medium">{record.status === "held" ? "Held back" : "Failed"}</span>
          {` after ${tries(record.attemptCount)}.`}
          <NextTry record={record} />
          {record.hasPendingDocument && (
            <span>
              {` The rendered ${pendingWhat(record)} waits`}
              {record.workBatch !== null && ` in work batch ${record.workBatch}`}
              {record.blocked ? " until the record is released." : " for the next try."}
              <StepsDone record={record} />
            </span>
          )}
          {record.lastError !== null && <div className="mt-1 break-words">{record.lastError}</div>}
        </Line>,
      );
      break;
    case "waiting":
      lines.push(
        <Line key="waiting" tone="info" icon={Hourglass} testId="record-waiting">
          <span>{record.lastError ?? `Waits for ${record.waitingFor ?? "a record it refers to"}.`}</span>
          {waitsOn && (
            <span>
              {" It goes out on its own once "}
              <RecordLink link={waitsOn} testId="record-waits-on-link" />
              {" is delivered."}
            </span>
          )}
          {record.planRequestedUtc !== null && <span>{" A plan last asked for it "}<RelativeTime value={record.planRequestedUtc} />.</span>}
        </Line>,
      );
      break;
    case "delivering":
      lines.push(
        leaseExpired
          ? (
            <Line key="lease" tone="warning" icon={KeyRound} testId="record-lease">
              {"The lease "}
              <TruncatedText text={record.leaseOwner ?? "a worker"} mono maxWidth={220} />
              {" held ran out "}
              <RelativeTime value={record.leaseExpiresUtc} />
              {": the worker stopped mid-delivery. The flow's next deliver run waits it out and recovers the record."}
            </Line>
          )
          : (
            <Line key="lease" tone="info" icon={Loader2} spin testId="record-lease">
              {"Being delivered by "}
              <TruncatedText text={record.leaseOwner ?? "a worker"} mono maxWidth={220} />
              {record.leaseExpiresUtc !== null && <span>{"; the lease runs until "}<RelativeTime value={record.leaseExpiresUtc} /></span>}
              .
            </Line>
          ),
      );
      break;
    case "pending":
      lines.push(
        <Line key="pending" tone="info" icon={Send} testId="record-pending">
          {record.hasPendingDocument
            ? (
              <span>
                {`A rendered document (${pendingWhat(record)}) waits to be dispatched`}
                {record.workBatch !== null && ` in work batch ${record.workBatch}`}
                {record.lastSubmissionId !== null && <span>{" of submission "}<span className="font-mono" title={record.lastSubmissionId}>{shortId(record.lastSubmissionId)}</span></span>}
                .
              </span>
            )
            : "Pending: the flow's next deliver run renders the record from its source row and sends it."}
          <NextTry record={record} />
          {record.hasPendingDocument && <StepsDone record={record} />}
        </Line>,
      );
      break;
    case "delivered":
      if (record.hasPendingDocument) {
        lines.push(
          <Line key="newer" tone="info" icon={Send} testId="record-pending">
            {`A newer document (${pendingWhat(record)}) is rendered and waits to go`}
            {record.workBatch !== null && ` in work batch ${record.workBatch}`}
            .
            <NextTry record={record} />
            <StepsDone record={record} />
          </Line>,
        );
      }
      break;
    case "deleted":
      lines.push(
        <Line key="deleted" tone="muted" icon={Trash2} testId="record-deleted">
          Removed from OSDU: nothing of this flow&apos;s is there under its id. Redeliver sends it again from the current source.
        </Line>,
      );
      break;
    case "reverted":
      lines.push(
        <Line key="reverted" tone="muted" icon={Undo2} testId="record-reverted">
          {`Reverted: a reversal gave OSDU back the version it held before the run it reversed${record.targetVersion !== null ? `, as version ${record.targetVersion}` : ""}. `}
          A release plans it again from the current source; a changed source plans it on its own.
        </Line>,
      );
      break;
  }

  if (waitedOnBy.length > 0) {
    lines.push(<Waiters key="waiters" record={record} waiters={waitedOnBy} />);
  }

  if (record.status !== "delivering" && record.leaseOwner !== null) {
    lines.push(
      <Line key="stale-lease" tone="muted" icon={KeyRound} testId="record-lease">
        {"A lease is still recorded for "}
        <TruncatedText text={record.leaseOwner} mono maxWidth={220} />
        {record.leaseExpiresUtc !== null && <span>{leaseExpired ? ", which ran out " : ", which runs until "}<RelativeTime value={record.leaseExpiresUtc} /></span>}
        .
      </Line>,
    );
  }

  if (lines.length === 0) {
    return null;
  }

  return <div className="flex flex-col gap-2" data-testid="record-situation">{lines}</div>;
}
