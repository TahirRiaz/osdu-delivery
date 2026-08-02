import { useMemo, useState } from "react";
import { Link as RouterLink, useNavigate } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ChartGantt, CirclePlay, FileCode2, Link2, Loader2, Pause, Play, Plus, Trash2, X } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group";
import { Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Switch } from "@/components/ui/switch";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import { pipelineApi, repoApi, scheduleApi } from "../../api/endpoints";
import type { RunGroupCounts, RunStatus, Schedule } from "../../api/types";
import { ComboBoxField } from "../../components/ComboBoxField";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { RunStatusBadge, rollupStatus, ScheduleStateBadge } from "../../components/StatusBadge";
import { RunScheduleDialog } from "./RunScheduleDialog";
import { ScheduleDefinitionSheet } from "./ScheduleDefinitionSheet";

/** The toast line for a failed mutation (DESIGN.md 8.1): the API's own message when it is one. */
function errorMessage(error: unknown): string {
  return isApiError(error) ? error.detail ?? error.title : String(error);
}

/** The states the last fire's members ended in, as the list the group rollup reads: presence is all it needs, so one
 * entry per non-zero state is enough to get the same worst-wins headline a run group shows. */
function presentStatuses(counts: RunGroupCounts): RunStatus[] {
  const pairs: [RunStatus, number][] = [
    ["failed", counts.failed],
    ["running", counts.running],
    ["queued", counts.queued],
    ["succeeded", counts.succeeded],
    ["cancelled", counts.cancelled],
    ["skipped", counts.skipped],
  ];
  return pairs.filter(([, n]) => n > 0).map(([status]) => status);
}

/** How the last fire ended, in words, for the hover: every non-zero state of the set it ran. */
function describeLastFire(counts: RunGroupCounts): string {
  const parts = [
    counts.succeeded > 0 ? `${counts.succeeded} succeeded` : null,
    counts.failed > 0 ? `${counts.failed} failed` : null,
    counts.cancelled > 0 ? `${counts.cancelled} cancelled` : null,
    counts.skipped > 0 ? `${counts.skipped} skipped` : null,
    counts.running > 0 ? `${counts.running} running` : null,
    counts.queued > 0 ? `${counts.queued} queued` : null,
  ].filter((part): part is string => part !== null);
  return `Last fire ran ${counts.total} ${counts.total === 1 ? "flow" : "flows"}: ${parts.join(", ")}.`;
}

/** The create-schedule form as a right-side sheet (DESIGN.md 7.4); the old dialog's testid stays on the
 * sheet content so e2e keeps passing. */
