import { useCallback, useMemo } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { deliveryApi } from "../../api/delivery";

/**
 * Which interface of a source a view is about. Every view of a source is about one of its interfaces: they have separate
 * ledgers, so their records, submissions, targets and previews are never summed. The choice travels in the URL, so a link
 * to a source's records (or its preview) is a link to one interface's. A flow in the single form has one interface and no
 * name, and `interfaceName` is null for it; `current` is the interface in view either way, once the listing has loaded.
 * `clearOnChange` names the URL parameters that meant something only for the interface that was showing.
 */
export function useInterfaceChoice(pipelineId: string, clearOnChange: readonly string[] = []) {
  const [searchParams, setSearchParams] = useSearchParams();
  const interfaces = useQuery({
    queryKey: ["delivery", "interfaces", pipelineId],
    queryFn: () => deliveryApi.interfaces(pipelineId),
    staleTime: 30000,
  });
  const names = useMemo(
    () => (interfaces.data ?? []).map((row) => row.interface).filter((name): name is string => name !== null),
    [interfaces.data]);
  const many = names.length > 1;
  const asked = searchParams.get("interface");
  const interfaceName = many ? (asked !== null && names.includes(asked) ? asked : names[0]) : null;
  const current = interfaces.data === undefined
    ? undefined
    : interfaces.data.find((row) => row.interface === interfaceName) ?? (many ? undefined : interfaces.data[0]);
  const clearKey = clearOnChange.join("\u001f");
  const selectInterface = useCallback((next: string) => setSearchParams((existing) => {
    const params = new URLSearchParams(existing);
    params.set("interface", next);
    for (const name of clearKey === "" ? [] : clearKey.split("\u001f")) {
      params.delete(name);
    }

    return params;
  }), [setSearchParams, clearKey]);
  return { interfaces, names, many, interfaceName, current, selectInterface };
}
