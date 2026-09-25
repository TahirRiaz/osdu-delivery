import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { BookOpenCheck, Download, Link2, SearchX, X } from "lucide-react";
import { toast } from "sonner";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { isApiError } from "@/api/client";
import type { ComputeTask } from "@/api/types";
import { CodeView } from "@/components/CodeView";
import { DataTable, type Column } from "@/components/DataTable";
import { DetailPair } from "@/components/DetailPair";
import { IdChip } from "@/components/IdChip";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import { deliveryApi, type DeliveryOsduRead } from "../../api/delivery";
import { downloadJson, fileNameOf, recordReferences, type RecordReferenceAt } from "./osduDocument";
import { ProblemView, TaskProgress } from "./TemplateSheet";
import { isTerminalTask, useComputeTask } from "./useComputeTask";

/** How many linked records can be opened one inside the other before the oldest is closed. */
const MAX_CHAIN = 6;

function texts(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : [];
}

function text(value: unknown): string | null {
  return typeof value === "string" ? value : typeof value === "number" ? String(value) : null;
}

/** A list of short values as badges, or a dash. */
function Values({ values, testId }: { values: string[]; testId?: string }) {
  return values.length === 0
    ? <span className="text-muted-foreground">-</span>
    : (
      <span className="inline-flex flex-wrap gap-1" data-testid={testId}>
        {values.map((value) => <Badge key={value} variant="outline" className="font-mono text-[11px]">{value}</Badge>)}
      </span>
    );
}

/**
 * A record as OSDU holds it: OSDU's own fields (its version, who created and last changed it, and when), its access and
 * legal tags, the document itself, and the records it refers to, each of which can be read in turn. A read that found
 * nothing says so, with the id it looked for.
 */
export function OsduRecordView({ read, onOpenLink, opening }: {
  read: DeliveryOsduRead;
  /** Reads a record the document refers to; absent where links are not followed. */
  onOpenLink?: (id: string) => void;
  /** The linked record being read now, whose button shows it. */
  opening?: string | null;
}) {
  if (!read.found || !read.record) {
    return (
      <Alert data-testid="osdu-not-found">
        <SearchX />
        <AlertTitle>OSDU holds no record under this id</AlertTitle>
        <AlertDescription>
          <span className="font-mono text-[12px] break-all">{read.targetId}</span>
          <span className="text-[12px] text-muted-foreground">
            {`Read through ${read.flow}`}
            {read.correlationId ? `, correlation id ${read.correlationId}` : ""}
            {". A record never delivered, or removed with a purge, reads this way."}
          </span>
        </AlertDescription>
      </Alert>
    );
  }

  const record = read.record;
  const acl = (record.acl ?? {}) as Record<string, unknown>;
  const legal = (record.legal ?? {}) as Record<string, unknown>;
  const links = recordReferences(record, read.targetId);
  const linkColumns: Column<RecordReferenceAt>[] = [
    { id: "id", header: "OSDU id", fill: true, render: (row) => <TruncatedText text={row.id} mono maxWidth={460} copy copyTestId="copy-osdu-link" /> },
    { id: "paths", header: "Where", render: (row) => <TruncatedText text={row.paths.join(", ")} mono maxWidth={300} /> },
    ...(onOpenLink === undefined ? [] : [{
      id: "open",
      header: "",
      render: (row: RecordReferenceAt) => (
        <Button
          variant="outline"
          size="sm"
          className="h-7"
          onClick={(event) => { event.stopPropagation(); onOpenLink(row.id); }}
          disabled={opening === row.id}
          data-testid="osdu-link-read"
        >
          <BookOpenCheck />
          Read
        </Button>
      ),
    }]),
  ];

  return (
    <div className="flex flex-col gap-3" data-testid="osdu-record">
      <div className="flex flex-wrap items-center gap-2">
        <IdChip label="osdu" value={read.targetId} display={read.targetId} testId="osdu-record-id" copyTestId="copy-osdu-record-id" />
        {read.version !== null && read.version !== undefined && <Badge variant="outline" className="font-mono" data-testid="osdu-record-version">{`version ${read.version}`}</Badge>}
        <Button
          variant="outline"
          size="sm"
          className="ml-auto"
          onClick={() => downloadJson(fileNameOf("osdu", read.targetId), record)}
          data-testid="osdu-record-download"
        >
          <Download />
          Download JSON
        </Button>
      </div>
      <div className="grid grid-cols-1 gap-x-6 gap-y-2 sm:grid-cols-2 xl:grid-cols-3">
        <DetailPair label="Kind"><TruncatedText text={text(record.kind)} mono maxWidth={320} /></DetailPair>
        <DetailPair label="Created">
          <span className="inline-flex flex-wrap items-baseline gap-1">
            <RelativeTime value={text(record.createTime)} absolute />
            {text(record.createUser) && <span className="text-[12px] text-muted-foreground">{`by ${text(record.createUser)}`}</span>}
          </span>
        </DetailPair>
        <DetailPair label="Last modified">
          <span className="inline-flex flex-wrap items-baseline gap-1">
            <RelativeTime value={text(record.modifyTime)} absolute />
            {text(record.modifyUser) && <span className="text-[12px] text-muted-foreground">{`by ${text(record.modifyUser)}`}</span>}
          </span>
        </DetailPair>
        <DetailPair label="Viewers"><Values values={texts(acl.viewers)} testId="osdu-record-viewers" /></DetailPair>
        <DetailPair label="Owners"><Values values={texts(acl.owners)} testId="osdu-record-owners" /></DetailPair>
        <DetailPair label="Legal tags"><Values values={texts(legal.legaltags)} testId="osdu-record-legal" /></DetailPair>
        <DetailPair label="Countries"><Values values={texts(legal.otherRelevantDataCountries)} /></DetailPair>
        <DetailPair label="Read"><RelativeTime value={read.readUtc} absolute /></DetailPair>
        <DetailPair label="Correlation id"><TruncatedText text={read.correlationId ?? null} mono maxWidth={260} copy={Boolean(read.correlationId)} /></DetailPair>
      </div>
      <CodeView value={JSON.stringify(record, null, 2)} language="json" height={420} data-testid="osdu-record-json" />
      <div className="flex flex-col gap-1">
        <h3 className="flex items-center gap-1 text-[13px] font-medium"><Link2 className="size-4" />Records it refers to</h3>
        <DataTable
          columns={linkColumns}
          rows={links}
          rowKey={(row) => row.id}
          emptyMessage="The record refers to no other OSDU record."
          data-testid="osdu-record-links"
        />
      </div>
    </div>
  );
}

