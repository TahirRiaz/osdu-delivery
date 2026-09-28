import { useQuery } from "@tanstack/react-query";
import { deliveryApi } from "../../api/delivery";

/** The query key every view of the partition listing shares, so a change to the registry refreshes them all at once. */
export const PARTITIONS_KEY = ["delivery", "partitions"] as const;

/**
 * The partitions the workbench knows (docs/partitions-design.md section 2.1): every registered one, marked the default or
 * not, and every partition something is still kept under. Read by the title bar's switcher, the Partitions page, a flow's
 * pages and the trigger dialog, which all take the registry's default when nothing else settles the partition.
 */
export function usePartitions() {
  const partitions = useQuery({
    queryKey: PARTITIONS_KEY,
    queryFn: deliveryApi.partitions,
    staleTime: 30000,
    refetchInterval: 60000,
  });
  const list = partitions.data ?? [];
  return {
    partitions,
    list,
    /** The partition a run that names none runs in, or null while none is the default. */
    defaultPartition: list.find((partition) => partition.isDefault)?.name ?? null,
  };
}
