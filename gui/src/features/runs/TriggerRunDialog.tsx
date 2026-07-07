import { useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useSnackbar } from "notistack";
import Accordion from "@mui/material/Accordion";
import AccordionDetails from "@mui/material/AccordionDetails";
import AccordionSummary from "@mui/material/AccordionSummary";
import Alert from "@mui/material/Alert";
import Autocomplete from "@mui/material/Autocomplete";
import Button from "@mui/material/Button";
import Dialog from "@mui/material/Dialog";
import DialogActions from "@mui/material/DialogActions";
import DialogContent from "@mui/material/DialogContent";
import DialogTitle from "@mui/material/DialogTitle";
import FormControlLabel from "@mui/material/FormControlLabel";
import Stack from "@mui/material/Stack";
import Switch from "@mui/material/Switch";
import TextField from "@mui/material/TextField";
import ToggleButton from "@mui/material/ToggleButton";
import ToggleButtonGroup from "@mui/material/ToggleButtonGroup";
import Typography from "@mui/material/Typography";
import ExpandMoreIcon from "@mui/icons-material/ExpandMore";
import { isApiError } from "../../api/client";
import { pipelineApi, repoApi, runApi } from "../../api/endpoints";
import type { RunScope } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";

export interface TriggerRunDialogProps {
  open: boolean;
  onClose: () => void;
  /** Prefills (and locks) the repo when launched from a repo/pipeline context. */
  repoId?: string;
  /** Prefills (and locks) the flow when launched from a pipeline detail page or a lineage node. */
  flowName?: string;
  /** The initial execution scope (defaults to "flow"). Set by the lineage graph's Run / Run + descendants / Run
   * batch actions and by the batch status board. */
  scope?: RunScope;
  /** The batch label for a batch-scoped launch (from the status board); locks the dialog to that batch. */
  batch?: string;
}

const SCOPE_LABELS: Record<RunScope, string> = {
  flow: "This flow",
  node: "This flow + descendants",
  batch: "Whole batch",
};

/**
 * The single trigger-run path in the GUI: launched from the runs page (free choice of repo + flow), a pipeline's
 * detail page (prefilled), and the lineage graph / batch board (prefilled with a scope). A "flow" run POSTs one flow
 * and navigates to it; a "node" (flow + descendants) or "batch" run POSTs a group and navigates to the group view.
 */
