import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useSnackbar } from "notistack";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import Dialog from "@mui/material/Dialog";
import DialogActions from "@mui/material/DialogActions";
import DialogContent from "@mui/material/DialogContent";
import DialogTitle from "@mui/material/DialogTitle";
import FormControlLabel from "@mui/material/FormControlLabel";
import Stack from "@mui/material/Stack";
import Switch from "@mui/material/Switch";
import TextField from "@mui/material/TextField";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";
import { isApiError } from "../../api/client";
import { repoSourceApi } from "../../api/endpoints";
import type { RepoSource } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { ActiveBadge } from "../../components/StatusBadge";

function RegisterSourceDialog({ onClose }: { onClose: () => void }) {
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const [name, setName] = useState("");
  const [remoteUrl, setRemoteUrl] = useState("");
  const [branch, setBranch] = useState("main");
  const [intervalText, setIntervalText] = useState("300");
  const [enabled, setEnabled] = useState(true);

  const register = useMutation({
    mutationFn: repoSourceApi.register,
    onSuccess: () => {
      enqueueSnackbar("Repo source registered.", { variant: "success" });
      void queryClient.invalidateQueries({ queryKey: ["repo-sources"] });
      onClose();
    },
  });

  const intervalValid = /^\d+$/.test(intervalText.trim()) && Number.parseInt(intervalText.trim(), 10) > 0;
  const canSubmit = name.trim() !== "" && remoteUrl.trim() !== "" && intervalValid && !register.isPending;

  const submit = () => {
    register.mutate({
      name: name.trim(),
      remoteUrl: remoteUrl.trim(),
      branch: branch.trim() === "" ? null : branch.trim(),
      syncIntervalSeconds: Number.parseInt(intervalText.trim(), 10),
      enabled,
    });
  };

  return (
    <Dialog open onClose={register.isPending ? undefined : onClose} fullWidth maxWidth="sm" data-testid="register-source-dialog">
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
          <FormControlLabel
            control={(
              <Switch
                checked={enabled}
                onChange={(e) => setEnabled(e.target.checked)}
                data-testid="source-enabled"
              />
            )}
            label="Enabled"
          />
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

/** The registered git sources the control plane syncs from, with health and an on-demand sync trigger. */
export default function RepoSourcesPage() {
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const [registerOpen, setRegisterOpen] = useState(false);

  const syncNow = useMutation({
    mutationFn: (id: string) => repoSourceApi.syncNow(id),
    onSuccess: () => {
      enqueueSnackbar("Sync requested", { variant: "success" });
      void queryClient.invalidateQueries({ queryKey: ["repo-sources"] });
    },
    onError: (error) =>
      enqueueSnackbar(error instanceof Error ? error.message : String(error), { variant: "error" }),
  });

  const columns: Column<RepoSource>[] = [
    {
      id: "name",
      header: "Name",
      render: (row) => <Typography variant="body2" fontWeight={600}>{row.name}</Typography>,
    },
    { id: "remoteUrl", header: "Remote URL", render: (row) => row.remoteUrl },
    { id: "branch", header: "Branch", render: (row) => row.branch },
    { id: "interval", header: "Interval", render: (row) => `${row.syncIntervalSeconds}s` },
    { id: "enabled", header: "Enabled", render: (row) => <ActiveBadge active={row.enabled} /> },
    { id: "lastSync", header: "Last sync", render: (row) => <RelativeTime value={row.lastSyncUtc} /> },
    {
      id: "syncedCommit",
      header: "Synced commit",
      render: (row) => (
        <Typography variant="body2" sx={{ fontFamily: "monospace" }}>
          {row.lastSyncedSha?.slice(0, 10) ?? "-"}
        </Typography>
      ),
    },
    {
      id: "health",
      header: "Health",
      render: (row) => (row.lastError !== null ? (
        <Tooltip title={row.lastError}>
          <Chip size="small" color="error" label="error" data-testid="source-error" />
        </Tooltip>
      ) : (
        <Chip size="small" color="success" variant="outlined" label="ok" />
      )),
    },
    {
      id: "actions",
      header: "Actions",
      align: "right",
      render: (row) => (
        <Button
          size="small"
          disabled={!row.enabled || syncNow.isPending}
          onClick={(e) => {
            e.stopPropagation();
            syncNow.mutate(row.id);
          }}
          data-testid="source-sync-now"
        >
          Sync now
        </Button>
      ),
    },
  ];

  return (
    <Stack spacing={2} data-testid="page-repo-sources">
      <Stack direction="row" alignItems="center" spacing={2}>
        <Typography variant="h5">Repo sources</Typography>
        <Box sx={{ flexGrow: 1 }} />
        <Button variant="contained" onClick={() => setRegisterOpen(true)} data-testid="open-register-source">
          Register source
        </Button>
      </Stack>

      <PagedTable
        queryKey={["repo-sources", "list"]}
        fetchPage={(page, pageSize) => repoSourceApi.list({ page, pageSize })}
        columns={columns}
        rowKey={(row) => row.id}
        pollMs={15000}
        emptyMessage="No repo sources registered yet."
      />

      {registerOpen && <RegisterSourceDialog onClose={() => setRegisterOpen(false)} />}
    </Stack>
  );
}
