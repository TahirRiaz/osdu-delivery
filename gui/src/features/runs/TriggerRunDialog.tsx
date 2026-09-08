import { useEffect, useId, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useMutation, useQuery } from "@tanstack/react-query";
import { CalendarClock, Loader2, Play } from "lucide-react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import {
  Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle,
} from "@/components/ui/sheet";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import { isApiError } from "../../api/client";
import { pipelineApi, repoApi, runApi, scheduleApi } from "../../api/endpoints";
import type { RunOperation, RunParameters } from "../../api/types";
import { RUN_OPERATIONS } from "../../api/types";
import { ComboBoxField } from "../../components/ComboBoxField";
import { CorrelationError } from "../../components/CorrelationError";
import { useRunDock } from "./RunDockContext";

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
  /** Prior-run parameters to prefill (Re-run, or a record page asking for a scoped redelivery or verify). */
  initialParameters?: RunParameters;
}

const OPERATION_LABELS: Record<RunOperation, string> = {
  "deliver": "Deliver",
  "verify": "Verify (drift check)",
  "plan": "Plan (dry run)",
  "known-state": "Publish known state",
  "intake": "Intake (plan into batches)",
  "drain": "Drain (deliver pending batches)",
};

const OPERATION_HINTS: Record<RunOperation, string> = {
  "deliver": "Read the flow's drop, plan it against the ledger, and deliver what changed.",
  "verify": "Read delivered records back from OSDU and compare versions; drifted records are queued for redelivery when the flow reconciles.",
  "plan": "Render and compare only, and report what a deliver would do. Nothing is written to OSDU or the ledger.",
  "known-state": "Publish the compact known-state file the preparing side reads before its next drop.",
  "intake": "Read the flow's drop, plan it against the ledger and write the rendered documents to work batches; nothing reaches OSDU until a drain.",
  "drain": "Deliver the pending work batches of a submission (or of the whole flow) to OSDU; the drop is not read.",
};

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const IDENTIFIER = /^[A-Za-z_][A-Za-z0-9_]*$/;

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

/** The non-blank, trimmed lines of a textarea. */
function lines(text: string): string[] {
  return text.split(/\r?\n/).map((line) => line.trim()).filter((line) => line !== "");
}

/** Parses "name=value" lines into the flow's parameter values; the first malformed line is the error. */
function parseValues(text: string): { values: Record<string, string>; error: string | null } {
  const values: Record<string, string> = {};
  for (const line of lines(text)) {
    const at = line.indexOf("=");
    const name = at > 0 ? line.slice(0, at).trim() : "";
    if (at <= 0 || !IDENTIFIER.test(name)) {
      return { values, error: `Parameter '${line}' must be written as name=value (the name an identifier).` };
    }

    values[name] = line.slice(at + 1);
  }

  return { values, error: null };
}

interface ComboOption {
  value: string;
  label: string;
}

