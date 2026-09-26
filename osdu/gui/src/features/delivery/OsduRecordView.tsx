import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { Download, Eye, Globe, History, Scale, SearchX, UserRoundCog, X, type LucideIcon } from "lucide-react";
import { toast } from "sonner";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { cn } from "@/lib/utils";
import { isApiError } from "@/api/client";
import type { ComputeTask } from "@/api/types";
import { IconAction } from "@/components/IconAction";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import { deliveryApi, type DeliveryOsduRead } from "../../api/delivery";
import { downloadJson, fileNameOf } from "./osduDocument";
import { OsduRecordTree } from "./OsduRecordTree";
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

/** A list of short values as chips, each carrying the glyph and hover title that say what it is; nothing when there are none. */
function Values({ values, icon: Icon, what, testId }: { values: string[]; icon: LucideIcon; what: string; testId?: string }) {
  if (values.length === 0) {
    return null;
  }

  return (
    <span className="inline-flex flex-wrap gap-1" data-testid={testId}>
      {values.map((value) => (
        <Badge key={value} variant="outline" className="gap-1 font-mono text-[11px] font-normal" title={what}>
          <Icon className="size-3 text-muted-foreground" />
          {value}
        </Badge>
      ))}
    </span>
  );
}

/** How many versions show before the older ones wait behind a button. */
const VERSIONS_SHOWN = 12;

/**
 * The record's history as the target keeps it: every version, newest first, each a click away, with the one being
 * shown, the latest, and the one this flow's ledger holds as delivered marked. A target that keeps no version list
 * says so in one line; a list that could not be read says why.
 */
function RecordVersions({ read, ledgerVersion, onReadVersion }: {
  read: DeliveryOsduRead;
  ledgerVersion: number | null;
  onReadVersion?: (version: number) => void;
}) {
  const [all, setAll] = useState(false);
  const versions = read.versions ?? null;
  if (versions === null) {
    return read.historyError
      ? <p className="text-[12px] text-muted-foreground" data-testid="osdu-history-error">{`The version list could not be read: ${read.historyError}`}</p>
      : <p className="text-[12px] text-muted-foreground" data-testid="osdu-no-history">This target keeps no version list for its records, so the record is read at its latest alone.</p>;
  }

  const shown = read.readVersion ?? read.version ?? null;
  const latest = versions[0] ?? null;
  const listed = all ? versions : versions.slice(0, VERSIONS_SHOWN);
  return (
    <div className="flex flex-wrap items-center gap-x-2 gap-y-1.5" data-testid="osdu-record-versions">
      <span className="inline-flex items-center gap-1 text-[11px] font-medium uppercase tracking-wide text-muted-foreground" title="Every version OSDU keeps of this record, newest first; each reads the record as it was then">
        <History className="size-3.5" />
        {versions.length === 0 ? "No versions" : `${versions.length} version${versions.length === 1 ? "" : "s"}`}
      </span>
      {listed.map((version) => {
        const current = version === shown;
        const marks = [version === latest ? "latest" : null, version === ledgerVersion ? "delivered by this flow" : null].filter((mark): mark is string => mark !== null);
        const title = current ? "The version shown" : "Read the record as it was at this version";
        return (
          <span key={version} className="inline-flex items-center gap-1">
            {current
              ? (
                <span className="inline-flex h-7 items-center rounded-md bg-primary/15 px-2.5 font-mono text-[11px] font-semibold tabular-nums text-primary ring-1 ring-inset ring-primary/40" title={title} data-testid="osdu-record-version" data-state="active">
                  {version}
                </span>
              )
              : (
                <Button
                  variant="outline"
                  size="sm"
                  className="h-7 font-mono text-[11px] tabular-nums"
                  onClick={() => onReadVersion?.(version)}
                  disabled={onReadVersion === undefined}
                  title={title}
                  data-testid="osdu-version"
                >
                  {version}
                </Button>
              )}
            {marks.map((mark) => (
              <span key={mark} className={cn("text-[10px] uppercase tracking-wide", mark === "latest" ? "text-muted-foreground" : "text-success")}>{mark}</span>
            ))}
          </span>
        );
      })}
      {versions.length > VERSIONS_SHOWN && (
        <Button variant="ghost" size="sm" className="h-7" onClick={() => setAll((was) => !was)} data-testid="osdu-versions-more">
          {all ? "Fewer" : `${versions.length - VERSIONS_SHOWN} older`}
        </Button>
      )}
      {shown !== null && latest !== null && shown !== latest && (
        <Badge variant="secondary" className="bg-warning/15 text-warning" data-testid="osdu-version-older">
          {`showing version ${shown}, not the latest`}
        </Badge>
      )}
      {ledgerVersion !== null && versions.length > 0 && !versions.includes(ledgerVersion) && (
        <p className="text-[12px] text-warning" data-testid="osdu-version-missing">
          {`The ledger holds version ${ledgerVersion} as delivered by this flow, and OSDU no longer lists it.`}
        </p>
      )}
    </div>
  );
}

