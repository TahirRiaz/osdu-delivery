import { useRef, useState } from "react";
import { Link as RouterLink, useNavigate, useSearchParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { CircleCheck, CirclePause, CircleX, ListFilter, Loader2, Play, RefreshCw, ScanSearch, Unlock } from "lucide-react";
import { toast } from "sonner";
import {
  AlertDialog, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { isApiError } from "@/api/client";
import { CopyButton } from "@/components/CopyButton";
import { CorrelationError } from "@/components/CorrelationError";
import { DataTable, type Column } from "@/components/DataTable";
import { EmptyState } from "@/components/EmptyState";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import {
  deliveryApi, deliveryRecordRoute, ledgerLabel,
  type DeliveryProblem, type DeliveryRecord,
} from "../../api/delivery";
import { InterfacePicker } from "./InterfacePicker";
import { OutsidePartition } from "./PartitionNotice";
import { CompactTime, RecordIdentity } from "./RecordCells";
import { problemText } from "./problemText";
import { useInterfaceChoice } from "./useInterfaceChoice";
import { useWindowFit } from "./useWindowFit";

/** The least height the problem list keeps, so a short window still shows a few problems. */
const MIN_LIST_HEIGHT = 240;

/** What stays under the list and the detail: the page's padding and the workbench's status bar. */
const BELOW = 24;

/** How often the counts are read again while the tab is open: each read counts the blocked records of the ledger in view. */
const REFRESH_MS = 30000;

/** How many records a deliver run plans again of those a release asked to be planned, as the run itself bounds it. */
const PLANNED_PER_RUN = 5000;

/** The placeholders a problem's pattern holds where each of its records says something of its own. */
const PLACEHOLDERS = new Map<string, string>([
  ["<value>", "a value the record read"],
  ["<n>", "a number"],
  ["<id>", "an id"],
  ["<osdu id>", "an OSDU record id"],
  ["<time>", "a moment"],
  ["<path>", "a file or folder"],
  ["<hash>", "a hash"],
  ["<query>", "a query string"],
]);

const PLACEHOLDER_SPLIT = /(<(?:value|n|id|osdu id|time|path|hash|query)>)/;

/** The table's own scrolling container, which the list sizes to the window. */
const tableContainer = (element: HTMLElement) => element.querySelector<HTMLElement>('[data-slot="table-container"]');

/**
 * A problem's pattern: the error its records share, with each part a record says in its own way drawn as a quiet chip
 * naming what goes there, so what the records have in common reads at a glance.
 */
function PatternText({ pattern, clamp = false }: { pattern: string; clamp?: boolean }) {
  return (
    <span className={cn("break-words", clamp && "line-clamp-2")} data-testid="problem-pattern">
      {pattern.split(PLACEHOLDER_SPLIT).map((part, index) => {
        const meaning = PLACEHOLDERS.get(part);
        return meaning === undefined
          ? <span key={index}>{part}</span>
          : (
            <span key={index} className="mx-px rounded border border-border bg-muted px-1 font-mono text-[11px] text-muted-foreground" title={`Here each record names ${meaning} of its own`}>
              {part.slice(1, -1)}
            </span>
          );
      })}
    </span>
  );
}

/**
 * Where a problem's records stand, in a few words: all held, all failed, or how many of each. A held record met a data
 * problem or a refusal that is not retried; a failed one ran out of retries. Each is led by its glyph in its tone.
 */
function ProblemStates({ problem }: { problem: DeliveryProblem }) {
  const held = <CirclePause className="size-3 shrink-0 text-warning" aria-hidden />;
  const failed = <CircleX className="size-3 shrink-0 text-destructive" aria-hidden />;
  const mixed = problem.held > 0 && problem.failed > 0;
  return (
    <span className="inline-flex items-center gap-1 whitespace-nowrap text-[11px] text-muted-foreground" data-testid="problem-states">
      {problem.held > 0 && held}
      {problem.held > 0 && (mixed ? <span><span className="font-mono tabular-nums">{problem.held.toLocaleString()}</span> held</span> : "all held")}
      {mixed && <span aria-hidden="true">·</span>}
      {problem.failed > 0 && failed}
      {problem.failed > 0 && (mixed ? <span><span className="font-mono tabular-nums">{problem.failed.toLocaleString()}</span> failed</span> : "all failed")}
    </span>
  );
}

const problemColumns: Column<DeliveryProblem>[] = [
  {
    id: "pattern",
    header: "Problem",
    fill: true,
    floor: 260,
    render: (row) => (
      <span className="block whitespace-normal text-[13px]">
        <PatternText pattern={row.pattern} clamp />
      </span>
    ),
  },
  {
    id: "records",
    header: "Records",
    align: "right",
    render: (row) => (
      <span className="flex flex-col items-end gap-0.5">
        <span className="font-mono text-[13px] font-medium tabular-nums" data-testid="problem-records">{row.records.toLocaleString()}</span>
        <ProblemStates problem={row} />
      </span>
    ),
  },
  {
    id: "newest",
    header: "Last",
    render: (row) => <CompactTime value={row.newestUtc} caption="A record of this problem last changed" className="text-[12px]" />,
  },
];

/**
 * A flow's blocked records grouped by the problem that keeps them blocked (docs/ledger.md, Problems): a million held
 * records read as the handful of problems they share. A problem's example is checked once its cause is fixed (rendered as
 * it would be now, which sends nothing, or released and tried at once), and then every record of the problem is released
 * together, with a deliver run when asked. One interface's ledger in the title bar's partition, as the Records tab is.
 */
export function DeliveryProblemsPanel({ pipelineId, flowName }: { pipelineId: string; flowName: string }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [searchParams] = useSearchParams();
  const {
    names, many, interfaceName, partitions, partition, headerPartition, outside, unplaced, active, scope, ready, selectInterface,
  } = useInterfaceChoice(pipelineId, ["problem"]);
  // A link may name the problem to open on (the Records tab's filter carries one); a click picks another.
  const [chosen, setChosen] = useState<string | null>(searchParams.get("problem"));
  const [releaseOpen, setReleaseOpen] = useState(false);
  const list = useRef<HTMLDivElement>(null);
  const detailFrame = useRef<HTMLDivElement>(null);
  useWindowFit(list, BELOW, MIN_LIST_HEIGHT, tableContainer);
  useWindowFit(detailFrame, BELOW, MIN_LIST_HEIGHT);

  const listing = useQuery({
    queryKey: ["delivery", "problems", pipelineId, interfaceName, partition],
    queryFn: () => deliveryApi.problems(pipelineId, scope),
    enabled: ready,
    refetchInterval: REFRESH_MS,
  });
  const problems = listing.data?.problems;
  const selected = problems?.find((row) => row.problem === chosen) ?? problems?.[0] ?? null;
  const detail = useQuery({
    queryKey: ["delivery", "problem", pipelineId, interfaceName, partition, selected?.problem],
    queryFn: () => deliveryApi.problem(pipelineId, selected!.problem, scope),
    enabled: ready && selected !== null,
    refetchInterval: REFRESH_MS,
  });

  const refresh = () => void queryClient.invalidateQueries({ queryKey: ["delivery"] });
  const openRun = (runId: string) => ({ label: "Open run", onClick: () => navigate(`/runs/${runId}`) });

  // One record released and run at once: whether its problem is fixed shows on its timeline, and in this problem's count.
  const tryExample = useMutation({
    mutationFn: (record: DeliveryRecord) => deliveryApi.release(record, true),
    onSuccess: (result) => {
      refresh();
      toast.success(
        result.released === 0 ? "The example was no longer blocked; a run for it is queued." : "The example is released and a run for it is queued.",
        result.runId ? { action: openRun(result.runId) } : undefined);
    },
    onError: (error) => toast.error(problemText(error)),
  });

  const release = useMutation({
    mutationFn: ({ problem, run }: { problem: string; run: boolean }) => deliveryApi.releaseProblem(pipelineId, problem, run, scope),
    onSuccess: (result) => {
      setReleaseOpen(false);
      refresh();
      const released = `Released ${result.released.toLocaleString()} record${result.released === 1 ? "" : "s"}`;
      toast.success(result.runId ? `${released}; a deliver run is queued.` : `${released}; the flow's next run picks them up.`,
        result.runId ? { action: openRun(result.runId) } : undefined);
    },
    onError: (error) => {
      setReleaseOpen(false);
      toast.error(problemText(error));
    },
  });

  if (outside !== null || unplaced) {
    return (
      <div className="flex flex-col gap-4" data-testid="delivery-panel-problems">
        <OutsidePartition flowName={flowName} active={active} partitions={partitions} headerPartition={headerPartition} />
      </div>
    );
  }

  const data = listing.data;
  const ledgerName = ledgerLabel(flowName, scope);
  const recordsOf = (problem: string) => {
    const params = new URLSearchParams(searchParams);
    params.set("tab", "records");
    params.set("problem", problem);
    return `?${params.toString()}`;
  };

  return (
    <div className="flex flex-col gap-3" data-testid="delivery-panel-problems">
      {many && (
        <InterfacePicker names={names} interfaceName={interfaceName} onSelect={selectInterface} caption={`of ${names.length} interfaces of ${flowName}`} />
      )}

      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-[13px]" data-testid="problems-summary">
        {data === undefined ? <Skeleton className="h-5 w-72" /> : (
          <span>
            <span className="font-mono font-medium tabular-nums">{data.totalRecords.toLocaleString()}</span>
            {` blocked record${data.totalRecords === 1 ? "" : "s"} in `}
            <span className="font-mono font-medium tabular-nums">{data.totalProblems.toLocaleString()}</span>
            {` problem${data.totalProblems === 1 ? "" : "s"}`}
            {data.problems.length < data.totalProblems && (
              <span className="text-muted-foreground">{`, the ${data.problems.length.toLocaleString()} largest listed`}</span>
            )}
          </span>
        )}
        {data !== undefined && data.unsorted > 0 && (
          <RichTooltip
            title="Not sorted yet"
            body="Held or failed before the ledger kept problems. The control plane sorts them into theirs in the background, a page at a time; records blocked from now on are sorted as they are blocked. Releasing every blocked record from the Delivery tab reaches them too."
          >
            <span className="text-muted-foreground underline decoration-dotted underline-offset-2" data-testid="problems-unsorted">
              {`and ${data.unsorted.toLocaleString()} not sorted into a problem yet`}
            </span>
          </RichTooltip>
        )}
        <Button variant="ghost" size="sm" className="ml-auto h-7" onClick={refresh} disabled={listing.isFetching} data-testid="problems-refresh">
          {listing.isFetching ? <Loader2 className="animate-spin" /> : <RefreshCw />}
          Refresh
        </Button>
      </div>

      {listing.isError ? (
        isApiError(listing.error) ? <CorrelationError error={listing.error} /> : <p className="text-[13px] text-destructive">{String(listing.error)}</p>
      ) : data !== undefined && data.totalProblems === 0 ? (
        <Card className="rounded-lg p-0">
          <EmptyState
            icon={<CircleCheck />}
            title={data.unsorted > 0 ? "No problem sorted yet" : "Nothing is blocked"}
            description="A record is blocked when it is held or fails, and stays blocked until its source row changes or it is released."
            data-testid="problems-empty"
          />
        </Card>
      ) : (
        <div className="grid min-w-0 gap-3 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
          <div ref={list} className="min-w-0 [&_[data-slot=table-container]]:overflow-y-auto [&_th]:sticky [&_th]:top-0 [&_th]:z-10 [&_th]:bg-card">
            <DataTable
              columns={problemColumns}
              rows={problems}
              rowKey={(row) => row.problem}
              onRowClick={(row) => setChosen(row.problem)}
              rowSx={(row) => (row.problem === selected?.problem ? { backgroundColor: "var(--accent)" } : undefined)}
              emptyMessage="No problem keeps a record of this ledger blocked."
              data-testid="problems-table"
            />
          </div>
          <div ref={detailFrame} className="min-w-0 overflow-y-auto">
            {selected === null ? <Skeleton className="h-64 w-full rounded-lg" /> : (
              <ProblemDetail
                problem={selected}
                files={detail.data?.files}
                recordsLink={recordsOf(selected.problem)}
                trying={tryExample.isPending}
                onTry={(record) => tryExample.mutate(record)}
                onRelease={() => setReleaseOpen(true)}
              />
            )}
          </div>
        </div>
      )}

      {selected !== null && (
        <ReleaseProblemDialog
          open={releaseOpen}
          problem={selected}
          ledgerName={ledgerName}
          busy={release.isPending}
          onConfirm={(run) => release.mutate({ problem: selected.problem, run })}
          onClose={() => setReleaseOpen(false)}
        />
      )}
    </div>
  );
}

/**
 * One problem: what its records share, how many there are and when they last changed, its example record with that
 * record's own error, where its records came from, and what can be done: check the example, try it, see every record of
 * the problem, release them all.
 */
function ProblemDetail({ problem, files, recordsLink, trying, onTry, onRelease }: {
  problem: DeliveryProblem;
  files: { fileName: string | null; records: number }[] | undefined;
  recordsLink: string;
  trying: boolean;
  onTry: (record: DeliveryRecord) => void;
  onRelease: () => void;
}) {
  const example = problem.example;
  return (
    <Card className="gap-3 rounded-lg p-3" data-testid="problem-detail">
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-[13px] font-medium">Problem</span>
        <span className="ml-auto flex items-center gap-1.5">
          <Button asChild variant="outline" size="sm" className="h-7" data-testid="problem-records-link">
            <RouterLink to={recordsLink} title="Every record this problem keeps blocked, on the Records tab">
              <ListFilter />
              Records
            </RouterLink>
          </Button>
          <Button size="sm" className="h-7" onClick={onRelease} data-testid="problem-release">
            <Unlock />
            {`Release ${problem.records.toLocaleString()}`}
          </Button>
        </span>
      </div>

      <div className="text-[13px] leading-relaxed">
        <PatternText pattern={problem.pattern} />
      </div>

      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-[12px] text-muted-foreground" data-testid="problem-facts">
        <span className="inline-flex items-center gap-1.5">
          <span><span className="font-mono font-medium tabular-nums text-foreground">{problem.records.toLocaleString()}</span>{` record${problem.records === 1 ? "" : "s"}`}</span>
          <ProblemStates problem={problem} />
        </span>
        <span className="inline-flex items-center gap-1">
          last changed <CompactTime value={problem.oldestUtc} caption="The record that changed longest ago last changed" />
          to <CompactTime value={problem.newestUtc} caption="The most recently changed record last changed" />
        </span>
        <span className="inline-flex items-center gap-0.5 font-mono" title="The problem's id, which the Records tab and the records verb of the command line take" data-testid="problem-id">
          {problem.problem}
          <CopyButton iconOnly label="Copy the problem's id" text={problem.problem} testId="copy-problem-id" />
        </span>
      </div>

      {example !== null && (
        <div className="flex flex-col gap-2 border-t pt-3" data-testid="problem-example">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-[13px] font-medium">Example</span>
            <span className="ml-auto flex items-center gap-1.5">
              <Tooltip>
                <TooltipTrigger asChild>
                  <Button asChild variant="outline" size="sm" className="h-7" data-testid="problem-check-example">
                    <RouterLink to={`${deliveryRecordRoute(example)}?tab=render`}>
                      <ScanSearch />
                      Check
                    </RouterLink>
                  </Button>
                </TooltipTrigger>
                <TooltipContent className="max-w-xs">
                  Opens the example on its Render tab, which builds it from its current row with the mapping and the cache as they are now and sends nothing: whether it still holds says whether the cause is fixed.
                </TooltipContent>
              </Tooltip>
              <Tooltip>
                <TooltipTrigger asChild>
                  <Button variant="outline" size="sm" className="h-7" onClick={() => onTry(example)} disabled={trying} data-testid="problem-try-example">
                    {trying ? <Loader2 className="animate-spin" /> : <Play />}
                    Try
                  </Button>
                </TooltipTrigger>
                <TooltipContent className="max-w-xs">
                  Releases the example alone and queues a deliver run for it, so OSDU is asked again at once: the way to check a refusal the render cannot show. What it answers lands on the example's timeline.
                </TooltipContent>
              </Tooltip>
            </span>
          </div>
          <RouterLink to={deliveryRecordRoute(example)} className="rounded-md hover:bg-accent/40" data-testid="problem-example-record">
            <RecordIdentity
              label={example.label}
              sourceKey={example.sourceKey}
              origin={example.pendingSourceFileName !== null
                ? { fileName: example.pendingSourceFileName, rowNumber: example.pendingSourceRowNumber }
                : { fileName: example.sourceFileName, rowNumber: example.sourceRowNumber }}
            />
          </RouterLink>
          {/* The example's own error, where its values stand in for the pattern's placeholders. */}
          {example.lastError && example.lastError !== problem.pattern && (
            <div className="flex items-start gap-1 rounded-md bg-muted/50 px-2 py-1.5" data-testid="problem-example-error">
              <span className="min-w-0 flex-1 whitespace-pre-wrap break-words font-mono text-[11px]">{example.lastError}</span>
              <CopyButton iconOnly label="Copy the example's error" text={example.lastError} testId="copy-problem-example-error" />
            </div>
          )}
        </div>
      )}

      <div className="flex flex-col gap-1 border-t pt-3" data-testid="problem-files">
        <span className="text-[13px] font-medium">From</span>
        {files === undefined ? <Skeleton className="h-10 w-full" /> : files.length === 0 ? (
          <span className="text-[12px] text-muted-foreground">No file recorded.</span>
        ) : (
          <ul className="flex flex-col gap-0.5">
            {files.map((file) => (
              <li key={file.fileName ?? ""} className="flex min-w-0 items-baseline gap-2 text-[12px]">
                <span className="w-16 shrink-0 text-right font-mono tabular-nums">{file.records.toLocaleString()}</span>
                <span className={cn("min-w-0 break-all font-mono", file.fileName === null && "text-muted-foreground")}>{file.fileName ?? "no file recorded"}</span>
              </li>
            ))}
          </ul>
        )}
      </div>
    </Card>
  );
}

/**
 * The confirmation of a release of every record a problem keeps blocked, with the choice of a deliver run straight after.
 * The release reaches the records blocked when it is confirmed; while it runs the dialog cannot be dismissed.
 */
function ReleaseProblemDialog({ open, problem, ledgerName, busy, onConfirm, onClose }: {
  open: boolean;
  problem: DeliveryProblem;
  ledgerName: string;
  busy: boolean;
  onConfirm: (run: boolean) => void;
  onClose: () => void;
}) {
  const [run, setRun] = useState(true);
  const count = problem.records.toLocaleString();
  return (
    <AlertDialog
      open={open}
      onOpenChange={(next) => {
        if (!next && !busy) {
          onClose();
        }
      }}
    >
      <AlertDialogContent data-testid="problem-release-dialog" className="max-w-lg">
        <AlertDialogHeader>
          <AlertDialogTitle>{`Release ${count} record${problem.records === 1 ? "" : "s"}`}</AlertDialogTitle>
          <AlertDialogDescription>
            {`Every record of ${ledgerName} this problem keeps blocked goes back to delivery. A record that still holds its rendered document is sent as it is; the others are planned again from their rows, at most ${PLANNED_PER_RUN.toLocaleString()} a run. A record blocked by it after you confirm stays blocked.`}
          </AlertDialogDescription>
        </AlertDialogHeader>
        <div className="rounded-md bg-muted/50 px-2 py-1.5 text-[12px]">
          <PatternText pattern={problem.pattern} />
        </div>
        <Label className="flex items-center gap-2 text-[13px] font-normal">
          <Checkbox checked={run} onCheckedChange={(next) => setRun(next === true)} disabled={busy} data-testid="problem-release-run" />
          Queue a deliver run now
        </Label>
        <AlertDialogFooter>
          <Button variant="ghost" size="sm" onClick={onClose} disabled={busy} data-testid="problem-release-cancel">
            Cancel
          </Button>
          <Button size="sm" onClick={() => onConfirm(run)} disabled={busy} data-testid="problem-release-confirm">
            {busy && <Loader2 className="animate-spin" />}
            {`Release ${count}`}
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}