/**
 * The single trigger-run path in the GUI: launched from the runs page (free choice of repo + flow), a pipeline's
 * detail page (prefilled), a run's Re-run (prefilled with the prior parameters), and a record's redeliver or
 * verify action (prefilled with the record scope). A run POSTs one flow and navigates to it. The schedules the
 * flow is a member of are listed alongside, since firing one runs the whole member set in order: that is how a
 * source is run as a whole.
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
  const [operation, setOperation] = useState<RunOperation>("deliver");
  const [force, setForce] = useState(false);
  const [valuesText, setValuesText] = useState("");
  const [drop, setDrop] = useState("");
  const [submissionId, setSubmissionId] = useState("");
  const [recordKeysText, setRecordKeysText] = useState("");
  const [publishTo, setPublishTo] = useState("");

  // Seed the form once per open, so a Re-run opens with the prior run's parameters and a fresh launch opens clean.
  useEffect(() => {
    if (open) {
      setOperation(initialParameters?.operation ?? "deliver");
      setForce(initialParameters?.force ?? false);
      setValuesText(Object.entries(initialParameters?.values ?? {}).map(([name, value]) => `${name}=${value}`).join("\n"));
      setDrop(initialParameters?.drop ?? "");
      setSubmissionId(initialParameters?.submissionId ?? "");
      setRecordKeysText((initialParameters?.recordKeys ?? []).join("\n"));
      setPublishTo(initialParameters?.publishTo ?? "");
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

  const readsDrop = operation === "deliver" || operation === "plan" || operation === "intake";
  const takesSubmission = operation === "deliver" || operation === "intake" || operation === "drain";
  const takesRecordScope = operation === "deliver" || operation === "verify";
  const parsedValues = parseValues(valuesText);
  const recordKeys = lines(recordKeysText);
  const trimmedSubmission = submissionId.trim();
  const trimmedPublishTo = publishTo.trim();

  // Client-side mirror of RunParameters.Validate, so obvious mistakes are caught before the round trip (the server
  // validates authoritatively and its ProblemDetails still renders if anything slips through).
  const parameterError = readsDrop && parsedValues.error !== null
    ? parsedValues.error
    : takesSubmission && trimmedSubmission !== "" && !UUID.test(trimmedSubmission)
      ? "The submission id must be a UUID."
      : takesRecordScope && recordKeys.some((key) => !UUID.test(key))
        ? "Every record key must be a UUID (one per line)."
        : null;

  const canSubmit = Boolean(effectiveRepoId) && Boolean(effectiveFlow) && parameterError === null && !trigger.isPending;

  const submit = () => {
    trigger.mutate({
      repoId: effectiveRepoId!,
      flowName: (effectiveFlow ?? "").trim(),
      scope: "flow",
      pool: pool.trim() === "" ? null : pool.trim(),
      commitSha: commitSha.trim() === "" ? null : commitSha.trim(),
      operation,
      force,
      values: readsDrop && Object.keys(parsedValues.values).length > 0 ? parsedValues.values : undefined,
      drop: readsDrop && drop.trim() !== "" ? drop.trim() : null,
      submissionId: takesSubmission && trimmedSubmission !== "" ? trimmedSubmission : null,
      recordKeys: takesRecordScope && recordKeys.length > 0 ? recordKeys : undefined,
      publishTo: operation === "known-state" && trimmedPublishTo !== "" ? trimmedPublishTo : null,
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
              <div className="flex flex-col gap-1.5">
                <Label htmlFor={`${idPrefix}-operation`}>Operation</Label>
                <Select value={operation} onValueChange={(value) => setOperation(value as RunOperation)}>
                  <SelectTrigger id={`${idPrefix}-operation`} size="sm" className="h-8 w-full" data-testid="trigger-operation">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {RUN_OPERATIONS.map((option) => (
                      <SelectItem key={option} value={option}>{OPERATION_LABELS[option]}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <p className="text-xs text-muted-foreground">{OPERATION_HINTS[operation]}</p>
              </div>
              <div className="flex flex-col gap-1">
                <Label className="flex items-center gap-2 text-[13px] font-normal">
                  <Switch checked={force} onCheckedChange={setForce} data-testid="trigger-force" />
                  Force
                </Label>
                <p className="pl-10 text-xs text-muted-foreground">
                  Push past the change gates: plan every record even when no source table advanced, re-plan a
                  completed submission, verify records verified recently.
                </p>
              </div>
              {readsDrop && (
                <>
                  <div className="flex flex-col gap-1.5">
                    <Label htmlFor={`${idPrefix}-drop`}>Drop location</Label>
                    <Input
                      id={`${idPrefix}-drop`}
                      className="h-8 font-mono"
                      placeholder="abfss://drops@lake.dfs.core.windows.net/recall/2026-09-01"
                      value={drop}
                      onChange={(event) => setDrop(event.target.value)}
                      data-testid="trigger-drop"
                    />
                    <p className="text-xs text-muted-foreground">
                      Read this drop instead of the flow&apos;s declared source location.
                    </p>
                  </div>
                  <div className="flex flex-col gap-1.5">
                    <Label htmlFor={`${idPrefix}-values`}>Flow parameters</Label>
                    <Textarea
                      id={`${idPrefix}-values`}
                      className="min-h-16 font-mono text-[12px]"
                      placeholder={"logSource=north\nregion=NO"}
                      value={valuesText}
                      onChange={(event) => setValuesText(event.target.value)}
                      data-testid="trigger-values"
                    />
                    <p className="text-xs text-muted-foreground">
                      Values for the parameters the flow declares, one name=value per line.
                    </p>
                  </div>
                </>
              )}
              {takesSubmission && (
                <div className="flex flex-col gap-1.5">
                  <Label htmlFor={`${idPrefix}-submission`}>{operation === "drain" ? "Submission to drain" : operation === "intake" ? "Submission" : "Re-run submission"}</Label>
                  <Input
                    id={`${idPrefix}-submission`}
                    className="h-8 font-mono"
                    value={submissionId}
                    onChange={(event) => setSubmissionId(event.target.value)}
                    data-testid="trigger-submission"
                  />
                  <p className="text-xs text-muted-foreground">
                    {operation === "drain"
                      ? "Deliver the pending batches of this submission; empty drains every pending record of the flow."
                      : "Work on one submission from its own drop, with the parameters it was received with."}
                  </p>
                </div>
              )}
              {takesRecordScope && (
                <div className="flex flex-col gap-1.5">
                  <Label htmlFor={`${idPrefix}-records`}>
                    {operation === "verify" ? "Verify only these records" : "Redeliver these records"}
                  </Label>
                  <Textarea
                    id={`${idPrefix}-records`}
                    className="min-h-16 font-mono text-[12px]"
                    placeholder="one delivery key per line"
                    value={recordKeysText}
                    onChange={(event) => setRecordKeysText(event.target.value)}
                    data-testid="trigger-record-keys"
                  />
                  <p className="text-xs text-muted-foreground">
                    {operation === "verify"
                      ? "Delivery keys to check; empty verifies the flow's delivered records."
                      : "Delivery keys to send again regardless of what OSDU holds; empty delivers what changed."}
                  </p>
                </div>
              )}
              {operation === "known-state" && (
                <div className="flex flex-col gap-1.5">
                  <Label htmlFor={`${idPrefix}-publish-to`}>Publish to</Label>
                  <Input
                    id={`${idPrefix}-publish-to`}
                    className="h-8 font-mono"
                    placeholder="abfss://drops@lake.dfs.core.windows.net/recall/known-state"
                    value={publishTo}
                    onChange={(event) => setPublishTo(event.target.value)}
                    data-testid="trigger-publish-to"
                  />
                  <p className="text-xs text-muted-foreground">
                    The directory or storage prefix the preparing side reads the known state from; empty uses the location the
                    flow declares under source.knownState.
                  </p>
                </div>
              )}
              {parameterError !== null && (
                <p className="text-xs font-medium text-destructive" data-testid="trigger-parameters-error">
                  {parameterError}
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
