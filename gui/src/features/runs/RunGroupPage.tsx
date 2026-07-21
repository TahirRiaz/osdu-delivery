import { useCallback, useState } from "react";
import { Navigate, useNavigate, useParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, Radio, RotateCcw } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import type { RunGroupCounts, RunStatus, RunSummary } from "../../api/types";
import { isApiError } from "../../api/client";
import { runApi } from "../../api/endpoints";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable } from "../../components/DataTable";
import { DetailHeaderCard } from "../../components/DetailHeaderCard";
import { DetailPair } from "../../components/DetailPair";
import { IdChip } from "../../components/IdChip";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { RunStatusBadge } from "../../components/StatusBadge";
import { TruncatedText } from "../../components/TruncatedText";
import { useRunDock } from "./RunDockContext";
import { pollingInterval } from "../../hooks/usePolling";
import { useTabTitle } from "../../layout/workbench/TabsContext";
import { formatDurationSeconds } from "../../lib/time";
import { useRunGroupStream } from "./useRunGroupStream";

const memberColumns: Column<RunSummary>[] = [
  { id: "status", header: "Status", render: (row) => <RunStatusBadge status={row.status} /> },
  {
    id: "flow",
    header: "Flow",
    render: (row) => <span className="font-mono text-[12px] font-medium">{row.flowName}</span>,
  },
  { id: "kind", header: "Kind", render: (row) => row.flowKind },
  { id: "batch", header: "Batch", render: (row) => row.batch },
  {
    id: "step",
    header: "Step",
    align: "right",
    render: (row) => <span className="font-mono tabular-nums">{row.wave >= 0 ? row.wave : "-"}</span>,
  },
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
    // right now; once a member ends it settles on its final event.
    id: "lastAction",
    header: "Last action",
    render: (row) => <TruncatedText text={row.lastAction} maxWidth={420} />,
  },
  {
    id: "rowsLoaded",
    header: "Rows loaded",
    align: "right",
    render: (row) => <span className="font-mono tabular-nums">{row.rowsLoaded ?? "-"}</span>,
  },
  { id: "pool", header: "Pool", render: (row) => row.targetPool ?? "-" },
  {
    id: "commit",
    header: "Commit",
    render: (row) => <Mono>{row.commitSha?.slice(0, 10) ?? "-"}</Mono>,
  },
];

/** A run group's member-count pills, one per non-zero lifecycle state, in the reserved status tones
 * (DESIGN.md 3.2); the state name in the label keeps color from carrying the meaning alone. */
function CountPills({ counts }: { counts: RunGroupCounts }) {
  const entries: [string, number, string][] = [
    ["queued", counts.queued, "bg-warning/15 text-warning"],
    ["running", counts.running, "bg-info/12 text-info"],
    ["succeeded", counts.succeeded, "bg-success/12 text-success"],
    ["failed", counts.failed, "bg-destructive/12 text-destructive"],
    ["cancelled", counts.cancelled, "bg-muted text-muted-foreground"],
    ["skipped", counts.skipped, "bg-muted text-muted-foreground"],
  ];
  return (
    <>
      {entries.filter(([, n]) => n > 0).map(([label, n, tone]) => (
        <span
          key={label}
          className={cn(
            "inline-flex items-center gap-1 whitespace-nowrap rounded-full px-2 py-0.5 text-[11px] font-medium leading-4",
            tone,
          )}
        >
          <span className="font-mono tabular-nums">{n}</span>
          {label}
        </span>
      ))}
    </>
  );
}

