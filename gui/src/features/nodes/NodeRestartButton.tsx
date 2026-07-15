import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useSnackbar } from "notistack";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import Tooltip from "@mui/material/Tooltip";
import RestartAltIcon from "@mui/icons-material/RestartAlt";
import { isApiError } from "../../api/client";
import { nodeApi } from "../../api/endpoints";
import type { Node } from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { ConfirmDialog } from "../../components/ConfirmDialog";

/** Restarts one node: a confirmation, then a request the worker honors on its next heartbeat (drain, then exit) so
 *  the orchestrator recreates it. A pending request shows as a chip instead of the button; the control is only
 *  offered to operators. */
export function NodeRestartButton({ node }: { node: Node }) {
  const { hasScope } = useAuth();
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const [confirmOpen, setConfirmOpen] = useState(false);

  const restart = useMutation({
    mutationFn: () => nodeApi.restart(node.name),
    onSuccess: () => {
      enqueueSnackbar(`Restart requested for '${node.name}'.`, { variant: "success" });
      setConfirmOpen(false);
      void queryClient.invalidateQueries({ queryKey: ["nodes", "list"] });
    },
    onError: (error) => {
      enqueueSnackbar(isApiError(error) ? error.title : String(error), { variant: "error" });
      setConfirmOpen(false);
    },
  });

  if (node.restartRequestedUtc !== null) {
    return (
      <Tooltip title="The node will drain its in-flight work and restart on its next heartbeat.">
        <Chip size="small" color="warning" variant="outlined" label="Restart pending" data-testid="node-restart-pending" />
      </Tooltip>
    );
  }

  if (!hasScope("operate")) {
    return null;
  }

  return (
    <>
      <Button
        size="small"
        variant="outlined"
        color="warning"
        startIcon={<RestartAltIcon />}
        disabled={!node.online || restart.isPending}
        onClick={() => setConfirmOpen(true)}
        data-testid="node-restart-button"
      >
        Restart
      </Button>
      <ConfirmDialog
        open={confirmOpen}
        title={`Restart node '${node.name}'?`}
        message="The node stops claiming, drains its in-flight runs, and exits; the orchestrator then recreates it. Runs in progress finish first, so this is safe to do at any time."
        confirmLabel="Restart"
        danger
        busy={restart.isPending}
        onConfirm={() => restart.mutate()}
        onClose={() => setConfirmOpen(false)}
      />
    </>
  );
}
