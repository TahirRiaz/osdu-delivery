import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { useAuth } from "@/auth/AuthContext";
import { ConfirmDialog } from "@/components/ConfirmDialog";
import { RichTooltip } from "@/components/RichTooltip";
import { deliveryApi, type DeliveryDimensionView } from "../../../api/delivery";
import { problemText } from "../problemText";
import { viewObject } from "./dimensionViewFormat";

/**
 * Removes a view no flow declares any more, for good: offered to an admin alone, for a view a build wrote and no active
 * flow declares, after a confirmation that says what goes. The control plane refuses one its flow still declares, or whose
 * flow cannot be read, and says why; every removal is an activity of the flow. In a list it is its glyph alone, named on
 * hover (`iconOnly`).
 */
export function DimensionViewRemoveButton({ view, size = "xs", iconOnly = false, onRemoved }: {
  view: DeliveryDimensionView;
  size?: "xs" | "sm";
  iconOnly?: boolean;
  /** Called once the view is gone, so a page showing it can move on. */
  onRemoved?: () => void;
}) {
  const { hasScope } = useAuth();
  const queryClient = useQueryClient();
  const [confirming, setConfirming] = useState(false);
  const remove = useMutation({
    mutationFn: (name: string) => deliveryApi.removeDimensionView(name),
    onSuccess: (removed) => {
      toast.success(removed.summary);
      setConfirming(false);
      void queryClient.invalidateQueries({ queryKey: ["delivery", "dimensions"] });
      onRemoved?.();
    },
    onError: (error) => toast.error(problemText(error)),
  });

  if (!hasScope("admin") || view.declared || !view.recorded) {
    return null;
  }

  const open = (event: { stopPropagation: () => void }) => { event.stopPropagation(); setConfirming(true); };
  return (
    <>
      {iconOnly
        ? (
          <RichTooltip body={`Remove ${view.name} for good: no active flow declares it.`}>
            <Button
              variant="ghost"
              size="icon-xs"
              className="text-destructive hover:text-destructive"
              aria-label={`Remove ${view.name}`}
              onClick={open}
              data-testid={`dimension-view-remove-${view.name}`}
            >
              <Trash2 />
            </Button>
          </RichTooltip>
        )
        : (
          <Button
            variant="ghost"
            size={size}
            className="text-destructive hover:text-destructive"
            onClick={open}
            data-testid={`dimension-view-remove-${view.name}`}
          >
            <Trash2 />
            Remove
          </Button>
        )}
      <ConfirmDialog
        open={confirming}
        title={`Remove view ${view.name} for good?`}
        message={`No active flow declares ${view.name}. Removing it drops ${viewObject(view)} from the database when it is there, and deletes its record and every check of it. A pipeline that still reads the view fails from then on. It cannot be undone; the removal is recorded with your name.`}
        confirmLabel="Remove"
        danger
        busy={remove.isPending}
        onConfirm={() => remove.mutate(view.name)}
        onClose={() => setConfirming(false)}
      />
    </>
  );
}
