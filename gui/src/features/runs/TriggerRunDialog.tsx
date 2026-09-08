import { useEffect, useId, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useMutation, useQuery } from "@tanstack/react-query";
import { CalendarClock, Loader2, Play } from "lucide-react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle,
} from "@/components/ui/sheet";
import { Switch } from "@/components/ui/switch";
import { isApiError } from "../../api/client";
import { pipelineApi, repoApi, runApi, scheduleApi } from "../../api/endpoints";
import { ComboBoxField } from "../../components/ComboBoxField";
import { CorrelationError } from "../../components/CorrelationError";
import { DateRangeCalendar } from "../../components/DateRangeCalendar";
import { useRunDock } from "./RunDockContext";

/** Prior-run values used to prefill the form on Re-run (ISO strings for the window; they are trimmed to the
 * minute for the datetime-local inputs). Absent fields default to empty/off. */
export interface TriggerRunParameterValues {
  fullLoad?: boolean;
  backfillFrom?: string | null;
  backfillTo?: string | null;
  filePattern?: string | null;
  sourceFilter?: string | null;
  assertionsOnly?: boolean;
}

export interface TriggerRunDialogProps {
  open: boolean;
  onClose: () => void;
  /** Prefills (and locks) the repo when launched from a repo/pipeline context. */
  repoId?: string;
  /** Prefills (and locks) the flow when launched from a pipeline detail page or a run. */
  flowName?: string;
  /** The flow's pipeline id, when the launching context knows it (pipeline detail, Re-run). Lets the dialog find
   * the flow's schedules without first resolving the id from the repo's pipeline list. */
  flowId?: string;
  /** Prior-run parameter values to prefill (Re-run). */
  initialParameters?: TriggerRunParameterValues;
}

/** A one-line cadence for a related schedule ("cron 0 2 * * *", "every 3600s", or "manual"). */
function describeCadence(cron: string | null, intervalSeconds: number | null): string {
  if (cron !== null && cron.trim() !== "") {
    return `cron ${cron}`;
  }

  if (intervalSeconds !== null) {
    return `every ${intervalSeconds}s`;
  }

  return "manual";
}

/** An ISO instant (or datetime-local string) trimmed to the "yyyy-MM-ddThh:mm" a datetime-local input expects. */
function toLocalInput(value: string | null | undefined): string {
  return value ? value.slice(0, 16) : "";
}

interface ComboOption {
  value: string;
  label: string;
}

/**
 * The single trigger-run path in the GUI: launched from the runs page (free choice of repo + flow), a pipeline's
 * detail page (prefilled), and a run's Re-run (prefilled with the prior parameters). A run POSTs one flow and
 * navigates to it. The schedules the flow is a member of are listed alongside, since firing one runs the whole
 * member set in order: that is how a source is run as a whole.
 */
