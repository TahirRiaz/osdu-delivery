import { useState, type ReactNode } from "react";
import { Link as RouterLink } from "react-router-dom";
import { useMutation, useQuery } from "@tanstack/react-query";
import { Loader2, RefreshCcw, Send } from "lucide-react";
import { toast } from "sonner";
import {
  AlertDialog, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import { isApiError } from "@/api/client";
import { runApi } from "@/api/endpoints";
import type { RunDetail } from "@/api/types";
import { CorrelationError } from "@/components/CorrelationError";
import {
  deliveryApi, deliveryRecordRoute, type DeliveryFlowScope, type DeliveryManyRecords, type DeliveryRecordFilter,
} from "../../api/delivery";
import { shortId } from "./idTail";
import { planPreview, type PlanPreview } from "./runOutcome";

/**
 * Which records a redelivery acts on: the ones an operator ticked, the listing they were looking at with the count it
 * showed (what "select all 3,481 matching" means, refused if the set changed since), or every record the flow delivered.
 */
export type RedeliverSelection =
  | { kind: "keys"; keys: string[] }
  | { kind: "filter"; filter: DeliveryRecordFilter; expected: number }
  | { kind: "all"; delivered: number };

type Mode = "uptodate" | "again";

/** What a part of a record is called where an operator chooses what to send again; the older names are not offered beside the newer. */
const PART_LABELS: Record<string, { title: string; effect: string }> = {
  all: { title: "Everything", effect: "The record and whatever payload its route sends with it." },
  record: { title: "The record only", effect: "The document. Files and bulk data stay as OSDU holds them." },
  files: { title: "Files only", effect: "Uploaded and registered again; the record is rewritten with the new dataset ids." },
  bulk: { title: "Bulk data only", effect: "Written again as a new version of the record's bulk data." },
  workflow: { title: "The workflow run only", effect: "Triggered again, without sending anything else again." },
  payload: { title: "Every part of the payload", effect: "Each part the route sends beside the record." },
};

/** The parts offered: the route's own, without the older names (metadata for the record, payload where one part is all of it). */
function offeredParts(parts: readonly string[]): string[] {
  const own = parts.filter((part) => part !== "metadata" && part !== "payload");
  const payloadParts = own.filter((part) => part !== "all" && part !== "record");
  return payloadParts.length > 1 && parts.includes("payload") ? [...own, "payload"] : own;
}

function requestFor(selection: RedeliverSelection, run: boolean): DeliveryManyRecords {
  switch (selection.kind) {
    case "keys":
      return { keys: selection.keys, run };
    case "filter":
      return { filter: selection.filter, expected: selection.expected, run };
    case "all":
      return { run };
  }
}

function selectionLine(selection: RedeliverSelection, flowName: string, singleLabel: string | undefined): string {
  switch (selection.kind) {
    case "keys":
      return selection.keys.length === 1
        ? `One record${singleLabel ? ` (${singleLabel})` : ""} of ${flowName}.`
        : `${selection.keys.length.toLocaleString()} records of ${flowName}.`;
    case "filter":
      return `Every record of ${flowName} the current filter matches, ${selection.expected.toLocaleString()} now, resolved when you confirm.`;
    case "all":
      return `Every record ${flowName} has delivered, ${selection.delivered.toLocaleString()} now.`;
  }
}

const TERMINAL = new Set(["succeeded", "failed", "cancelled", "skipped"]);

const plural = (count: number, one: string, many = `${one}s`) => `${count.toLocaleString()} ${count === 1 ? one : many}`;

/**
 * The one redelivery surface, for a record, a selection, a filtered listing or a whole flow. Its two ways are side by side
 * because they make different promises. Bringing records up to date renders them again under the rules of now and sends
 * only what comes out different, which is what a change to a mapping, a cache or the engine asks for; the dialog first asks
 * a plan run what it would send and shows the answer. Sending again sends the chosen part of every record whatever its
 * hashes say, which OSDU keeps as a new version of each: for a record OSDU lost or someone changed there.
 */
export function RedeliverDialog({ open, onClose, pipelineId, flowId, flowScope, flowName, selection, singleLabel, onDone }: {
  open: boolean;
  onClose: () => void;
  pipelineId: string;
  /** The ledger the records are in, for links to the records the preview names. */
  flowId: string | null;
  flowScope?: DeliveryFlowScope;
  flowName: string;
  selection: RedeliverSelection;
  /** What the record is called in the dialog when there is exactly one. */
  singleLabel?: string;
  onDone: (outcome: { marked: number; runId: string | null; mode: Mode }) => void;
}) {
  const [mode, setMode] = useState<Mode>("uptodate");
  const [part, setPart] = useState("record");
  const [run, setRun] = useState(true);
  const [wasOpen, setWasOpen] = useState(open);

  // Every opening starts from bringing records up to date, the way that sends least, with a run queued.
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setMode("uptodate");
      setPart("record");
      setRun(true);
    }
  }

  const selectionKey = JSON.stringify(requestFor(selection, true));
  const parts = useQuery({
    queryKey: ["delivery", "redeliver-parts", pipelineId, flowScope?.interfaceName ?? null, flowScope?.partition ?? null],
    queryFn: () => deliveryApi.stats(pipelineId, flowScope),
    enabled: open,
    staleTime: 60_000,
  });
  const offered = offeredParts(parts.data?.redeliverParts ?? ["all", "record"]);

  const preview = useQuery({
    queryKey: ["delivery", "rerender-preview", pipelineId, flowScope?.interfaceName ?? null, flowScope?.partition ?? null, selectionKey],
    queryFn: () => deliveryApi.previewRerender(pipelineId, requestFor(selection, true), flowScope),
    enabled: open,
    staleTime: Infinity,
    gcTime: 0,
    retry: false,
  });
  const runId = preview.data?.runId ?? null;
  const planRun = useQuery({
    queryKey: ["runs", runId],
    queryFn: () => runApi.getById(runId!),
    enabled: open && runId !== null,
    refetchInterval: (query) => (query.state.data !== undefined && TERMINAL.has(query.state.data.status) ? false : 1000),
  });

  const act = useMutation({
    mutationFn: async () => {
      const request = requestFor(selection, run);
      if (mode === "uptodate") {
        const result = await deliveryApi.rerenderFlow(pipelineId, request, flowScope);
        return { marked: result.marked, runId: result.runId ?? null, mode };
      }

      const result = await deliveryApi.redeliverFlow(pipelineId, { ...request, scope: part }, flowScope);
      return { marked: result.marked, runId: result.runId ?? null, mode };
    },
    onSuccess: (outcome) => {
      onDone(outcome);
      onClose();
    },
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });

  const busy = act.isPending;
  const chosenPart = offered.includes(part) ? part : offered[0] ?? "all";

  return (
    <AlertDialog
      open={open}
      onOpenChange={(next) => {
        if (!next && !busy) {
          onClose();
        }
      }}
    >
      <AlertDialogContent data-testid="redeliver-dialog" className="max-h-[calc(100dvh-2rem)] gap-3 overflow-y-auto sm:max-w-2xl">
        <AlertDialogHeader>
          <AlertDialogTitle className="flex items-center gap-2">
            <RefreshCcw className="size-4" />
            Redeliver
          </AlertDialogTitle>
          <AlertDialogDescription data-testid="redeliver-selection">{selectionLine(selection, flowName, singleLabel)}</AlertDialogDescription>
        </AlertDialogHeader>

        <fieldset className="flex flex-col gap-2" disabled={busy}>
          <legend className="sr-only">How to redeliver</legend>
          <ModeButton
            active={mode === "uptodate"}
            onClick={() => setMode("uptodate")}
            title="Bring up to date"
            note="recommended"
            effect="Renders each record again with the mapping, template, cache and engine of now, and sends only what comes out different. A record that renders the same stays as OSDU holds it."
            testId="redeliver-mode-uptodate"
          >
            <PreviewPanel
              loading={preview.isPending}
              error={preview.error}
              accepted={preview.data}
              run={planRun.data}
              flowId={flowId}
            />
          </ModeButton>
          <ModeButton
            active={mode === "again"}
            onClick={() => setMode("again")}
            title="Send again"
            effect="Sends the chosen part of every record again, changed or not. OSDU keeps a new version of each: for records OSDU lost, or that someone changed there."
            testId="redeliver-mode-again"
          >
            {mode === "again" && (
              <div className="mt-1 flex w-full flex-col gap-1.5" role="radiogroup" aria-label="What to send again">
                {parts.isPending && <Skeleton className="h-10 w-full rounded-md" />}
                {!parts.isPending && offered.map((name) => {
                  const label = PART_LABELS[name] ?? { title: name, effect: "" };
                  const active = name === chosenPart;
                  return (
                    <button
                      key={name}
                      type="button"
                      role="radio"
                      aria-checked={active}
                      onClick={(event) => { event.stopPropagation(); setPart(name); }}
                      className={cn(
                        "flex w-full flex-col items-start rounded-md border px-2.5 py-1.5 text-left transition-colors",
                        active ? "border-primary bg-primary/5" : "border-border hover:bg-accent/50",
                      )}
                      data-testid={`redeliver-part-${name}`}
                    >
                      <span className="text-[13px] font-medium">{label.title}</span>
                      {label.effect !== "" && <span className="text-[12px] text-muted-foreground">{label.effect}</span>}
                    </button>
                  );
                })}
              </div>
            )}
          </ModeButton>
        </fieldset>

        <Label className="flex items-center gap-2 text-[13px] font-normal" title="The run plans the records a pass of 5,000 at a time and sends what each pass decides. Without it, the flow's next run does.">
          <Checkbox checked={run} onCheckedChange={(next) => setRun(next === true)} disabled={busy} data-testid="redeliver-run" />
          Queue a deliver run now
        </Label>

        <AlertDialogFooter>
          <Button variant="ghost" size="sm" onClick={onClose} disabled={busy} data-testid="redeliver-cancel">
            Cancel
          </Button>
          <Button
            size="sm"
            onClick={() => { setPart(chosenPart); act.mutate(); }}
            disabled={busy || (mode === "again" && parts.isPending)}
            data-testid="redeliver-confirm"
          >
            {busy ? <Loader2 className="animate-spin" /> : <Send />}
            {mode === "uptodate" ? "Bring up to date" : "Send again"}
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}

