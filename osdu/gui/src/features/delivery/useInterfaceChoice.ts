import { useCallback, useEffect, useMemo } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { deliveryApi, type DeliveryFlowScope } from "../../api/delivery";
import { useActivePartition } from "./activePartition";
import { usePartitions } from "./usePartitions";

/**
 * Which ledger of a flow a view is about: the interface of a source, and the partition of a flow that names its
 * partitions. Every view of a flow is about one ledger: interfaces and partitions keep separate ledgers, so their records,
 * submissions, targets and previews are never summed. The choice travels in the URL (`interface`, `partition`), so a link
 * to a flow's records (or its preview) is a link to one ledger's.
 *
 * A flow in the single form has one interface and no name, and `interfaceName` is null for it; a flow whose partition is
 * its header's has `partitions` empty and `partition` null. A flow that works in partitions (the ones it names, or every
 * registered one) is shown in the partition the URL names, else in the workbench's partition (the title bar's) when the
 * flow serves it, else in the registry's default when it serves that, else in the first it serves; `outside` is then the
 * workbench's partition the flow does not deliver to, for the view to say so. A flow whose partition is its header's
 * delivers to the partition its ledger is kept under (`headerPartition`, from its counts; null until it has run), and is
 * `outside` the workbench's partition when that is another. A partition the URL names, or one picked here, becomes the
 * workbench's. `current` is the interface in view either way, once the listing has loaded, and
 * `scope` is what every flow-level request of the view carries. `clearOnChange` names the URL parameters that meant
 * something only for the ledger that was showing.
 */
export function useInterfaceChoice(pipelineId: string, clearOnChange: readonly string[] = []) {
  const [searchParams, setSearchParams] = useSearchParams();
  // Listed without a partition, a flow that works in partitions lists every interface in every partition, each row
  // naming its partition: the one listing that says both which partitions there are and how each stands.
  const interfaces = useQuery({
    queryKey: ["delivery", "interfaces", pipelineId],
    queryFn: () => deliveryApi.interfaces(pipelineId),
    staleTime: 30000,
  });
  const partitions = useMemo(
    () => [...new Set((interfaces.data ?? []).map((row) => row.partition ?? null).filter((name): name is string => name !== null))],
    [interfaces.data]);
  const [active, setActive] = useActivePartition();
  const { defaultPartition } = usePartitions();
  const askedPartition = searchParams.get("partition");
  const askedKnown = askedPartition !== null && partitions.includes(askedPartition);
  const partition = partitions.length === 0
    ? null
    : askedKnown
      ? askedPartition
      : active !== null && partitions.includes(active)
        ? active
        : defaultPartition !== null && partitions.includes(defaultPartition) ? defaultPartition : partitions[0];
  const headerPartition = partitions.length > 0
    ? null
    : (interfaces.data ?? []).map((row) => row.stats.headerPartition ?? null).find((name): name is string => name !== null) ?? null;
  const outside = active === null
    ? null
    : partitions.length > 0
      ? (partitions.includes(active) ? null : active)
      : headerPartition !== null && headerPartition !== active ? active : null;

  // A link that names the partition makes it the workbench's, so the title bar says which partition the page is about.
  useEffect(() => {
    if (askedKnown && askedPartition !== active) {
      setActive(askedPartition);
    }
  }, [askedKnown, askedPartition, active, setActive]);
  const rows = useMemo(
    () => (interfaces.data === undefined ? undefined : interfaces.data.filter((row) => (row.partition ?? null) === partition)),
    [interfaces.data, partition]);
  const names = useMemo(
    () => (rows ?? []).map((row) => row.interface).filter((name): name is string => name !== null),
    [rows]);
  const many = names.length > 1;
  const asked = searchParams.get("interface");
  const interfaceName = many ? (asked !== null && names.includes(asked) ? asked : names[0]) : null;
  const current = rows === undefined
    ? undefined
    : rows.find((row) => row.interface === interfaceName) ?? (many ? undefined : rows[0]);
  // Until the listing answers, nothing says whether the flow names partitions, so a request that must name one waits.
  const ready = interfaces.data !== undefined || interfaces.isError;
  const scope = useMemo<DeliveryFlowScope>(() => ({ interfaceName, partition }), [interfaceName, partition]);
  const clearKey = clearOnChange.join("\u001f");
  const choose = useCallback((name: "interface" | "partition", next: string) => setSearchParams((existing) => {
    const params = new URLSearchParams(existing);
    params.set(name, next);
    for (const cleared of clearKey === "" ? [] : clearKey.split("\u001f")) {
      params.delete(cleared);
    }

    return params;
  }), [setSearchParams, clearKey]);
  const selectInterface = useCallback((next: string) => choose("interface", next), [choose]);
  const selectPartition = useCallback((next: string) => {
    choose("partition", next);
    setActive(next);
  }, [choose, setActive]);
  return {
    interfaces, rows, names, many, interfaceName, current, partitions, partition, headerPartition, outside, scope, ready, selectInterface, selectPartition,
  };
}
