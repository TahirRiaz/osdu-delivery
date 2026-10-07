import { useMemo, useState, type ReactNode } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { CircleAlert, Info, Loader2, RotateCcw, Trash2, TriangleAlert, Undo2, type LucideIcon } from "lucide-react";
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
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { isApiError } from "@/api/client";
import {
  deliveryApi,
  type DeliveryFlowScope,
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
  /**
   * The ledger whose records these are: the interface of a source (null for the single form) and the partition of a flow
   * that works in partitions (null for one whose partition is its header's).
   */
  flowScope?: DeliveryFlowScope;
  flowName: string;
  selection: RemovalSelection;
  /** What the records are called in the dialog's title when there is exactly one of them. */
  singleLabel?: string;
  onQueued: (accepted: DeliveryRemovalAccepted) => void;
}

interface ScopeChoice {
  scope: RemovalScope;
  title: string;
  /** What happens, in one line of the operator's terms. */
  effect: string;
  /** What the ledger does about it, which is not the same question as what OSDU does: shown on hover, with the call. */
  ledger: string;
  reversible: boolean;
  /** The call the scope makes against the target, as the flow resolves it. */
  call: (target: DeliveryTarget) => string;
  /** Why the flow cannot make the call, or null when it can. */
  refusal: (target: DeliveryTarget) => string | null;
  confirmLabel: string;
}

/**
 * A removal endpoint the flow cannot resolve, or that its DDMS refuses, reads as a bracketed reason. The bracketed
 * placeholders for a collection that is only known at run time are not refusals: the node resolves them.
 */
function refusalOf(path: string): string | null {
  return /^\((not configured|not routable|refused)/.test(path) ? path.replace(/^\(|\)$/g, "") : null;
}

const SCOPES: ScopeChoice[] = [
  {
    scope: "record",
    title: "Remove the record",
    effect: "Stops resolving. Every version is kept.",
    ledger: "Marked removed here. The next run that reads its row delivers it again.",
    reversible: true,
    // The node says which call the record scope makes: a DDMS's own DELETE, or a POST to storage's :delete path or the
    // dataset service's reversible removal.
    call: (target) => `${target.recordMethod} ${target.recordPath}`,
    refusal: (target) => refusalOf(target.recordPath),
    confirmLabel: "Remove",
  },
  {
    scope: "previous",
    title: "Restore the previous version",
    effect: "The version before the latest becomes current again.",
    ledger: "OSDU cannot delete only the latest version, so the previous one is written back as a new version and the "
      + "replaced one stays in the history. Marked reverted here, and blocked from redelivery until its source changes "
      + "or it is released. Asked again, it brings the replaced version back.",
    reversible: true,
    call: (target) => target.previousPath,
    refusal: (target) => target.previousRefusal ?? null,
    confirmLabel: "Restore previous version",
  },
  {
    scope: "history",
    title: "Purge earlier versions",
    effect: "Only the latest version is kept.",
    ledger: "The record stays delivered here; the purge is written to its history.",
    reversible: false,
    call: (target) => `DELETE ${target.historyPath}`,
    refusal: (target) => refusalOf(target.historyPath),
    confirmLabel: "Purge history",
  },
  {
    scope: "everything",
    title: "Purge everything",
    effect: "The record and every version are destroyed.",
    ledger: "Marked removed here. The next run that reads its row delivers it again, as a new record.",
    reversible: false,
    call: (target) => `DELETE ${target.everythingPath}`,
    refusal: (target) => refusalOf(target.everythingPath),
    confirmLabel: "Purge everything",
  },
];

const GROUPS: { reversible: boolean; label: string; icon: LucideIcon; tone: string }[] = [
  { reversible: true, label: "Reversible", icon: RotateCcw, tone: "text-success" },
  { reversible: false, label: "Permanent", icon: TriangleAlert, tone: "text-destructive" },
];

/** The word an operator types to confirm a permanent removal: the partition it is aimed at, or the flow. */
function confirmationWord(target: DeliveryTarget | undefined, flowName: string): string {
  return target?.dataPartition ?? flowName;
}

/** The scopes that take a record out of OSDU, after which the ledger may delete it too. */
function takesItOut(scope: RemovalScope): boolean {
  return scope === "record" || scope === "everything";
}

function requestFor(selection: RemovalSelection, scope: RemovalScope, purgeLedger = false): DeliveryRemovalRequest {
  const purge = purgeLedger && takesItOut(scope) ? { purgeLedger: true } : {};
  return selection.kind === "keys"
    ? { scope, keys: selection.keys, ...purge }
    : { scope, filter: selection.filter, expected: selection.expected, ...purge };
}

/** A hover panel of facts or an explanation, behind a small info mark beside what it explains. */
function InfoTip({ label, children, testId }: { label: string; children: ReactNode; testId?: string }) {
  return (
    <Tooltip delayDuration={200}>
      <TooltipTrigger asChild>
        <button
          type="button"
          aria-label={label}
          className="inline-flex size-6 shrink-0 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-foreground focus-visible:outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50"
          data-testid={testId}
        >
          <Info className="size-3.5" />
        </button>
      </TooltipTrigger>
      <TooltipContent variant="panel" side="right" align="start" className="max-w-sm">
        {children}
      </TooltipContent>
    </Tooltip>
  );
}

/** One fact of the target panel: a quiet label over its value. */
function TargetFact({ label, value, mono = false }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="flex flex-col">
      <span className="text-[11px] text-muted-foreground">{label}</span>
      <span className={cn("break-all", mono && "font-mono text-[11.5px]")}>{value}</span>
    </div>
  );
}

