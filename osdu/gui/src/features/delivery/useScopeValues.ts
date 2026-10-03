import { useQuery } from "@tanstack/react-query";
import { deliveryApi, type DeliveryFlowScope } from "../../api/delivery";

/** How long the values read are taken as current: a scope's values change as the ingestion flows load, not by the minute. */
const STALE_MS = 5 * 60_000;

/**
 * The values each parameter of an interface's scope can take, read from the columns the flow's scope binds them to
 * (`source.record.scope`): what a page offers for a scope's value rather than having it typed. The control plane answers
 * at once, and the answer is kept for a few minutes per interface and partition, so every page asking for the same flow's
 * scope shares it. `enabled` is false for a reader who may not read the flow's tables, or while nothing asks for the values.
 */
export function useScopeValues(pipelineId: string | null, scope: DeliveryFlowScope, enabled: boolean) {
  return useQuery({
    queryKey: ["delivery", "scope-values", pipelineId, scope.interfaceName ?? null, scope.partition ?? null],
    queryFn: () => deliveryApi.scopeValues(pipelineId!, scope),
    enabled: enabled && pipelineId !== null,
    staleTime: STALE_MS,
    retry: false,
  });
}
