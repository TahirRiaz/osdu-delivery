import { Layers } from "lucide-react";
import { Card } from "@/components/ui/card";

/**
 * What a flow's view shows when the partition picked in the title bar is not one the flow serves (docs/partitions-design.md
 * section 7): the title bar decides which partition every page is about, so the view says where the flow delivers and
 * leaves the switch to the title bar, rather than showing another partition's ledger under this one's name. With nothing
 * picked, it asks for a partition to be picked.
 */
export function OutsidePartition({ flowName, active, partitions, headerPartition = null, testId = "delivery-outside-partition" }: {
  flowName: string;
  /** The partition picked in the title bar, or null while none is. */
  active: string | null;
  /** The partitions the flow names or serves; empty for a flow whose partition is its header's. */
  partitions: readonly string[];
  /** The partition a flow whose partition is its header's is kept under, once it has run. */
  headerPartition?: string | null;
  testId?: string;
}) {
  const where = partitions.length > 0
    ? partitions.join(", ")
    : headerPartition;
  return (
    <Card className="flex-row items-start gap-3 rounded-lg p-4" data-testid={testId}>
      <Layers className="mt-0.5 size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
      <div className="flex flex-col gap-1 text-[13px]">
        {active === null
          ? <p className="font-medium">No partition is picked in the title bar.</p>
          : (
            <p className="font-medium">
              <span className="font-mono">{flowName}</span> does not deliver to <span className="font-mono">{active}</span>, the partition picked in
              the title bar.
            </p>
          )}
        {where !== null && (
          <p className="text-muted-foreground">
            It delivers to <span className="font-mono text-foreground">{where}</span>
            {partitions.length === 0 ? ", the partition its data-partition-id header names" : ""}. Pick
            {partitions.length > 1 ? " one of them" : " it"} in the title bar to see this flow there and act on it.
          </p>
        )}
      </div>
    </Card>
  );
}
