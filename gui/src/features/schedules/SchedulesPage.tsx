import { useMemo, useState } from "react";
import { Link as RouterLink, useNavigate } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useSnackbar } from "notistack";
import Autocomplete from "@mui/material/Autocomplete";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import Dialog from "@mui/material/Dialog";
import DialogActions from "@mui/material/DialogActions";
import DialogContent from "@mui/material/DialogContent";
import DialogTitle from "@mui/material/DialogTitle";
import FormControl from "@mui/material/FormControl";
import FormControlLabel from "@mui/material/FormControlLabel";
import FormLabel from "@mui/material/FormLabel";
import IconButton from "@mui/material/IconButton";
import Link from "@mui/material/Link";
import Radio from "@mui/material/Radio";
import RadioGroup from "@mui/material/RadioGroup";
import Stack from "@mui/material/Stack";
import Switch from "@mui/material/Switch";
import TextField from "@mui/material/TextField";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";
import DeleteIcon from "@mui/icons-material/Delete";
import PauseIcon from "@mui/icons-material/Pause";
import PlayArrowIcon from "@mui/icons-material/PlayArrow";
import { isApiError } from "../../api/client";
import { pipelineApi, repoApi, scheduleApi } from "../../api/endpoints";
import type { Schedule } from "../../api/types";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { ScheduleStateBadge } from "../../components/StatusBadge";

function CreateScheduleDialog({ onClose }: { onClose: () => void }) {
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const [repoId, setRepoId] = useState<string | null>(null);
  const [flowName, setFlowName] = useState<string | null>(null);
  const [triggerKind, setTriggerKind] = useState<"cron" | "interval">("cron");
  const [cron, setCron] = useState("");
  const [intervalText, setIntervalText] = useState("");
  const [timezone, setTimezone] = useState("UTC");
  const [enabled, setEnabled] = useState(true);
  const [catchup, setCatchup] = useState(false);

  const repos = useQuery({
    queryKey: ["repos", "all-for-schedule"],
    queryFn: () => repoApi.list({ page: 1, pageSize: 200 }),
  });
  const pipelines = useQuery({
    queryKey: ["pipelines", "for-schedule", repoId],
    queryFn: () => pipelineApi.list({ repoId: repoId!, active: true, page: 1, pageSize: 200 }),
    enabled: repoId !== null,
  });

  const create = useMutation({
    mutationFn: scheduleApi.create,
    onSuccess: () => {
      enqueueSnackbar("Schedule created.", { variant: "success" });
      void queryClient.invalidateQueries({ queryKey: ["schedules"] });
      onClose();
    },
  });

  const repoOptions = useMemo(() => repos.data?.items ?? [], [repos.data]);
  const flowOptions = useMemo(() => pipelines.data?.items.map((p) => p.name) ?? [], [pipelines.data]);

  const intervalValid = /^\d+$/.test(intervalText.trim()) && Number.parseInt(intervalText.trim(), 10) > 0;
  const triggerValid = triggerKind === "cron" ? cron.trim() !== "" : intervalValid;
  const canSubmit = repoId !== null && flowName !== null && triggerValid && !create.isPending;

  const submit = () => {
    create.mutate({
      repoId: repoId!,
      flowName: flowName!,
      cron: triggerKind === "cron" ? cron.trim() : null,
      intervalSeconds: triggerKind === "interval" ? Number.parseInt(intervalText.trim(), 10) : null,
      timezone: timezone.trim() === "" ? "UTC" : timezone.trim(),
      enabled,
      catchup,
    });
  };

  return (
    <Dialog open onClose={create.isPending ? undefined : onClose} fullWidth maxWidth="sm" data-testid="create-schedule-dialog">
      <DialogTitle>Create schedule</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          {create.isError && isApiError(create.error) && <CorrelationError error={create.error} />}
          {create.isError && !isApiError(create.error) && (
            <Typography color="error">{String(create.error)}</Typography>
          )}

          <Autocomplete
            options={repoOptions}
            getOptionLabel={(repo) => repo.name}
            value={repoOptions.find((r) => r.id === repoId) ?? null}
            onChange={(_, repo) => {
              setRepoId(repo?.id ?? null);
              setFlowName(null);
            }}
            loading={repos.isLoading}
            renderInput={(params) => (
              <TextField {...params} label="Repo" inputProps={{ ...params.inputProps, "data-testid": "schedule-repo" }} />
            )}
          />
          <Autocomplete
            options={flowOptions}
            value={flowName}
            onChange={(_, value) => setFlowName(value)}
            disabled={repoId === null}
            loading={pipelines.isLoading}
            renderInput={(params) => (
              <TextField {...params} label="Flow" inputProps={{ ...params.inputProps, "data-testid": "schedule-flow" }} />
            )}
          />

          <FormControl>
            <FormLabel>Trigger</FormLabel>
            <RadioGroup
              row
              value={triggerKind}
              onChange={(e) => setTriggerKind(e.target.value === "interval" ? "interval" : "cron")}
              data-testid="schedule-trigger-kind"
            >
              <FormControlLabel
                value="cron"
                control={<Radio data-testid="schedule-trigger-cron" />}
                label="Cron"
              />
              <FormControlLabel
                value="interval"
                control={<Radio data-testid="schedule-trigger-interval" />}
                label="Interval"
              />
            </RadioGroup>
          </FormControl>

          {triggerKind === "cron" ? (
            <TextField
              label="Cron expression"
              placeholder="0 8 * * *"
              value={cron}
              onChange={(e) => setCron(e.target.value)}
              inputProps={{ "data-testid": "schedule-cron" }}
            />
          ) : (
            <TextField
              label="Interval (seconds)"
              type="number"
              value={intervalText}
              onChange={(e) => setIntervalText(e.target.value)}
              error={intervalText.trim() !== "" && !intervalValid}
              helperText="A positive whole number of seconds between fires."
              inputProps={{ min: 1, "data-testid": "schedule-interval" }}
            />
          )}

          <TextField
            label="Timezone"
            value={timezone}
            onChange={(e) => setTimezone(e.target.value)}
            helperText="IANA timezone the cron expression is evaluated in."
            inputProps={{ "data-testid": "schedule-timezone" }}
          />
          <FormControlLabel
            control={(
              <Switch
                checked={enabled}
                onChange={(e) => setEnabled(e.target.checked)}
                data-testid="schedule-enabled"
              />
            )}
            label="Enabled"
          />
          <FormControlLabel
            control={(
              <Switch
                checked={catchup}
                onChange={(e) => setCatchup(e.target.checked)}
                data-testid="schedule-catchup"
              />
            )}
            label="Catch up missed occurrences"
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={create.isPending} data-testid="create-schedule-cancel">Cancel</Button>
        <Button variant="contained" onClick={submit} disabled={!canSubmit} data-testid="create-schedule-submit">
          Create
        </Button>
      </DialogActions>
    </Dialog>
  );
}

