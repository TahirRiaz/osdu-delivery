import { useCallback, useSyncExternalStore, type ReactNode } from "react";
import { Link as RouterLink } from "react-router-dom";
import { Ban, CircleAlert, Hourglass, KeyRound, Loader2, Send, Trash2, type LucideIcon } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { cn } from "@/lib/utils";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import type { DeliveryRecord, DeliveryRecordLink } from "../../api/delivery";

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

/** A link to another record's page, named as the ledger names the record. */
export function RecordLink({ link, testId }: { link: DeliveryRecordLink; testId?: string }) {
  return (
    <RouterLink to={`/delivery/records/${link.flowId}/${link.deliveryKey}`} className="text-primary hover:underline" data-testid={testId}>
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
 * Where the record stands right now and why, said once, in the lines its state calls for and in no others: blocked,
 * held or failed with its error, waiting for another record, mid-delivery under a lease (or a lease that ran out), a
 * rendered document waiting to go, removed. A delivered record with nothing wrong has no situation and gets no lines:
 * the status pill and the milestones say everything there is.
 */
export function RecordSituation({ record, waitsOn }: { record: DeliveryRecord; waitsOn: DeliveryRecordLink | null | undefined }) {
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
                {record.lastSubmissionId !== null && <span>{" of submission "}<span className="font-mono">{record.lastSubmissionId.slice(0, 8)}</span></span>}
                .
              </span>
            )
            : "Pending: the flow's next deliver run renders the record from its source row and sends it."}
          <NextTry record={record} />
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
