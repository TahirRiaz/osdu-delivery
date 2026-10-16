import { useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ArrowRight, ChevronDown, ChevronRight, CircleAlert } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { DataTable, type Column } from "@/components/DataTable";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { inventoryApi, type InventoryEntry } from "../../../api/inventories";
import { KindText } from "../KindText";
import { useActivePartition } from "../activePartition";
import { ProblemView } from "../TemplateSheet";
import { RaisedCounts, StandingGlyph } from "./InventoryBadges";
import { STANDING_VISUALS, inventoryRoute, ownersSourceText, standingOf } from "./inventoryFormat";

/** How often the tab reads the ledger again, so a build under way shows its outcome as it lands. */
const REFRESH_MS = 15000;

/**
 * An inventory flow's Inventories tab: each inventory it declares in the workbench's partition, where it stands, the kind it
 * reads, what its last reconcile raised and when it was built, with a way to open its report on the Inventories page. The
 * page's own Trigger run runs the pipeline, and its dialog picks the inventories a run builds or reconciles, so the tab adds
 * no button of its own for that. Inventories the flow no longer declares, which keep what their last build found, wait
 * behind a toggle.
 */
export function InventoriesPanel({ pipelineId }: { pipelineId: string }) {
  const [active] = useActivePartition();
  const [showRetired, setShowRetired] = useState(false);
  const flow = useQuery({
    queryKey: ["delivery", "inventories", "flow", pipelineId, active],
    queryFn: () => inventoryApi.flow(pipelineId),
    refetchInterval: REFRESH_MS,
  });
  const entries = useMemo(() => flow.data?.inventories ?? [], [flow.data]);
  const declared = entries.filter((entry) => entry.declared);
  const retired = entries.filter((entry) => !entry.declared);

  if (flow.isError) {
    return <ProblemView error={flow.error} testId="inventory-panel-error" />;
  }

  if (flow.data === undefined) {
    return <Skeleton className="h-48 w-full rounded-lg" />;
  }

  const data = flow.data;
  const columns: Column<InventoryEntry>[] = [
    {
      id: "inventory",
      header: "Inventory",
      render: (row) => (
        <span className="flex items-center gap-2">
          <StandingGlyph standing={standingOf(row.inventory, row.declared)} />
          {row.description === undefined
            ? <span className="font-medium">{row.name}</span>
            : <RichTooltip title={row.name} body={row.description}><span className="font-medium">{row.name}</span></RichTooltip>}
        </span>
      ),
    },
    {
      id: "kind",
      header: "Kind",
      fill: true,
      floor: 200,
      render: (row) => (
        <span className="flex min-w-0 items-center gap-1.5">
          <span className="min-w-0"><KindText kind={row.kind} /></span>
          {row.query !== undefined && (
            <RichTooltip title="Narrowed by" body={row.query} mono>
              <span className="shrink-0 rounded-sm border border-border px-1 text-[10.5px] text-muted-foreground">query</span>
            </RichTooltip>
          )}
        </span>
      ),
    },
    { id: "raised", header: "Raised", render: (row) => <RaisedCounts counts={row.inventory?.reconciled} testId={`inventory-panel-raised-${row.name}`} /> },
    {
      id: "built",
      header: "Built",
      render: (row) => (row.inventory?.lastBuiltUtc === undefined
        ? <span className="text-[12px] text-muted-foreground">{STANDING_VISUALS[standingOf(row.inventory, row.declared)].label.toLowerCase()}</span>
        : <span className="text-[12px]"><RelativeTime value={row.inventory.lastBuiltUtc} absolute={false} /></span>),
    },
    {
      id: "actions",
      header: "",
      align: "right",
      render: (row) => (row.inventory === undefined
        ? null
        : (
          <Button asChild variant="ghost" size="xs" data-testid={`inventory-panel-open-${row.name}`}>
            <Link to={inventoryRoute({ partition: row.inventory.partition, inventoryId: row.inventory.inventoryId })} onClick={(event) => event.stopPropagation()}>
              Open
              <ArrowRight />
            </Link>
          </Button>
        )),
    },
  ];

  const where = data.partition === undefined
    ? "Read in the partition its data-partition-id header names"
    : `In ${data.partition}${data.partitions.length > 1 ? `, one of ${data.partitions.join(", ")}` : ""}`;
  const owners = data.owners.length === 0 ? "owners inferred from the ids a ledger claims" : `owners ${ownersSourceText("declared")}: ${data.owners.join(", ")}`;
  return (
    <div className="flex flex-col gap-3" data-testid="inventory-panel">
      {data.problem !== undefined && (
        <Alert variant="destructive" data-testid="inventory-panel-problem">
          <CircleAlert />
          <AlertDescription>{data.problem}</AlertDescription>
        </Alert>
      )}
      <p className="text-[13px] text-muted-foreground" data-testid="inventory-panel-where">
        {where}{data.read === undefined ? "" : `, through ${data.read}`}; {owners}.
      </p>
      <DataTable
        columns={columns}
        rows={declared}
        rowKey={(row) => row.name}
        emptyMessage="The flow declares no inventory."
        data-testid="inventory-panel-table"
      />
      {retired.length > 0 && (
        <div className="flex flex-col gap-2" data-testid="inventory-panel-retired">
          <Button
            variant="ghost"
            size="sm"
            className="self-start text-muted-foreground"
            onClick={() => setShowRetired((shown) => !shown)}
            aria-expanded={showRetired}
            data-testid="inventory-panel-retired-toggle"
          >
            {showRetired ? <ChevronDown /> : <ChevronRight />}
            No longer declared ({retired.length})
          </Button>
          {showRetired && (
            <DataTable
              columns={columns}
              rows={retired}
              rowKey={(row) => row.name}
              emptyMessage="Every inventory kept here is declared."
              data-testid="inventory-panel-retired-table"
            />
          )}
        </div>
      )}
    </div>
  );
}
