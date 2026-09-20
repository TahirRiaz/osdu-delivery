import { useMemo, useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { CircleAlert, Loader2, RotateCcwSquare, Trash2 } from "lucide-react";
import { toast } from "sonner";
import {
  AlertDialog,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import { isApiError } from "@/api/client";
import {
  deliveryApi,
  type DeliveryRecordFilter,
  type DeliveryRemovalAccepted,
  type DeliveryRemovalRequest,
  type DeliveryTarget,
  type RemovalScope,
} from "../../api/delivery";
import { CorrelationError } from "@/components/CorrelationError";

/**
 * Which records the removal acts on: the exact ones an operator ticked, or the listing they were looking at with
 * the count it showed. The filter form is what "select all 3,481 matching" means; the count travels with it so the
 * API can refuse the removal if the set has changed since it was shown.
 */
export type RemovalSelection =
  | { kind: "keys"; keys: string[] }
  | { kind: "filter"; filter: DeliveryRecordFilter; expected: number };

interface RemovalDialogProps {
  open: boolean;
  onClose: () => void;
  pipelineId: string;
  /** The interface of the source whose records these are; null for a flow in the single form. */
  interfaceName?: string | null;
  flowName: string;
  selection: RemovalSelection;
  /** What the records are called in the dialog's title when there is exactly one of them. */
  singleLabel?: string;
  onQueued: (accepted: DeliveryRemovalAccepted) => void;
}

interface ScopeChoice {
  scope: RemovalScope;
  title: string;
  /** What actually disappears, in the operator's terms. */
  effect: string;
  /** What the ledger does about it here, which is not the same question as what OSDU does. */
  ledger: string;
  reversible: boolean;
  path: (target: DeliveryTarget) => string;
  method: (target: DeliveryTarget) => string;
  confirmLabel: string;
}

const SCOPES: ScopeChoice[] = [
  {
    scope: "record",
    title: "Remove the record",
    effect: "The record stops resolving in OSDU. Nothing is destroyed: every version stays on disk and OSDU can restore it.",
    ledger: "Marked deleted here and blocked from redelivery until its source changes or it is released.",
    reversible: true,
    path: (target) => target.recordPath,
    // The node says which call the record scope makes: a DDMS's own DELETE, or a POST to storage's :delete path or the
    // dataset service's reversible removal.
    method: (target) => target.recordMethod,
    confirmLabel: "Remove from OSDU",
  },
  {
    scope: "history",
    title: "Purge the history",
    effect: "Every earlier version is destroyed permanently. The latest version stays live and retrievable.",
    ledger: "The record stays delivered; only the purge is written to its history.",
    reversible: false,
    path: (target) => target.historyPath,
    method: () => "DELETE",
    confirmLabel: "Purge history",
  },
  {
    scope: "everything",
    title: "Purge everything",
    effect: "The record and every one of its versions are destroyed permanently. This cannot be undone in OSDU.",
    ledger: "Marked deleted here and blocked from redelivery until its source changes or it is released.",
    reversible: false,
    path: (target) => target.everythingPath,
    method: () => "DELETE",
    confirmLabel: "Purge everything",
  },
];

/** The word an operator types to confirm a permanent removal: the partition it is aimed at, or the flow. */
function confirmationWord(target: DeliveryTarget | undefined, flowName: string): string {
  return target?.dataPartition ?? flowName;
}

function requestFor(selection: RemovalSelection, scope: RemovalScope): DeliveryRemovalRequest {
  return selection.kind === "keys"
    ? { scope, keys: selection.keys }
    : { scope, filter: selection.filter, expected: selection.expected };
}

/**
 * The one removal surface: which records, from which OSDU, how much of each goes, and whether it can be undone.
 * The three scopes are genuinely different OSDU calls with different promises, so they are shown side by side with
 * the call each one makes rather than hidden behind a single "delete" that means one of them. The target block is
 * not decoration: an operator who is one tab away from another environment needs to see the endpoint and partition
 * they are about to act on, which is also why a permanent scope asks them to type the partition back.
 */
export function RemovalDialog({ open, onClose, pipelineId, interfaceName = null, flowName, selection, singleLabel, onQueued }: RemovalDialogProps) {
  const [scope, setScope] = useState<RemovalScope>("record");
  const [typed, setTyped] = useState("");
  const [wasOpen, setWasOpen] = useState(open);

  // Every opening starts from the reversible scope with an empty confirmation, so a purge is never one click away
  // from the last thing the operator did.
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setScope("record");
      setTyped("");
    }
  }

  const request = useMemo(() => requestFor(selection, scope), [selection, scope]);
  const preview = useQuery({
    queryKey: ["delivery", "removal-preview", pipelineId, interfaceName, JSON.stringify(requestFor(selection, "record"))],
    queryFn: () => deliveryApi.previewRemoval(pipelineId, requestFor(selection, "record"), interfaceName),
    enabled: open,
    staleTime: 0,
    gcTime: 0,
  });

  const remove = useMutation({
    mutationFn: () => deliveryApi.removeRecords(pipelineId, request, interfaceName),
    onSuccess: (accepted) => {
      onQueued(accepted);
      onClose();
    },
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });

  const choice = SCOPES.find((s) => s.scope === scope)!;
  const target = preview.data?.target;
  const word = confirmationWord(target, flowName);
  const needsTyping = !choice.reversible;
  const confirmed = !needsTyping || typed.trim() === word;
  const records = preview.data?.records ?? (selection.kind === "keys" ? selection.keys.length : selection.expected);
  // More than one removal takes: the API would refuse it, so the dialog says so and does not offer it.
  const capped = preview.data?.capped === true;
  const busy = remove.isPending;

  return (
    <AlertDialog
      open={open}
      onOpenChange={(next) => {
        if (!next && !busy) {
          onClose();
        }
      }}
    >
      {/* Three scopes, a target block and a typed confirmation outgrow a short window: the dialog scrolls rather than
          putting its buttons past the bottom of the viewport. */}
      <AlertDialogContent data-testid="removal-dialog" className="max-h-[calc(100dvh-2rem)] max-w-2xl gap-3 overflow-y-auto">
        <AlertDialogHeader>
          <AlertDialogTitle className="flex items-center gap-2">
            <Trash2 className="size-4 text-destructive" />
            Remove from OSDU
          </AlertDialogTitle>
          <AlertDialogDescription data-testid="removal-scope-line">
            {capped
              ? `More records than one removal takes: ${records.toLocaleString()}${selection.kind === "filter" ? "+" : ""} of ${flowName}.`
              : records === 1
                ? `One record${singleLabel ? ` (${singleLabel})` : ""} of ${flowName}.`
                : `${records.toLocaleString()} records of ${flowName}.`}
            {capped
              ? " Narrow the selection and remove the records in parts."
              : selection.kind === "filter" && " Every record the current filter matches, resolved when the removal runs."}
          </AlertDialogDescription>
        </AlertDialogHeader>

        {preview.isError && (isApiError(preview.error)
          ? <CorrelationError error={preview.error} />
          : <p className="text-[13px] text-destructive">{String(preview.error)}</p>)}

        {/* Where. An operator with several environments open needs this before anything else. */}
        {target === undefined
          ? <Skeleton className="h-16 w-full rounded-md" />
          : (
            <div className="rounded-md border border-border bg-muted/40 p-3" data-testid="removal-target">
              <div className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Target</div>
              <div className="mt-1 break-all font-mono text-[13px]" data-testid="removal-endpoint">{target.endpoint}</div>
              <div className="mt-1 flex flex-wrap items-center gap-1.5">
                {target.dataPartition !== null && (
                  <Badge variant="secondary" className="font-mono" data-testid="removal-partition">
                    {target.dataPartition}
                  </Badge>
                )}
                <Badge variant="outline">{target.protocol}</Badge>
                <Badge variant="outline">{target.authType}</Badge>
              </div>
              {target.ddms !== null && (
                <p className="mt-1.5 text-[12px] text-muted-foreground" data-testid="removal-ddms">{target.ddms}</p>
              )}
            </div>
          )}

        <fieldset className="flex flex-col gap-2" disabled={busy}>
          <legend className="mb-1 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
            What to remove
          </legend>
          {SCOPES.map((option) => {
            const active = option.scope === scope;

            // A scope whose call the flow cannot resolve, or whose DDMS refuses it, is not offered: the node would
            // refuse it anyway, and showing it as available invites an operator to ask for a removal that never happens.
            const path = target === undefined ? undefined : option.path(target);
            const unavailable = path !== undefined
              && (path.startsWith("(not configured") || path.startsWith("(not routable") || path.startsWith("(refused"));
            return (
              <button
                key={option.scope}
                type="button"
                disabled={unavailable}
                onClick={() => { setScope(option.scope); setTyped(""); }}
                aria-pressed={active}
                className={cn(
                  "flex w-full flex-col items-start gap-1 rounded-md border p-2.5 text-left transition-colors",
                  active ? "border-primary bg-primary/5" : "border-border hover:bg-accent/50",
                  !option.reversible && active && "border-destructive bg-destructive/5",
                  unavailable && "cursor-not-allowed opacity-60 hover:bg-transparent",
                )}
                data-testid={`removal-scope-${option.scope}`}
              >
                <span className="flex w-full items-center gap-2">
                  <span className="text-[13px] font-medium">{option.title}</span>
                  {option.reversible
                    ? (
                      <Badge variant="secondary" className="gap-1 bg-success/15 text-success">
                        <RotateCcwSquare className="size-3" />
                        reversible
                      </Badge>
                    )
                    : <Badge variant="secondary" className="bg-destructive/15 text-destructive">permanent</Badge>}
                </span>
                <span className="text-[13px] text-muted-foreground">{option.effect}</span>
                <span className="text-[12px] text-muted-foreground">Here: {option.ledger}</span>
                {target !== undefined && (
                  <span className="font-mono text-[11px] text-muted-foreground">
                    {option.method(target)} {path}
                  </span>
                )}
              </button>
            );
          })}
        </fieldset>

        {preview.data !== undefined && preview.data.neverDelivered > 0 && (
          <p className="flex items-start gap-2 text-[13px] text-warning" data-testid="removal-never-delivered">
            <CircleAlert className="mt-0.5 size-4 shrink-0" />
            {preview.data.neverDelivered.toLocaleString()} of these have never been delivered, so OSDU holds
            nothing for them. They are still asked for, and come back reported as already gone.
          </p>
        )}

        {needsTyping && (
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="removal-confirm" className="text-[13px] font-normal">
              This cannot be undone. Type <span className="font-mono font-medium">{word}</span> to confirm.
            </Label>
            <Input
              id="removal-confirm"
              value={typed}
              onChange={(event) => setTyped(event.target.value)}
              disabled={busy}
              autoComplete="off"
              spellCheck={false}
              className="font-mono"
              data-testid="removal-confirm-input"
            />
          </div>
        )}

        <AlertDialogFooter>
          <Button variant="ghost" size="sm" onClick={onClose} disabled={busy} data-testid="removal-cancel">
            Cancel
          </Button>
          <Button
            variant="destructive"
            size="sm"
            onClick={() => remove.mutate()}
            disabled={busy || !confirmed || records === 0 || capped || preview.isError}
            data-testid="removal-confirm"
          >
            {busy && <Loader2 className="animate-spin" />}
            {choice.confirmLabel}
            {records > 1 ? ` (${records.toLocaleString()})` : ""}
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
