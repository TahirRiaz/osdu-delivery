import { useCallback, useState } from "react";
import { Navigate, useNavigate, useParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useSnackbar } from "notistack";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import Typography from "@mui/material/Typography";
import ReplayIcon from "@mui/icons-material/Replay";
import type { RunGroupCounts, RunStatus, RunSummary } from "../../api/types";
import { isApiError } from "../../api/client";
import { runApi } from "../../api/endpoints";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable } from "../../components/DataTable";
import { DetailHeaderCard } from "../../components/DetailHeaderCard";
import { DetailPair } from "../../components/DetailPair";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { RunStatusBadge } from "../../components/StatusBadge";
import { TruncatedText } from "../../components/TruncatedText";
import { pollingInterval } from "../../hooks/usePolling";
import { formatDurationSeconds } from "../../lib/time";
import { useRunGroupStream } from "./useRunGroupStream";

const memberColumns: Column<RunSummary>[] = [
  { id: "status", header: "Status", render: (row) => <RunStatusBadge status={row.status} /> },
  {
    id: "flow",
    header: "Flow",
    render: (row) => <Typography variant="body2" sx={{ fontWeight: 600 }}>{row.flowName}</Typography>,
  },
  { id: "kind", header: "Kind", render: (row) => row.flowKind },
  { id: "batch", header: "Batch", render: (row) => row.batch },
  { id: "step", header: "Step", align: "right", render: (row) => (row.wave >= 0 ? row.wave : "-") },
  {
    id: "enqueued",
    header: "Enqueued",
    render: (row) => <RelativeTime value={row.enqueuedUtc ?? row.writtenUtc} />,
  },
  {
    id: "duration",
    header: "Duration",
    render: (row) => (row.durationSeconds != null ? formatDurationSeconds(row.durationSeconds) : "-"),
  },
  {
    // The member's newest trace event: while the group executes this ticks live with what each member is doing
    // right now (the table polls every 3s); once a member ends it settles on its final event.
    id: "lastAction",
    header: "Last action",
    render: (row) => <TruncatedText text={row.lastAction} maxWidth={420} />,
  },
  { id: "rowsLoaded", header: "Rows loaded", align: "right", render: (row) => row.rowsLoaded ?? "-" },
  { id: "pool", header: "Pool", render: (row) => row.targetPool ?? "-" },
  {
    id: "commit",
    header: "Commit",
    render: (row) => <Mono>{row.commitSha?.slice(0, 10) ?? "-"}</Mono>,
  },
];

/** A run group's member-count chips, one per non-zero lifecycle state. */
function CountChips({ counts }: { counts: RunGroupCounts }) {
  const entries: [string, number, "default" | "info" | "primary" | "success" | "error" | "warning"][] = [
    ["queued", counts.queued, "info"],
    ["running", counts.running, "primary"],
    ["succeeded", counts.succeeded, "success"],
    ["failed", counts.failed, "error"],
    ["cancelled", counts.cancelled, "warning"],
    ["skipped", counts.skipped, "default"],
  ];
  return (
    <>
      {entries.filter(([, n]) => n > 0).map(([label, n, color]) => (
        <Chip key={label} size="small" color={color} variant="outlined" label={`${n} ${label}`} />
      ))}
    </>
  );
}

/** One Node or Batch run group: the header (mode, anchor, live rollup) and its member flows in wave order. */
export default function RunGroupPage() {
  const { groupId } = useParams<{ groupId: string }>();
  if (!groupId) {
    return <Navigate to="/runs" replace />;
  }

  return <RunGroupContent groupId={groupId} />;
}