export function TriggerRunDialog({
  open, onClose, repoId, flowName, flowId, initialParameters,
}: TriggerRunDialogProps) {
  const navigate = useNavigate();
  const idPrefix = useId();
  const { track } = useRunDock();
  const [selectedRepoId, setSelectedRepoId] = useState<string | null>(repoId ?? null);
  const [selectedFlow, setSelectedFlow] = useState<string | null>(flowName ?? null);
  const [pool, setPool] = useState("");
  const [commitSha, setCommitSha] = useState("");
  const [fullLoad, setFullLoad] = useState(false);
  const [backfillFrom, setBackfillFrom] = useState("");
  const [backfillTo, setBackfillTo] = useState("");
  const [filePattern, setFilePattern] = useState("");
  const [sourceFilter, setSourceFilter] = useState("");
  const [assertionsOnly, setAssertionsOnly] = useState(false);

  // Seed the form once per open, so a Re-run opens with the prior run's parameters and a fresh launch opens clean.
  useEffect(() => {
    if (open) {
      setFullLoad(initialParameters?.fullLoad ?? false);
      setBackfillFrom(toLocalInput(initialParameters?.backfillFrom));
      setBackfillTo(toLocalInput(initialParameters?.backfillTo));
      setFilePattern(initialParameters?.filePattern ?? "");
      setSourceFilter(initialParameters?.sourceFilter ?? "");
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
  // The repo's flows back the free-choice dropdown AND resolve a locked flow's pipeline id, which the schedule
  // lookup keys on. A context that already knows the id (Re-run, pipeline detail) skips the list.
  const pipelines = useQuery({
    queryKey: ["pipelines", "for-trigger", effectiveRepoId],
    queryFn: () => pipelineApi.list({ repoId: effectiveRepoId!, active: true, page: 1, pageSize: 200 }),
    enabled: open && !flowId && Boolean(effectiveRepoId),
  });

  // The selected flow's pipeline id: given directly by the launching context, or resolved from the repo's pipeline
  // list for a free-choice launch.
  const effectiveFlowId = flowId
    ?? pipelines.data?.items.find((p) => p.name === effectiveFlow)?.id
    ?? null;

  // The schedule(s) this flow is a member of: firing one runs the whole member set in order. Membership is the
  // selector, so this asks the API by pipeline id.
  const schedules = useQuery({
    queryKey: ["flow-schedules", effectiveRepoId, effectiveFlowId],
    queryFn: () => scheduleApi.list({ repoId: effectiveRepoId!, pipelineId: effectiveFlowId!, page: 1, pageSize: 50 }),
    enabled: open && Boolean(effectiveRepoId) && Boolean(effectiveFlowId),
  });
  const relatedSchedules = schedules.data?.items ?? [];

  const runSchedule = useMutation({
    mutationFn: (scheduleId: string) => scheduleApi.runNow(scheduleId),
    onSuccess: (accepted) => {
      onClose();
      if (accepted.groupId) {
        track(accepted.groupId);
        toast.success(`Schedule fired (${accepted.memberCount} ${accepted.memberCount === 1 ? "flow" : "flows"}).`);
        navigate(`/runs/groups/${accepted.groupId}`);
      } else {
        toast.success("Schedule fired.");
        navigate(`/runs/${accepted.runId}`);
      }
    },
    onError: (error) => {
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error));
    },
  });

  const trigger = useMutation({
    mutationFn: runApi.trigger,
    onSuccess: (accepted) => {
      onClose();
      toast.success(`Run queued: ${effectiveFlow ?? accepted.runId}`);
      navigate(`/runs/${accepted.runId}`);
    },
    onError: (error) => {
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error));
    },
  });

  const repoOptions = useMemo<ComboOption[]>(
    () => (repos.data?.items ?? []).map((repo) => ({ value: repo.id, label: repo.name })),
    [repos.data],
  );
  const flowOptions = useMemo<ComboOption[]>(
    () => (pipelines.data?.items ?? []).map((p) => ({ value: p.name, label: p.name })),
    [pipelines.data],
  );

  const trimmedFrom = backfillFrom.trim();
  const trimmedTo = backfillTo.trim();
  const hasWindow = trimmedFrom !== "" || trimmedTo !== "";
  const hasPattern = filePattern.trim() !== "";
  const hasSourceFilter = sourceFilter.trim() !== "";

  // Client-side mirror of RunParameters.Validate, so obvious mistakes are caught before the round trip (the server
  // validates authoritatively and its ProblemDetails still renders if anything slips through).
  const windowError = assertionsOnly && (fullLoad || hasWindow || hasPattern)
    ? "Verify only cannot be combined with a full reprocess, a window, or a file pattern."
    : trimmedTo !== "" && trimmedFrom === ""
      ? "An end date needs a start date."
      : fullLoad && hasWindow
        ? "A full reprocess and a window are mutually exclusive."
        : null;

  const canSubmit = Boolean(effectiveRepoId) && Boolean(effectiveFlow) && windowError === null && !trigger.isPending;

  const submit = () => {
    trigger.mutate({
      repoId: effectiveRepoId!,
      flowName: (effectiveFlow ?? "").trim(),
      scope: "flow",
      pool: pool.trim() === "" ? null : pool.trim(),
      commitSha: commitSha.trim() === "" ? null : commitSha.trim(),
      fullLoad,
      backfillFrom: trimmedFrom !== "" ? `${trimmedFrom}:00Z` : null,
      backfillTo: trimmedTo !== "" ? `${trimmedTo}:00Z` : null,
      filePattern: hasPattern ? filePattern.trim() : null,
      sourceFilter: hasSourceFilter ? sourceFilter.trim() : null,
      assertionsOnly,
    });
  };

  return (
    <Sheet
      open={open}
      onOpenChange={(next) => {
        if (!next && !trigger.isPending) {
          onClose();
        }
      }}
    >
      <SheetContent className="w-full gap-0 sm:max-w-xl" data-testid="trigger-run-dialog">
        <SheetHeader>
          <SheetTitle>Trigger run</SheetTitle>
          <SheetDescription>
            Launch one flow now, with one-off parameters applied to this run only.
          </SheetDescription>
        </SheetHeader>

        <div className="flex flex-1 flex-col gap-4 overflow-y-auto px-4 pb-4">
          {trigger.isError && isApiError(trigger.error) && <CorrelationError error={trigger.error} />}

          {repoId ? null : (
            <ComboBoxField
              label="Repo"
              options={repoOptions}
              optionValue={(option) => option.value}
              optionLabel={(option) => option.label}
              value={selectedRepoId}
              onChange={(value) => {
                setSelectedRepoId(value);
                setSelectedFlow(null);
              }}
              loading={repos.isLoading}
              testId="trigger-repo"
            />
          )}

          {flowName ? (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor={`${idPrefix}-flow-locked`}>Flow</Label>
              <Input id={`${idPrefix}-flow-locked`} className="h-8 font-mono" value={flowName} disabled />
            </div>
          ) : (
            <ComboBoxField
              label="Flow"
              options={flowOptions}
              optionValue={(option) => option.value}
              optionLabel={(option) => option.label}
              value={selectedFlow}
              onChange={setSelectedFlow}
              disabled={!effectiveRepoId}
              loading={pipelines.isLoading}
              testId="trigger-flow"
            />
          )}

          <div className="flex flex-col gap-1.5">
            <Label htmlFor={`${idPrefix}-pool`}>Pool (optional)</Label>
            <Input
              id={`${idPrefix}-pool`}
              className="h-8"
              value={pool}
              onChange={(event) => setPool(event.target.value)}
              data-testid="trigger-pool"
            />
            <p className="text-xs text-muted-foreground">
              Route the run to workers serving this pool; empty runs on any node.
            </p>
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor={`${idPrefix}-commit`}>Commit SHA (optional)</Label>
            <Input
              id={`${idPrefix}-commit`}
              className="h-8 font-mono"
              value={commitSha}
              onChange={(event) => setCommitSha(event.target.value)}
              data-testid="trigger-commit"
            />
            <p className="text-xs text-muted-foreground">
              Pin the run to an exact git commit; empty pins to the repo&apos;s last synced commit.
            </p>
          </div>

          <div className="rounded-lg border border-border bg-muted/40 p-3" data-testid="trigger-parameters">
            <h3 className="text-[13px] font-medium">Run parameters</h3>
            <div className="mt-2 flex flex-col gap-3">
              <p className="text-xs text-muted-foreground">
                One-off overrides applied to this run only. The flow definition in git is unchanged.
              </p>
              <div className="flex flex-col gap-1">
                <Label className="flex items-center gap-2 text-[13px] font-normal">
                  <Switch
                    checked={fullLoad}
                    disabled={assertionsOnly}
                    onCheckedChange={setFullLoad}
                    data-testid="trigger-fullLoad"
                  />
                  Full reprocess
                </Label>
                <p className="pl-10 text-xs text-muted-foreground">
                  Ignore what was delivered before and process the whole source again.
                </p>
              </div>
              <div className="flex flex-col gap-1">
                <Label className="flex items-center gap-2 text-[13px] font-normal">
                  <Switch
                    checked={assertionsOnly}
                    disabled={fullLoad || hasWindow || hasPattern || hasSourceFilter}
                    onCheckedChange={setAssertionsOnly}
                    data-testid="trigger-assertionsOnly"
                  />
                  Verify only
                </Label>
                <p className="pl-10 text-xs text-muted-foreground">
                  Check the target against what the ledger says was delivered; nothing is written.
                </p>
              </div>
              <div className="flex flex-col gap-1.5">
                <span className="text-[13px] font-medium">Window</span>
                <DateRangeCalendar
                  from={backfillFrom}
                  to={backfillTo}
                  onChange={(from, to) => {
                    setBackfillFrom(from);
                    setBackfillTo(to);
                  }}
                  disabled={fullLoad || assertionsOnly}
                  testId="trigger-backfill"
                />
                <p className="text-xs text-muted-foreground">
                  Bound the run to the source data inside this window (UTC).
                </p>
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor={`${idPrefix}-file-pattern`}>File pattern</Label>
                <Input
                  id={`${idPrefix}-file-pattern`}
                  className="h-8 font-mono"
                  placeholder="wells_2026-03*.parquet"
                  value={filePattern}
                  onChange={(event) => setFilePattern(event.target.value)}
                  disabled={assertionsOnly}
                  data-testid="trigger-file-pattern"
                />
                <p className="text-xs text-muted-foreground">
                  Only the source files matching this glob are read.
                </p>
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor={`${idPrefix}-source-filter`}>Source filter</Label>
                <Input
                  id={`${idPrefix}-source-filter`}
                  className="h-8 font-mono"
                  value={sourceFilter}
                  onChange={(event) => setSourceFilter(event.target.value)}
                  disabled={assertionsOnly}
                  data-testid="trigger-source-filter"
                />
                <p className="text-xs text-muted-foreground">
                  An extra predicate the flow applies when it reads the source.
                </p>
              </div>
              {windowError !== null && (
                <p className="text-xs font-medium text-destructive" data-testid="trigger-backfill-error">
                  {windowError}
                </p>
              )}
            </div>
          </div>

          {relatedSchedules.length > 0 && (
            <div className="rounded-lg border border-border bg-muted/40 p-3" data-testid="trigger-schedules">
              <h3 className="flex items-center gap-2 text-[13px] font-medium">
                <CalendarClock className="size-4 text-muted-foreground" />
                Runs as part of
              </h3>
              <p className="mt-1 text-xs text-muted-foreground">
                Executing a schedule fires its whole member set in order, without moving the next scheduled fire.
              </p>
              <div className="mt-2 flex flex-col gap-2">
                {relatedSchedules.map((schedule) => (
                  <div
                    key={schedule.id}
                    className="flex items-center gap-2 rounded-md border border-border bg-background px-2.5 py-1.5"
                    data-testid={`trigger-schedule-${schedule.id}`}
                  >
                    <div className="min-w-0 grow">
                      <div className="truncate font-mono text-[12px] font-medium">{schedule.name}</div>
                      <div className="mt-0.5 flex flex-wrap items-center gap-1.5">
                        <Badge variant="outline" className="font-mono text-[11px]">
                          {describeCadence(schedule.cron, schedule.intervalSeconds)}
                        </Badge>
                        {schedule.paused && (
                          <Badge className="border-transparent bg-warning/15 text-warning">paused</Badge>
                        )}
                        {!schedule.enabled && (
                          <Badge variant="outline" className="text-muted-foreground">disabled</Badge>
                        )}
                      </div>
                    </div>
                    <Button
                      variant="outline"
                      size="sm"
                      className="h-8 shrink-0"
                      onClick={() => runSchedule.mutate(schedule.id)}
                      disabled={runSchedule.isPending}
                      data-testid={`trigger-schedule-run-${schedule.id}`}
                    >
                      {runSchedule.isPending && runSchedule.variables === schedule.id
                        ? <Loader2 className="animate-spin" />
                        : <Play />}
                      Execute schedule
                    </Button>
                  </div>
                ))}
              </div>
            </div>
          )}
        </div>

        <SheetFooter className="flex-row justify-end gap-2 border-t border-border">
          <Button variant="ghost" size="sm" onClick={onClose} disabled={trigger.isPending}>
            Cancel
          </Button>
          <Button size="sm" onClick={submit} disabled={!canSubmit} data-testid="trigger-submit">
            {trigger.isPending && <Loader2 className="animate-spin" />}
            Trigger
          </Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}