/** All schedules: state at a glance, pause/resume/delete inline, and creation of API-sourced schedules. */
export default function SchedulesPage() {
  const navigate = useNavigate();
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const [createOpen, setCreateOpen] = useState(false);
  const [deleteTarget, setDeleteTarget] = useState<Schedule | null>(null);

  const showError = (error: unknown) =>
    enqueueSnackbar(error instanceof Error ? error.message : String(error), { variant: "error" });

  const pauseResume = useMutation({
    mutationFn: (row: Schedule) => (row.paused ? scheduleApi.resume(row.id) : scheduleApi.pause(row.id)),
    onSuccess: (updated) => {
      enqueueSnackbar(updated.paused ? "Schedule paused." : "Schedule resumed.", { variant: "success" });
      void queryClient.invalidateQueries({ queryKey: ["schedules"] });
    },
    onError: showError,
  });

  const remove = useMutation({
    mutationFn: (id: string) => scheduleApi.remove(id),
    onSuccess: () => {
      enqueueSnackbar("Schedule deleted.", { variant: "success" });
      void queryClient.invalidateQueries({ queryKey: ["schedules"] });
      setDeleteTarget(null);
    },
    onError: (error) => {
      showError(error);
      setDeleteTarget(null);
    },
  });

  const columns: Column<Schedule>[] = [
    {
      id: "flow",
      header: "Flow",
      render: (row) => (
        <Link
          component={RouterLink}
          to={`/pipelines/${row.pipelineId}`}
          fontWeight={600}
          underline="hover"
          data-testid="schedule-flow-link"
        >
          {row.flowName}
        </Link>
      ),
    },
    {
      id: "trigger",
      header: "Trigger",
      render: (row) => {
        if (row.cron !== null) {
          return `cron: ${row.cron}`;
        }

        return row.intervalSeconds !== null ? `every ${row.intervalSeconds}s` : "-";
      },
    },
    { id: "timezone", header: "Timezone", render: (row) => row.timezone },
    {
      id: "state",
      header: "State",
      render: (row) => (
        <Stack direction="row" spacing={0.5} alignItems="center">
          <ScheduleStateBadge enabled={row.enabled} paused={row.paused} />
          {row.catchup && (
            <Tooltip title="Missed occurrences are backfilled (one per tick), not skipped.">
              <Chip size="small" variant="outlined" label="catchup" />
            </Tooltip>
          )}
        </Stack>
      ),
    },
    { id: "source", header: "Source", render: (row) => <Chip size="small" label={row.source} variant="outlined" /> },
    { id: "nextFire", header: "Next fire", render: (row) => <RelativeTime value={row.nextFireUtc} /> },
    { id: "lastFire", header: "Last fire", render: (row) => <RelativeTime value={row.lastFireUtc} /> },
    {
      id: "lastRun",
      header: "Last run",
      render: (row) => (row.lastRunId !== null ? (
        <Button
          size="small"
          onClick={(e) => {
            e.stopPropagation();
            navigate(`/runs/${row.lastRunId}`);
          }}
          data-testid="schedule-last-run"
        >
          view
        </Button>
      ) : "-"),
    },
    {
      id: "actions",
      header: "Actions",
      align: "right",
      render: (row) => (
        <Stack direction="row" spacing={0.5} justifyContent="flex-end">
          {row.paused ? (
            <Tooltip title="Resume schedule">
              <span>
                <IconButton
                  size="small"
                  disabled={pauseResume.isPending}
                  onClick={(e) => {
                    e.stopPropagation();
                    pauseResume.mutate(row);
                  }}
                  data-testid="schedule-resume"
                >
                  <PlayArrowIcon fontSize="small" />
                </IconButton>
              </span>
            </Tooltip>
          ) : (
            <Tooltip title="Pause schedule">
              <span>
                <IconButton
                  size="small"
                  disabled={pauseResume.isPending}
                  onClick={(e) => {
                    e.stopPropagation();
                    pauseResume.mutate(row);
                  }}
                  data-testid="schedule-pause"
                >
                  <PauseIcon fontSize="small" />
                </IconButton>
              </span>
            </Tooltip>
          )}
          <Tooltip title="Delete schedule">
            <span>
              <IconButton
                size="small"
                disabled={remove.isPending}
                onClick={(e) => {
                  e.stopPropagation();
                  setDeleteTarget(row);
                }}
                data-testid="schedule-delete"
              >
                <DeleteIcon fontSize="small" />
              </IconButton>
            </span>
          </Tooltip>
        </Stack>
      ),
    },
  ];

  return (
    <Page data-testid="page-schedules">
      <PageHeader
        title="Schedules"
        actions={(
          <Button variant="contained" onClick={() => setCreateOpen(true)} data-testid="open-create-schedule">
            Create schedule
          </Button>
        )}
      />

      <PagedTable
        queryKey={["schedules", "list"]}
        fetchPage={(page, pageSize) => scheduleApi.list({ page, pageSize })}
        columns={columns}
        rowKey={(row) => row.id}
        pollMs={15000}
        emptyMessage="No schedules exist yet."
      />

      {createOpen && <CreateScheduleDialog onClose={() => setCreateOpen(false)} />}

      <ConfirmDialog
        open={deleteTarget !== null}
        title="Delete schedule"
        message={`Delete the schedule for "${deleteTarget?.flowName ?? ""}"? This cannot be undone.`}
        confirmLabel="Delete"
        danger
        busy={remove.isPending}
        onConfirm={() => {
          if (deleteTarget !== null) {
            remove.mutate(deleteTarget.id);
          }
        }}
        onClose={() => setDeleteTarget(null)}
      />
    </Page>
  );
}
