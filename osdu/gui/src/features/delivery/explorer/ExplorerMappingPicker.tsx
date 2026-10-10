import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { deliveryApi, type DeliveryMapping } from "../../../api/delivery";

/** The value the picker holds for judging no mapping's assertions, since a select item cannot hold an empty value. */
const NONE = "none";

/** How many assertions the sync counted in a mapping, or null for a mapping synced before it counted them. */
function assertionCount(mapping: DeliveryMapping): number | null {
  const count = mapping.summary.assertions;
  return typeof count === "number" ? count : null;
}

/**
 * The mapping whose assertions a check judges on the records OSDU holds (osdu/docs/reference/flow/mapping-assertions.md):
 * none, or one of the synced mappings that load, of the kind the records are when it is known. A reference two
 * repositories sync is told apart by the repository. A mapping that states assertions lists how many; the server judges
 * only the records of the entity type the mapping renders.
 */
export function ExplorerMappingPicker({ kind, value, onChoose }: {
  /** The kind the records are, when the check knows it; every synced mapping is offered otherwise. */
  kind: string | null;
  /** The id of the mapping chosen, or null for none. */
  value: string | null;
  onChoose: (mappingId: string | null) => void;
}) {
  const mappings = useQuery({ queryKey: ["delivery", "mappings", "valid"], queryFn: () => deliveryApi.mappings(undefined, "valid"), staleTime: 60_000 });
  const offered = useMemo(() => {
    const valid = (mappings.data ?? []).filter((m) => m.status === "valid" && (kind === null || m.kind === kind));
    const named = new Map<string, number>();
    for (const m of valid) {
      named.set(m.reference, (named.get(m.reference) ?? 0) + 1);
    }

    return valid
      .map((m) => ({ mapping: m, label: (named.get(m.reference) ?? 0) > 1 ? `${m.reference} in ${m.repoId.slice(0, 8)}` : m.reference, assertions: assertionCount(m) }))
      .sort((a, b) => (b.assertions ?? 0) - (a.assertions ?? 0) || a.label.localeCompare(b.label));
  }, [mappings.data, kind]);
  const chosen = value !== null && offered.some((o) => o.mapping.id === value) ? value : NONE;

  return (
    <Select value={chosen} onValueChange={(picked) => onChoose(picked === NONE ? null : picked)} disabled={mappings.isError}>
      <SelectTrigger
        size="sm"
        className="h-7 w-[240px] text-[12px]"
        title={mappings.isError ? "The synced mappings could not be read" : "The mapping whose assertions are judged on the records"}
        data-testid="explorer-validate-mapping"
      >
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value={NONE} className="text-[12px]">No mapping's assertions</SelectItem>
        {offered.map(({ mapping, label, assertions }) => (
          <SelectItem key={mapping.id} value={mapping.id} className="text-[12px]" title={`${mapping.relativePath}, ${mapping.kind}`}>
            <span className="font-mono text-[11px]">{label}</span>
            {assertions !== null && (
              <span className={assertions === 0 ? "text-muted-foreground/60" : "text-muted-foreground"}>
                {` ${assertions.toLocaleString("en-US")} assertion${assertions === 1 ? "" : "s"}`}
              </span>
            )}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}