export function TriggerRunDialog({ open, onClose, repoId, flowName, scope, batch }: TriggerRunDialogProps) {
  const navigate = useNavigate();
  const { enqueueSnackbar } = useSnackbar();
  const [selectedRepoId, setSelectedRepoId] = useState<string | null>(repoId ?? null);
  const [selectedFlow, setSelectedFlow] = useState<string | null>(flowName ?? null);
  const [selectedScope, setSelectedScope] = useState<RunScope>(scope ?? "flow");
  const [pool, setPool] = useState("");
  const [commitSha, setCommitSha] = useState("");
  const [fullLoad, setFullLoad] = useState(false);
  const [backfillFrom, setBackfillFrom] = useState("");
  const [backfillTo, setBackfillTo] = useState("");
  const [filePattern, setFilePattern] = useState("");

  // A batch-locked launch (from the status board) carries no flow: force batch scope and keep it there.
  const batchLocked = batch !== undefined;
  useEffect(() => {
    if (open) {
      setSelectedScope(batchLocked ? "batch" : scope ?? "flow");
    }
  }, [open, scope, batchLocked]);

  const repos = useQuery({
    queryKey: ["repos", "all-for-trigger"],
    queryFn: () => repoApi.list({ page: 1, pageSize: 200 }),
    enabled: open && !repoId,
  });

  const effectiveRepoId = repoId ?? selectedRepoId;
  const effectiveFlow = flowName ?? selectedFlow;
  const pipelines = useQuery({
    queryKey: ["pipelines", "for-trigger", effectiveRepoId],
    queryFn: () => pipelineApi.list({ repoId: effectiveRepoId!, active: true, page: 1, pageSize: 200 }),
    enabled: open && !flowName && !batchLocked && Boolean(effectiveRepoId),
  });

  // For a Node or Batch scope, preview which flows the run would touch (the same expansion the trigger uses), so the
  // operator sees "will run N flows across M waves" before committing. A single flow needs no preview.
  const previewEnabled = open
    && selectedScope !== "flow"
    && Boolean(effectiveRepoId)
    && (batchLocked ? Boolean(batch) : Boolean(effectiveFlow));
  const preview = useQuery({
    queryKey: ["run-scope-preview", effectiveRepoId, effectiveFlow, selectedScope, batch],
    queryFn: () => runApi.previewScope({
      repoId: effectiveRepoId!,
      flowName: batchLocked ? undefined : effectiveFlow ?? undefined,
      scope: selectedScope,
      batch: batchLocked ? batch : undefined,
    }),
    enabled: previewEnabled,
  });

  const trigger = useMutation({
    mutationFn: runApi.trigger,
    onSuccess: (accepted) => {
      onClose();
      if (accepted.groupId) {
        enqueueSnackbar(`Run group queued (${accepted.memberCount} flows).`, { variant: "success" });
        navigate(`/runs/groups/${accepted.groupId}`);
      } else {
        enqueueSnackbar(`Run queued (${accepted.runId}).`, { variant: "success" });
        navigate(`/runs/${accepted.runId}`);
      }
    },
  });

  const repoOptions = useMemo(() => repos.data?.items ?? [], [repos.data]);
  const flowOptions = useMemo(() => pipelines.data?.items.map((p) => p.name) ?? [], [pipelines.data]);

  const isGroup = selectedScope !== "flow";

  // Client-side mirror of RunParameters.Validate (single-flow only), so obvious mistakes are caught before the round
  // trip (the server validates authoritatively and its ProblemDetails still renders if anything slips through).
  const windowError = isGroup
    ? null
    : backfillTo.trim() !== "" && backfillFrom.trim() === ""
      ? "An end date needs a start date."
      : fullLoad && (backfillFrom.trim() !== "" || backfillTo.trim() !== "")
        ? "Full load and a backfill window are mutually exclusive."
        : null;

  const hasTarget = batchLocked
    ? Boolean(batch)
    : selectedScope === "batch"
      ? Boolean(effectiveFlow)
      : Boolean(effectiveFlow);
  // A group run stays disabled until the preview confirms there is at least one flow to run.
  const groupReady = !isGroup || (preview.data !== undefined && preview.data.memberCount > 0);
  const canSubmit = Boolean(effectiveRepoId) && hasTarget && windowError === null && groupReady && !trigger.isPending;

  const submit = () => {
    const trimmedFrom = backfillFrom.trim();
    const trimmedTo = backfillTo.trim();
    const trimmedPattern = filePattern.trim();
    trigger.mutate({
      repoId: effectiveRepoId!,
      flowName: batchLocked ? "" : (effectiveFlow ?? "").trim(),
      scope: selectedScope,
      batch: batchLocked ? batch : null,
      pool: pool.trim() === "" ? null : pool.trim(),
      commitSha: commitSha.trim() === "" ? null : commitSha.trim(),
      // Backfill is single-flow only; a group always runs default parameters.
      fullLoad: isGroup ? false : fullLoad,
      backfillFrom: isGroup || trimmedFrom === "" ? null : `${trimmedFrom}:00Z`,
      backfillTo: isGroup || trimmedTo === "" ? null : `${trimmedTo}:00Z`,
      filePattern: isGroup || trimmedPattern === "" ? null : trimmedPattern,
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

          {batchLocked ? (
            <TextField label="Batch" value={batch} disabled fullWidth data-testid="trigger-batch" />
          ) : flowName ? (
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

          {batchLocked ? null : (
            <Stack spacing={0.5}>
              <Typography variant="body2" color="text.secondary">Scope</Typography>
              <ToggleButtonGroup
                exclusive
                size="small"
                value={selectedScope}
                onChange={(_, value: RunScope | null) => value && setSelectedScope(value)}
                data-testid="trigger-scope"
              >
                {(Object.keys(SCOPE_LABELS) as RunScope[]).map((s) => (
                  <ToggleButton key={s} value={s} data-testid={`trigger-scope-${s}`}>
                    {SCOPE_LABELS[s]}
                  </ToggleButton>
                ))}
              </ToggleButtonGroup>
            </Stack>
          )}

          {isGroup && (
            <Alert severity="info" data-testid="trigger-scope-preview">
              {preview.isLoading
                ? "Resolving the flows to run..."
                : preview.isError
                  ? "Could not resolve the flows to run."
                  : preview.data && preview.data.memberCount > 0
                    ? `Will run ${preview.data.memberCount} ${preview.data.memberCount === 1 ? "flow" : "flows"} across ${preview.data.waveCount} ${preview.data.waveCount === 1 ? "wave" : "waves"}, in dependency order`
                      + (selectedScope === "batch" ? ` (batch "${preview.data.anchor}").` : ".")
                    : "No flows to run for this selection."}
            </Alert>
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

          {isGroup ? null : (
            <Accordion
              disableGutters
              elevation={0}
              sx={{ "&:before": { display: "none" }, bgcolor: "transparent" }}
            >
              <AccordionSummary expandIcon={<ExpandMoreIcon />} sx={{ px: 0 }} data-testid="trigger-backfill-expander">
                <Typography variant="body2" color="text.secondary">Backfill (advanced)</Typography>
              </AccordionSummary>
              <AccordionDetails sx={{ px: 0 }}>
                <Stack spacing={2}>
                  <Typography variant="caption" color="text.secondary">
                    Reprocess history for this run only, without editing the flow in git. A window bounds file dates
                    (file flows) or the incremental date column (ingestion), and re-windows an export or init-load
                    chunk plan. Keyed targets upsert, so a reload is idempotent; a keyless append will duplicate.
                  </Typography>
                  <FormControlLabel
                    control={(
                      <Switch
                        checked={fullLoad}
                        onChange={(e) => setFullLoad(e.target.checked)}
                        inputProps={{ "data-testid": "trigger-full-load" } as React.InputHTMLAttributes<HTMLInputElement>}
                      />
                    )}
                    label="Full load (ignore the watermark, read everything)"
                  />
                  <Stack direction={{ xs: "column", sm: "row" }} spacing={2}>
                    <TextField
                      label="Backfill from"
                      type="datetime-local"
                      value={backfillFrom}
                      onChange={(e) => setBackfillFrom(e.target.value)}
                      disabled={fullLoad}
                      InputLabelProps={{ shrink: true }}
                      inputProps={{ "data-testid": "trigger-backfill-from" }}
                      fullWidth
                    />
                    <TextField
                      label="Backfill to"
                      type="datetime-local"
                      value={backfillTo}
                      onChange={(e) => setBackfillTo(e.target.value)}
                      disabled={fullLoad}
                      InputLabelProps={{ shrink: true }}
                      inputProps={{ "data-testid": "trigger-backfill-to" }}
                      fullWidth
                    />
                  </Stack>
                  <TextField
                    label="File pattern (file flows)"
                    placeholder="orders_2023-01*.csv"
                    helperText="Narrow which files this run reads; ignored by non-file flows."
                    value={filePattern}
                    onChange={(e) => setFilePattern(e.target.value)}
                    inputProps={{ "data-testid": "trigger-file-pattern" }}
                  />
                  {windowError !== null && <Alert severity="warning" data-testid="trigger-backfill-error">{windowError}</Alert>}
                </Stack>
              </AccordionDetails>
            </Accordion>
          )}
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
