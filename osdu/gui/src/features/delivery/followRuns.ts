import { useEffect, useRef } from "react";
import { useQuery, useQueryClient, type Query } from "@tanstack/react-query";
import { runApi } from "@/api/endpoints";
import type { RunSummary } from "@/api/types";
import { pollingInterval } from "@/hooks/usePolling";

/** Whether a run has still to settle what it does to its flow's ledger. */
function underWay(run: RunSummary): boolean {
  return run.status === "queued" || run.status === "running";
}

/** Whether a delivery query reads what the flow of `pipelineId` holds: its counts, submissions, records and the rest. */
function readsFlow(query: Query, pipelineId: string): boolean {
  return query.queryKey[0] === "delivery" && query.queryKey.includes(pipelineId);
}

/**
 * Keeps a page's delivery views in step with the runs of the flows it shows, on an API that is polled rather than pushed.
 * One probe reads the newest run of one flow (`pipelineId`) or of every delivery flow (null), the run board's own listing,
 * every five seconds, and every three while a run is queued or running. While a flow's run is under way, its counts and
 * submissions are read again at each probe, so they move with the run; when a flow's newest run starts, moves on or ends,
 * every delivery view of that flow is read again at once. A page then shows what a run did within seconds of it, not at
 * its own next poll, and asks for no more than the probe while nothing runs.
 */
export function useFollowDeliveryRuns(pipelineId: string | null, enabled = true): void {
  const queryClient = useQueryClient();
  const seen = useRef(new Map<string, string>());
  const runs = useQuery({
    queryKey: ["delivery-run-follow", pipelineId ?? "every"],
    queryFn: () => (pipelineId === null
      ? runApi.list({ flowKind: "delivery", latest: true, pageSize: 200 })
      : runApi.list({ pipelineId, pageSize: 1 })),
    enabled,
    refetchInterval: (query) => pollingInterval((query.state.data?.items ?? []).some(underWay) ? 3000 : 5000)(),
  });

  useEffect(() => {
    const items = runs.data?.items;
    if (items === undefined) {
      return;
    }

    for (const run of items) {
      const signature = `${run.runId}:${run.status}`;
      const before = seen.current.get(run.pipelineId);
      seen.current.set(run.pipelineId, signature);
      if (before !== undefined && before !== signature) {
        void queryClient.invalidateQueries({ predicate: (query) => readsFlow(query, run.pipelineId) });
      } else if (underWay(run)) {
        void queryClient.invalidateQueries({ queryKey: ["delivery", "stats", run.pipelineId] });
        void queryClient.invalidateQueries({ queryKey: ["delivery", "submissions", run.pipelineId] });
      }
    }
  }, [runs.data, queryClient]);
}
