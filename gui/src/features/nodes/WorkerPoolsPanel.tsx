import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useSnackbar } from "notistack";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Chip from "@mui/material/Chip";
import FormControlLabel from "@mui/material/FormControlLabel";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import Switch from "@mui/material/Switch";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import TextField from "@mui/material/TextField";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";
import BoltIcon from "@mui/icons-material/Bolt";
import { isApiError } from "../../api/client";
import { nodeApi } from "../../api/endpoints";
import type { WorkerPool, WorkerPoolScaleRequest } from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { RelativeTime } from "../../components/RelativeTime";

const DEFAULT_WINDOW_MINUTES = 60;
const SPAWN_WINDOW_MINUTES = 30;

const poolLabel = (pool: string) => (pool.length === 0 ? "default (untargeted)" : pool);

/** Per-pool desired-compute controls: the always-on floor, a bounded manual scale-up, and a one-click spawn, all of
 *  which the control plane applies by writing a catalog row the autoscaler reads (no orchestrator calls). */
export function WorkerPoolsPanel() {
  const { hasScope } = useAuth();
  const canOperate = hasScope("operate");
  const query = useQuery({
    queryKey: ["nodes", "pools"],
    queryFn: () => nodeApi.listPools(),
    refetchInterval: 5000,
  });

  return (
    <Card variant="outlined" data-testid="worker-pools-panel">
      <CardContent>
        <Typography variant="h6" gutterBottom>Worker pools</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          The autoscaler runs each pool at the greatest of its queued work, an always-on floor, and any active manual
          override. Keep a worker warm, scale a pool up for a window, or spawn one when the pool is at zero.
        </Typography>

        {query.isLoading ? (
          <Skeleton variant="rounded" height={160} />
        ) : query.isError ? (
          <Typography color="error">
            {isApiError(query.error) ? query.error.title : String(query.error)}
          </Typography>
        ) : (
          <Box sx={{ overflowX: "auto" }}>
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell>Pool</TableCell>
                  <TableCell align="right">Queued</TableCell>
                  <TableCell align="right">Target</TableCell>
                  <TableCell>State</TableCell>
                  {canOperate && <TableCell>Controls</TableCell>}
                </TableRow>
              </TableHead>
              <TableBody>
                {(query.data ?? []).map((pool) => (
                  <WorkerPoolRow key={pool.pool} pool={pool} canOperate={canOperate} />
                ))}
              </TableBody>
            </Table>
          </Box>
        )}
      </CardContent>
    </Card>
  );
}

