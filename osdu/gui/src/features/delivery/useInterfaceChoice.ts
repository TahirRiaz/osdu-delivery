import { useCallback, useEffect, useMemo } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { deliveryApi, type DeliveryFlowScope } from "../../api/delivery";
import { useActivePartition } from "./activePartition";

/**
 * Which ledger of a flow a view is about: the interface of a source, and the partition of a flow that names its
 * partitions. Every view of a flow is about one ledger: interfaces and partitions keep separate ledgers, so their records,
 * submissions, targets and previews are never summed. The interface travels in the URL (`interface`), so a link to a
 * flow's records (or its preview) is a link to one ledger's.
 *
 * The partition is the one picked in the title bar, which every page follows (docs/partitions-design.md section 7): no view
 * picks one of its own. A link that names a partition the flow serves (`partition`) makes it the title bar's, so a link
 * still lands on the ledger it names. A flow in the single form has one interface and no name, and `interfaceName` is null
 * for it; a flow whose partition is its header's has `partitions` empty and `partition` null, and delivers to the
 * partition its ledger is kept under (`headerPartition`, from its counts; null until it has run). `outside` is the title
 * bar's partition when the flow does not deliver to it (or null when it does), and the view then says where the flow
 * delivers instead of showing another partition's ledger; `ready` is false until the listing has loaded and while the
 * flow is outside. `current` is the interface in view once the listing has loaded, and `scope` is what every flow-level
 * request of the view carries. `clearOnChange` names the URL parameters that meant something only for the ledger that
 * was showing.
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
  const askedPartition = searchParams.get("partition");
  const askedKnown = askedPartition !== null && partitions.includes(askedPartition);
  const partition = partitions.length === 0
    ? null
    : askedKnown
      ? askedPartition
      : active !== null && partitions.includes(active) ? active : null;
  const headerPartition = partitions.length > 0
    ? null
    : (interfaces.data ?? []).map((row) => row.stats.headerPartition ?? null).find((name): name is string => name !== null) ?? null;
  const outside = partitions.length > 0
    ? (partition === null ? active : null)
    : active !== null && headerPartition !== null && headerPartition !== active ? active : null;
  // A flow that works in partitions with none picked in the title bar is not in view either, though nothing is outside.
  const unplaced = partitions.length > 0 && partition === null;

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
  // Until the listing answers, nothing says whether the flow names partitions, so a request that must name one waits; a
  // flow the title bar's partition does not reach reads nothing at all.
  const ready = (interfaces.data !== undefined || interfaces.isError) && outside === null && !unplaced;
  const scope = useMemo<DeliveryFlowScope>(() => ({ interfaceName, partition }), [interfaceName, partition]);
  const clearKey = clearOnChange.join("\u001f");
  const choose = useCallback((name: "interface", next: string) => setSearchParams((existing) => {
    const params = new URLSearchParams(existing);
    params.set(name, next);
    for (const cleared of clearKey === "" ? [] : clearKey.split("\u001f")) {
      params.delete(cleared);
    }

    return params;
  }), [setSearchParams, clearKey]);
  const selectInterface = useCallback((next: string) => choose("interface", next), [choose]);
  return {
    interfaces, rows, names, many, interfaceName, current, partitions, partition, headerPartition, outside, unplaced, active, scope, ready,
    selectInterface,
  };
}
