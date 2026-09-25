import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { CircleAlert, GitCompare, Info, Loader2 } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import type { ComputeTask } from "@/api/types";
import { CodeView } from "@/components/CodeView";
import { DataTable, type Column } from "@/components/DataTable";
import { DiffView } from "@/components/DiffView";
import { TruncatedText } from "@/components/TruncatedText";
import { deliveryApi, type DeliveryOsduRead, type DeliveryRecordPreview, type DeliveryRecordRef } from "../../api/delivery";
import { canonicalText, differences, shortValue, withoutOsduFields, type DifferenceKind, type JsonDifference } from "./osduDocument";
import { PreviewActionBadge } from "./RecordPreviewView";
import { ProblemView, TaskProgress } from "./TemplateSheet";
import { isTerminalTask, useComputeTask } from "./useComputeTask";

const KIND_LABELS: Record<DifferenceKind, string> = {
  changed: "changed",
  onlyInOsdu: "only in OSDU",
  onlyInPreview: "would be added",
  placeholder: "given when sent",
};

const KIND_TONES: Record<DifferenceKind, string> = {
  changed: "bg-info/12 text-info",
  onlyInOsdu: "bg-warning/15 text-warning",
  onlyInPreview: "bg-success/15 text-success",
  placeholder: "bg-muted text-muted-foreground",
};

const differenceColumns: Column<JsonDifference>[] = [
  { id: "path", header: "Path", render: (row) => <span className="font-mono text-[12px] break-all">{row.path || "(the record)"}</span> },
  { id: "kind", header: "", render: (row) => <Badge variant="secondary" className={KIND_TONES[row.kind]}>{KIND_LABELS[row.kind]}</Badge> },
  { id: "osdu", header: "In OSDU", fill: true, render: (row) => <TruncatedText text={row.osdu === undefined ? null : shortValue(row.osdu)} mono maxWidth={360} /> },
  { id: "preview", header: "Would send now", fill: true, render: (row) => <TruncatedText text={row.preview === undefined ? null : shortValue(row.preview)} mono maxWidth={360} /> },
];

/** A settled task's result, a failure, or null while it runs. */
function settledOf<T>(task: ComputeTask | undefined): { result: T | null; failure: string | null } | null {
  if (task === undefined || !isTerminalTask(task)) {
    return null;
  }

  return task.status === "succeeded" && task.result !== null && task.result !== undefined
    ? { result: task.result as T, failure: null }
    : { result: null, failure: task.error ?? `The task ended ${task.status} without an answer.` };
}

/**
 * What OSDU holds for a record beside what a delivery would send now: the record rendered afresh from its current source
 * row, and read back from OSDU through its flow's route, both on a node and at the same moment. OSDU's own fields (its
 * version, who changed it and when) are set aside, keys are compared in order, and a value the platform gives when the
 * record is sent (a dataset id the File service mints) is named as such rather than as a change. The ledger keeps what it
 * sent as a hash, not as the document, so the comparison is with what the record renders to now.
 */