function WorkerPoolRow({ pool, canOperate }: { pool: WorkerPool; canOperate: boolean }) {
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const [scaleTo, setScaleTo] = useState("");
  const [minutes, setMinutes] = useState(String(DEFAULT_WINDOW_MINUTES));

  const scale = useMutation({
    mutationFn: (request: WorkerPoolScaleRequest) => nodeApi.scalePool(request),
    onSuccess: (result, request) => {
      enqueueSnackbar(scaleMessage(request, result), { variant: "success" });
      void queryClient.invalidateQueries({ queryKey: ["nodes", "pools"] });
      void queryClient.invalidateQueries({ queryKey: ["nodes", "list"] });
    },
    onError: (error) => {
      enqueueSnackbar(isApiError(error) ? error.title : String(error), { variant: "error" });
    },
  });

  const windowMinutes = () => {
    const parsed = Number.parseInt(minutes, 10);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : DEFAULT_WINDOW_MINUTES;
  };

  const applyScale = () => {
    const target = Number.parseInt(scaleTo, 10);
    if (!Number.isFinite(target) || target < 0) {
      enqueueSnackbar("Enter a replica count of 0 or more.", { variant: "warning" });
      return;
    }
    scale.mutate({ pool: pool.pool, manualReplicas: target, manualForMinutes: windowMinutes() });
    setScaleTo("");
  };

  return (
    <TableRow data-testid={`worker-pool-row-${pool.pool || "default"}`}>
      <TableCell>
        <Typography variant="body2" sx={{ fontWeight: 600 }}>{poolLabel(pool.pool)}</Typography>
        {pool.updatedUtc !== null && (
          <Typography variant="caption" color="text.secondary">
            updated <RelativeTime value={pool.updatedUtc} />{pool.updatedBy !== null ? ` by ${pool.updatedBy}` : ""}
          </Typography>
        )}
      </TableCell>
      <TableCell align="right">{pool.queuedRuns}</TableCell>
      <TableCell align="right">
        <Typography variant="body2" sx={{ fontWeight: 600 }}>{pool.replicaTarget}</Typography>
      </TableCell>
      <TableCell>
        <Stack direction="row" spacing={0.5} sx={{ flexWrap: "wrap", gap: 0.5 }}>
          {pool.minReplicas > 0 && (
            <Chip size="small" color="success" variant="outlined" label={`always-on ${pool.minReplicas}`} />
          )}
          {pool.manualActive && (
            <Tooltip title={<>reverts <RelativeTime value={pool.manualUntilUtc ?? ""} /></>}>
              <Chip size="small" color="info" variant="outlined" label={`manual ${pool.manualReplicas}`} />
            </Tooltip>
          )}
          {pool.minReplicas === 0 && !pool.manualActive && (
            <Chip size="small" variant="outlined" label="autoscale" />
          )}
        </Stack>
      </TableCell>
      {canOperate && (
        <TableCell>
          <Stack direction="row" spacing={1} sx={{ alignItems: "center", flexWrap: "wrap", gap: 1 }}>
            <Tooltip title="Keep at least one worker warm in this pool at all times.">
              <FormControlLabel
                sx={{ mr: 0 }}
                control={
                  <Switch
                    size="small"
                    checked={pool.minReplicas > 0}
                    disabled={scale.isPending}
                    onChange={(event) =>
                      scale.mutate({ pool: pool.pool, minReplicas: event.target.checked ? 1 : 0 })
                    }
                    data-testid={`worker-pool-alwayson-${pool.pool || "default"}`}
                  />
                }
                label={<Typography variant="caption">Always&nbsp;on</Typography>}
              />
            </Tooltip>
            <TextField
              size="small"
              label="Scale to"
              value={scaleTo}
              onChange={(event) => setScaleTo(event.target.value.replace(/[^0-9]/g, ""))}
              sx={{ width: 88 }}
              inputProps={{ inputMode: "numeric", "data-testid": `worker-pool-scale-input-${pool.pool || "default"}` }}
            />
            <TextField
              size="small"
              label="for min"
              value={minutes}
              onChange={(event) => setMinutes(event.target.value.replace(/[^0-9]/g, ""))}
              sx={{ width: 80 }}
              inputProps={{ inputMode: "numeric" }}
            />
            <Button
              size="small"
              variant="outlined"
              disabled={scale.isPending || scaleTo.length === 0}
              onClick={applyScale}
              data-testid={`worker-pool-scale-apply-${pool.pool || "default"}`}
            >
              Apply
            </Button>
            <Button
              size="small"
              variant="contained"
              startIcon={<BoltIcon />}
              disabled={scale.isPending}
              onClick={() =>
                scale.mutate({ pool: pool.pool, manualReplicas: Math.max(1, pool.replicaTarget + 1), manualForMinutes: SPAWN_WINDOW_MINUTES })
              }
              data-testid={`worker-pool-spawn-${pool.pool || "default"}`}
            >
              Spawn
            </Button>
            {pool.manualActive && (
              <Button
                size="small"
                color="inherit"
                disabled={scale.isPending}
                onClick={() => scale.mutate({ pool: pool.pool, manualReplicas: 0 })}
                data-testid={`worker-pool-stop-${pool.pool || "default"}`}
              >
                Stop override
              </Button>
            )}
          </Stack>
        </TableCell>
      )}
    </TableRow>
  );
}

function scaleMessage(request: WorkerPoolScaleRequest, pool: WorkerPool): string {
  const name = poolLabel(pool.pool);
  if (request.minReplicas !== undefined && request.manualReplicas === undefined) {
    return request.minReplicas > 0
      ? `Keeping at least ${request.minReplicas} worker warm in ${name}.`
      : `Always-on disabled for ${name}; it will scale to zero when idle.`;
  }
  if (request.manualReplicas === 0) {
    return `Manual override cleared for ${name}.`;
  }
  return `${name} will run ${pool.replicaTarget} worker(s); the override reverts automatically.`;
}
