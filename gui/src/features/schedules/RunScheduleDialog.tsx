import { useCallback, useMemo, useState } from "react";
import type React from "react";
import { useNavigate } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useSnackbar } from "notistack";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import CircularProgress from "@mui/material/CircularProgress";
import Dialog from "@mui/material/Dialog";
import DialogActions from "@mui/material/DialogActions";
import DialogContent from "@mui/material/DialogContent";
import DialogTitle from "@mui/material/DialogTitle";
import IconButton from "@mui/material/IconButton";
import LinearProgress from "@mui/material/LinearProgress";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";
import { alpha, useTheme } from "@mui/material/styles";
import BoltIcon from "@mui/icons-material/Bolt";
import CheckCircleIcon from "@mui/icons-material/CheckCircle";
import CloseIcon from "@mui/icons-material/Close";
import ErrorIcon from "@mui/icons-material/Error";
import HourglassEmptyIcon from "@mui/icons-material/HourglassEmpty";
import KeyboardArrowDownIcon from "@mui/icons-material/KeyboardArrowDown";
import OpenInNewIcon from "@mui/icons-material/OpenInNew";
import PlayArrowIcon from "@mui/icons-material/PlayArrow";
import RemoveCircleOutlineIcon from "@mui/icons-material/RemoveCircleOutline";
import ScheduleIcon from "@mui/icons-material/Schedule";
import { isApiError } from "../../api/client";
import { scheduleApi } from "../../api/endpoints";
import type { RunStatus, RunSummary, Schedule, SchedulePlanMember } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { RelativeTime } from "../../components/RelativeTime";
import { useRunGroupStream } from "../runs/useRunGroupStream";

/** The status a plan member shows on the board: the live run status once a fire is underway, or "pending" before
 * Start (and for the brief window before the group's connect snapshot lands). */
type MemberStatus = RunStatus | "pending";

const TERMINAL: ReadonlySet<RunStatus> = new Set<RunStatus>(["succeeded", "failed", "cancelled", "skipped"]);

/** The theme palette key each wave's accent border cycles through, echoing the batch-coloured rails in the board. */
const WAVE_ACCENTS = ["warning", "secondary", "info", "success", "primary", "error"] as const;

/** One wave of the plan: its members, grouped and ordered so the board renders them concurrently under one rail. */
interface PlanWave {
  wave: number;
  members: SchedulePlanMember[];
}

function describeTrigger(cron: string | null, intervalSeconds: number | null): string {
  if (cron !== null && cron.trim() !== "") {
    return `cron ${cron}`;
  }

  if (intervalSeconds !== null) {
    return `every ${intervalSeconds}s`;
  }

  return "manual";
}

/** Groups the plan's flat, wave-ordered member list into the waves the board draws, each sorted by flow name so a
 * member keeps its row across live updates. */
function toWaves(members: SchedulePlanMember[]): PlanWave[] {
  const byWave = new Map<number, SchedulePlanMember[]>();
  for (const member of members) {
    const existing = byWave.get(member.wave);
    if (existing) {
      existing.push(member);
    } else {
      byWave.set(member.wave, [member]);
    }
  }

  return [...byWave.entries()]
    .sort(([a], [b]) => a - b)
    .map(([wave, waveMembers]) => ({
      wave,
      members: [...waveMembers].sort((a, b) => (a.flowName < b.flowName ? -1 : a.flowName > b.flowName ? 1 : 0)),
    }));
}

/** The compact status glyph + label for one member row: a synthetic "pending" before Start, then the live run
 * status once the group is streaming. Kept local (not RunStatusBadge) so "pending" has a first-class rest state. */
function MemberStatusChip({ status }: { status: MemberStatus }) {
  const theme = useTheme();
  const spec: Record<MemberStatus, { label: string; color: string; icon: React.ReactNode }> = {
    pending: {
      label: "Pending",
      color: theme.palette.text.disabled,
      icon: <ScheduleIcon sx={{ fontSize: 16 }} />,
    },
    queued: {
      label: "Queued",
      color: theme.palette.info.main,
      icon: <HourglassEmptyIcon sx={{ fontSize: 16 }} />,
    },
    running: {
      label: "Running",
      color: theme.palette.primary.main,
      icon: <CircularProgress size={13} thickness={6} color="inherit" />,
    },
    succeeded: {
      label: "Succeeded",
      color: theme.palette.success.main,
      icon: <CheckCircleIcon sx={{ fontSize: 16 }} />,
    },
    failed: {
      label: "Failed",
      color: theme.palette.error.main,
      icon: <ErrorIcon sx={{ fontSize: 16 }} />,
    },
    cancelled: {
      label: "Cancelled",
      color: theme.palette.warning.main,
      icon: <RemoveCircleOutlineIcon sx={{ fontSize: 16 }} />,
    },
    skipped: {
      label: "Skipped",
      color: theme.palette.text.disabled,
      icon: <RemoveCircleOutlineIcon sx={{ fontSize: 16 }} />,
    },
  };
  const { label, color, icon } = spec[status];
  return (
    <Stack direction="row" spacing={0.75} alignItems="center" sx={{ color, minWidth: 108, justifyContent: "flex-end" }}>
      {icon}
      <Typography variant="caption" sx={{ fontWeight: 600, color: "inherit" }}>{label}</Typography>
    </Stack>
  );
}

