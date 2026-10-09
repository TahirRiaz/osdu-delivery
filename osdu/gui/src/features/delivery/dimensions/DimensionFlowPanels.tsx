import { useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ArrowRight, ChevronDown, ChevronRight, CircleAlert } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { DataTable, type Column } from "@/components/DataTable";
import { RelativeTime } from "@/components/RelativeTime";
import { deliveryApi } from "../../../api/delivery";
import { KindText } from "../KindText";
import { useActivePartition } from "../activePartition";
import { ProblemView } from "../TemplateSheet";
import { StandingGlyph } from "./DimensionBadges";
import { DimensionRemoveButton } from "./DimensionRemoveButton";
import { DimensionViewsSection } from "./DimensionViewList";
import { STANDING_VISUALS, dimensionRef, standingOf, type DimensionEntry } from "./dimensionFormat";

/** How often the tab reads the ledger again, so a build under way shows its outcome as it lands. */
const REFRESH_MS = 15000;

/**
 * A dimension flow's Dimensions tab: each dimension it declares in the workbench's partition, where it stands, what it
 * reads, what it holds and when it was built, with a way to open it on the Dimensions page. The page's own Trigger run
 * runs the pipeline, and its dialog picks the dimensions a run builds, so the tab adds no button of its own for that.
 * The dimensions the flow no longer declares, which keep what their last build wrote, wait behind a toggle, where an
 * admin can remove them. Under them, the views the flow declares over its dimensions' tables.
 */
export function DimensionsPanel({ pipelineId }: { pipelineId: string }) {
  const [active] = useActivePartition();
  const [showRetired, setShowRetired] = useState(false);
  const board = useQuery({
    queryKey: ["delivery", "dimensions", "flow", pipelineId, active],
    queryFn: () => deliveryApi.dimensionFlowBoard(pipelineId),
    refetchInterval: REFRESH_MS,
  });
  const flow = board.data?.flows[0];
  const entries = useMemo<DimensionEntry[]>(
    () => (flow === undefined ? [] : flow.dimensions.map((dimension) => ({ flow, dimension, ref: dimensionRef(flow, dimension), standing: standingOf(dimension) }))),
    [flow],
  );
  const declared = entries.filter((entry) => entry.standing !== "undeclared");
  const retired = entries.filter((entry) => entry.standing === "undeclared");

  if (board.isError) {
    return <ProblemView error={board.error} testId="dimension-panel-error" />;
  }

  if (flow === undefined) {
    return <Skeleton className="h-48 w-full rounded-lg" />;
  }

  const columns: Column<DimensionEntry>[] = [
    {
      id: "dimension",
      header: "Dimension",
      render: (row) => (
        <span className="flex items-center gap-2">
          <StandingGlyph standing={row.standing} />
          <span className="font-medium">{row.dimension.name}</span>
        </span>
      ),
    },
    { id: "kind", header: "Kind", fill: true, floor: 200, render: (row) => <KindText kind={row.dimension.kind} /> },
    { id: "path", header: "Path", render: (row) => <span className="font-mono text-[12px]">{row.dimension.path}</span> },
    { id: "values", header: "Values", align: "right", render: (row) => <span className="font-mono text-[12px] tabular-nums">{row.dimension.values.toLocaleString("en-US")}</span> },
    { id: "keys", header: "Keys", align: "right", render: (row) => <span className="font-mono text-[12px] tabular-nums">{row.dimension.keys.toLocaleString("en-US")}</span> },
    {
      id: "built",
      header: "Built",
      render: (row) => (row.dimension.lastBuiltUtc === null
        ? <span className="text-[12px] text-muted-foreground">{STANDING_VISUALS[row.standing].label.toLowerCase()}</span>
        : <span className="text-[12px]"><RelativeTime value={row.dimension.lastBuiltUtc} absolute={false} /></span>),
    },
    {
      id: "actions",
      header: "",
      align: "right",
      render: (row) => (
        <span className="flex items-center justify-end gap-1" onClick={(event) => event.stopPropagation()}>
          <DimensionRemoveButton dimension={row.dimension} flowName={flow.name} />
          <Button asChild variant="ghost" size="xs" data-testid={`dimension-panel-open-${row.dimension.name}`}>
            <Link to={`/delivery/dimensions?d=${encodeURIComponent(row.ref)}`}>
              Open
              <ArrowRight />
            </Link>
          </Button>
        </span>
      ),
    },
  ];

  return (
    <div className="flex flex-col gap-3" data-testid="dimension-panel">
      {flow.problem !== null && (
        <Alert variant="destructive" data-testid="dimension-panel-problem">
          <CircleAlert />
          <AlertDescription>{flow.problem}</AlertDescription>
        </Alert>
      )}
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-[13px] text-muted-foreground">
          {flow.partition === null ? "Built in the partition its data-partition-id header names." : <>In <span className="font-mono text-foreground">{flow.partition}</span>{flow.partitions.length > 1 ? `, one of ${flow.partitions.join(", ")}` : ""}.</>}
        </span>
      </div>
      <DataTable
        columns={columns}
        rows={declared}
        rowKey={(row) => row.ref}
        emptyMessage="The flow declares no dimension."
        data-testid="dimension-panel-table"
      />
      {retired.length > 0 && (
        <div className="flex flex-col gap-2" data-testid="dimension-panel-retired">
          <Button
            variant="ghost"
            size="sm"
            className="self-start text-muted-foreground"
            onClick={() => setShowRetired((shown) => !shown)}
            aria-expanded={showRetired}
            data-testid="dimension-panel-retired-toggle"
          >
            {showRetired ? <ChevronDown /> : <ChevronRight />}
            No longer declared ({retired.length})
          </Button>
          {showRetired && (
            <DataTable
              columns={columns}
              rows={retired}
              rowKey={(row) => row.ref}
              emptyMessage="Every dimension built here is declared."
              data-testid="dimension-panel-retired-table"
            />
          )}
        </div>
      )}
      <DimensionViewsSection pipelineId={pipelineId} dimensions={declared.map((entry) => entry.dimension.name)} />
    </div>
  );
}