/** What a read of an OSDU record came to: in progress, failed, or the record, with the reads it opened beneath it. */
export function OsduReadResult({ task, label, onOpenLink, opening }: {
  task: ComputeTask | undefined;
  label: string;
  onOpenLink?: (id: string) => void;
  opening?: string | null;
}) {
  if (task === undefined || !isTerminalTask(task)) {
    return <TaskProgress label={label} task={task} testId="osdu-read-progress" />;
  }

  if (task.status !== "succeeded" || task.result === null || task.result === undefined) {
    return (
      <Alert variant="destructive" data-testid="osdu-read-failed">
        <AlertTitle>The record could not be read</AlertTitle>
        <AlertDescription className="whitespace-pre-wrap">{task.error ?? `The read ended ${task.status} without an answer.`}</AlertDescription>
      </Alert>
    );
  }

  return <OsduRecordView read={task.result as DeliveryOsduRead} onOpenLink={onOpenLink} opening={opening} />;
}

/** One linked record read in turn: its own task, polled to its end, and the records it refers to, readable in their turn. */
function LinkedRead({ id, taskId, onOpenLink, opening, onClose }: {
  id: string;
  taskId: string;
  onOpenLink: (id: string) => void;
  opening: string | null;
  onClose: () => void;
}) {
  const task = useComputeTask(taskId);
  return (
    <Card className="gap-2 rounded-lg p-3" data-testid="osdu-linked">
      <div className="flex items-center gap-2 text-[13px]">
        <span className="font-medium">Linked record</span>
        <span className="min-w-0 truncate font-mono text-[12px] text-muted-foreground">{id}</span>
        <Button variant="ghost" size="icon" className="ml-auto size-7" onClick={onClose} aria-label="Close the linked record" data-testid="osdu-linked-close">
          <X />
        </Button>
      </div>
      {task.isError ? <ProblemView error={task.error} /> : <OsduReadResult task={task.data} label="Reading the linked record through the flow's route" onOpenLink={onOpenLink} opening={opening} />}
    </Card>
  );
}

/**
 * A record read from OSDU, and the records it refers to read in turn beneath it: following a link reads that record
 * through the same flow's route and credentials, on a node, and opens it below the one it was found in. Following a link
 * from further up closes what was opened below it.
 */
export function OsduRecordPanel({ pipelineId, interfaceName, task, label }: {
  pipelineId: string | null;
  interfaceName: string | null;
  task: ComputeTask | undefined;
  label: string;
}) {
  const [chain, setChain] = useState<{ id: string; taskId: string }[]>([]);
  const [opening, setOpening] = useState<{ id: string; level: number } | null>(null);
  const open = useMutation({
    mutationFn: ({ id }: { id: string; level: number }) => deliveryApi.readOsdu(pipelineId!, id, interfaceName),
    onMutate: (asked) => setOpening(asked),
    // A link followed from a level replaces what was opened below it; the oldest reads close past the chain's length.
    onSuccess: (accepted, asked) => setChain((was) => [...was.slice(0, asked.level), { id: asked.id, taskId: accepted.taskId }].slice(-MAX_CHAIN)),
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
    onSettled: () => setOpening(null),
  });
  const openFrom = (level: number) => pipelineId === null ? undefined : (id: string) => open.mutate({ id, level });

  return (
    <div className="flex flex-col gap-3" data-testid="osdu-panel">
      <OsduReadResult task={task} label={label} onOpenLink={openFrom(0)} opening={opening?.level === 0 ? opening.id : null} />
      {chain.map((link, index) => (
        <LinkedRead
          key={link.taskId}
          id={link.id}
          taskId={link.taskId}
          onOpenLink={(id) => open.mutate({ id, level: index + 1 })}
          opening={opening?.level === index + 1 ? opening.id : null}
          onClose={() => setChain((was) => was.slice(0, index))}
        />
      ))}
    </div>
  );
}
