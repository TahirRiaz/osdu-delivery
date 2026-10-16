import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { useAuth } from "@/auth/AuthContext";
import { ConfirmDialog } from "@/components/ConfirmDialog";
import { deliveryApi, type DeliveryDimension } from "../../../api/delivery";
import { counted } from "../assertions/assertionFormat";
import { problemText } from "../problemText";

/**
 * Removes a dimension its flow no longer declares, and everything kept of it in its partition, for good: offered to an
 * admin alone, for a dimension that was built and that its flow no longer declares, after a confirmation that says what
 * goes. The control plane refuses one a cache flow still captures and says why; every removal is an activity of the flow.
 */
export function DimensionRemoveButton({ dimension, flowName, size = "xs", onRemoved }: {
  dimension: DeliveryDimension;
  flowName: string;
  size?: "xs" | "sm";
  /** Called once the dimension is gone, so a page showing it can move on. */
  onRemoved?: () => void;
}) {
  const { hasScope } = useAuth();
  const queryClient = useQueryClient();
  const [confirming, setConfirming] = useState(false);
  const remove = useMutation({
    mutationFn: (dimensionId: number) => deliveryApi.removeDimension(dimensionId),
    onSuccess: (removed) => {
      toast.success(removed.summary);
      setConfirming(false);
      void queryClient.invalidateQueries({ queryKey: ["delivery", "dimensions"] });
      onRemoved?.();
    },
    onError: (error) => toast.error(problemText(error)),
  });

  if (!hasScope("admin") || dimension.declared || dimension.dimensionId === null) {
    return null;
  }

  const dimensionId = dimension.dimensionId;
  return (
    <>
      <Button
        variant="ghost"
        size={size}
        className="text-destructive hover:text-destructive"
        onClick={() => setConfirming(true)}
        data-testid={`dimension-remove-${dimension.name}`}
      >
        <Trash2 />
        Remove
      </Button>
      <ConfirmDialog
        open={confirming}
        title={`Remove ${dimension.name} for good?`}
        message={`${flowName} no longer declares ${dimension.name}. Removing it deletes ${counted(dimension.values, "value")}, ${counted(dimension.keys, "key")}, their attributes, its change log and every build of it in this partition. It cannot be undone; the removal is recorded with your name.`}
        confirmLabel="Remove"
        danger
        busy={remove.isPending}
        onConfirm={() => remove.mutate(dimensionId)}
        onClose={() => setConfirming(false)}
      />
    </>
  );
}
