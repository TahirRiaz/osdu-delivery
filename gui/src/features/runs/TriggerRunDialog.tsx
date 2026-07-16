import { useEffect, useMemo, useState } from "react";
import type React from "react";
import { useNavigate } from "react-router-dom";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useSnackbar } from "notistack";
import Alert from "@mui/material/Alert";
import Autocomplete from "@mui/material/Autocomplete";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import CircularProgress from "@mui/material/CircularProgress";
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
import { isApiError } from "../../api/client";
import { pipelineApi, repoApi, runApi } from "../../api/endpoints";
import type { RunParameterDescriptor, RunScope } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";

/** Prior-run values used to prefill the form on Re-run (ISO strings for the window; they are trimmed to the
 * minute for the datetime-local inputs). Absent fields default to empty/off. */
export interface TriggerRunParameterValues {
  fullLoad?: boolean;
  backfillFrom?: string | null;
  backfillTo?: string | null;
  filePattern?: string | null;
  assertionsOnly?: boolean;
}

export interface TriggerRunDialogProps {
  open: boolean;
  onClose: () => void;
  /** Prefills (and locks) the repo when launched from a repo/pipeline context. */
  repoId?: string;
  /** Prefills (and locks) the flow when launched from a pipeline detail page or a lineage node. */
  flowName?: string;
  /** The flow's pipeline id, when the launching context knows it (pipeline detail, Re-run). Lets the dialog load
   * the flow's applicable parameters without first resolving the id from the repo's pipeline list. */
  flowId?: string;
  /** Prior-run parameter values to prefill (Re-run). */
  initialParameters?: TriggerRunParameterValues;
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

/** An ISO instant (or datetime-local string) trimmed to the "yyyy-MM-ddThh:mm" a datetime-local input expects. */
function toLocalInput(value: string | null | undefined): string {
  return value ? value.slice(0, 16) : "";
}

/**
 * The single trigger-run path in the GUI: launched from the runs page (free choice of repo + flow), a pipeline's
 * detail page (prefilled), the lineage graph / batch board (prefilled with a scope), and a run's Re-run (prefilled
 * with the prior parameters). A "flow" run POSTs one flow and navigates to it; a "node" (flow + descendants) or
 * "batch" run POSTs a group and navigates to the group view.
 *
 * The parameter form is built from the selected flow's own definition: the control plane returns exactly the run
 * parameters that flow's kind honors (a copy flow gets a modified-date window, a file flow adds a glob, a
 * relational ingestion adds full-load / assertions and a window when it has a date column, and kinds with no
 * selection surface get none), so a user is never shown a control the run would ignore.
 */
export function TriggerRunDialog({
  open, onClose, repoId, flowName, flowId, initialParameters, scope, batch,
}: TriggerRunDialogProps) {
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
  const [assertionsOnly, setAssertionsOnly] = useState(false);

  // A batch-locked launch (from the status board) carries no flow: force batch scope and keep it there.
  const batchLocked = batch !== undefined;
  // Seed the form once per open, so a Re-run opens with the prior run's parameters and a fresh launch opens clean.
  useEffect(() => {
    if (open) {
      setSelectedScope(batchLocked ? "batch" : scope ?? "flow");
      setFullLoad(initialParameters?.fullLoad ?? false);
      setBackfillFrom(toLocalInput(initialParameters?.backfillFrom));
      setBackfillTo(toLocalInput(initialParameters?.backfillTo));
      setFilePattern(initialParameters?.filePattern ?? "");
      setAssertionsOnly(initialParameters?.assertionsOnly ?? false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

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

  const isGroup = selectedScope !== "flow";

  // The selected flow's pipeline id: given directly by the launching context, or resolved from the repo's pipeline
  // list for a free-choice launch. Drives the applicable-parameters lookup.
  const effectiveFlowId = flowId
    ?? pipelines.data?.items.find((p) => p.name === effectiveFlow)?.id
    ?? null;

  const flowParameters = useQuery({
    queryKey: ["pipeline-parameters", effectiveFlowId],
    queryFn: () => pipelineApi.parameters(effectiveFlowId!),
    enabled: open && !isGroup && Boolean(effectiveFlowId),
  });
  const applicable = useMemo(() => flowParameters.data?.parameters ?? [], [flowParameters.data]);
  const paramKeys = useMemo(() => new Set(applicable.map((p) => p.key)), [applicable]);

  // For a Node or Batch scope, preview which flows the run would touch, so the operator sees "will run N flows across
  // M waves" before committing. A single flow needs no preview.
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

  const trimmedFrom = backfillFrom.trim();
  const trimmedTo = backfillTo.trim();
  const hasWindow = trimmedFrom !== "" || trimmedTo !== "";
  const hasPattern = filePattern.trim() !== "";

  // Client-side mirror of RunParameters.Validate (single-flow only), so obvious mistakes are caught before the round
  // trip (the server validates authoritatively and its ProblemDetails still renders if anything slips through).
  const windowError = isGroup
    ? null
    : assertionsOnly && (fullLoad || hasWindow || hasPattern)
      ? "Assertions-only cannot be combined with full load, a window, or a file pattern."
      : trimmedTo !== "" && trimmedFrom === ""
        ? "An end date needs a start date."
        : fullLoad && hasWindow
          ? "Full load and a backfill window are mutually exclusive."
          : null;

  const hasTarget = batchLocked ? Boolean(batch) : Boolean(effectiveFlow);
  // A group run stays disabled until the preview confirms there is at least one flow to run.
  const groupReady = !isGroup || (preview.data !== undefined && preview.data.memberCount > 0);
  const canSubmit = Boolean(effectiveRepoId) && hasTarget && windowError === null && groupReady && !trigger.isPending;

  const submit = () => {
    const single = !isGroup;
    const applies = (key: string) => single && paramKeys.has(key);
    trigger.mutate({
      repoId: effectiveRepoId!,
      flowName: batchLocked ? "" : (effectiveFlow ?? "").trim(),
      scope: selectedScope,
      batch: batchLocked ? batch : null,
      pool: pool.trim() === "" ? null : pool.trim(),
      commitSha: commitSha.trim() === "" ? null : commitSha.trim(),
      // Only the parameters the flow's kind honors are sent; a group always runs default parameters.
      fullLoad: applies("fullLoad") ? fullLoad : false,
      backfillFrom: applies("backfillWindow") && trimmedFrom !== "" ? `${trimmedFrom}:00Z` : null,
      backfillTo: applies("backfillWindow") && trimmedTo !== "" ? `${trimmedTo}:00Z` : null,
      filePattern: applies("filePattern") && hasPattern ? filePattern.trim() : null,
      assertionsOnly: applies("assertionsOnly") ? assertionsOnly : false,
    });
  };

  const switchProps = (testid: string) =>
    ({ "data-testid": testid } as React.InputHTMLAttributes<HTMLInputElement>);

  const renderParameter = (desc: RunParameterDescriptor) => {
    switch (desc.input) {
      case "Toggle": {
        const isFull = desc.key === "fullLoad";
        const checked = isFull ? fullLoad : assertionsOnly;
        const onChange = isFull ? setFullLoad : setAssertionsOnly;
        const disabled = isFull ? assertionsOnly : (fullLoad || hasWindow || hasPattern);
        return (
          <Box key={desc.key}>
            <FormControlLabel
              control={(
                <Switch
                  checked={checked}
                  disabled={disabled}
                  onChange={(e) => onChange(e.target.checked)}
                  inputProps={switchProps(`trigger-${desc.key}`)}
                />
              )}
              label={desc.label}
            />
            <Typography variant="caption" color="text.secondary" sx={{ display: "block", ml: 6, mt: -0.5 }}>
              {desc.help}
            </Typography>
          </Box>
        );
      }
      case "DateRange":
        return (
          <Box key={desc.key}>
            <Typography variant="body2" sx={{ fontWeight: 500, mb: 0.75 }}>{desc.label}</Typography>
            <Stack direction={{ xs: "column", sm: "row" }} spacing={2}>
              <TextField
                label="From"
                type="datetime-local"
                size="small"
                value={backfillFrom}
                onChange={(e) => setBackfillFrom(e.target.value)}
                disabled={fullLoad || assertionsOnly}
                InputLabelProps={{ shrink: true }}
                inputProps={{ "data-testid": "trigger-backfill-from" }}
                fullWidth
              />
              <TextField
                label="To"
                type="datetime-local"
                size="small"
                value={backfillTo}
                onChange={(e) => setBackfillTo(e.target.value)}
                disabled={fullLoad || assertionsOnly}
                InputLabelProps={{ shrink: true }}
                inputProps={{ "data-testid": "trigger-backfill-to" }}
                fullWidth
              />
            </Stack>
            <Typography variant="caption" color="text.secondary" sx={{ display: "block", mt: 0.75 }}>
              {desc.help}
            </Typography>
          </Box>
        );
      case "Glob":
        return (
          <TextField
            key={desc.key}
            label={desc.label}
            size="small"
            placeholder="orders_2026-03*.json"
            helperText={desc.help}
            value={filePattern}
            onChange={(e) => setFilePattern(e.target.value)}
            disabled={assertionsOnly}
            inputProps={{ "data-testid": "trigger-file-pattern" }}
          />
        );
      default:
        return null;
    }
  };

  const showParameters = !isGroup && Boolean(effectiveFlow);

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

          {showParameters && (
            <Box
              data-testid="trigger-parameters"
              sx={{ border: 1, borderColor: "divider", borderRadius: 1.5, p: 2, bgcolor: "action.hover" }}
            >
              <Typography variant="subtitle2">Run parameters</Typography>
              {flowParameters.isLoading ? (
                <Stack direction="row" spacing={1} alignItems="center" sx={{ mt: 1.5 }}>
                  <CircularProgress size={16} />
                  <Typography variant="body2" color="text.secondary">Loading this flow's parameters...</Typography>
                </Stack>
              ) : applicable.length === 0 ? (
                <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
                  This flow runs as defined; it has no adjustable run parameters.
                </Typography>
              ) : (
                <Stack spacing={2} sx={{ mt: 1.5 }}>
                  <Typography variant="caption" color="text.secondary">
                    One-off overrides applied to this run only. The flow definition in git is unchanged.
                  </Typography>
                  {applicable.map(renderParameter)}
                  {windowError !== null && (
                    <Alert severity="warning" data-testid="trigger-backfill-error">{windowError}</Alert>
                  )}
                </Stack>
              )}
            </Box>
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
