import { useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useSnackbar } from "notistack";
import Autocomplete from "@mui/material/Autocomplete";
import Button from "@mui/material/Button";
import Dialog from "@mui/material/Dialog";
import DialogActions from "@mui/material/DialogActions";
import DialogContent from "@mui/material/DialogContent";
import DialogTitle from "@mui/material/DialogTitle";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import { isApiError } from "../../api/client";
import { pipelineApi, repoApi, runApi } from "../../api/endpoints";
import { CorrelationError } from "../../components/CorrelationError";

export interface TriggerRunDialogProps {
  open: boolean;
  onClose: () => void;
  /** Prefills (and locks) the repo when launched from a repo/pipeline context. */
  repoId?: string;
  /** Prefills (and locks) the flow when launched from a pipeline detail page. */
  flowName?: string;
}

/**
 * The single trigger-run path in the GUI: launched from the runs page (free choice of repo + flow) and from a
 * pipeline's detail page (prefilled). POSTs /runs and navigates to the accepted run, where polling takes over.
 */
export function TriggerRunDialog({ open, onClose, repoId, flowName }: TriggerRunDialogProps) {
  const navigate = useNavigate();
  const { enqueueSnackbar } = useSnackbar();
  const [selectedRepoId, setSelectedRepoId] = useState<string | null>(repoId ?? null);
  const [selectedFlow, setSelectedFlow] = useState<string | null>(flowName ?? null);
  const [pool, setPool] = useState("");
  const [commitSha, setCommitSha] = useState("");

  const repos = useQuery({
    queryKey: ["repos", "all-for-trigger"],
    queryFn: () => repoApi.list({ page: 1, pageSize: 200 }),
    enabled: open && !repoId,
  });

  const effectiveRepoId = repoId ?? selectedRepoId;
  const pipelines = useQuery({
    queryKey: ["pipelines", "for-trigger", effectiveRepoId],
    queryFn: () => pipelineApi.list({ repoId: effectiveRepoId!, active: true, page: 1, pageSize: 200 }),
    enabled: open && !flowName && Boolean(effectiveRepoId),
  });

  const trigger = useMutation({
    mutationFn: runApi.trigger,
    onSuccess: (accepted) => {
      enqueueSnackbar(`Run queued (${accepted.runId}).`, { variant: "success" });
      onClose();
      navigate(`/runs/${accepted.runId}`);
    },
  });

  const repoOptions = useMemo(() => repos.data?.items ?? [], [repos.data]);
  const flowOptions = useMemo(() => pipelines.data?.items.map((p) => p.name) ?? [], [pipelines.data]);
  const canSubmit = Boolean(effectiveRepoId) && Boolean(flowName ?? selectedFlow) && !trigger.isPending;

  const submit = () => {
    trigger.mutate({
      repoId: effectiveRepoId!,
      flowName: (flowName ?? selectedFlow!).trim(),
      pool: pool.trim() === "" ? null : pool.trim(),
      commitSha: commitSha.trim() === "" ? null : commitSha.trim(),
    });
  };

  return (
    <Dialog open={open} onClose={trigger.isPending ? undefined : onClose} fullWidth maxWidth="sm" data-testid="trigger-run-dialog">
      <DialogTitle>Trigger run</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          {trigger.isError && isApiError(trigger.error) && <CorrelationError error={trigger.error} />}

          {repoId ? null : (
            <Autocomplete
              options={repoOptions}
              getOptionLabel={(repo) => repo.name}
              value={repoOptions.find((r) => r.id === selectedRepoId) ?? null}
              onChange={(_, repo) => {
                setSelectedRepoId(repo?.id ?? null);
                setSelectedFlow(null);
              }}
              loading={repos.isLoading}
              renderInput={(params) => (
                <TextField {...params} label="Repo" inputProps={{ ...params.inputProps, "data-testid": "trigger-repo" }} />
              )}
            />
          )}

          {flowName ? (
            <TextField label="Flow" value={flowName} disabled fullWidth />
          ) : (
            <Autocomplete
              options={flowOptions}
              value={selectedFlow}
              onChange={(_, value) => setSelectedFlow(value)}
              disabled={!effectiveRepoId}
              loading={pipelines.isLoading}
              renderInput={(params) => (
                <TextField {...params} label="Flow" inputProps={{ ...params.inputProps, "data-testid": "trigger-flow" }} />
              )}
            />
          )}

          <TextField
            label="Pool (optional)"
            helperText="Route the run to workers serving this pool; empty runs on any node."
            value={pool}
            onChange={(e) => setPool(e.target.value)}
            inputProps={{ "data-testid": "trigger-pool" }}
          />
          <TextField
            label="Commit SHA (optional)"
            helperText="Pin the run to an exact git commit; empty pins to the repo's last synced commit."
            value={commitSha}
            onChange={(e) => setCommitSha(e.target.value)}
            inputProps={{ "data-testid": "trigger-commit" }}
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={trigger.isPending}>Cancel</Button>
        <Button variant="contained" onClick={submit} disabled={!canSubmit} data-testid="trigger-submit">
          Trigger
        </Button>
      </DialogActions>
    </Dialog>
  );
}
