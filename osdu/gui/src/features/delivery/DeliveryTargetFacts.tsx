import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { ConnectionRef } from "@/components/ConnectionRef";
import { DetailPair } from "@/components/DetailPair";
import { Skeleton } from "@/components/ui/skeleton";
import { deliveryApi } from "../../api/delivery";

/**
 * Where a delivery source delivers, in its pipeline header: the endpoint and data partition as the flow declares them,
 * secret references and all, since the reference is what names the environment. They stand in for the catalog's target
 * server, which for a flow whose target is not a database says only "file". Every interface of a source shares its
 * target, so the first interface answers for the source. A flow that names its partitions delivers to each of them, and
 * the header lists them all, in the order the flow names them.
 */
export function DeliveryTargetFacts({ pipelineId }: { pipelineId: string }) {
  const interfaces = useQuery({
    queryKey: ["delivery", "interfaces", pipelineId],
    queryFn: () => deliveryApi.interfaces(pipelineId),
  });
  const first = interfaces.data?.[0];
  const partitions = useMemo(
    () => [...new Set((interfaces.data ?? []).map((row) => row.partition ?? null).filter((name): name is string => name !== null))],
    [interfaces.data]);
  const scope = { interfaceName: first?.interface ?? null, partition: first?.partition ?? null };
  const target = useQuery({
    queryKey: ["delivery", "target", pipelineId, scope.interfaceName, scope.partition],
    queryFn: () => deliveryApi.target(pipelineId, scope),
    enabled: interfaces.data !== undefined,
  });

  // A source whose document no longer parses answers with a problem: the header then says so in the one place the target
  // would be, rather than hiding the fact and leaving the reader to wonder where the flow delivers.
  const failed = interfaces.isError || target.isError;
  const loading = !failed && target.data === undefined;
  return (
    <>
      <DetailPair label="Target">
        {failed
          ? <span className="text-muted-foreground" data-testid="delivery-target-unknown">not readable</span>
          : loading
            ? <Skeleton className="h-4 w-28" />
            : <ConnectionRef value={target.data?.endpoint} copyTestId="copy-delivery-target" />}
      </DetailPair>
      <DetailPair label={partitions.length > 1 ? "Partitions" : "Partition"}>
        {failed
          ? <span className="text-muted-foreground">-</span>
          : partitions.length > 0
            ? <span className="font-mono text-[12px]" data-testid="delivery-target-partitions">{partitions.join(", ")}</span>
            : loading
              ? <Skeleton className="h-4 w-20" />
              : <ConnectionRef value={target.data?.dataPartition} copyTestId="copy-delivery-partition" />}
      </DetailPair>
    </>
  );
}
