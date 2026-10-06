import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { DatabaseZap, ListTree, Undo2 } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import type { RunDetail } from "@/api/types";
import { DetailPair } from "@/components/DetailPair";
import { IdChip } from "@/components/IdChip";
import { ledgerLabel } from "../../api/delivery";
import { DELIVERING_OPERATIONS, runRecordCounts, runResult, runScope, runSubmissionId } from "./runOutcome";
import { ReverseDialog } from "./ReverseDialog";
import { shortId } from "./idTail";

/** A record counter: grouped for readability, a real zero kept distinct from an unreported value (muted dash). */
function RecordCount({ value, testId }: { value: number | null; testId: string }) {
  if (value === null) {
    return <span className="text-muted-foreground" data-testid={testId}>-</span>;
  }

  return <span className="font-mono tabular-nums" data-testid={testId}>{value.toLocaleString("en-US")}</span>;
}

/**
 * The delivery run's way to the records it touched (the flow's Records tab, filtered to this run), and, for a run that sent
 * records once it has ended, its reversal: what it put into OSDU put back as OSDU held it before (docs/reversal-plan.md).
 */
export function DeliveryRunActions({ run }: { run: RunDetail }) {
  const navigate = useNavigate();
  const [reverseOpen, setReverseOpen] = useState(false);
  const ended = run.status === "succeeded" || run.status === "failed" || run.status === "cancelled";
  const reversible = DELIVERING_OPERATIONS.includes(run.operation ?? "deliver") && run.pipelineId !== null;
  const scope = runScope(run);
  return (
    <>
      <Button
        variant="outline"
        size="sm"
        onClick={() => navigate(`/pipelines/${run.pipelineId}?tab=records&run=${run.runId}`)}
        data-testid="run-records"
      >
        <ListTree />
        Records of this run
      </Button>
      {reversible && (
        <>
          <Button
            variant="destructive-outline"
            size="sm"
            onClick={() => setReverseOpen(true)}
            disabled={!ended}
            title={ended ? "Put OSDU back as it was before this run." : "A run is reversed once it has ended."}
            data-testid="run-reverse"
          >
            <Undo2 />
            Reverse this run
          </Button>
          <ReverseDialog
            open={reverseOpen}
            onClose={() => setReverseOpen(false)}
            pipelineId={run.pipelineId!}
            flowScope={scope}
            flowName={ledgerLabel(run.flowName, scope)}
            source={{ kind: "run", id: run.runId }}
            onQueued={(accepted) => {
              toast.success(`Reversal of ${accepted.records.toLocaleString()} record(s) this run delivered queued.`, {
                action: { label: "Open run", onClick: () => navigate(`/runs/${accepted.runId}`) },
              });
            }}
          />
        </>
      )}
    </>
  );
}

/** The cache run's way to the cache it refreshed. */
export function CacheRunActions({ run }: { run: RunDetail }) {
  const navigate = useNavigate();
  return (
    <Button
      variant="outline"
      size="sm"
      onClick={() => navigate(`/delivery/cache?flow=${encodeURIComponent(run.flowName)}`)}
      data-testid="run-cache"
    >
      <DatabaseZap />
      Open the cache
    </Button>
  );
}

/** The submission a delivery run worked on, as a chip that opens it. */
export function DeliveryRunMeta({ run }: { run: RunDetail }) {
  const submissionId = runSubmissionId(run, runResult(run));
  return submissionId === null
    ? null
    : (
      <IdChip
        label="submission"
        value={submissionId}
        display={shortId(submissionId)}
        to={`/delivery/submissions/${submissionId}`}
        testId="run-submission"
        copyTestId="copy-run-submission"
      />
    );
}

/** The record counts a delivery run reported, in place of the row counts SQLFlow's own runs carry. */
export function DeliveryRunCounts({ run }: { run: RunDetail }) {
  const counts = runRecordCounts(runResult(run));
  return (
    <>
      <DetailPair label="Planned"><RecordCount value={counts.planned} testId="run-records-planned" /></DetailPair>
      <DetailPair label="Delivered"><RecordCount value={counts.delivered} testId="run-records-delivered" /></DetailPair>
      <DetailPair label="Held"><RecordCount value={counts.held} testId="run-records-held" /></DetailPair>
      <DetailPair label="Failed"><RecordCount value={counts.failed} testId="run-records-failed" /></DetailPair>
      <DetailPair label="Unchanged"><RecordCount value={counts.unchanged} testId="run-records-unchanged" /></DetailPair>
      <DetailPair label="Waiting"><RecordCount value={counts.waiting} testId="run-records-waiting" /></DetailPair>
    </>
  );
}