function CreateScheduleSheet({ onClose }: { onClose: () => void }) {
  const queryClient = useQueryClient();
  const [repoId, setRepoId] = useState<string | null>(null);
  // Membership is the only selector: a schedule runs the flows that joined it, in lineage wave order.
  const [members, setMembers] = useState<string[]>([]);
  const [name, setName] = useState("");
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
    onSuccess: (_created, request) => {
      toast.success(`Schedule "${request.name ?? request.members[0]}" created`);
      void queryClient.invalidateQueries({ queryKey: ["schedules"] });
      onClose();
    },
    onError: (error) => toast.error(errorMessage(error)),
  });

  const repoOptions = useMemo(() => repos.data?.items ?? [], [repos.data]);
  const flowOptions = useMemo(() => pipelines.data?.items.map((p) => p.name) ?? [], [pipelines.data]);

  const intervalValid = /^\d+$/.test(intervalText.trim()) && Number.parseInt(intervalText.trim(), 10) > 0;
  const triggerValid = triggerKind === "cron" ? cron.trim() !== "" : intervalValid;
  const canSubmit = repoId !== null && members.length > 0 && triggerValid && !create.isPending;

  const submit = () => {
    create.mutate({
      repoId: repoId!,
      members,
      // Blank falls back to the first member's flow name, matching how an unnamed inline block is named after its flow.
      name: name.trim() === "" ? null : name.trim(),
      cron: triggerKind === "cron" ? cron.trim() : null,
      intervalSeconds: triggerKind === "interval" ? Number.parseInt(intervalText.trim(), 10) : null,
      timezone: timezone.trim() === "" ? "UTC" : timezone.trim(),
      enabled,
      catchup,
    });
  };

  return (
    <Sheet
      open
      onOpenChange={(next) => {
        if (!next && !create.isPending) {
          onClose();
        }
      }}
    >
      <SheetContent className="w-full gap-0 sm:max-w-xl" data-testid="create-schedule-dialog">
        <SheetHeader className="border-b">
          <SheetTitle>Create schedule</SheetTitle>
          <SheetDescription>
            Flows join by name; one fire runs the whole member set as a wave-ordered group.
          </SheetDescription>
        </SheetHeader>

        <div className="flex flex-1 flex-col gap-4 overflow-y-auto p-4">
          {create.isError && (
            isApiError(create.error)
              ? <CorrelationError error={create.error} />
              : <p className="text-[13px] text-destructive">{String(create.error)}</p>
          )}

          <ComboBoxField
            label="Repo"
            options={repoOptions}
            optionValue={(repo) => repo.id}
            optionLabel={(repo) => repo.name}
            value={repoId}
            onChange={(value) => {
              setRepoId(value);
              setMembers([]);
            }}
            loading={repos.isLoading}
            placeholder={repos.isLoading ? "Loading repos..." : "Select a repo"}
            loadingMessage="Loading repos..."
            emptyMessage="No repo matches."
            testId="schedule-repo"
          />

          <div className="flex flex-col gap-1.5">
            <ComboBoxField
              label="Flows this schedule runs"
              multiple
              options={flowOptions}
              optionValue={(flow) => flow}
              renderOption={(flow) => <span className="font-mono text-[12px]">{flow}</span>}
              values={members}
              onToggle={(flow) =>
                setMembers((prev) => (prev.includes(flow) ? prev.filter((f) => f !== flow) : [...prev, flow]))}
              disabled={repoId === null}
              loading={pipelines.isLoading}
              placeholder={members.length === 0
                ? repoId === null ? "Pick a repo first" : "Select flows"
                : `${members.length} ${members.length === 1 ? "flow" : "flows"} selected`}
              loadingMessage="Loading flows..."
              emptyMessage="No flow matches."
              testId="schedule-flow"
            />
            {members.length > 0 && (
              <div className="flex flex-wrap gap-1">
                {members.map((member) => (
                  <Badge key={member} variant="secondary" className="gap-1 font-mono">
                    {member}
                    <button
                      type="button"
                      aria-label={`Remove ${member}`}
                      onClick={() => setMembers((prev) => prev.filter((f) => f !== member))}
                      className="rounded-full hover:text-destructive"
                    >
                      <X className="size-3" />
                    </button>
                  </Badge>
                ))}
              </div>
            )}
            <p className="text-xs text-muted-foreground">
              One fire enqueues every flow here as a single wave-ordered group, so a flow never runs before what it
              depends on.
            </p>
          </div>

          <div className="flex flex-col gap-1.5">
            <Label htmlFor="schedule-name">Name (optional)</Label>
            <Input
              id="schedule-name"
              value={name}
              onChange={(e) => setName(e.target.value)}
              className="h-8"
              data-testid="schedule-name"
            />
            <p className="text-xs text-muted-foreground">
              What flows would join with 'schedule: &lt;name&gt;'. Defaults to the first flow's name.
            </p>
          </div>

          <div className="flex flex-col gap-1.5">
            <Label>Trigger</Label>
            <RadioGroup
              value={triggerKind}
              onValueChange={(value) => setTriggerKind(value === "interval" ? "interval" : "cron")}
              className="flex items-center gap-4"
              data-testid="schedule-trigger-kind"
            >
              <Label className="flex items-center gap-2 text-[13px] font-normal">
                <RadioGroupItem value="cron" data-testid="schedule-trigger-cron" />
                Cron
              </Label>
              <Label className="flex items-center gap-2 text-[13px] font-normal">
                <RadioGroupItem value="interval" data-testid="schedule-trigger-interval" />
                Interval
              </Label>
            </RadioGroup>
          </div>

          {triggerKind === "cron" ? (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="schedule-cron">Cron expression</Label>
              <Input
                id="schedule-cron"
                placeholder="0 8 * * *"
                value={cron}
                onChange={(e) => setCron(e.target.value)}
                className="h-8 font-mono"
                data-testid="schedule-cron"
              />
            </div>
          ) : (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="schedule-interval">Interval (seconds)</Label>
              <Input
                id="schedule-interval"
                type="number"
                min={1}
                value={intervalText}
                onChange={(e) => setIntervalText(e.target.value)}
                aria-invalid={intervalText.trim() !== "" && !intervalValid}
                className="h-8 font-mono"
                data-testid="schedule-interval"
              />
              <p
                className={cn(
                  "text-xs",
                  intervalText.trim() !== "" && !intervalValid ? "text-destructive" : "text-muted-foreground",
                )}
              >
                A positive whole number of seconds between fires.
              </p>
            </div>
          )}

          <div className="flex flex-col gap-1.5">
            <Label htmlFor="schedule-timezone">Timezone</Label>
            <Input
              id="schedule-timezone"
              value={timezone}
              onChange={(e) => setTimezone(e.target.value)}
              className="h-8 font-mono"
              data-testid="schedule-timezone"
            />
            <p className="text-xs text-muted-foreground">IANA timezone the cron expression is evaluated in.</p>
          </div>

          <Label className="flex items-center gap-2 text-[13px] font-normal">
            <Switch checked={enabled} onCheckedChange={setEnabled} data-testid="schedule-enabled" />
            Enabled
          </Label>
          <Label className="flex items-center gap-2 text-[13px] font-normal">
            <Switch checked={catchup} onCheckedChange={setCatchup} data-testid="schedule-catchup" />
            Catch up missed occurrences
          </Label>
        </div>

        <SheetFooter className="flex-row justify-end border-t">
          <Button variant="ghost" size="sm" onClick={onClose} disabled={create.isPending} data-testid="create-schedule-cancel">
            Cancel
          </Button>
          <Button size="sm" onClick={submit} disabled={!canSubmit} data-testid="create-schedule-submit">
            {create.isPending && <Loader2 className="animate-spin" />}
            Create
          </Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}

