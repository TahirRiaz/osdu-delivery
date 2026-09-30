import { TriggerRunDialog } from "@/features/runs/TriggerRunDialog";
import { dimensionsPayload } from "./dimensionFormat";

/** A build of a dimension flow to launch: the flow, the dimensions it builds (none builds every one), and the operation. */
export interface DimensionLaunch {
  pipelineId: string;
  repoId: string;
  flowName: string;
  dimensions: readonly string[];
  operation?: "build" | "plan";
}

/**
 * Launches a build of a dimension flow through the platform's one trigger dialog, opened on the dimensions asked for: the
 * dialog shows them picked, and the operator can change the pick, the parameters and the operation (a plan settles each
 * field and counts the records without reading a value) before it runs.
 */
export function DimensionBuildDialog({ launch, onClose }: { launch: DimensionLaunch | null; onClose: () => void }) {
  if (launch === null) {
    return null;
  }

  const payload = dimensionsPayload(launch.dimensions);
  return (
    <TriggerRunDialog
      open
      onClose={onClose}
      repoId={launch.repoId}
      flowName={launch.flowName}
      flowId={launch.pipelineId}
      flowKind="dimension"
      initialOperation={launch.operation ?? "build"}
      initialValues={null}
      initialPayload={Object.keys(payload).length > 0 ? payload : null}
    />
  );
}
