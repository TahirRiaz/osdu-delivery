import { useCallback, useEffect, useRef, useState } from "react";
import { Navigate, useNavigate, useParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { CircleAlert, Info, RotateCcw, Terminal } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "../../api/client";
import { pipelineApi, runApi } from "../../api/endpoints";
import type { RunParameters } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { prettyJson } from "../delivery/DeliveryFlowPanel";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { DetailHeaderCard } from "../../components/DetailHeaderCard";
import { DetailPair } from "../../components/DetailPair";
import { IdChip } from "../../components/IdChip";
import { Page } from "../../components/Page";
import { RelativeTime } from "../../components/RelativeTime";
import { RunStatusBadge } from "../../components/StatusBadge";
import { pollingInterval } from "../../hooks/usePolling";
import { usePanel } from "../../layout/workbench/PanelContext";
import { useTabTitle } from "../../layout/workbench/TabsContext";
import { formatDurationSeconds } from "../../lib/time";
import { RunTracePanel } from "./RunTracePanel";
import { TriggerRunDialog } from "./TriggerRunDialog";

/** The stored run parameters, or null when the run carried the defaults (or the stored form is unreadable). */
function parseRunParameters(json: string | null): RunParameters | null {
  if (json === null) {
    return null;
  }

  try {
    return JSON.parse(json) as RunParameters;
  } catch {
    return null;
  }
}

/** A record counter: grouped for readability, a real zero kept distinct from an unrecorded value (muted dash). */
function RowCount({ value }: { value: number | null }) {
  if (value == null) {
    return <span className="text-muted-foreground">-</span>;
  }

  return <span className="font-mono tabular-nums">{value.toLocaleString("en-US")}</span>;
}

/** Everything the API recorded about one run: a live header while active, the parameters it ran with, the flow
 * source it executed, and the trace in the workbench bottom panel. */
export default function RunDetailPage() {
  const { runId } = useParams<{ runId: string }>();
  if (!runId) {
    return <Navigate to="/runs" replace />;
  }

  return <RunDetailContent runId={runId} />;
}

function RunDetailContent({ runId }: { runId: string }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const panel = usePanel();
  const [confirmOpen, setConfirmOpen] = useState(false);

  const query = useQuery({
    queryKey: ["runs", runId],
    queryFn: () => runApi.getById(runId),
    refetchInterval: (q) => {
      const data = q.state.data;
      if (data && (data.status === "succeeded" || data.status === "failed" || data.status === "cancelled")) {
        return false;
      }

      return pollingInterval(3000)();
    },
  });
  useTabTitle(query.data?.flowName);

  // The pipeline's stored definition backs the Source section: the flow YAML as the catalog holds it now. Cached
  // under the same key the pipeline detail page uses.
  const pipelineQuery = useQuery({
    queryKey: ["pipelines", "detail", query.data?.pipelineId ?? ""],
    queryFn: () => pipelineApi.getById(query.data!.pipelineId),
    enabled: query.data !== undefined,
  });

  // Once the run reaches a terminal status, refetch it so the header shows the authoritative re-projected result
  // without a manual reload (the header poll observes the transition).
  const status = query.data?.status;
  const live = status === "queued" || status === "running";
  const refreshRun = useCallback(
    () => void queryClient.invalidateQueries({ queryKey: ["runs", runId] }),
    [queryClient, runId],
  );
  const wasLive = useRef(false);
  useEffect(() => {
    if (live) {
      wasLive.current = true;
    } else if (wasLive.current) {
      wasLive.current = false;
      refreshRun();
    }
  }, [live, refreshRun]);

  // The trace lives in the workbench bottom panel (the "trace window"), not an embedded tab: opening a run raises
  // it there and it streams live while the run executes, exactly like the repository sync trace. Opened once per
  // run, when the flow name is known so the panel is titled; the header's Trace button reopens it if closed.
  const flowName = query.data?.flowName;
  const openTrace = useCallback(() => {
    panel.open({
      id: `run-trace:${runId}`,
      title: `Trace · ${flowName ?? runId}`,
      node: <RunTracePanel runId={runId} />,
    });
  }, [panel, runId, flowName]);
  const openedFor = useRef<string | null>(null);
  useEffect(() => {
    if (openedFor.current === runId || flowName === undefined) {
      return;
    }

    openedFor.current = runId;
    openTrace();
  }, [runId, flowName, openTrace]);

  // Re-run opens the trigger sheet prefilled with this run's flow and the parameters it carried, so the run can be
  // repeated as-is or adjusted before launching. The sheet navigates to the new run on submit.
  const [rerunOpen, setRerunOpen] = useState(false);

  const cancel = useMutation({
    mutationFn: () => runApi.cancel(runId),
    onSuccess: () => {
      toast.success("Cancel requested for this run.");
      setConfirmOpen(false);
      void queryClient.invalidateQueries({ queryKey: ["runs", runId] });
    },
    onError: (error) => {
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error));
      setConfirmOpen(false);
    },
  });

  if (query.isError) {
    return (
      <Page data-testid="page-run-detail">
        {isApiError(query.error)
          ? <CorrelationError error={query.error} />
          : <p className="text-[13px] text-destructive">{String(query.error)}</p>}
      </Page>
    );
  }

  const run = query.data;
  if (run === undefined) {
    return (
      <Page data-testid="page-run-detail">
        <Skeleton className="h-60 w-full rounded-lg" />
        <Skeleton className="h-80 w-full rounded-lg" />
      </Page>
    );
  }

  // A run can be cancelled while queued (dequeued outright) or while running (the node aborts the in-flight
  // work). Once a running run's cancel is in flight, the button is disabled and a "cancelling" badge shows,
  // until polling reflects the terminal "cancelled" status.
  const cancellable = run.status === "queued" || run.status === "running";
  const cancelling = run.status === "running" && run.cancelRequestedUtc !== null;
  const parameters = parseRunParameters(run.parametersJson);
  const hasParameters = run.operation !== "deliver" || run.force || parameters !== null;

  return (
    <Page data-testid="page-run-detail">
      {/* Why it failed comes first: on a failed run that is the only thing being looked for, and behind a full
          header card of timings and counts it was the last thing read. */}
      {run.status === "failed" && run.error !== null && (
        <Alert variant="destructive" data-testid="run-error">
          <CircleAlert />
          <AlertDescription>{run.error}</AlertDescription>
        </Alert>
      )}

      <DetailHeaderCard
        title={run.flowName}
        badges={(
          <>
            <RunStatusBadge status={run.status} />
            {cancelling && (
              <Badge variant="secondary" className="bg-warning/15 text-warning" data-testid="run-cancelling">
                cancelling
              </Badge>
            )}
            <Badge variant="outline" data-testid="run-kind">{run.flowKind}</Badge>
            <Badge variant="outline" data-testid="run-batch">batch: {run.batch}</Badge>
          </>
        )}
        actions={(
          <>
            <Button variant="outline" size="sm" onClick={openTrace} data-testid="open-run-trace">
              <Terminal />
              Trace
            </Button>
            {run.flowKind === "delivery" && (
              <Button variant="outline" size="sm" onClick={() => navigate(`/pipelines/${run.pipelineId}?tab=records&run=${run.runId}`)} data-testid="run-records">
                Records of this run
              </Button>
            )}
            {cancellable
              ? (
                <Button
                  variant="outline"
                  size="sm"
                  className="border-destructive/40 text-destructive hover:bg-destructive/10 hover:text-destructive"
                  onClick={() => setConfirmOpen(true)}
                  disabled={cancelling || cancel.isPending}
                  data-testid="cancel-run"
                >
                  {cancelling ? "Cancelling..." : "Cancel run"}
                </Button>
              )
              : (run.repoId !== null && (
                <Button variant="outline" size="sm" onClick={() => setRerunOpen(true)} data-testid="rerun-run">
                  <RotateCcw />
                  Re-run
                </Button>
              ))}
          </>
        )}
        meta={(
          <>
            <IdChip label="run" value={run.runId} testId="run-id" copyTestId="copy-run-id" />
            {run.repoId && (
              <IdChip label="repo" value={run.repoId} to={`/repos/${run.repoId}`} testId="run-repo-link" copyTestId="copy-run-repo" />
            )}
            <IdChip label="pipeline" value={run.pipelineId} to={`/pipelines/${run.pipelineId}`} testId="run-pipeline-link" copyTestId="copy-run-pipeline" />
            {run.groupId && (
              <IdChip label="group" value={run.groupId} to={`/runs/groups/${run.groupId}`} testId="run-group-link" copyTestId="copy-run-group" />
            )}
            {run.fanOutRoot && (
              <IdChip label="fan-out of" value={run.fanOutRoot} to={`/runs/${run.fanOutRoot}`} testId="run-fanout-root" copyTestId="copy-run-fanout-root" />
            )}
            {run.commitSha && (
              <IdChip label="commit" value={run.commitSha} display={run.commitSha.slice(0, 7)} testId="run-commit" copyTestId="copy-run-commit" />
            )}
            {run.resultSubmissionId && (
              <IdChip label="submission" value={run.resultSubmissionId} testId="run-submission" copyTestId="copy-run-submission" />
            )}
          </>
        )}
      >
        <DetailPair label="Enqueued"><RelativeTime value={run.enqueuedUtc} absolute /></DetailPair>
        <DetailPair label="Started"><RelativeTime value={run.startUtc} absolute /></DetailPair>
        <DetailPair label="Ended"><RelativeTime value={run.endUtc} absolute /></DetailPair>
        <DetailPair label="Duration">
          <span className="font-mono tabular-nums">
            {run.durationSeconds != null ? formatDurationSeconds(run.durationSeconds) : "-"}
          </span>
        </DetailPair>
        <DetailPair label="Planned"><RowCount value={run.recordsPlanned} /></DetailPair>
        <DetailPair label="Delivered"><RowCount value={run.recordsDelivered} /></DetailPair>
        <DetailPair label="Held"><RowCount value={run.recordsHeld} /></DetailPair>
        <DetailPair label="Failed"><RowCount value={run.recordsFailed} /></DetailPair>
        <DetailPair label="Unchanged"><RowCount value={run.recordsSkipped} /></DetailPair>
        <DetailPair label="Step">
          <span className="font-mono tabular-nums">{run.wave >= 0 ? run.wave : "-"}</span>
        </DetailPair>
        {run.fanOutSlot !== null && (
          <DetailPair label="Fan-out">
            <span className="font-mono tabular-nums" data-testid="run-fanout-slot">{`member ${run.fanOutSlot} of ${run.fanOutCount ?? "?"}`}</span>
          </DetailPair>
        )}
        <DetailPair label="Target pool">{run.targetPool ?? "-"}</DetailPair>
        <DetailPair label="Node"><span className="break-all font-mono text-[12px]">{run.claimedByNode ?? "-"}</span></DetailPair>
        {/* Host repeats the node name on a single-container node; surface it only when it actually adds a value. */}
        {run.host && run.host !== run.claimedByNode && (
          <DetailPair label="Host"><span className="break-all font-mono text-[12px]">{run.host}</span></DetailPair>
        )}
      </DetailHeaderCard>

      {hasParameters && (
        <Alert data-testid="run-parameters">
          <Info />
          <AlertDescription>
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-medium text-foreground">Parameters:</span>
              <Badge variant="secondary" className="bg-info/12 text-info" data-testid="run-operation">{run.operation}</Badge>
              {run.force && <Badge variant="secondary" className="bg-warning/15 text-warning">forced</Badge>}
              {parameters?.drop && <Badge variant="secondary" className="font-mono">drop {parameters.drop}</Badge>}
              {parameters?.submissionId && (
                <Badge variant="secondary" className="font-mono">submission {parameters.submissionId}</Badge>
              )}
              {parameters?.recordKeys && parameters.recordKeys.length > 0 && (
                <Badge variant="secondary">{parameters.recordKeys.length} scoped {parameters.recordKeys.length === 1 ? "record" : "records"}</Badge>
              )}
              {parameters?.publishTo && <Badge variant="secondary" className="font-mono">publish to {parameters.publishTo}</Badge>}
              {Object.entries(parameters?.values ?? {}).map(([name, value]) => (
                <Badge key={name} variant="secondary" className="font-mono">{name}={value}</Badge>
              ))}
            </div>
          </AlertDescription>
        </Alert>
      )}

      {run.resultJson !== null && (
        <Card className="gap-2 rounded-lg p-3" data-testid="run-result">
          <h2 className="text-[13px] font-medium">Outcome</h2>
          <p className="text-[13px] text-muted-foreground">What the run reported when it finished: its counts, the submission it worked on, and how far it fanned out.</p>
          <CodeView value={prettyJson(run.resultJson)} language="json" height={220} data-testid="run-result-json" />
        </Card>
      )}

      <Card className="gap-2 rounded-lg p-3" data-testid="run-source">
        <h2 className="text-[13px] font-medium">Source</h2>
        <p className="text-[13px] text-muted-foreground">
          The flow's YAML definition as registered in the catalog. This is the current source for this pipeline;
          {run.commitSha
            ? ` the run executed against commit ${run.commitSha.slice(0, 12)}, so a later change to the flow file may differ from what ran.`
            : " if the flow file has changed since this run, the executed source may differ."}
        </p>
        {pipelineQuery.isError && (
          <Alert variant="destructive" data-testid="source-error">
            <CircleAlert />
            <AlertDescription>
              {isApiError(pipelineQuery.error)
                ? pipelineQuery.error.title
                : "Could not load the flow definition from the catalog."}
            </AlertDescription>
          </Alert>
        )}
        {pipelineQuery.data === undefined && !pipelineQuery.isError
          ? <Skeleton className="h-[480px] w-full rounded-lg" data-testid="source-loading" />
          : pipelineQuery.data !== undefined && (
            <CodeView value={pipelineQuery.data.yaml} language="yaml" height={480} data-testid="run-source-yaml" />
          )}
      </Card>

      <ConfirmDialog
        open={confirmOpen}
        title="Cancel run"
        message={run.status === "running"
          ? `Cancel running run ${run.runId}? The node executing it stops at the next safe point and records the cancellation.`
          : `Cancel queued run ${run.runId}? A queued run is dequeued before any node claims it.`}
        confirmLabel="Cancel run"
        danger
        busy={cancel.isPending}
        onConfirm={() => cancel.mutate()}
        onClose={() => setConfirmOpen(false)}
      />
      {run.repoId !== null && (
        <TriggerRunDialog
          open={rerunOpen}
          onClose={() => setRerunOpen(false)}
          repoId={run.repoId}
          flowName={run.flowName}
          flowId={run.pipelineId}
          initialParameters={{ ...(parameters ?? {}), operation: run.operation, force: run.force }}
        />
      )}
    </Page>
  );
}
