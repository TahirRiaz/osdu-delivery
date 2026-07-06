import { useMemo, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useSnackbar } from "notistack";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Checkbox from "@mui/material/Checkbox";
import Chip from "@mui/material/Chip";
import CircularProgress from "@mui/material/CircularProgress";
import Collapse from "@mui/material/Collapse";
import Dialog from "@mui/material/Dialog";
import DialogActions from "@mui/material/DialogActions";
import DialogContent from "@mui/material/DialogContent";
import DialogTitle from "@mui/material/DialogTitle";
import Divider from "@mui/material/Divider";
import FormControlLabel from "@mui/material/FormControlLabel";
import IconButton from "@mui/material/IconButton";
import Stack from "@mui/material/Stack";
import Switch from "@mui/material/Switch";
import TextField from "@mui/material/TextField";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";
import CodeIcon from "@mui/icons-material/Code";
import SearchIcon from "@mui/icons-material/Search";
import { isApiError } from "../../api/client";
import { repoSourceApi } from "../../api/endpoints";
import type { DiscoveredFlow } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";

// Mirrors the server's guard: a whole ${scheme:locator} reference, never a raw token.
const REFERENCE_RE = /^\$\{[a-zA-Z]+:[^}]+\}$/;

/**
 * Registers a git repo as a tracked source, with an optional preview-first scan: discover the repo's flows, preview
 * each, and choose which to import. The credential is a ${...} reference (created in the vault), never a raw token.
 * Registering persists the source and its selection; the SYNC (managed on the interval, or "Sync now") is what
 * actually imports the selected flows into the catalog, which is what the scheduler then executes.
 */