/** All schedules: state at a glance, pause/resume/delete inline, and creation of API-sourced schedules. */
export default function SchedulesPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [createOpen, setCreateOpen] = useState(false);
  const [deleteTarget, setDeleteTarget] = useState<Schedule | null>(null);
  // The schedule whose pre-flight run board is open: Run-now opens it rather than firing blind, so an operator sees
  // the waves it will run and presses Start.
  const [runTarget, setRunTarget] = useState<Schedule | null>(null);
  // The schedule whose defining YAML is open: the cadence is declared in git, so the list can show the document
  // behind it rather than sending an operator to the repo to find out why a schedule fires when it does.
  const [definitionTarget, setDefinitionTarget] = useState<Schedule | null>(null);

  const pauseResume = useMutation({
    mutationFn: (row: Schedule) => (row.paused ? scheduleApi.resume(row.id) : scheduleApi.pause(row.id)),
    onSuccess: (updated) => {
      toast.success(updated.paused ? `Schedule "${updated.name}" paused` : `Schedule "${updated.name}" resumed`);
      void queryClient.invalidateQueries({ queryKey: ["schedules"] });
    },
    onError: (error) => toast.error(errorMessage(error)),
  });

  const remove = useMutation({
    mutationFn: (row: Schedule) => scheduleApi.remove(row.id),
    onSuccess: (_result, row) => {
      toast.success(`Schedule "${row.name}" deleted`);
      void queryClient.invalidateQueries({ queryKey: ["schedules"] });
      setDeleteTarget(null);
    },
    onError: (error) => {
      toast.error(errorMessage(error));
      setDeleteTarget(null);
    },
  });

  const columns: Column<Schedule>[] = [
    {
      id: "name",
      header: "Schedule",
      render: (row) => (
        <RouterLink
          to={`/runs?scheduleId=${row.id}`}
          className="font-mono text-[12px] font-medium text-primary hover:underline"
          data-testid="schedule-name-link"
        >
          {row.name}
        </RouterLink>
      ),
    },
    {
      id: "members",
      header: "Runs",
      render: (row) => (row.memberPipelineIds.length === 1 ? (
        <Tooltip>
          <TooltipTrigger asChild>
            {/* A single-member schedule links straight to its flow's pipeline detail. */}
            <RouterLink
              to={`/pipelines/${row.memberPipelineIds[0]}`}
              onClick={(event) => event.stopPropagation()}
              className="text-[13px] text-primary hover:underline"
              data-testid="schedule-flow-link"
            >
              1 flow
            </RouterLink>
          </TooltipTrigger>
          <TooltipContent>One flow joined this schedule; open its pipeline detail.</TooltipContent>
        </Tooltip>
      ) : (
        <Tooltip>
          <TooltipTrigger asChild>
            <span className="inline-flex">
              <Badge
                variant="outline"
                className={row.memberPipelineIds.length === 0 ? "text-warning" : "text-primary"}
                data-testid="schedule-members-chip"
              >
                {row.memberPipelineIds.length} flows
              </Badge>
            </span>
          </TooltipTrigger>
          <TooltipContent>
            {row.memberPipelineIds.length === 0
              ? "No flow joins this schedule, so a fire runs nothing. Join one with 'schedule: <name>'."
              : "The flows that joined this schedule. One fire enqueues them all as a single wave-ordered group."}
          </TooltipContent>
        </Tooltip>
      )),
    },
    {
      id: "trigger",
      header: "Trigger",
      render: (row) => {
        if (row.cron !== null) {
          return <Mono>cron: {row.cron}</Mono>;
        }

        if (row.intervalSeconds !== null) {
          return <Mono>every {row.intervalSeconds}s</Mono>;
        }

        // A chained schedule has no cadence on purpose. Naming what fires it is the whole answer to "why does
        // this never run on its own"; a bare dash reads as a broken schedule.
        if (row.afterSchedule) {
          return (
            <span className="inline-flex items-center gap-1">
              <Link2 className="size-3 shrink-0 text-muted-foreground" aria-hidden />
              <span className="text-muted-foreground">after</span>
              <Mono>{row.afterSchedule}</Mono>
            </span>
          );
        }

        return "-";
      },
    },
    { id: "timezone", header: "Timezone", render: (row) => row.timezone },
    {
      id: "state",
      header: "State",
      render: (row) => (
        <div className="flex items-center gap-1">
          <ScheduleStateBadge enabled={row.enabled} paused={row.paused} />
          {row.lastGroupActive && (
            <Tooltip>
              <TooltipTrigger asChild>
                <span className="inline-flex">
                  <Badge className="gap-1 border-transparent bg-info/12 text-info" data-testid="schedule-running">
                    <Loader2 className="size-3 animate-spin" />
                    running
                  </Badge>
                </span>
              </TooltipTrigger>
              <TooltipContent>A fire of this schedule is executing now. Open "view running" to watch or cancel it.</TooltipContent>
            </Tooltip>
          )}
          {row.catchup && (
            <Tooltip>
              <TooltipTrigger asChild>
                <span className="inline-flex">
                  <Badge variant="outline">catchup</Badge>
                </span>
              </TooltipTrigger>
              <TooltipContent>Missed occurrences are backfilled (one per tick), not skipped.</TooltipContent>
            </Tooltip>
          )}
        </div>
      ),
    },
    { id: "source", header: "Source", render: (row) => <Badge variant="outline">{row.source}</Badge> },
    { id: "nextFire", header: "Next fire", render: (row) => <RelativeTime value={row.nextFireUtc} absolute /> },
    { id: "lastFire", header: "Last fire", render: (row) => <RelativeTime value={row.lastFireUtc} absolute /> },
    {
      id: "lastRun",
      header: "Last run",
      render: (row) => {
        if (row.lastGroupId === null && row.lastRunId === null) {
          return "-";
        }

        // How the last fire ended, worst-wins over its members, exactly as a run group's own header rolls up. Without
        // it the list showed only that a fire happened, never whether it worked.
        const counts = row.lastCounts;
        const outcome = counts !== null && counts.total > 0 ? rollupStatus(presentStatuses(counts)) : null;
        // A scoped fire is a set, so its "last run" is the whole group, not one member. While that group is still
        // executing, this is the durable way back to the live run board (the pre-flight sheet is ephemeral; closing
        // it or switching tabs must not strand the run), so it reads as an active link.
        const isGroup = row.lastGroupId !== null;
        const target = isGroup ? `/runs/groups/${row.lastGroupId}` : `/runs/${row.lastRunId}`;

        return (
          <div className="flex items-center gap-1.5">
            {outcome !== null && <RunStatusBadge status={outcome} testId="schedule-last-outcome" />}
            <Tooltip>
              <TooltipTrigger asChild>
                <Button
                  variant={row.lastGroupActive ? "outline" : "ghost"}
                  size="xs"
                  className={row.lastGroupActive ? "border-info/40 text-info hover:text-info" : undefined}
                  onClick={(e) => {
                    e.stopPropagation();
                    navigate(target);
                  }}
                  data-testid={isGroup ? "schedule-last-group" : "schedule-last-run"}
                >
                  {row.lastGroupActive
                    ? <><Loader2 className="animate-spin" />view running</>
                    : isGroup ? "view set" : "view"}
                </Button>
              </TooltipTrigger>
              <TooltipContent>
                {counts !== null && counts.total > 0
                  ? describeLastFire(counts)
                  : "The runs this fire enqueued are no longer in the catalog."}
              </TooltipContent>
            </Tooltip>
          </div>
        );
      },
    },
    {
      id: "actions",
      header: "Actions",
      align: "right",
      render: (row) => {
        const pausePending = pauseResume.isPending && pauseResume.variables?.id === row.id;
        return (
          <div className="flex items-center justify-end gap-0.5">
            <Tooltip>
              <TooltipTrigger asChild>
                <Button
                  variant="ghost"
                  size="icon-xs"
                  aria-label="Run now"
                  className="text-primary hover:text-primary"
                  onClick={(e) => {
                    e.stopPropagation();
                    setRunTarget(row);
                  }}
                  data-testid="schedule-run-now"
                >
                  <CirclePlay />
                </Button>
              </TooltipTrigger>
              <TooltipContent>Run now (preview the waves, then start)</TooltipContent>
            </Tooltip>
            <Tooltip>
              <TooltipTrigger asChild>
                <Button
                  variant="ghost"
                  size="icon-xs"
                  aria-label="View definition"
                  onClick={(e) => {
                    e.stopPropagation();
                    setDefinitionTarget(row);
                  }}
                  data-testid="schedule-definition"
                >
                  <FileCode2 />
                </Button>
              </TooltipTrigger>
              <TooltipContent>View the YAML that defines this schedule</TooltipContent>
            </Tooltip>
            {row.paused ? (
              <Tooltip>
                <TooltipTrigger asChild>
                  <Button
                    variant="ghost"
                    size="icon-xs"
                    aria-label="Resume schedule"
                    disabled={pauseResume.isPending}
                    onClick={(e) => {
                      e.stopPropagation();
                      pauseResume.mutate(row);
                    }}
                    data-testid="schedule-resume"
                  >
                    {pausePending ? <Loader2 className="animate-spin" /> : <Play />}
                  </Button>
                </TooltipTrigger>
                <TooltipContent>Resume schedule</TooltipContent>
              </Tooltip>
            ) : (
              <Tooltip>
                <TooltipTrigger asChild>
                  <Button
                    variant="ghost"
                    size="icon-xs"
                    aria-label="Pause schedule"
                    disabled={pauseResume.isPending}
                    onClick={(e) => {
                      e.stopPropagation();
                      pauseResume.mutate(row);
                    }}
                    data-testid="schedule-pause"
                  >
                    {pausePending ? <Loader2 className="animate-spin" /> : <Pause />}
                  </Button>
                </TooltipTrigger>
                <TooltipContent>Pause schedule</TooltipContent>
              </Tooltip>
            )}
            <Tooltip>
              <TooltipTrigger asChild>
                <Button
                  variant="ghost"
                  size="icon-xs"
                  aria-label="Delete schedule"
                  disabled={remove.isPending}
                  onClick={(e) => {
                    e.stopPropagation();
                    setDeleteTarget(row);
                  }}
                  data-testid="schedule-delete"
                >
                  <Trash2 />
                </Button>
              </TooltipTrigger>
              <TooltipContent>Delete schedule</TooltipContent>
            </Tooltip>
          </div>
        );
      },
    },
  ];

  return (
    <Page data-testid="page-schedules">
      <PageHeader
        title="Schedules"
        actions={(
          <>
            <Button variant="outline" size="sm" asChild data-testid="open-schedule-timeline">
              <RouterLink to="/schedules/timeline">
                <ChartGantt />
                Timeline
              </RouterLink>
            </Button>
            <Button size="sm" onClick={() => setCreateOpen(true)} data-testid="open-create-schedule">
              <Plus />
              Create schedule
            </Button>
          </>
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

      {createOpen && <CreateScheduleSheet onClose={() => setCreateOpen(false)} />}

      {runTarget && <RunScheduleDialog schedule={runTarget} onClose={() => setRunTarget(null)} />}

      {definitionTarget && (
        <ScheduleDefinitionSheet schedule={definitionTarget} onClose={() => setDefinitionTarget(null)} />
      )}

      <ConfirmDialog
        open={deleteTarget !== null}
        title="Delete schedule"
        message={`Delete the schedule "${deleteTarget?.name ?? ""}"? This cannot be undone.`}
        confirmLabel="Delete"
        danger
        busy={remove.isPending}
        onConfirm={() => {
          if (deleteTarget !== null) {
            remove.mutate(deleteTarget);
          }
        }}
        onClose={() => setDeleteTarget(null)}
      />
    </Page>
  );
}
