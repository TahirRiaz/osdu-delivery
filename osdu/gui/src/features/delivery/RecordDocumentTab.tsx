import { Link as RouterLink } from "react-router-dom";
import { Card } from "@/components/ui/card";
import { CodeView } from "@/components/CodeView";
import { DetailPair } from "@/components/DetailPair";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import type { DeliveryRecord } from "../../api/delivery";
import { prettyJson } from "./prettyJson";

function SectionHeading({ title, description }: { title: string; description?: string }) {
  return (
    <div className="flex flex-wrap items-baseline gap-2">
      <h3 className="text-[13px] font-medium">{title}</h3>
      {description !== undefined && <span className="text-[12px] text-muted-foreground">{description}</span>}
    </div>
  );
}

function Hash({ value, testId }: { value: string | null; testId: string }) {
  return <TruncatedText text={value} mono maxWidth={200} copy={value !== null} copyTestId={testId} />;
}

/** What is waiting to go: the metadata, the payload, or both. */
function pendingWhat(record: DeliveryRecord): string {
  return record.pendingMetadata && record.pendingPayload ? "metadata and payload" : record.pendingPayload ? "payload" : "metadata";
}

/**
 * The document side of the record: what is rendered and waiting to go (where it sits, its payload, the steps an
 * earlier try completed), what OSDU answered when it last landed, the fingerprints the ledger decides "changed" by,
 * and the render context the mapping was given. The In OSDU tab reads what OSDU holds; this tab is what the ledger holds.
 */
export function RecordDocumentTab({ record }: { record: DeliveryRecord }) {
  return (
    <div className="flex flex-col gap-3">
      <Card className="gap-3 rounded-lg p-3" data-testid="record-pending-document">
        <SectionHeading title="Waiting to go" description="the rendered document the next dispatch sends" />
        {record.hasPendingDocument
          ? (
            <>
              <p className="text-[13px] text-muted-foreground" data-testid="record-pending-ref">
                {`The ${pendingWhat(record)} is rendered and waits in work batch ${record.workBatch ?? "?"} of submission `}
                {record.lastSubmissionId !== null
                  ? <RouterLink to={`/delivery/submissions/${record.lastSubmissionId}`} className="font-mono text-primary hover:underline">{record.lastSubmissionId.slice(0, 8)}</RouterLink>
                  : "?"}
                {` (reference ${record.pendingDocumentRef ?? "?"}); the node that drains the batch reads it from the flow's work location.`}
              </p>
              <div className="grid gap-4 [grid-template-columns:repeat(auto-fill,minmax(200px,1fr))]">
                <DetailPair label="Payload location">
                  <TruncatedText text={record.pendingPayloadLocation} mono maxWidth={260} copy={record.pendingPayloadLocation !== null} copyTestId="copy-record-payload-location" />
                </DetailPair>
                <DetailPair label="Payload files modified"><RelativeTime value={record.payloadModifiedUtc} /></DetailPair>
                <DetailPair label="Next try"><RelativeTime value={record.nextAttemptUtc} /></DetailPair>
              </div>
              {record.pendingSteps !== null && (
                <div className="flex flex-col gap-1">
                  <div className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Steps an earlier try completed</div>
                  <CodeView value={prettyJson(JSON.stringify(record.pendingSteps))} language="json" height={160} data-testid="record-pending-steps" />
                </div>
              )}
            </>
          )
          : (
            <p className="text-[13px] text-muted-foreground">
              {record.status === "deleted"
                ? "Nothing is waiting: the record was removed from OSDU. Redeliver renders it again."
                : record.status === "delivered"
                  ? "Nothing is waiting: OSDU holds the current version. A change to the source row plans a new document."
                  : "Nothing is rendered yet: the flow's next deliver run renders the record from its source row."}
            </p>
          )}
      </Card>

      <Card className="gap-3 rounded-lg p-3">
        <SectionHeading title="What OSDU returned" description="the identifiers the target answered with when the record last landed" />
        {record.targetState !== null
          ? <CodeView value={prettyJson(JSON.stringify(record.targetState))} language="json" height={220} data-testid="record-target-state" />
          : <p className="text-[13px] text-muted-foreground">Nothing yet: the record has not been delivered.</p>}
      </Card>

      <Card className="gap-3 rounded-lg p-3">
        <SectionHeading title="Fingerprints" description="what the ledger compares to decide whether a source change needs sending" />
        <div className="grid gap-4 [grid-template-columns:repeat(auto-fill,minmax(200px,1fr))]">
          <DetailPair label="Metadata hash"><Hash value={record.metadataHash} testId="copy-record-metadata-hash" /></DetailPair>
          <DetailPair label="Payload hash"><Hash value={record.payloadHash} testId="copy-record-payload-hash" /></DetailPair>
          <DetailPair label="Source fingerprint"><Hash value={record.sourceFingerprint} testId="copy-record-fingerprint" /></DetailPair>
        </div>
      </Card>

      <Card className="gap-3 rounded-lg p-3">
        <SectionHeading title="Render context" description="the parameters and values the mapping was rendered with" />
        {record.renderContext
          ? <CodeView value={prettyJson(record.renderContext)} language="json" height={280} data-testid="record-render-context" />
          : <p className="text-[13px] text-muted-foreground">Recorded once the record has been delivered.</p>}
      </Card>
    </div>
  );
}