export function RegisterSourceDialog({ onClose }: { onClose: () => void }) {
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const [name, setName] = useState("");
  const [remoteUrl, setRemoteUrl] = useState("");
  const [branch, setBranch] = useState("main");
  const [intervalText, setIntervalText] = useState("300");
  const [enabled, setEnabled] = useState(true);
  const [credentialUsername, setCredentialUsername] = useState("");
  const [credentialReference, setCredentialReference] = useState("");
  const [discovered, setDiscovered] = useState<DiscoveredFlow[] | null>(null);
  const [included, setIncluded] = useState<ReadonlySet<string>>(new Set());
  const [previewPath, setPreviewPath] = useState<string | null>(null);

  const intervalValid = /^\d+$/.test(intervalText.trim()) && Number.parseInt(intervalText.trim(), 10) > 0;
  const referenceValid = credentialReference.trim() === "" || REFERENCE_RE.test(credentialReference.trim());

  const credentialPayload = () => ({
    credentialUsername: credentialUsername.trim() === "" ? null : credentialUsername.trim(),
    credentialReference: credentialReference.trim() === "" ? null : credentialReference.trim(),
  });

  const discover = useMutation({
    mutationFn: () =>
      repoSourceApi.discover({
        remoteUrl: remoteUrl.trim(),
        branch: branch.trim() === "" ? null : branch.trim(),
        ...credentialPayload(),
      }),
    onSuccess: (flows) => {
      setDiscovered(flows);
      setIncluded(new Set(flows.map((f) => f.relativePath))); // default: import everything discovered
      setPreviewPath(null);
      if (flows.length === 0) {
        enqueueSnackbar("No *.flow.yaml files were found in this repo/branch.", { variant: "info" });
      }
    },
    onError: (error) =>
      enqueueSnackbar(error instanceof Error ? error.message : String(error), { variant: "error" }),
  });

  const register = useMutation({
    mutationFn: repoSourceApi.register,
    onSuccess: () => {
      enqueueSnackbar("Repo source registered. The next sync imports the selected flows into the catalog.", {
        variant: "success",
      });
      void queryClient.invalidateQueries({ queryKey: ["repos"] });
      void queryClient.invalidateQueries({ queryKey: ["repo-sources"] });
      onClose();
    },
  });

  // Never discovered: send null so the sync imports every flow (backward compatible). Otherwise send the unchecked
  // files as the exclusion, which the sync honors (an excluded flow never becomes a catalog pipeline).
  const excludedFlowPaths = useMemo(
    () => (discovered === null ? null : discovered.filter((f) => !included.has(f.relativePath)).map((f) => f.relativePath)),
    [discovered, included],
  );

  const canDiscover = remoteUrl.trim() !== "" && referenceValid && !discover.isPending;
  const canSubmit = name.trim() !== "" && remoteUrl.trim() !== "" && intervalValid && referenceValid && !register.isPending;
  const includedCount = discovered === null ? 0 : discovered.filter((f) => included.has(f.relativePath)).length;

  const toggle = (path: string) =>
    setIncluded((current) => {
      const next = new Set(current);
      if (next.has(path)) {
        next.delete(path);
      } else {
        next.add(path);
      }

      return next;
    });

  const submit = () => {
    register.mutate({
      name: name.trim(),
      remoteUrl: remoteUrl.trim(),
      branch: branch.trim() === "" ? null : branch.trim(),
      syncIntervalSeconds: Number.parseInt(intervalText.trim(), 10),
      enabled,
      ...credentialPayload(),
      excludedFlowPaths,
    });
  };

  return (
    <Dialog
      open
      onClose={register.isPending ? undefined : onClose}
      fullWidth
      maxWidth={discovered ? "md" : "sm"}
      data-testid="register-source-dialog"
    >
      <DialogTitle>Register source</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          {register.isError && isApiError(register.error) && <CorrelationError error={register.error} />}
          {register.isError && !isApiError(register.error) && (
            <Typography color="error">{String(register.error)}</Typography>
          )}

          <TextField
            label="Name"
            required
            value={name}
            onChange={(e) => setName(e.target.value)}
            inputProps={{ "data-testid": "source-name" }}
          />
          <TextField
            label="Remote URL"
            required
            placeholder="https://git.example.com/org/repo.git"
            value={remoteUrl}
            onChange={(e) => setRemoteUrl(e.target.value)}
            inputProps={{ "data-testid": "source-remote-url" }}
          />
          <TextField
            label="Branch"
            value={branch}
            onChange={(e) => setBranch(e.target.value)}
            helperText="Empty falls back to the server default (main)."
            inputProps={{ "data-testid": "source-branch" }}
          />
          <TextField
            label="Sync interval (seconds)"
            type="number"
            value={intervalText}
            onChange={(e) => setIntervalText(e.target.value)}
            error={!intervalValid}
            helperText="A positive whole number of seconds between syncs."
            inputProps={{ min: 1, "data-testid": "source-interval" }}
          />
          <TextField
            label="Git username (optional)"
            value={credentialUsername}
            onChange={(e) => setCredentialUsername(e.target.value)}
            helperText="Only needed for hosts that authenticate the username too (e.g. Bitbucket app passwords)."
            inputProps={{ "data-testid": "source-credential-username" }}
          />
          <TextField
            label="Credential reference (optional)"
            placeholder="${keyvault:my-vault/github-pat}"
            value={credentialReference}
            onChange={(e) => setCredentialReference(e.target.value)}
            error={!referenceValid}
            helperText={
              referenceValid
                ? "A ${keyvault:vault/secret} or ${env:NAME} reference to the token. Create the secret in your vault; SQLFlow only references it. Leave blank for a public remote."
                : "Must be a reference like ${keyvault:my-vault/github-pat}, not a raw token."
            }
            inputProps={{ "data-testid": "source-credential-reference" }}
          />
          <FormControlLabel
            control={(
              <Switch checked={enabled} onChange={(e) => setEnabled(e.target.checked)} data-testid="source-enabled" />
            )}
            label="Enabled"
          />

          <Divider />

          <Stack direction="row" alignItems="center" spacing={2} flexWrap="wrap" useFlexGap>
            <Button
              variant="outlined"
              startIcon={discover.isPending ? <CircularProgress size={16} /> : <SearchIcon />}
              disabled={!canDiscover}
              onClick={() => discover.mutate()}
              data-testid="discover-flows"
            >
              {discovered ? "Re-discover flows" : "Discover flows"}
            </Button>
            <Typography variant="body2" color="text.secondary" sx={{ flex: 1, minWidth: 200 }}>
              {discovered === null
                ? "Optional: preview the repo's flows and choose which to import. Skip to import every flow."
                : `${includedCount} of ${discovered.length} flow(s) will be imported on sync.`}
            </Typography>
          </Stack>
          {discover.isError && isApiError(discover.error) && <CorrelationError error={discover.error} />}

          {discovered !== null && discovered.length > 0 && (
            <Box
              sx={{ border: 1, borderColor: "divider", borderRadius: 1, maxHeight: 340, overflow: "auto" }}
              data-testid="discovered-flows"
            >
              <Stack
                direction="row"
                spacing={1}
                sx={{ p: 1, position: "sticky", top: 0, bgcolor: "background.paper", zIndex: 1 }}
              >
                <Button size="small" onClick={() => setIncluded(new Set(discovered.map((f) => f.relativePath)))}>
                  Select all
                </Button>
                <Button size="small" onClick={() => setIncluded(new Set())}>Select none</Button>
              </Stack>
              <Divider />
              {discovered.map((f) => (
                <Box key={f.relativePath} sx={{ px: 1 }} data-testid="discovered-flow-row">
                  <Stack direction="row" alignItems="center" spacing={1}>
                    <Checkbox
                      size="small"
                      checked={included.has(f.relativePath)}
                      onChange={() => toggle(f.relativePath)}
                      aria-label={`include ${f.relativePath}`}
                    />
                    <Box sx={{ flexGrow: 1, minWidth: 0 }}>
                      <Typography variant="body2" fontWeight={600} noWrap>{f.flowName ?? f.relativePath}</Typography>
                      <Typography variant="caption" color="text.secondary" noWrap display="block">{f.relativePath}</Typography>
                    </Box>
                    {f.parseOk
                      ? f.kind !== null && <Chip size="small" variant="outlined" label={f.kind} />
                      : (
                        <Tooltip title={f.parseError ?? "parse error"}>
                          <Chip size="small" color="error" label="parse error" data-testid="discovered-flow-error" />
                        </Tooltip>
                      )}
                    {f.content !== null && (
                      <Tooltip title="Preview">
                        <IconButton
                          size="small"
                          onClick={() => setPreviewPath((p) => (p === f.relativePath ? null : f.relativePath))}
                          data-testid="discovered-flow-preview"
                        >
                          <CodeIcon fontSize="small" />
                        </IconButton>
                      </Tooltip>
                    )}
                  </Stack>
                  <Collapse in={previewPath === f.relativePath} unmountOnExit>
                    <Box
                      component="pre"
                      sx={{ m: 0, mb: 1, p: 1, bgcolor: "action.hover", borderRadius: 1, fontSize: 12, overflow: "auto", maxHeight: 240 }}
                    >
                      {f.content}
                    </Box>
                  </Collapse>
                  <Divider />
                </Box>
              ))}
            </Box>
          )}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={register.isPending} data-testid="register-source-cancel">Cancel</Button>
        <Button variant="contained" onClick={submit} disabled={!canSubmit} data-testid="register-source-submit">
          Register
        </Button>
      </DialogActions>
    </Dialog>
  );
}
