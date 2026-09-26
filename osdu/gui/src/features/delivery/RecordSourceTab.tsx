import { Database } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import type { ComputeTask } from "@/api/types";
import { CodeView } from "@/components/CodeView";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import type { DeliveryRecord } from "../../api/delivery";
import { Fact, FactGrid, NoFact } from "./Facts";
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

/** An ingestion file and the row in it, on one line, the file clipped at its cell and copyable whole. */
function FileRow({ file, row, copyTestId, testId }: { file: string | null; row: number | null; copyTestId?: string; testId?: string }) {
  if (file === null) {
    return <NoFact>not recorded</NoFact>;
  }

  // Two tracks, the file's sized by the cell rather than by its content, so the file clips there and the row number
  // keeps its place after it: a content-sized wrapper would size to the whole file name and run out of the cell.
  return (
    <span className="grid max-w-full items-baseline gap-x-1 [grid-template-columns:minmax(0,max-content)_auto]" data-testid={testId}>
      <TruncatedText text={file} mono maxWidth={360} copy copyTestId={copyTestId} title="Ingestion file" />
      {row !== null && <span className="text-muted-foreground">row <span className="font-mono">{row}</span></span>}
    </span>
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
      <Card className="gap-2 rounded-lg p-3">
        <SectionHeading title="Where the row came from" description="the file and row the ledger records against every delivered version" />
        <FactGrid>
          <Fact label="Source key"><TruncatedText text={record.sourceKey} mono maxWidth={360} copy copyTestId="copy-record-source-key" title="Source key" /></Fact>
          <Fact label="Delivery key"><TruncatedText text={record.deliveryKey} mono maxWidth={360} copy copyTestId="copy-record-key" title="Delivery key" /></Fact>
          <Fact label="Mapping">{record.mappingName}</Fact>
          <Fact label="Ingestion file"><FileRow file={file} row={row} copyTestId="copy-record-origin" testId="record-origin" /></Fact>
          <Fact label="Row received"><RelativeTime value={record.sourceUpdatedUtc ?? record.pendingSourceUpdatedUtc} /></Fact>
          <Fact label="Source modified"><RelativeTime value={record.sourceModifiedUtc} /></Fact>
        </FactGrid>
        {newerRow && (
          <div className="flex flex-col gap-1.5 rounded-md border border-info/40 px-3 py-2" data-testid="record-newer-row">
            <SectionHeading title="A newer row is waiting" description="the version the waiting document is built from" />
            <FactGrid>
              <Fact label="Ingestion file"><FileRow file={record.pendingSourceFileName} row={record.pendingSourceRowNumber} /></Fact>
              <Fact label="Row received"><RelativeTime value={record.pendingSourceUpdatedUtc} /></Fact>
            </FactGrid>
          </div>
        )}
        {record.sourceKeyJson !== null && (
          <div className="flex flex-col gap-1">
            <div className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Key columns</div>
            <CodeView value={keyColumns(record.sourceKeyJson)} language="json" height={120} data-testid="record-source-key-json" />
          </div>
        )}
      </Card>

      <Card className="gap-2 rounded-lg p-3">
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