/**
 * A record as OSDU holds it: OSDU's own fields (its version, who created and last changed it, and when), its access and
 * legal tags, the versions it keeps, and the document as a tree with the records it refers to readable where they
 * stand. A read that found nothing says so, with the id it looked for.
 */
export function OsduRecordView({ read, onOpenLink, opening, onReadVersion, ledgerVersion }: {
  read: DeliveryOsduRead;
  /** Reads a record the document refers to; absent where links are not followed. */
  onOpenLink?: (id: string) => void;
  /** The linked record being read now, whose button shows it. */
  opening?: string | null;
  /** Reads this same record at one of the versions the target keeps; absent where a version cannot be asked for. */
  onReadVersion?: (version: number) => void;
  /** The version the ledger holds as delivered by this flow, marked in the version list. */
  ledgerVersion?: number | null;
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
  const kind = text(record.kind);
  const created = text(record.createTime);
  const modified = text(record.modifyTime);

  return (
    <Card className="gap-0 rounded-lg p-0" data-testid="osdu-record">
      {/* Who the record is in OSDU: its kind, and who wrote it when. One quiet line each; the id is in the page's header. */}
      <div className="flex flex-col gap-2.5 px-4 pt-3 pb-3">
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
          <TruncatedText text={kind} mono maxWidth={520} copy className="text-[13px] font-medium text-foreground" title="Kind" />
          <span className="text-[12px] text-muted-foreground" data-testid="osdu-record-provenance">
            {created !== null && (
              <span>
                {"created "}
                <RelativeTime value={created} absolute />
                {text(record.createUser) && <span>{` by ${text(record.createUser)}`}</span>}
              </span>
            )}
            {created !== null && modified !== null && <span>{" · "}</span>}
            {modified !== null && (
              <span>
                {"last modified "}
                <RelativeTime value={modified} absolute />
                {text(record.modifyUser) && <span>{` by ${text(record.modifyUser)}`}</span>}
              </span>
            )}
          </span>
        </div>
        <AccessRow acl={acl} legal={legal} />
        <RecordVersions read={read} ledgerVersion={ledgerVersion ?? null} onReadVersion={onReadVersion} />
      </div>
      <div className="border-t px-4 py-3">
        <OsduRecordTree
          record={record}
          ownId={read.targetId}
          onOpenLink={onOpenLink}
          opening={opening}
          actions={(
            <IconAction
              label="Download the record as JSON"
              icon={<Download />}
              variant="outline"
              className="size-8"
              onClick={() => downloadJson(fileNameOf("osdu", read.targetId), record)}
              data-testid="osdu-record-download"
            />
          )}
        />
      </div>
      {/* The read's own diagnostics: when it happened and the correlation id its requests carried, for whoever reads OSDU's logs. */}
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 border-t px-4 py-2 text-[11px] text-muted-foreground">
        <span>{"read "}<RelativeTime value={read.readUtc} absolute /></span>
        {read.correlationId && (
          <span className="inline-flex items-center gap-1">
            correlation
            <TruncatedText text={read.correlationId} mono maxWidth={300} copy className="text-[11px]" />
          </span>
        )}
      </div>
    </Card>
  );
}

/** The one way access and legal reach a reader: a chip per group and tag, each with the glyph that says what it is. */
function AccessRow({ acl, legal }: { acl: Record<string, unknown>; legal: Record<string, unknown> }) {
  const viewers = texts(acl.viewers);
  const owners = texts(acl.owners);
  const tags = texts(legal.legaltags);
  const countries = texts(legal.otherRelevantDataCountries);
  if (viewers.length + owners.length + tags.length + countries.length === 0) {
    return <span className="text-[12px] text-muted-foreground">No access groups or legal tags on the record.</span>;
  }

  return (
    <div className="flex flex-wrap items-center gap-1.5" data-testid="osdu-record-access">
      <Values values={viewers} icon={Eye} what="viewer group" testId="osdu-record-viewers" />
      <Values values={owners} icon={UserRoundCog} what="owner group" testId="osdu-record-owners" />
      <Values values={tags} icon={Scale} what="legal tag" testId="osdu-record-legal" />
      <Values values={countries} icon={Globe} what="relevant country" />
    </div>
  );
}

/** What a read of an OSDU record came to: in progress, failed, or the record, with the reads it opened beneath it. */
export function OsduReadResult({ task, label, onOpenLink, opening, onReadVersion, ledgerVersion }: {
  task: ComputeTask | undefined;
  label: string;
  onOpenLink?: (id: string) => void;
  opening?: string | null;
  onReadVersion?: (version: number) => void;
  ledgerVersion?: number | null;
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

  return <OsduRecordView read={task.result as DeliveryOsduRead} onOpenLink={onOpenLink} opening={opening} onReadVersion={onReadVersion} ledgerVersion={ledgerVersion} />;
}

/** One linked record read in turn: its own task, polled to its end, and the records it refers to, readable in their turn. */
function LinkedRead({ id, taskId, onOpenLink, opening, onClose, onReadVersion }: {
  id: string;
  taskId: string;
  onOpenLink: (id: string) => void;
  opening: string | null;
  onClose: () => void;
  /** Reads this linked record again at one of its versions, in its own place in the chain. */
  onReadVersion: (version: number) => void;
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
      {task.isError ? <ProblemView error={task.error} /> : <OsduReadResult task={task.data} label="Reading the linked record through the flow's route" onOpenLink={onOpenLink} opening={opening} onReadVersion={onReadVersion} />}
    </Card>
  );
}

/**
 * A record read from OSDU, and the records it refers to read in turn beneath it: following a link reads that record
 * through the same flow's route and credentials, on a node, and opens it below the one it was found in. Following a link
 * from further up closes what was opened below it.
 */
export function OsduRecordPanel({ pipelineId, interfaceName, task, label, onReadVersion, ledgerVersion }: {
  pipelineId: string | null;
  interfaceName: string | null;
  task: ComputeTask | undefined;
  label: string;
  /** Reads the page's own record again at one of its versions; absent where it cannot be asked for. */
  onReadVersion?: (version: number) => void;
  /** The version the ledger holds as delivered by this flow, marked in the record's version list. */
  ledgerVersion?: number | null;
}) {
  const [chain, setChain] = useState<{ id: string; taskId: string }[]>([]);
  const [opening, setOpening] = useState<{ id: string; level: number } | null>(null);
  const open = useMutation({
    mutationFn: ({ id, version }: { id: string; level: number; version?: number }) => deliveryApi.readOsdu(pipelineId!, id, interfaceName, version),
    onMutate: (asked) => setOpening(asked),
    // A link followed from a level replaces what was opened below it; the oldest reads close past the chain's length.
    onSuccess: (accepted, asked) => setChain((was) => [...was.slice(0, asked.level), { id: asked.id, taskId: accepted.taskId }].slice(-MAX_CHAIN)),
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
    onSettled: () => setOpening(null),
  });
  const openFrom = (level: number) => pipelineId === null ? undefined : (id: string) => open.mutate({ id, level });

  return (
    <div className="flex flex-col gap-3" data-testid="osdu-panel">
      <OsduReadResult task={task} label={label} onOpenLink={openFrom(0)} opening={opening?.level === 0 ? opening.id : null} onReadVersion={onReadVersion} ledgerVersion={ledgerVersion} />
      {chain.map((link, index) => (
        <LinkedRead
          key={link.taskId}
          id={link.id}
          taskId={link.taskId}
          onOpenLink={(id) => open.mutate({ id, level: index + 1 })}
          opening={opening?.level === index + 1 ? opening.id : null}
          onClose={() => setChain((was) => was.slice(0, index))}
          // A version of a linked record takes that record's own place in the chain, and closes what was opened below it.
          onReadVersion={(version) => open.mutate({ id: link.id, level: index, version })}
        />
      ))}
    </div>
  );
}