/** The live/reconnecting pill next to the streaming member table. */
function StreamStateBadge({ connected }: { connected: boolean }) {
  return (
    <span
      data-testid="group-stream-state"
      className={cn(
        "inline-flex items-center gap-1 whitespace-nowrap rounded-full px-2 py-0.5 text-[11px] font-medium leading-4",
        connected ? "bg-success/12 text-success" : "bg-warning/15 text-warning",
      )}
    >
      {connected ? <Radio className="size-3 shrink-0" /> : <Loader2 className="size-3 shrink-0 animate-spin" />}
      {connected ? "live" : "reconnecting"}
    </span>
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
  const { track } = useRunDock();
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
  // The group's scope label: its batch label in batch mode, its anchor flow's name in node mode.
  useTabTitle(query.data?.anchor);

  // The live group: while any member is queued or running the member table is fed by the group's SSE stream
  // (each row updates the moment its flow's status or last action changes), and the header pills roll up from
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
      toast.success(`Re-run enqueued: ${accepted.memberCount ?? 0} member(s).`);
      if (accepted.groupId) {
        track(accepted.groupId);
        navigate(`/runs/groups/${accepted.groupId}`);
      }
    },
    onError: (error) => {
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error));
    },
  });

  const cancel = useMutation({
    mutationFn: () => runApi.cancelGroup(groupId),
    onSuccess: () => {
      toast.success("Cancel requested for this run group.");
      setConfirmOpen(false);
      void queryClient.invalidateQueries({ queryKey: ["run-groups", groupId] });
      void queryClient.invalidateQueries({ queryKey: ["runs", "group", groupId] });
    },
    onError: (error) => {
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error));
      setConfirmOpen(false);
    },
  });

  if (query.isError) {
    return (
      <Page data-testid="page-run-group">
        {isApiError(query.error)
          ? <CorrelationError error={query.error} />
          : <p className="text-[13px] text-destructive">{String(query.error)}</p>}
      </Page>
    );
  }

  const group = query.data;
  if (group === undefined) {
    return (
      <Page data-testid="page-run-group">
        <Skeleton className="h-50 w-full rounded-lg" />
        <Skeleton className="h-80 w-full rounded-lg" />
      </Page>
    );
  }

  const active = group.counts.queued > 0 || group.counts.running > 0;
  const modeLabel = group.mode === "node" ? "Flow + descendants" : "Batch";

  // While streaming, the rollup pills count the streamed member statuses directly, so the header agrees with
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
            <Badge variant="outline" data-testid="group-mode">{group.mode}</Badge>
            <CountPills counts={liveCounts ?? group.counts} />
          </>
        )}
        actions={active
          ? (
            <Button
              variant="outline"
              size="sm"
              className="border-destructive/40 text-destructive hover:bg-destructive/10 hover:text-destructive"
              onClick={() => setConfirmOpen(true)}
              disabled={cancel.isPending}
              data-testid="cancel-group"
            >
              Cancel group
            </Button>
          )
          : (
            <Button
              variant="outline"
              size="sm"
              onClick={() => rerun.mutate()}
              disabled={rerun.isPending}
              data-testid="rerun-group"
            >
              {rerun.isPending ? <Loader2 className="animate-spin" /> : <RotateCcw />}
              Re-run
            </Button>
          )}
        meta={(
          <>
            <IdChip label="group" value={group.groupId} testId="group-id" copyTestId="copy-group-id" />
            {group.commitSha && (
              <IdChip label="commit" value={group.commitSha} display={group.commitSha.slice(0, 7)} testId="group-commit" copyTestId="copy-group-commit" />
            )}
          </>
        )}
      >
        <DetailPair label="Mode">{modeLabel}</DetailPair>
        <DetailPair label="Anchor"><Mono>{group.anchor}</Mono></DetailPair>
        <DetailPair label="Members">
          <span className="font-mono tabular-nums">{group.memberCount}</span>
        </DetailPair>
        <DetailPair label="Enqueued"><RelativeTime value={group.enqueuedUtc} absolute /></DetailPair>
      </DetailHeaderCard>

      {active
        ? (
          <div className="flex flex-col gap-2">
            <div className="flex items-center gap-2">
              <StreamStateBadge connected={streamConnected} />
              <span className="text-[13px] text-muted-foreground">
                {streamConnected
                  ? "Streaming member status and actions as the group executes."
                  : "Connection lost; resuming the stream."}
              </span>
            </div>
            <DataTable
              columns={memberColumns}
              rows={liveMembers.length > 0 ? liveMembers : undefined}
              rowKey={(row) => row.runId}
              onRowClick={(row) => navigate(`/runs/${row.runId}`)}
              emptyMessage="This run group has no members."
              data-testid="group-members-live-table"
            />
          </div>
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