export function RecordCompare({ recordRef, targetId, canRun }: {
  recordRef: DeliveryRecordRef;
  targetId: string | null;
  /** Whether a node task may be queued for the record: the operate scope, and a record OSDU may hold. */
  canRun: boolean;
}) {
  const [tasks, setTasks] = useState<{ preview: string; read: string | null } | null>(null);
  const start = useMutation({
    mutationFn: async () => {
      const preview = await deliveryApi.previewRecord(recordRef);
      const read = targetId === null ? null : await deliveryApi.read(recordRef);
      return { preview: preview.taskId, read: read?.taskId ?? null };
    },
    onSuccess: setTasks,
  });
  const previewTask = useComputeTask(tasks?.preview ?? null);
  const readTask = useComputeTask(tasks?.read ?? null);
  const preview = settledOf<DeliveryRecordPreview>(previewTask.data);
  const read = tasks?.read === null ? { result: null, failure: null } : settledOf<DeliveryOsduRead>(readTask.data);
  const running = start.isPending || (tasks !== null && (preview === null || read === null));

  const document = preview?.result?.document ?? null;
  const wouldSend = document === null ? null : document.sent ?? document.rendered ?? null;
  const readResult = read?.result ?? null;
  const notes = preview?.result?.notes ?? [];
  // Worked out on each render: a render follows only a click or a task that settled, and the texts compare by value.
  const holds = readResult?.found === true && readResult.record ? withoutOsduFields(readResult.record) : null;
  const compared = holds !== null && wouldSend !== null
    ? { ...differences(holds, wouldSend), original: canonicalText(holds), modified: canonicalText(wouldSend) }
    : null;

  return (
    <div className="flex flex-col gap-3" data-testid="record-compare">
      <div className="flex flex-wrap items-center gap-3">
        <Button variant="outline" size="sm" onClick={() => start.mutate()} disabled={!canRun || running} data-testid="record-compare-run">
          {running ? <Loader2 className="animate-spin" /> : <GitCompare />}
          {tasks === null ? "Compare" : "Compare again"}
        </Button>
        <p className="text-[12px] text-muted-foreground">
          Renders the record from its current source row, reads what OSDU holds, and shows the two side by side. Nothing is sent.
        </p>
      </div>
      {start.isError && <ProblemView error={start.error} testId="record-compare-error" />}
      {tasks !== null && preview === null && <TaskProgress label="Rendering the record on a node" task={previewTask.data} testId="record-compare-preview-progress" />}
      {tasks !== null && tasks.read !== null && read === null && <TaskProgress label="Reading the record from OSDU" task={readTask.data} testId="record-compare-read-progress" />}
      {previewTask.isError && <ProblemView error={previewTask.error} />}
      {readTask.isError && <ProblemView error={readTask.error} />}
      {preview?.failure && (
        <Alert variant="destructive" data-testid="record-compare-preview-failed">
          <CircleAlert />
          <AlertTitle>The record could not be rendered</AlertTitle>
          <AlertDescription className="whitespace-pre-wrap">{preview.failure}</AlertDescription>
        </Alert>
      )}
      {read?.failure && (
        <Alert variant="destructive" data-testid="record-compare-read-failed">
          <CircleAlert />
          <AlertTitle>OSDU could not be read</AlertTitle>
          <AlertDescription className="whitespace-pre-wrap">{read.failure}</AlertDescription>
        </Alert>
      )}
      {preview?.result && !preview.result.found && (
        <Alert data-testid="record-compare-no-row">
          <Info />
          <AlertTitle>The record's row is not in the ingestion tables now</AlertTitle>
          <AlertDescription>{preview.result.reason}</AlertDescription>
        </Alert>
      )}
      {preview?.result?.found && document === null && (
        <Alert data-testid="record-compare-no-document">
          <Info />
          <AlertTitle>The row renders no document now</AlertTitle>
          <AlertDescription>{preview.result.noDocument}</AlertDescription>
        </Alert>
      )}
      {document?.omitted && <Alert><Info /><AlertDescription>{document.omitted}</AlertDescription></Alert>}
      {preview?.result?.decision && read !== null && (
        <div className="flex flex-wrap items-center gap-2 text-[13px]" data-testid="record-compare-summary">
          <span className="text-muted-foreground">The next run:</span>
          <PreviewActionBadge decision={preview.result.decision} />
          <span className="text-muted-foreground">{preview.result.decision.reason}</span>
        </div>
      )}
      {tasks !== null && tasks.read === null && wouldSend !== null && (
        <>
          <Alert data-testid="record-compare-no-target">
            <Info />
            <AlertTitle>OSDU holds nothing of this record to compare with</AlertTitle>
            <AlertDescription>The record has no OSDU id this flow delivered to, or was removed. A delivery would send the document below.</AlertDescription>
          </Alert>
          <CodeView value={canonicalText(wouldSend)} language="json" height={480} data-testid="record-compare-would-send" />
        </>
      )}
      {read?.result && !read.result.found && wouldSend !== null && (
        <>
          <Alert data-testid="record-compare-absent">
            <Info />
            <AlertTitle>OSDU holds no record under this id</AlertTitle>
            <AlertDescription>A delivery would create it with the document below.</AlertDescription>
          </Alert>
          <CodeView value={canonicalText(wouldSend)} language="json" height={480} data-testid="record-compare-would-send" />
        </>
      )}
      {compared !== null && (
        <>
          <div className="flex flex-wrap items-center gap-2 text-[13px]" data-testid="record-compare-counts">
            {compared.items.length === 0
              ? <Badge variant="secondary" className="bg-success/15 text-success" data-testid="record-compare-same">OSDU holds what a delivery would send now</Badge>
              : (Object.keys(KIND_LABELS) as DifferenceKind[]).map((kind) => {
                const count = compared.items.filter((item) => item.kind === kind).length;
                return count === 0 ? null : <Badge key={kind} variant="secondary" className={KIND_TONES[kind]}>{`${count} ${KIND_LABELS[kind]}`}</Badge>;
              })}
            {compared.truncated && <span className="text-muted-foreground">(the first {compared.items.length} differences)</span>}
          </div>
          <DiffView
            original={compared.original}
            modified={compared.modified}
            language="json"
            height={520}
            foldUnchanged
            sideLabels={{
              original: `In OSDU${read?.result?.version !== null && read?.result?.version !== undefined ? ` (version ${read.result.version})` : ""}`,
              modified: "Would send now",
            }}
            data-testid="record-compare-diff"
          />
          {compared.items.length > 0 && (
            <DataTable columns={differenceColumns} rows={compared.items} rowKey={(row) => `${row.kind}|${row.path}`} emptyMessage="No differences." data-testid="record-compare-differences" />
          )}
          {notes.length > 0 && (
            <ul className="flex flex-col gap-1 text-[12px] text-muted-foreground" data-testid="record-compare-notes">
              {notes.map((note) => <li key={note}>{note}</li>)}
            </ul>
          )}
        </>
      )}
    </div>
  );
}
