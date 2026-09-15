import { Link as RouterLink } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { DatabaseZap } from "lucide-react";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "../../api/client";
import { deliveryApi } from "../../api/delivery";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { DeliveryCacheHistory } from "./DeliveryCacheHistory";

/**
 * A cache flow's view of the cache it fills, for its pipeline page: the partition, how many other flows fill the same cache,
 * and every version of that cache with the flow that wrote each, with a link to the cache itself.
 */
export function DeliveryCacheFlowVersions({ flowName }: { flowName: string }) {
  const caches = useQuery({ queryKey: ["delivery", "cache", "caches"], queryFn: () => deliveryApi.caches() });

  if (caches.isPending) {
    return <Skeleton className="h-40 w-full rounded-lg" />;
  }

  if (caches.isError) {
    return isApiError(caches.error)
      ? <CorrelationError error={caches.error} />
      : <p className="text-[13px] text-destructive">{String(caches.error)}</p>;
  }

  const cache = caches.data.find((candidate) => candidate.flows.some((flow) => flow.name === flowName)) ?? null;
  if (cache === null) {
    return (
      <Card className="gap-0 rounded-lg p-0">
        <EmptyState
          icon={<DatabaseZap />}
          title="This cache flow fills no cache yet"
          description="The repository sync has not recorded what it declares, or left every type out because another cache flow of the partition declares it differently; the sync warnings say which."
          data-testid="pipeline-cache-none"
        />
      </Card>
    );
  }

  const others = cache.flows.filter((flow) => flow.name !== flowName).length;
  return (
    <div className="flex flex-col gap-2">
      <p className="text-[12.5px] text-muted-foreground">
        This flow fills the cache of partition <span className="font-mono text-foreground">{cache.scope}</span>
        {others > 0 ? `, together with ${others} other cache flow${others === 1 ? "" : "s"}` : ""}. Every version of that cache
        is listed with the flow that wrote it. What the cache holds, and the changes waiting for approval, are on the{" "}
        <RouterLink
          to={`/delivery/cache?scope=${encodeURIComponent(cache.scope)}`}
          className="text-primary hover:underline"
          data-testid="pipeline-cache-link"
        >
          OSDU cache page
        </RouterLink>
        .
      </p>
      <DeliveryCacheHistory scope={cache.scope} type={null} />
    </div>
  );
}
