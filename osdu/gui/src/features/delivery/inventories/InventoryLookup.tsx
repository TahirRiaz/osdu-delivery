import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Popover, PopoverAnchor, PopoverContent } from "@/components/ui/popover";
import { Skeleton } from "@/components/ui/skeleton";
import { SearchInput } from "@/components/SearchInput";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { inventoryApi } from "../../../api/inventories";
import { useActivePartition } from "../activePartition";
import { ProblemView } from "../TemplateSheet";
import { FindingGlyph } from "./InventoryBadges";
import { findingVisual, inventoryRoute } from "./inventoryFormat";

const LOOKUP_DELAY_MS = 400;

/** The longest id a lookup sends, as the control plane takes it. */
const MAX_ID_LENGTH = 1024;

/**
 * A lookup by OSDU id across the inventories of the partition picked in the title bar: what each inventory holds of the id
 * (listed by it, or expected from a ledger), with its finding there; picking one opens that inventory on the id's finding.
 */
export function InventoryLookupBox() {
  const navigate = useNavigate();
  const [active] = useActivePartition();
  const [typed, setTyped] = useState("");
  const [open, setOpen] = useState(false);
  const id = useDebouncedValue(typed.trim(), LOOKUP_DELAY_MS);
  const asked = id !== "" && id.length <= MAX_ID_LENGTH && active !== null;
  const lookup = useQuery({
    queryKey: ["delivery", "inventories", "lookup", active, id],
    queryFn: () => inventoryApi.lookup(id),
    enabled: asked,
    staleTime: 15000,
  });

  const body = () => {
    if (active === null) {
      return <p className="p-3 text-[12.5px] text-muted-foreground">Pick a partition in the title bar: an id is looked up in one partition's inventories.</p>;
    }

    if (id.length > MAX_ID_LENGTH) {
      return <p className="p-3 text-[12.5px] text-muted-foreground">An OSDU id is at most {MAX_ID_LENGTH.toLocaleString("en-US")} characters.</p>;
    }

    if (lookup.isError) {
      return <div className="p-3"><ProblemView error={lookup.error} testId="inventory-lookup-error" /></div>;
    }

    if (lookup.data === undefined) {
      return <div className="p-3"><Skeleton className="h-10 w-full" /></div>;
    }

    if (lookup.data.hits.length === 0) {
      return <p className="p-3 text-[12.5px] text-muted-foreground" data-testid="inventory-lookup-none">No inventory of {lookup.data.partition} lists it or expects it.</p>;
    }

    return (
      <ul className="max-h-80 overflow-y-auto py-1" data-testid="inventory-lookup-hits">
        {lookup.data.hits.map(({ inventory, record }) => (
          <li key={`${inventory.inventoryId}-${record.inventoryRecordId}`}>
            <button
              type="button"
              onClick={() => {
                setOpen(false);
                navigate(inventoryRoute({ partition: inventory.partition, inventoryId: inventory.inventoryId }, record.finding));
              }}
              className="flex w-full flex-col gap-0.5 px-3 py-1.5 text-left outline-none hover:bg-accent/60 focus-visible:bg-accent/60"
              data-testid="inventory-lookup-hit"
            >
              <span className="flex min-w-0 items-center gap-1.5 text-[13px]">
                <FindingGlyph finding={record.finding} />
                <span className="font-medium">{inventory.name}</span>
                <span className="text-muted-foreground">{findingVisual(record.finding).label.toLowerCase()}</span>
                {record.version !== undefined && <span className="font-mono text-[12px] tabular-nums text-muted-foreground">v{record.version}</span>}
                <span className="ml-auto min-w-0 truncate font-mono text-[11px] text-muted-foreground">{inventory.flowName}</span>
              </span>
              {record.detail !== undefined && <span className="truncate pl-5 text-[11.5px] text-muted-foreground">{record.detail}</span>}
            </button>
          </li>
        ))}
      </ul>
    );
  };

  return (
    <Popover open={open && id !== ""} onOpenChange={setOpen}>
      <PopoverAnchor asChild>
        <div>
          <SearchInput
            value={typed}
            onChange={(value) => {
              setTyped(value);
              setOpen(true);
            }}
            placeholder="Look up an id"
            label="Look up an OSDU id in the partition's inventories"
            className="sm:w-60"
            testId="inventory-lookup"
          />
        </div>
      </PopoverAnchor>
      <PopoverContent align="end" className="w-[30rem] max-w-[calc(100vw-2rem)] p-0" onOpenAutoFocus={(event) => event.preventDefault()} data-testid="inventory-lookup-results">
        {body()}
      </PopoverContent>
    </Popover>
  );
}