export interface RunScheduleDialogProps {
  schedule: Schedule;
  onClose: () => void;
}

/**
 * The pre-flight run board for a schedule. It reads the same wave-ordered plan a fire enqueues (GET
 * /schedules/{id}/plan), lays the flows out as the batches they run in (a wave is a row of flows that run
 * concurrently; the next wave starts only once the previous is terminal), and gates execution behind Start.
 *
 * On Start it fires the schedule through the existing run-now path (the cadence is untouched). A multi-flow fire
 * becomes a run group, and the board then streams that group live in place: every flow transitions Pending →
 * Queued → Running → its terminal status as the group executes, with a progress rail and a link to the full run
 * group view. A single-flow schedule has no group, so Start hands off straight to that run's detail page.
 */
export function RunScheduleDialog({ schedule, onClose }: RunScheduleDialogProps) {
  const theme = useTheme();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();

  const [groupId, setGroupId] = useState<string | null>(null);
  const [ended, setEnded] = useState(false);
  const phase: "preview" | "running" = groupId === null ? "preview" : "running";

  const plan = useQuery({
    queryKey: ["schedule-plan", schedule.id],
    queryFn: () => scheduleApi.plan(schedule.id),
  });

  const onEnded = useCallback(() => setEnded(true), []);
  const { members: liveMembers, connected } = useRunGroupStream(groupId ?? "", groupId !== null && !ended, onEnded);
  const liveByFlow = useMemo(() => {
    const map = new Map<string, RunSummary>();
    for (const member of liveMembers) {
      map.set(member.flowName, member);
    }

    return map;
  }, [liveMembers]);

  const run = useMutation({
    mutationFn: () => scheduleApi.runNow(schedule.id),
    onSuccess: (accepted) => {
      void queryClient.invalidateQueries({ queryKey: ["schedules"] });
      if (accepted.groupId !== null) {
        // A multi-flow fire streams live in this board; keep the dialog open and switch to the running phase.
        setGroupId(accepted.groupId);
        return;
      }

      // A single-flow schedule enqueues one run with no group to stream, so hand off to that run's detail page.
      enqueueSnackbar("Run started.", { variant: "success" });
      onClose();
      navigate(`/runs/${accepted.runId}`);
    },
    onError: (error) => {
      enqueueSnackbar(isApiError(error) ? error.title : String(error), { variant: "error" });
    },
  });

  const waves = useMemo(() => toWaves(plan.data?.members ?? []), [plan.data]);
  const memberCount = plan.data?.memberCount ?? 0;

  const statusOf = (flowName: string): MemberStatus => {
    if (phase !== "running") {
      return "pending";
    }

    return liveByFlow.get(flowName)?.status ?? "pending";
  };

  const doneCount = phase === "running"
    ? (plan.data?.members ?? []).filter((m) => {
      const s = liveByFlow.get(m.flowName)?.status;
      return s !== undefined && TERMINAL.has(s);
    }).length
    : 0;
  const failedCount = phase === "running"
    ? (plan.data?.members ?? []).filter((m) => liveByFlow.get(m.flowName)?.status === "failed").length
    : 0;
  const progress = memberCount > 0 ? Math.round((doneCount / memberCount) * 100) : 0;

  const nothingToRun = plan.isSuccess && memberCount === 0;
  const canStart = plan.isSuccess && memberCount > 0 && !run.isPending && phase === "preview";
  const closeDisabled = run.isPending;

  const viewFullRun = () => {
    if (groupId !== null) {
      onClose();
      navigate(`/runs/groups/${groupId}`);
    }
  };

  return (
    <Dialog
      open
      onClose={closeDisabled ? undefined : onClose}
      fullWidth
      maxWidth="sm"
      data-testid="run-schedule-dialog"
      PaperProps={{ sx: { backgroundImage: "none" } }}
    >
      <DialogTitle sx={{ pb: 1.5 }}>
        <Stack direction="row" spacing={1.5} alignItems="center">
          <Box
            sx={{
              width: 40,
              height: 40,
              borderRadius: 1.5,
              display: "flex",
              alignItems: "center",
              justifyContent: "center",
              color: theme.palette.warning.main,
              bgcolor: alpha(theme.palette.warning.main, 0.14),
            }}
          >
            <BoltIcon />
          </Box>
          <Box sx={{ minWidth: 0, flexGrow: 1 }}>
            <Typography variant="h6" sx={{ lineHeight: 1.2 }}>Run schedule</Typography>
            <Typography variant="body2" color="text.secondary" noWrap data-testid="run-schedule-name">
              {schedule.name}
            </Typography>
          </Box>
          <IconButton onClick={onClose} disabled={closeDisabled} size="small" aria-label="Close">
            <CloseIcon fontSize="small" />
          </IconButton>
        </Stack>
      </DialogTitle>

      <DialogContent dividers>
        {plan.isError && (
          isApiError(plan.error)
            ? <CorrelationError error={plan.error} />
            : <Alert severity="error">{String(plan.error)}</Alert>
        )}

        {plan.isLoading && (
          <Stack spacing={1.5} data-testid="run-schedule-loading">
            <Skeleton variant="rounded" height={64} />
            <Skeleton variant="rounded" height={120} />
            <Skeleton variant="rounded" height={120} />
          </Stack>
        )}

        {plan.isSuccess && (
          <Stack spacing={2}>
            {run.isError && isApiError(run.error) && <CorrelationError error={run.error} />}

            {/* Summary rail: what one fire runs and on what cadence. */}
            <Box
              sx={{
                border: 1,
                borderColor: "divider",
                borderRadius: 1.5,
                p: 1.5,
                bgcolor: "action.hover",
              }}
            >
              <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap alignItems="center">
                <Typography variant="subtitle2" data-testid="run-schedule-summary">
                  {memberCount} {memberCount === 1 ? "flow" : "flows"}
                </Typography>
                <Typography variant="body2" color="text.secondary">·</Typography>
                <Typography variant="subtitle2">
                  {waves.length} {waves.length === 1 ? "wave" : "waves"}
                </Typography>
                <Box sx={{ flexGrow: 1 }} />
                <Chip
                  size="small"
                  variant="outlined"
                  label={describeTrigger(plan.data.cron, plan.data.intervalSeconds)}
                  data-testid="run-schedule-cadence"
                />
                {!schedule.enabled && <Chip size="small" color="default" variant="outlined" label="disabled" />}
                {schedule.paused && <Chip size="small" color="warning" label="paused" />}
              </Stack>
              <Typography variant="caption" color="text.secondary" sx={{ display: "block", mt: 0.75 }}>
                {phase === "running"
                  ? `${doneCount} of ${memberCount} done${failedCount > 0 ? `, ${failedCount} failed` : ""}.`
                  : (
                    <>
                      Next scheduled fire <RelativeTime value={plan.data.nextFireUtc} />. Starting now runs it on
                      demand and does not move the schedule.
                    </>
                  )}
              </Typography>
            </Box>

            {(!schedule.enabled || schedule.paused) && phase === "preview" && (
              <Alert severity="info" data-testid="run-schedule-inactive-note">
                This schedule is {schedule.paused ? "paused" : "disabled"}, so it will not fire on its own. Starting
                here runs its flows once, immediately, without changing that.
              </Alert>
            )}

            {phase === "running" && (
              <Box>
                <LinearProgress
                  variant="determinate"
                  value={progress}
                  color={failedCount > 0 ? "error" : ended ? "success" : "primary"}
                  sx={{ height: 6, borderRadius: 3 }}
                  data-testid="run-schedule-progress"
                />
                <Stack direction="row" spacing={1} alignItems="center" sx={{ mt: 0.75 }}>
                  <Chip
                    size="small"
                    variant="outlined"
                    color={ended ? "default" : connected ? "success" : "warning"}
                    label={ended ? "finished" : connected ? "live" : "reconnecting"}
                    data-testid="run-schedule-stream-state"
                  />
                  <Typography variant="caption" color="text.secondary">
                    {ended
                      ? failedCount > 0
                        ? `Finished with ${failedCount} failed.`
                        : "All flows finished."
                      : "Executing in dependency order."}
                  </Typography>
                </Stack>
              </Box>
            )}

            {nothingToRun ? (
              <Alert severity="warning" data-testid="run-schedule-empty">
                No runnable flow joins this schedule right now. A flow joins with <code>schedule: {schedule.name}</code>,
                and must be active and not <code>mode: manual</code>.
              </Alert>
            ) : (
              <Stack spacing={1}>
                {waves.map((planWave, index) => {
                  const accent = theme.palette[WAVE_ACCENTS[index % WAVE_ACCENTS.length]].main;
                  const running = phase === "running";
                  const waveDone = running
                    && planWave.members.every((m) => {
                      const s = liveByFlow.get(m.flowName)?.status;
                      return s !== undefined && TERMINAL.has(s);
                    });
                  const waveActive = running
                    && !waveDone
                    && planWave.members.some((m) => {
                      const s = liveByFlow.get(m.flowName)?.status;
                      return s === "running" || s === "queued";
                    });
                  return (
                    <Box key={planWave.wave}>
                      {index > 0 && (
                        <Stack alignItems="center" sx={{ color: "text.disabled", my: 0.25 }}>
                          <KeyboardArrowDownIcon fontSize="small" />
                        </Stack>
                      )}
                      <Box
                        sx={{
                          borderRadius: 1.5,
                          border: 1,
                          borderColor: "divider",
                          borderLeft: 3,
                          borderLeftColor: accent,
                          bgcolor: alpha(accent, waveActive ? 0.08 : 0.03),
                          overflow: "hidden",
                        }}
                      >
                        <Stack
                          direction="row"
                          spacing={1}
                          alignItems="center"
                          sx={{ px: 1.5, py: 1, bgcolor: alpha(accent, 0.06) }}
                        >
                          <Typography variant="overline" sx={{ color: accent, fontWeight: 700, letterSpacing: 0.5 }}>
                            Wave {index + 1}
                          </Typography>
                          <Typography variant="caption" color="text.secondary">
                            {planWave.members.length} {planWave.members.length === 1 ? "flow" : "flows"} · concurrent
                          </Typography>
                          <Box sx={{ flexGrow: 1 }} />
                          {index === 0 ? (
                            <Typography variant="caption" color="text.secondary">runs first</Typography>
                          ) : (
                            <Typography variant="caption" color="text.secondary">after previous</Typography>
                          )}
                          {waveDone && <CheckCircleIcon sx={{ fontSize: 16, color: theme.palette.success.main }} />}
                        </Stack>
                        <Stack divider={<Box sx={{ borderTop: 1, borderColor: "divider" }} />}>
                          {planWave.members.map((member) => {
                            const live = liveByFlow.get(member.flowName);
                            return (
                              <Stack
                                key={member.flowName}
                                direction="row"
                                spacing={1}
                                alignItems="center"
                                sx={{ px: 1.5, py: 1 }}
                                data-testid="run-schedule-member"
                              >
                                <Box sx={{ minWidth: 0, flexGrow: 1 }}>
                                  <Typography variant="body2" sx={{ fontWeight: 600 }} noWrap>
                                    {member.flowName}
                                  </Typography>
                                  <Typography variant="caption" color="text.secondary" noWrap>
                                    {member.flowKind}
                                    {running && live?.lastAction ? ` · ${live.lastAction}` : ""}
                                  </Typography>
                                </Box>
                                <MemberStatusChip status={statusOf(member.flowName)} />
                              </Stack>
                            );
                          })}
                        </Stack>
                      </Box>
                    </Box>
                  );
                })}
              </Stack>
            )}
          </Stack>
        )}
      </DialogContent>

      <DialogActions sx={{ px: 3, py: 2 }}>
        {phase === "preview" ? (
          <>
            <Typography variant="caption" color="text.secondary" sx={{ mr: "auto" }}>
              {nothingToRun ? "Nothing to run." : "Review the waves, then start."}
            </Typography>
            <Button onClick={onClose} disabled={closeDisabled}>Cancel</Button>
            <Button
              variant="contained"
              color="warning"
              startIcon={run.isPending ? <CircularProgress size={16} color="inherit" /> : <PlayArrowIcon />}
              onClick={() => run.mutate()}
              disabled={!canStart}
              data-testid="run-schedule-start"
            >
              Start
            </Button>
          </>
        ) : (
          <>
            <Tooltip title="Open the full run group view">
              <Button
                startIcon={<OpenInNewIcon fontSize="small" />}
                onClick={viewFullRun}
                sx={{ mr: "auto" }}
                data-testid="run-schedule-view-group"
              >
                View full run
              </Button>
            </Tooltip>
            <Button variant={ended ? "contained" : "outlined"} onClick={onClose} data-testid="run-schedule-close">
              {ended ? "Done" : "Close"}
            </Button>
          </>
        )}
      </DialogActions>
    </Dialog>
  );
}
