import { useMemo, useState } from "react";
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
import Typography from "@mui/material/Typography";
import ExpandMoreIcon from "@mui/icons-material/ExpandMore";
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
  const [fullLoad, setFullLoad] = useState(false);
  const [backfillFrom, setBackfillFrom] = useState("");
  const [backfillTo, setBackfillTo] = useState("");
  const [filePattern, setFilePattern] = useState("");

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

  // Client-side mirror of RunParameters.Validate, so the obvious mistakes are caught before the round trip
  // (the server validates authoritatively and its ProblemDetails still renders if anything slips through).
  const windowError = backfillTo.trim() !== "" && backfillFrom.trim() === ""
    ? "An end date needs a start date."
    : fullLoad && (backfillFrom.trim() !== "" || backfillTo.trim() !== "")
      ? "Full load and a backfill window are mutually exclusive."
      : null;
  const canSubmit = Boolean(effectiveRepoId) && Boolean(flowName ?? selectedFlow) && windowError === null && !trigger.isPending;

  const submit = () => {
    const trimmedFrom = backfillFrom.trim();
    const trimmedTo = backfillTo.trim();
    const trimmedPattern = filePattern.trim();
    trigger.mutate({
      repoId: effectiveRepoId!,
      flowName: (flowName ?? selectedFlow!).trim(),
      pool: pool.trim() === "" ? null : pool.trim(),
      commitSha: commitSha.trim() === "" ? null : commitSha.trim(),
      fullLoad,
      // datetime-local yields "YYYY-MM-DDTHH:mm"; treat it as UTC to match the API's UTC contract.
      backfillFrom: trimmedFrom === "" ? null : `${trimmedFrom}:00Z`,
      backfillTo: trimmedTo === "" ? null : `${trimmedTo}:00Z`,
      filePattern: trimmedPattern === "" ? null : trimmedPattern,
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
