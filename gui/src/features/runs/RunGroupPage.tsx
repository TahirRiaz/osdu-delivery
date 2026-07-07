import { useState } from "react";
import { Navigate, useNavigate, useParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useSnackbar } from "notistack";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import Skeleton from "@mui/material/Skeleton";
import Typography from "@mui/material/Typography";
import type { RunGroupCounts, RunSummary } from "../../api/types";
import { isApiError } from "../../api/client";
import { runApi } from "../../api/endpoints";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { DetailHeaderCard } from "../../components/DetailHeaderCard";
import { DetailPair } from "../../components/DetailPair";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { RunStatusBadge } from "../../components/StatusBadge";
import { pollingInterval } from "../../hooks/usePolling";
import { formatDurationSeconds } from "../../lib/time";

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

  return (
    <Page data-testid="page-run-group">
      <DetailHeaderCard
        title={`${modeLabel}: ${group.anchor}`}
        badges={(
          <>
            <Chip size="small" label={group.mode} variant="outlined" data-testid="group-mode" />
            <CountChips counts={group.counts} />
          </>
        )}
        actions={active && (
          <Button
            color="error"
            variant="outlined"
            onClick={() => setConfirmOpen(true)}
            disabled={cancel.isPending}
            data-testid="cancel-group"
          >
            Cancel group
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

      <PagedTable
        queryKey={["runs", "group", groupId]}
        fetchPage={(page, pageSize) => runApi.list({ groupId, page, pageSize })}
        columns={memberColumns}
        rowKey={(row) => row.runId}
        onRowClick={(row) => navigate(`/runs/${row.runId}`)}
        pollMs={active ? 3000 : undefined}
        emptyMessage="This run group has no members."
        data-testid="group-members-table"
      />

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
