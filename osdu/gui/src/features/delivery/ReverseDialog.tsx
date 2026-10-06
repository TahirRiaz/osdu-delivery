import { useMutation, useQuery } from "@tanstack/react-query";
import { CircleAlert, Info, Loader2, Undo2 } from "lucide-react";
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
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "@/api/client";
import {
  deliveryApi,
  type DeliveryFlowScope,
  type DeliveryReversalAccepted,
  type DeliveryReversalRequest,
  type ReversalSource,
} from "../../api/delivery";
import { CorrelationError } from "@/components/CorrelationError";
import { shortId } from "./idTail";
import { ReversalOutcomeCount } from "./ReversalParts";
import { REVERSAL_OUTCOME_ORDER } from "./reversalOutcomes";

interface ReverseDialogProps {
  open: boolean;
  onClose: () => void;
  pipelineId: string;
  /** The ledger the source belongs to: the interface of a source (null for the single form) and the partition. */
  flowScope?: DeliveryFlowScope;
  flowName: string;
  source: ReversalSource;
  onQueued: (accepted: DeliveryReversalAccepted) => void;
}

function requestOf(source: ReversalSource, expected?: number): DeliveryReversalRequest {
  return source.kind === "run"
    ? { runId: source.id, expected }
    : { submissionId: source.id, expected };
}

/**
 * The one reversal surface (docs/reversal-plan.md): what reversing a run or a submission reaches, in which OSDU, what it would
 * do to each record (decided as the run decides, on the source's records or a sample of them), what the route calls, and
 * what the records are left as. Confirmed, the reverse run is queued with the count shown, so a source that moved since is
 * refused rather than reversed further than the operator saw.
 */
