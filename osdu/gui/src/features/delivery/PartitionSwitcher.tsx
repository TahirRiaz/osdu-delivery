import { useEffect } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, Layers } from "lucide-react";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { RelativeTime } from "@/components/RelativeTime";
import { deliveryApi, type DeliveryPartition } from "../../api/delivery";
import { useActivePartition } from "./activePartition";

/** What a partition's cache serves, in a few words. */
function cacheLine(partition: DeliveryPartition) {
  if (partition.currentVersion === null) {
    return <span>no cache version yet</span>;
  }

  return (
    <span>
      cache {partition.types.toLocaleString()} {partition.types === 1 ? "type" : "types"}, {partition.items.toLocaleString()}{" "}
      {partition.items === 1 ? "entry" : "entries"}, captured {partition.capturedUtc === null ? "at an unknown time" : <RelativeTime value={partition.capturedUtc} absolute={false} />}
    </span>
  );
}

/**
 * The OSDU partition the workbench works in, picked in the title bar (docs/partitions-design.md section 7). It lists every
 * partition the catalog keeps a cache for or a delivery flow names, each with what its cache serves, how many delivery
 * flows deliver there and how many cache changes wait for a decision, so another partition is one choice away and says
 * what it holds before it is picked. The choice is remembered, and a page that names its partition in its link
 * (`?partition=`) follows it. Nothing shows while the catalog knows no partition.
 */
export function PartitionSwitcher() {
  const [searchParams, setSearchParams] = useSearchParams();
  const [active, setActive] = useActivePartition();
  const partitions = useQuery({
    queryKey: ["delivery", "partitions"],
    queryFn: deliveryApi.partitions,
    staleTime: 30000,
    refetchInterval: 60000,
  });
  const list = partitions.data ?? [];
  const known = active !== null && list.some((partition) => partition.name === active);
  const shown = known ? active : list[0]?.name ?? null;

  // A partition remembered from a catalog that no longer knows it gives way to the first one this catalog knows, so the
  // title bar and the pages that read it always agree on one.
  useEffect(() => {
    if (partitions.data !== undefined && shown !== null && shown !== active) {
      setActive(shown);
    }
  }, [partitions.data, shown, active, setActive]);

  if (shown === null) {
    return null;
  }

  const choose = (next: string) => {
    setActive(next);
    // A page that names its partition in its link follows the choice, rather than keeping the one its link named.
    if (searchParams.has("partition")) {
      setSearchParams((current) => {
        const params = new URLSearchParams(current);
        params.set("partition", next);
        return params;
      }, { replace: true });
    }
  };

  const current = list.find((partition) => partition.name === shown)!;
  return (
    <DropdownMenu>
      <DropdownMenuTrigger
        data-testid="osdu-partition-switcher"
        aria-label={`OSDU partition: ${shown}`}
        title="The OSDU partition every OSDU page is read in"
        className="flex h-6 shrink-0 items-center gap-1.5 rounded-md bg-white/10 px-2 text-white/80 transition-colors hover:bg-white/15 hover:text-white"
      >
        <Layers className="size-3.5" />
        <span className="hidden text-[11px] text-white/55 sm:inline">Partition</span>
        <span className="font-mono text-[12px] leading-none text-white" data-testid="osdu-partition-current">{shown}</span>
        {current.pendingChanges > 0 && (
          <span
            className="rounded-full bg-warning px-1.5 font-mono text-[10px] leading-4 text-warning-foreground"
            title={`${current.pendingChanges.toLocaleString()} cache ${current.pendingChanges === 1 ? "change waits" : "changes wait"} for a decision in ${shown}`}
          >
            {current.pendingChanges.toLocaleString()}
          </span>
        )}
        <ChevronDown className="size-3.5 text-white/55" />
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-80">
        <DropdownMenuLabel className="text-[12px] font-normal text-muted-foreground">
          The OSDU partition the cache, the flows and the mapping builder are read in
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        <DropdownMenuRadioGroup value={shown} onValueChange={choose}>
          {list.map((partition) => (
            <DropdownMenuRadioItem
              key={partition.name}
              value={partition.name}
              className="items-start py-1.5"
              data-testid={`osdu-partition-option-${partition.name}`}
            >
              <span className="flex min-w-0 flex-col gap-0.5">
                <span className="flex items-center gap-2">
                  <span className="font-mono text-[13px] font-medium">{partition.name}</span>
                  {partition.pendingChanges > 0 && (
                    <span className="text-[11px] text-warning">
                      {partition.pendingChanges.toLocaleString()} {partition.pendingChanges === 1 ? "change waits" : "changes wait"}
                    </span>
                  )}
                </span>
                <span className="text-[11px] text-muted-foreground">{cacheLine(partition)}</span>
                <span className="text-[11px] text-muted-foreground">
                  {partition.deliveryFlows.length === 0
                    ? "no delivery flow names it"
                    : `${partition.deliveryFlows.length.toLocaleString()} delivery ${partition.deliveryFlows.length === 1 ? "flow delivers" : "flows deliver"} here`}
                </span>
              </span>
            </DropdownMenuRadioItem>
          ))}
        </DropdownMenuRadioGroup>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
