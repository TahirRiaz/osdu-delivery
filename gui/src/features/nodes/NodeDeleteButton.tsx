import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useSnackbar } from "notistack";
import Button from "@mui/material/Button";
import DeleteOutlineIcon from "@mui/icons-material/DeleteOutline";
import { isApiError } from "../../api/client";
import { nodeApi } from "../../api/endpoints";
import type { Node } from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { ConfirmDialog } from "../../components/ConfirmDialog";

/** Removes a dead node from the fleet registry. Offered only for offline nodes (a live node would just re-register
 *  on its next heartbeat) and only to operators. The 24h prune clears these automatically; this is the on-demand
 *  removal. */
export function NodeDeleteButton({ node }: { node: Node }) {
  const { hasScope } = useAuth();
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const [confirmOpen, setConfirmOpen] = useState(false);

  const remove = useMutation({
    mutationFn: () => nodeApi.delete(node.name),
    onSuccess: () => {
      enqueueSnackbar(`Removed '${node.name}' from the fleet.`, { variant: "success" });
      setConfirmOpen(false);
      void queryClient.invalidateQueries({ queryKey: ["nodes", "list"] });
    },
    onError: (error) => {
      enqueueSnackbar(isApiError(error) ? error.title : String(error), { variant: "error" });
      setConfirmOpen(false);
    },
  });

  // A live node re-registers on its next heartbeat, so deleting it is pointless; offer removal only for dead entries.
  if (node.online || !hasScope("operate")) {
    return null;
  }

  return (
    <>
      <Button
        size="small"
        variant="outlined"
        color="inherit"
        startIcon={<DeleteOutlineIcon />}
        disabled={remove.isPending}
        onClick={() => setConfirmOpen(true)}
        data-testid="node-delete-button"
      >
        Delete
      </Button>
      <ConfirmDialog
        open={confirmOpen}
        title={`Remove node '${node.name}'?`}
        message="This drops the dead node's entry from the fleet registry. It carries no state, so removal is safe; if the node is somehow still alive it re-registers on its next heartbeat."
        confirmLabel="Delete"
        danger
        busy={remove.isPending}
        onConfirm={() => remove.mutate()}
        onClose={() => setConfirmOpen(false)}
      />
    </>
  );
}
