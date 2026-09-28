import { useQuery } from "@tanstack/react-query";
import { datasourceApi } from "@/api/endpoints";
import { deliveryApi, type DeliveryFlowScope, type DeliveryScopeValues } from "../../api/delivery";
import { isTerminalTask } from "./useComputeTask";

/** How long one poll waits on the control plane for the task to finish. */
const WAIT_MS = 10_000;

/** How long a read may wait for a node before it gives up and the fields fall back to typing. */
const DEADLINE_MS = 120_000;

/** How long the values read are taken as current: a scope's values change as the ingestion flows load, not by the minute. */
const STALE_MS = 5 * 60_000;

/**
 * The values each parameter of an interface's scope can take, read on a node from the columns the flow's scope binds them
 * to (`source.record.scope`): what a page offers for a scope's value rather than having it typed. The read is queued once
 * and polled to its end, then kept for a few minutes per interface and partition, so every page asking for the same flow's
 * scope shares it. `enabled` is false for a reader who may not queue node work, or while nothing asks for the values.
 */
export function useScopeValues(pipelineId: string | null, scope: DeliveryFlowScope, enabled: boolean) {
  return useQuery({
    queryKey: ["delivery", "scope-values", pipelineId, scope.interfaceName ?? null, scope.partition ?? null],
    queryFn: async ({ signal }): Promise<DeliveryScopeValues> => {
      const accepted = await deliveryApi.scopeValues(pipelineId!, scope);
      const deadline = Date.now() + DEADLINE_MS;
      for (;;) {
        const task = await datasourceApi.task(accepted.taskId, WAIT_MS, signal);
        if (isTerminalTask(task)) {
          if (task.status !== "succeeded" || task.result === null || task.result === undefined) {
            throw new Error(task.error ?? "The values of the scope could not be read.");
          }

          return task.result as DeliveryScopeValues;
        }

        if (Date.now() > deadline) {
          throw new Error("No node read the scope's values in time; type the value instead.");
        }
      }
    },
    enabled: enabled && pipelineId !== null,
    staleTime: STALE_MS,
    retry: false,
  });
}
