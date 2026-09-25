import { Database } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import type { ComputeTask } from "@/api/types";
import { CodeView } from "@/components/CodeView";
import { DetailPair } from "@/components/DetailPair";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import type { DeliveryRecord } from "../../api/delivery";
import { prettyJson } from "./prettyJson";
import { TaskResultCard } from "./TaskResultCard";

function SectionHeading({ title, description }: { title: string; description?: string }) {
  return (
    <div className="flex flex-wrap items-baseline gap-2">
      <h3 className="text-[13px] font-medium">{title}</h3>
      {description !== undefined && <span className="text-[12px] text-muted-foreground">{description}</span>}
    </div>
  );
}

/** The record's key columns as the ledger stored them, pretty when they parse, verbatim when they do not. */
function keyColumns(json: string): string {
  try {
    return prettyJson(json);
  } catch {
    return json;
  }
}

/**
 * Where the record came from: the source key that names it, the ingestion file and row the delivered document was
 * built from (and the newer row a waiting document is built from), the key columns that find it in the ingestion
 * tables, and a read of its rows as those tables hold them now, on a node with the flow's own connection.
 */
export function RecordSourceTab({ record, canRead, reading, onRead, task }: {
  record: DeliveryRecord;
  /** Whether the read can be queued: the record's flow is known and no other node task of the page is in flight. */
  canRead: boolean;
  reading: boolean;
  onRead: () => void;
  /** The read as it stands, once one was queued. */
  task: { id: string; state: ComputeTask | undefined } | null;
}) {
  // A record that has not been delivered has only the pending row, which is then simply where it came from; a newer
  // row is a fact only once there is a delivered row for it to be newer than.
  const newerRow = record.sourceUpdatedUtc !== null && record.pendingSourceUpdatedUtc !== null && record.pendingSourceUpdatedUtc !== record.sourceUpdatedUtc;
  const file = record.sourceFileName ?? record.pendingSourceFileName;
  const row = record.sourceFileName !== null ? record.sourceRowNumber : record.pendingSourceRowNumber;

  return (
    <div className="flex flex-col gap-3">
      <Card className="gap-3 rounded-lg p-3">
        <SectionHeading title="Where the row came from" description="the file and row the ledger records against every delivered version" />
        <div className="grid gap-4 [grid-template-columns:repeat(auto-fill,minmax(200px,1fr))]">
          <DetailPair label="Source key"><TruncatedText text={record.sourceKey} mono maxWidth={260} copy copyTestId="copy-record-source-key" /></DetailPair>
          <DetailPair label="Delivery key"><TruncatedText text={record.deliveryKey} mono maxWidth={200} copy copyTestId="copy-record-key" /></DetailPair>
          <DetailPair label="Ingestion file">
            {file === null
              ? <span className="text-muted-foreground">not recorded</span>
              : (
                <span className="flex min-w-0 flex-wrap items-baseline gap-1" data-testid="record-origin">
                  <TruncatedText text={file} mono maxWidth={240} copy copyTestId="copy-record-origin" />
                  {row !== null && <span className="text-[12px] text-muted-foreground">row <span className="font-mono">{row}</span></span>}
                </span>
              )}
          </DetailPair>
          <DetailPair label="Row received"><RelativeTime value={record.sourceUpdatedUtc ?? record.pendingSourceUpdatedUtc} /></DetailPair>
          <DetailPair label="Source last modified"><RelativeTime value={record.sourceModifiedUtc} /></DetailPair>
          <DetailPair label="Mapping">{record.mappingName}</DetailPair>
        </div>
        {newerRow && (
          <div className="flex flex-col gap-2 rounded-md border border-info/40 p-3" data-testid="record-newer-row">
            <SectionHeading title="A newer row is waiting" description="the version the waiting document is built from" />
            <div className="grid gap-4 [grid-template-columns:repeat(auto-fill,minmax(200px,1fr))]">
              <DetailPair label="Ingestion file">
                {record.pendingSourceFileName === null
                  ? <span className="text-muted-foreground">not recorded</span>
                  : (
                    <span className="flex min-w-0 flex-wrap items-baseline gap-1">
                      <TruncatedText text={record.pendingSourceFileName} mono maxWidth={240} copy />
                      {record.pendingSourceRowNumber !== null && <span className="text-[12px] text-muted-foreground">row <span className="font-mono">{record.pendingSourceRowNumber}</span></span>}
                    </span>
                  )}
              </DetailPair>
              <DetailPair label="Row received"><RelativeTime value={record.pendingSourceUpdatedUtc} /></DetailPair>
            </div>
          </div>
        )}
        {record.sourceKeyJson !== null && (
          <div className="flex flex-col gap-1">
            <div className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Key columns</div>
            <CodeView value={keyColumns(record.sourceKeyJson)} language="json" height={120} data-testid="record-source-key-json" />
          </div>
        )}
      </Card>

      <Card className="gap-3 rounded-lg p-3">
        <SectionHeading title="As the ingestion tables hold it now" description="read on a node with the flow's own connection; nothing is written" />
        <div>
          <Button variant="outline" size="sm" onClick={onRead} disabled={!canRead || reading} data-testid="record-read-source">
            <Database />
            {task === null ? "Read the source row" : "Read again"}
          </Button>
        </div>
        {task !== null && <TaskResultCard key={task.id} label="The record in the ingestion tables" task={task.state} testId="record-source-task" />}
      </Card>
    </div>
  );
}