function RunGroupContent({ groupId }: { groupId: string }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const [confirmOpen, setConfirmOpen] = useState(false);

  const query = useQuery({
    queryKey: ["run-groups", groupId],
    queryFn: () => runApi.group(groupId),
    // Poll while any member is still queued or running; stop once the whole group is terminal.
    refetchInterval: (q) => {
      const data = q.state.data;
      if (data && data.counts.queued === 0 && data.counts.running === 0) {
        return false;
      }

      return pollingInterval(3000)();
    },
  });

  // The live group: while any member is queued or running the member table is fed by the group's SSE stream
  // (each row updates the moment its flow's status or last action changes), and the header chips roll up from
  // the streamed statuses. The end frame refetches the header and the paged member list, which the page then
  // swaps back to as the authoritative at-rest view.
  const counts = query.data?.counts;
  const live = counts !== undefined && (counts.queued > 0 || counts.running > 0);
  const refreshGroup = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ["run-groups", groupId] });
    void queryClient.invalidateQueries({ queryKey: ["runs", "group", groupId] });
  }, [queryClient, groupId]);
  const { members: liveMembers, connected: streamConnected } = useRunGroupStream(groupId, live, refreshGroup);

  // Re-run repeats the group's own scope: the same batch, or the same anchor flow plus descendants, expanded
  // fresh against the current estate (so members added to the batch since last time are included). The new
  // execution is a new group; the button navigates there.
  const rerun = useMutation({
    mutationFn: () => {
      const g = query.data;
      if (!g) {
        throw new Error("The group has not loaded yet.");
      }

      return runApi.trigger(g.mode === "batch"
        ? { repoId: g.repoId, flowName: "", scope: "batch", batch: g.anchor }
        : { repoId: g.repoId, flowName: g.anchor, scope: "node" });
    },
    onSuccess: (accepted) => {
      enqueueSnackbar(`Re-run enqueued: ${accepted.memberCount ?? 0} member(s).`, { variant: "success" });
      if (accepted.groupId) {
        navigate(`/runs/groups/${accepted.groupId}`);
      }
    },
    onError: (error) => {
      enqueueSnackbar(isApiError(error) ? error.title : String(error), { variant: "error" });
    },
  });

  const cancel = useMutation({
    mutationFn: () => runApi.cancelGroup(groupId),
    onSuccess: () => {
      enqueueSnackbar("Cancel requested for this run group.", { variant: "success" });
      setConfirmOpen(false);
      void queryClient.invalidateQueries({ queryKey: ["run-groups", groupId] });
      void queryClient.invalidateQueries({ queryKey: ["runs", "group", groupId] });
    },
    onError: (error) => {
      enqueueSnackbar(isApiError(error) ? error.title : String(error), { variant: "error" });
      setConfirmOpen(false);
    },
  });

  if (query.isError) {
    return (
      <Page data-testid="page-run-group">
        {isApiError(query.error)
          ? <CorrelationError error={query.error} />
          : <Typography color="error">{String(query.error)}</Typography>}
      </Page>
    );
  }

  const group = query.data;
  if (group === undefined) {
    return (
      <Page data-testid="page-run-group">
        <Skeleton variant="rounded" height={200} />
        <Skeleton variant="rounded" height={320} />
      </Page>
    );
  }

  const active = group.counts.queued > 0 || group.counts.running > 0;
  const modeLabel = group.mode === "node" ? "Flow + descendants" : "Batch";

  // While streaming, the rollup chips count the streamed member statuses directly, so the header agrees with
  // the rows beneath it instead of trailing on the 3s header poll.
  const liveCounts: RunGroupCounts | null = active && liveMembers.length > 0
    ? (() => {
      const of = (status: RunStatus) => liveMembers.filter((m) => m.status === status).length;
      return {
        total: liveMembers.length,
        queued: of("queued"),
        running: of("running"),
        succeeded: of("succeeded"),
        failed: of("failed"),
        cancelled: of("cancelled"),
        skipped: of("skipped"),
      };
    })()
    : null;

  return (
    <Page data-testid="page-run-group">
      <DetailHeaderCard
        title={`${modeLabel}: ${group.anchor}`}
        badges={(
          <>
            <Chip size="small" label={group.mode} variant="outlined" data-testid="group-mode" />
            <CountChips counts={liveCounts ?? group.counts} />
          </>
        )}
        actions={active
          ? (
            <Button
              color="error"
              variant="outlined"
              onClick={() => setConfirmOpen(true)}
              disabled={cancel.isPending}
              data-testid="cancel-group"
            >
              Cancel group
            </Button>
          )
          : (
            <Button
              variant="outlined"
              startIcon={<ReplayIcon fontSize="small" />}
              onClick={() => rerun.mutate()}
              disabled={rerun.isPending}
              data-testid="rerun-group"
            >
              Re-run
            </Button>
          )}
      >
        <DetailPair label="Group id"><Mono>{group.groupId}</Mono></DetailPair>
        <DetailPair label="Mode">{modeLabel}</DetailPair>
        <DetailPair label="Anchor">{group.anchor}</DetailPair>
        <DetailPair label="Members">{group.memberCount}</DetailPair>
        <DetailPair label="Enqueued"><RelativeTime value={group.enqueuedUtc} /></DetailPair>
        <DetailPair label="Commit">{group.commitSha ? <Mono>{group.commitSha.slice(0, 12)}</Mono> : "-"}</DetailPair>
      </DetailHeaderCard>

      {active
        ? (
          <Stack spacing={1}>
            <Stack direction="row" spacing={1} alignItems="center">
              <Chip
                size="small"
                color={streamConnected ? "success" : "warning"}
                variant="outlined"
                label={streamConnected ? "live" : "reconnecting"}
                data-testid="group-stream-state"
              />
              <Typography variant="body2" color="text.secondary">
                {streamConnected
                  ? "Streaming member status and actions as the group executes."
                  : "Connection lost; resuming the stream."}
              </Typography>
            </Stack>
            <DataTable
              columns={memberColumns}
              rows={liveMembers.length > 0 ? liveMembers : undefined}
              rowKey={(row) => row.runId}
              onRowClick={(row) => navigate(`/runs/${row.runId}`)}
              emptyMessage="This run group has no members."
              data-testid="group-members-live-table"
            />
          </Stack>
        )
        : (
          <PagedTable
            queryKey={["runs", "group", groupId]}
            fetchPage={(page, pageSize) => runApi.list({ groupId, page, pageSize })}
            columns={memberColumns}
            rowKey={(row) => row.runId}
            onRowClick={(row) => navigate(`/runs/${row.runId}`)}
            emptyMessage="This run group has no members."
            data-testid="group-members-table"
          />
        )}

      <ConfirmDialog
        open={confirmOpen}
        title="Cancel run group"
        message={`Cancel run group ${group.groupId}? Queued members are dequeued and any running member's node aborts its in-flight statement.`}
        confirmLabel="Cancel group"
        danger
        busy={cancel.isPending}
        onConfirm={() => cancel.mutate()}
        onClose={() => setConfirmOpen(false)}
      />
    </Page>
  );
}
