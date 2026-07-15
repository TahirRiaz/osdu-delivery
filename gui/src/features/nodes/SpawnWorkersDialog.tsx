import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useSnackbar } from "notistack";
import Button from "@mui/material/Button";
import Dialog from "@mui/material/Dialog";
import DialogActions from "@mui/material/DialogActions";
import DialogContent from "@mui/material/DialogContent";
import DialogContentText from "@mui/material/DialogContentText";
import DialogTitle from "@mui/material/DialogTitle";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import { isApiError } from "../../api/client";
import { nodeApi } from "../../api/endpoints";
import type { WorkerPool } from "../../api/types";

const poolLabel = (pool: string) => (pool.length === 0 ? "the default pool" : `pool '${pool}'`);

/** Starts workers on demand: pick how many and for how long. Defaults to one worker for 30 minutes (the common
 *  "spawn a worker now" case); a higher count is a temporary scale-up. After the window the pool returns to
 *  autoscaling on its own, so nothing stays warm by accident. */
export function SpawnWorkersDialog({ pool, open, onClose }: { pool: WorkerPool; open: boolean; onClose: () => void }) {
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const [count, setCount] = useState("1");
  const [minutes, setMinutes] = useState("30");

  const spawn = useMutation({
    mutationFn: (input: { count: number; minutes: number }) =>
      nodeApi.scalePool({ pool: pool.pool, manualReplicas: input.count, manualForMinutes: input.minutes }),
    onSuccess: (_result, input) => {
      enqueueSnackbar(
        `Starting ${input.count} worker${input.count === 1 ? "" : "s"} in ${poolLabel(pool.pool)} for ${input.minutes} min.`,
        { variant: "success" },
      );
      void queryClient.invalidateQueries({ queryKey: ["nodes", "pools"] });
      void queryClient.invalidateQueries({ queryKey: ["nodes", "list"] });
      onClose();
    },
    onError: (error) => {
      enqueueSnackbar(isApiError(error) ? error.title : String(error), { variant: "error" });
    },
  });

  const parsedCount = Number.parseInt(count, 10);
  const parsedMinutes = Number.parseInt(minutes, 10);
  const valid = Number.isFinite(parsedCount) && parsedCount >= 1 && Number.isFinite(parsedMinutes) && parsedMinutes >= 1;

  return (
    <Dialog open={open} onClose={spawn.isPending ? undefined : onClose} maxWidth="xs" fullWidth data-testid="spawn-dialog">
      <DialogTitle>Spawn workers</DialogTitle>
      <DialogContent>
        <DialogContentText sx={{ mb: 2 }}>
          Start workers in {poolLabel(pool.pool)} now, even if nothing is queued. They run for the window you set, then
          the pool goes back to autoscaling on its own.
        </DialogContentText>
        <Stack direction="row" spacing={2}>
          <TextField
            label="Workers"
            value={count}
            onChange={(event) => setCount(event.target.value.replace(/[^0-9]/g, ""))}
            fullWidth
            inputProps={{ inputMode: "numeric", "data-testid": "spawn-count" }}
          />
          <TextField
            label="For (minutes)"
            value={minutes}
            onChange={(event) => setMinutes(event.target.value.replace(/[^0-9]/g, ""))}
            fullWidth
            inputProps={{ inputMode: "numeric", "data-testid": "spawn-minutes" }}
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={spawn.isPending}>Cancel</Button>
        <Button
          variant="contained"
          disabled={!valid || spawn.isPending}
          onClick={() => spawn.mutate({ count: parsedCount, minutes: parsedMinutes })}
          data-testid="spawn-confirm"
        >
          {valid && parsedCount === 1 ? "Spawn 1 worker" : valid ? `Spawn ${parsedCount} workers` : "Spawn"}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