/**
 * The one removal surface: which records, how much of each goes, and whether it can be undone. The four scopes are
 * genuinely different OSDU calls with different promises, so each is named with one line of what it does, grouped by
 * whether it can be undone; the call it makes and what the ledger does about it are on hover, beside it. The partition
 * stays in sight, since an operator one tab away from another environment needs it before anything else, and a
 * permanent scope asks for it to be typed back; the endpoint and route are on hover beside it.
 */
export function RemovalDialog({ open, onClose, pipelineId, flowScope, flowName, selection, singleLabel, onQueued }: RemovalDialogProps) {
  const [scope, setScope] = useState<RemovalScope>("record");
  const [purgeLedger, setPurgeLedger] = useState(false);
  const [typed, setTyped] = useState("");
  const [wasOpen, setWasOpen] = useState(open);

  // Every opening starts from the reversible removal with an empty confirmation and the ledger left alone, so a purge is
  // never one click away from the last thing the operator did.
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setScope("record");
      setPurgeLedger(false);
      setTyped("");
    }
  }

  const purging = purgeLedger && takesItOut(scope);
  const request = useMemo(() => requestFor(selection, scope, purging), [selection, scope, purging]);
  const preview = useQuery({
    queryKey: ["delivery", "removal-preview", pipelineId, flowScope?.interfaceName ?? null, flowScope?.partition ?? null, JSON.stringify(requestFor(selection, "record"))],
    queryFn: () => deliveryApi.previewRemoval(pipelineId, requestFor(selection, "record"), flowScope),
    enabled: open,
    staleTime: 0,
    gcTime: 0,
  });

  const remove = useMutation({
    mutationFn: () => deliveryApi.removeRecords(pipelineId, request, flowScope),
    onSuccess: (accepted) => {
      onQueued(accepted);
      onClose();
    },
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });

  const choice = SCOPES.find((s) => s.scope === scope)!;
  const target = preview.data?.target;
  const word = confirmationWord(target, flowName);
  // Deleting from the ledger cannot be undone either: the record's history goes, whichever removal takes it out of OSDU.
  const needsTyping = !choice.reversible || purging;
  const confirmed = !needsTyping || typed.trim() === word;
  const records = preview.data?.records ?? (selection.kind === "keys" ? selection.keys.length : selection.expected);
  // More than one removal takes: the API would refuse it, so the dialog says so and does not offer it.
  const capped = preview.data?.capped === true;
  const refused = target === undefined ? null : choice.refusal(target);
  const busy = remove.isPending;

  const subject = capped
    ? `More than one removal takes: ${records.toLocaleString()}${selection.kind === "filter" ? "+" : ""} records. Narrow the selection and remove them in parts.`
    : records === 1
      ? singleLabel ?? "One record"
      : selection.kind === "filter"
        ? `${records.toLocaleString()} records matching the filter`
        : `${records.toLocaleString()} records`;

  return (
    <AlertDialog
      open={open}
      onOpenChange={(next) => {
        if (!next && !busy) {
          onClose();
        }
      }}
    >
      {/* Four scopes and a typed confirmation can outgrow a short window: the dialog scrolls rather than putting its
          buttons past the bottom of the viewport. */}
      <AlertDialogContent data-testid="removal-dialog" className="max-h-[calc(100dvh-2rem)] max-w-lg gap-4 overflow-y-auto">
        <AlertDialogHeader className="gap-1 text-left">
          <div className="flex items-center gap-2">
            <AlertDialogTitle className="flex min-w-0 flex-1 items-center gap-2">
              <Trash2 className="size-4 shrink-0 text-destructive" />
              Remove from OSDU
            </AlertDialogTitle>
            {target === undefined
              ? !preview.isError && <Skeleton className="h-5 w-16 rounded-md" />
              : (
                <span className="flex items-center gap-0.5" data-testid="removal-target">
                  {target.dataPartition !== null && (
                    <Badge variant="secondary" className="font-mono" data-testid="removal-partition">
                      {target.dataPartition}
                    </Badge>
                  )}
                  <InfoTip label="Where the records live" testId="removal-target-info">
                    <div className="flex flex-col gap-1.5">
                      <TargetFact label="Flow" value={flowName} />
                      <TargetFact label="Endpoint" value={target.endpoint} mono />
                      <TargetFact label="Route" value={`${target.protocol} · ${target.authType}`} />
                      {target.ddms !== null && <span className="text-muted-foreground" data-testid="removal-ddms">{target.ddms}</span>}
                    </div>
                  </InfoTip>
                </span>
              )}
          </div>
          <AlertDialogDescription className="break-words" data-testid="removal-scope-line">
            {subject}
          </AlertDialogDescription>
        </AlertDialogHeader>

        {preview.isError && (isApiError(preview.error)
          ? <CorrelationError error={preview.error} />
          : <p className="text-[13px] text-destructive">{String(preview.error)}</p>)}

        <RadioGroup
          value={scope}
          onValueChange={(next) => {
            setScope(next as RemovalScope);
            setTyped("");
          }}
          disabled={busy}
          aria-label="What to remove"
          className="gap-3"
        >
          {GROUPS.map((group) => (
            <div key={group.label} className="flex flex-col gap-1.5" role="group" aria-label={group.label}>
              <div className="flex items-center gap-1.5 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
                <group.icon className={cn("size-3", group.tone)} aria-hidden />
                {group.label}
              </div>
              {SCOPES.filter((option) => option.reversible === group.reversible).map((option) => {
                const active = option.scope === scope;
                // A scope whose call the flow cannot resolve, or whose route refuses it, is not offered: the node would
                // refuse it anyway, and offering it invites an operator to ask for a removal that never happens.
                const unavailable = target === undefined ? null : option.refusal(target);
                const id = `removal-scope-${option.scope}`;
                return (
                  <div
                    key={option.scope}
                    className={cn(
                      "flex items-start gap-1 rounded-md border pl-3 pr-1 transition-colors",
                      active
                        ? option.reversible ? "border-primary bg-primary/5" : "border-destructive/70 bg-destructive/5"
                        : "border-border hover:bg-accent/40",
                      unavailable !== null && "opacity-60 hover:bg-transparent",
                    )}
                  >
                    <label
                      htmlFor={id}
                      className={cn("flex min-w-0 flex-1 items-start gap-2.5 py-2", unavailable === null ? "cursor-pointer" : "cursor-not-allowed")}
                    >
                      <RadioGroupItem
                        id={id}
                        value={option.scope}
                        disabled={unavailable !== null}
                        className={cn("mt-0.5", !option.reversible && "text-destructive [&_svg]:fill-destructive")}
                        data-testid={id}
                      />
                      <span className="flex min-w-0 flex-col gap-0.5">
                        <span className="text-[13px] font-medium leading-tight">{option.title}</span>
                        <span className="text-[12px] leading-snug text-muted-foreground">
                          {unavailable !== null ? "Not available for this flow." : option.effect}
                        </span>
                      </span>
                    </label>
                    {target !== undefined && (
                      <span className="pt-1.5">
                        <InfoTip label={`About ${option.title.toLowerCase()}`} testId={`removal-scope-${option.scope}-info`}>
                          {unavailable !== null
                            ? <span>{unavailable}</span>
                            : (
                              <div className="flex flex-col gap-1.5">
                                <span>{option.ledger}</span>
                                <span className="break-all font-mono text-[11px] text-muted-foreground">{option.call(target)}</span>
                              </div>
                            )}
                        </InfoTip>
                      </span>
                    )}
                  </div>
                );
              })}
            </div>
          ))}
        </RadioGroup>

        {takesItOut(scope) && (
          <div className="flex items-start gap-1 pr-1">
            <label htmlFor="removal-purge-ledger" className="flex min-w-0 flex-1 cursor-pointer items-start gap-2.5 pl-3">
              <Checkbox
                id="removal-purge-ledger"
                checked={purgeLedger}
                onCheckedChange={(checked) => {
                  setPurgeLedger(checked === true);
                  setTyped("");
                }}
                disabled={busy}
                className="mt-0.5"
                data-testid="removal-purge-ledger"
              />
              <span className="flex min-w-0 flex-col gap-0.5">
                <span className="text-[13px] font-medium leading-tight">Also delete from the ledger</span>
                <span className="text-[12px] leading-snug text-muted-foreground">Its history here goes once OSDU confirms the removal.</span>
              </span>
            </label>
            {target !== undefined && (
              <InfoTip label="About deleting from the ledger" testId="removal-purge-ledger-info">
                <div className="flex flex-col gap-1.5">
                  <span>
                    Each record OSDU confirms removed is deleted from the ledger: its attempts and search entries go. One line is kept
                    of it: what it was, its OSDU id and last version, who deleted it and when.
                  </span>
                  <span>
                    A record whose removal failed stays as it was. If its row is still in the source, the next run that reads it
                    delivers it as a new record.
                  </span>
                </div>
              </InfoTip>
            )}
          </div>
        )}

        {preview.data !== undefined && preview.data.neverDelivered > 0 && (
          <p className="flex items-start gap-2 text-[12px] text-muted-foreground" data-testid="removal-never-delivered">
            <CircleAlert className="mt-0.5 size-3.5 shrink-0 text-warning" />
            {`${preview.data.neverDelivered.toLocaleString()} of these were never delivered: they are asked for, and come back as already gone.`}
          </p>
        )}

        {needsTyping && (
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="removal-confirm" className="text-[12px] font-normal text-muted-foreground">
              <span>
                This cannot be undone. Type <span className="font-mono font-medium text-foreground">{word}</span> to confirm.
              </span>
            </Label>
            <Input
              id="removal-confirm"
              value={typed}
              onChange={(event) => setTyped(event.target.value)}
              disabled={busy}
              autoComplete="off"
              spellCheck={false}
              className="h-8 font-mono"
              data-testid="removal-confirm-input"
            />
          </div>
        )}

        <AlertDialogFooter>
          <Button variant="ghost" size="sm" onClick={onClose} disabled={busy} data-testid="removal-cancel">
            Cancel
          </Button>
          <Button
            variant={scope === "previous" ? "default" : "destructive"}
            size="sm"
            onClick={() => remove.mutate()}
            disabled={busy || !confirmed || records === 0 || capped || preview.isError || refused !== null}
            data-testid="removal-confirm"
          >
            {busy ? <Loader2 className="animate-spin" /> : scope === "previous" ? <Undo2 /> : null}
            {purging ? `${choice.confirmLabel} and delete from ledger` : choice.confirmLabel}
            {records > 1 ? ` (${records.toLocaleString()})` : ""}
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