function ModeButton({ active, onClick, title, note, effect, testId, children }: {
  active: boolean;
  onClick: () => void;
  title: string;
  note?: string;
  effect: string;
  testId: string;
  children?: ReactNode;
}) {
  return (
    <div
      role="button"
      tabIndex={0}
      aria-pressed={active}
      onClick={onClick}
      onKeyDown={(event) => {
        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          onClick();
        }
      }}
      className={cn(
        "flex w-full cursor-pointer flex-col items-start gap-1 rounded-md border p-2.5 text-left transition-colors",
        active ? "border-primary bg-primary/5" : "border-border hover:bg-accent/50",
      )}
      data-testid={testId}
    >
      <span className="flex items-center gap-2">
        <span className="text-[13px] font-medium">{title}</span>
        {note !== undefined && <span className="text-[11px] text-muted-foreground">{note}</span>}
      </span>
      <span className="text-[13px] text-muted-foreground">{effect}</span>
      {children}
    </div>
  );
}

/** What the plan run found: how many of the checked records would be sent and which part, how many stay, and the first ones. */
function PreviewPanel({ loading, error, accepted, run, flowId }: {
  loading: boolean;
  error: unknown;
  accepted: { runId: string; checked: number; selected: number; selectedCapped: boolean } | undefined;
  run: RunDetail | undefined;
  flowId: string | null;
}) {
  if (error !== null && error !== undefined) {
    return (
      <div className="mt-1 w-full" data-testid="redeliver-preview-error">
        {isApiError(error) ? <CorrelationError error={error} /> : <p className="text-[13px] text-destructive">{String(error)}</p>}
      </div>
    );
  }

  if (loading || accepted === undefined) {
    return <Skeleton className="mt-1 h-12 w-full rounded-md" />;
  }

  const runLink = (
    <RouterLink to={`/runs/${accepted.runId}`} className="font-mono text-primary underline-offset-2 hover:underline" onClick={(event) => event.stopPropagation()}>
      {shortId(accepted.runId)}
    </RouterLink>
  );
  const of = accepted.checked < accepted.selected
    ? ` of ${accepted.selectedCapped ? "more than " : ""}${accepted.selected.toLocaleString()}`
    : "";

  if (run === undefined || !TERMINAL.has(run.status)) {
    return (
      <p className="mt-1 flex items-center gap-2 text-[13px] text-muted-foreground" data-testid="redeliver-preview-running">
        <Loader2 className="size-3.5 animate-spin" />
        Checking {plural(accepted.checked, "delivered record")}{of} without sending anything (plan run {runLink}).
      </p>
    );
  }

  const outcome: PlanPreview | null = run.status === "succeeded" ? planPreview(run) : null;
  if (outcome === null) {
    return (
      <p className="mt-1 text-[13px] text-destructive" data-testid="redeliver-preview-failed">
        The check did not finish{run.error ? `: ${run.error}` : "."} See plan run {runLink}. Bringing the records up to date still works; it only cannot say beforehand what it sends.
      </p>
    );
  }

  // One record says what of it changed; several say how many of them each part goes for.
  const parts = outcome.deliveries === 1
    ? [outcome.metadata > 0 && outcome.payload > 0 ? "with a new document and payload" : outcome.metadata > 0 ? "with a new document" : "with a new payload"]
    : [
      outcome.metadata > 0 && `${outcome.metadata.toLocaleString()} with a new document`,
      outcome.payload > 0 && `${outcome.payload.toLocaleString()} with a new payload`,
    ].filter((text): text is string => typeof text === "string");
  return (
    <div className="mt-1 flex w-full flex-col gap-1.5 text-[13px]" data-testid="redeliver-preview">
      <p>
        Checked {plural(accepted.checked, "delivered record")}{of} (plan run {runLink}):{" "}
        <span className="font-medium" data-testid="redeliver-preview-sends">
          {outcome.deliveries === 0 ? "none renders differently, so nothing would be sent" : `${outcome.deliveries.toLocaleString()} would be sent`}
        </span>
        {outcome.deliveries > 0 && parts.length > 0 && ` (${parts.join(", ")})`}
        {outcome.deliveries > 0 && outcome.unchanged > 0 && `; ${outcome.unchanged.toLocaleString()} ${outcome.unchanged === 1 ? "renders the same and stays as it is" : "render the same and stay as they are"}`}
        {outcome.held + outcome.blocked > 0 && `; ${(outcome.held + outcome.blocked).toLocaleString()} would be held`}
        .
      </p>
      {accepted.checked < accepted.selected && (
        <p className="text-[12px] text-muted-foreground">
          The first {accepted.checked.toLocaleString()} stand for the rest: bringing them up to date renders every one and decides each on its own.
        </p>
      )}
      {outcome.sample.length > 0 && (
        <ul className="flex flex-col gap-0.5 text-[12px]" data-testid="redeliver-preview-sample">
          {outcome.sample.slice(0, 5).map((entry) => (
            <li key={entry.key} className="flex min-w-0 items-baseline gap-2">
              {flowId === null
                ? <span className="truncate">{entry.label ?? entry.sourceKey}</span>
                : (
                  <RouterLink
                    to={deliveryRecordRoute({ flowId, deliveryKey: entry.key })}
                    className="truncate underline-offset-2 hover:underline"
                    onClick={(event) => event.stopPropagation()}
                  >
                    {entry.label ?? entry.sourceKey}
                  </RouterLink>
                )}
              <span className="shrink-0 text-muted-foreground">{entry.reason}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