export function ReverseDialog({ open, onClose, pipelineId, flowScope, flowName, source, onQueued }: ReverseDialogProps) {
  const preview = useQuery({
    queryKey: ["delivery", "reversal-preview", pipelineId, flowScope?.interfaceName ?? null, flowScope?.partition ?? null, source.kind, source.id],
    queryFn: () => deliveryApi.previewReversal(pipelineId, requestOf(source), flowScope),
    enabled: open,
    staleTime: 0,
    gcTime: 0,
  });

  const reverse = useMutation({
    mutationFn: () => deliveryApi.reverse(pipelineId, requestOf(source, preview.data?.records), flowScope),
    onSuccess: (accepted) => {
      onQueued(accepted);
      onClose();
    },
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });

  const data = preview.data;
  const target = data?.target;
  const busy = reverse.isPending;
  const passed = data === undefined
    ? []
    : REVERSAL_OUTCOME_ORDER.filter((outcome) => (data.passedOver[outcome] ?? 0) > 0).map((outcome) => [outcome, data.passedOver[outcome]] as const);
  const acts = data !== undefined && data.restore + data.remove + data.resolvedFromOsdu > 0;
  const existing = data?.existing ?? null;
  const resumes = existing !== null && existing !== undefined;
  const what = source.kind === "run" ? "run" : "submission";

  return (
    <AlertDialog
      open={open}
      onOpenChange={(next) => {
        if (!next && !busy) {
          onClose();
        }
      }}
    >
      <AlertDialogContent data-testid="reverse-dialog" className="max-h-[calc(100dvh-2rem)] max-w-2xl gap-3 overflow-y-auto">
        <AlertDialogHeader>
          <AlertDialogTitle className="flex items-center gap-2">
            <Undo2 className="size-4" />
            {`Reverse ${what} ${shortId(source.id)}`}
          </AlertDialogTitle>
          <AlertDialogDescription data-testid="reverse-scope-line">
            {data === undefined
              ? `Puts OSDU back as it was before this ${what} of ${flowName}.`
              : source.kind === "run"
                ? `${data.records.toLocaleString()} record(s) of ${flowName} delivered under ${data.submissions.toLocaleString()} submission(s).`
                : `${data.records.toLocaleString()} record(s) of ${flowName} delivered by this submission.`}
            {" "}
            What it created is removed again, reversibly; what it updated gets back the version OSDU held before, as a new version.
            Nothing is purged.
          </AlertDialogDescription>
        </AlertDialogHeader>

        {preview.isError && (isApiError(preview.error)
          ? <CorrelationError error={preview.error} />
          : <p className="text-[13px] text-destructive">{String(preview.error)}</p>)}

        {target === undefined
          ? (!preview.isError && <Skeleton className="h-16 w-full rounded-md" />)
          : (
            <div className="rounded-md border border-border bg-muted/40 p-3" data-testid="reverse-target">
              <div className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Target</div>
              <div className="mt-1 break-all font-mono text-[13px]" data-testid="reverse-endpoint">{target.endpoint}</div>
              <div className="mt-1 flex flex-wrap items-center gap-1.5">
                {target.dataPartition !== null && (
                  <Badge variant="secondary" className="font-mono" data-testid="reverse-partition">{target.dataPartition}</Badge>
                )}
                <Badge variant="outline">{target.protocol}</Badge>
                <Badge variant="outline">{target.authType}</Badge>
              </div>
            </div>
          )}

        {data !== undefined && (
          <div className="flex flex-col gap-2 rounded-md border border-border p-3" data-testid="reverse-plan">
            <div className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
              {data.sampleIsAll ? "What it would do" : `What it would do, by the first ${data.sampled.toLocaleString()} records in key order`}
            </div>
            <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
              <ReversalOutcomeCount outcome="restored" count={data.restore} testId="reverse-plan-restore" />
              <ReversalOutcomeCount outcome="removed" count={data.remove} testId="reverse-plan-remove" />
              {data.resolvedFromOsdu > 0 && (
                <span className="text-[12px] text-muted-foreground" data-testid="reverse-plan-resolve">
                  {`${data.resolvedFromOsdu.toLocaleString()} decided by OSDU's version list (the ledger no longer says what it held before)`}
                </span>
              )}
            </div>
            {passed.length > 0 && (
              <div className="flex flex-wrap items-center gap-x-3 gap-y-1" data-testid="reverse-plan-passed">
                <span className="text-[12px] text-muted-foreground">Passed over:</span>
                {passed.map(([outcome, count]) => (
                  <ReversalOutcomeCount key={outcome} outcome={outcome} count={count} testId={`reverse-plan-${outcome}`} />
                ))}
              </div>
            )}
            <div className="mt-1 flex flex-col gap-0.5 font-mono text-[11px] text-muted-foreground" data-testid="reverse-route">
              <span>{`restore: ${data.route.restore}`}</span>
              <span>{`remove: ${data.route.remove}`}</span>
            </div>
          </div>
        )}

        {data !== undefined && !data.route.restores && (
          <p className="flex items-start gap-2 text-[13px] text-warning" data-testid="reverse-no-restore">
            <CircleAlert className="mt-0.5 size-4 shrink-0" />
            {`This route cannot write an earlier version back, so a record the ${what} updated is passed over as not reversible.`}
          </p>
        )}

        {resumes && (
          <p className="flex items-start gap-2 text-[13px] text-muted-foreground" data-testid="reverse-existing">
            <Info className="mt-0.5 size-4 shrink-0" />
            {`Reversal ${existing.reversalId} of this ${what} is ${existing.status}. Asking again resumes it: the records not done yet, those that failed and those passed over as busy are taken again.`}
          </p>
        )}

        <p className="text-[12px] text-muted-foreground">
          Every record put back is blocked: the next runs pass it over until its source changes or you release it. Each one keeps
          an attempt saying what was done and why, and the reverse run is on the audit trail under your name.
        </p>

        <AlertDialogFooter>
          <Button variant="ghost" size="sm" onClick={onClose} disabled={busy} data-testid="reverse-cancel">
            Cancel
          </Button>
          <Button
            size="sm"
            onClick={() => reverse.mutate()}
            disabled={busy || data === undefined || preview.isError || data.records === 0 || (!acts && !resumes)}
            data-testid="reverse-confirm"
          >
            {busy ? <Loader2 className="animate-spin" /> : <Undo2 />}
            {resumes ? "Resume the reversal" : `Reverse ${data?.records.toLocaleString() ?? ""} record(s)`}
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
