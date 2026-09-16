import { useEffect, useId, useMemo, useState } from "react";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import type { TriggerBodyContribution, TriggerFieldsProps } from "@/modules/registry";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const IDENTIFIER = /^[A-Za-z_][A-Za-z0-9_]*$/;

/** The operation a kind's run performs when the dialog could not learn the registered ones. */
const DEFAULT_OPERATION: Record<string, string> = {
  delivery: "deliver",
  retrieval: "retrieve",
  cache: "refresh",
};

type RedeliverScope = "all" | "metadata" | "payload";

const REDELIVER_SCOPES: readonly { value: RedeliverScope; label: string }[] = [
  { value: "all", label: "Metadata and payload" },
  { value: "metadata", label: "Metadata only" },
  { value: "payload", label: "Payload only" },
];

/** The payload keys these fields own; anything else the run being repeated carried goes with it unchanged. */
const OWNED_KEYS = new Set(["force", "submissionId", "recordKeys", "redeliver"]);

const FORCE_HINTS: Record<string, string> = {
  delivery: "Push past the change gates: plan every record even when no source table advanced, re-plan a completed submission, verify records verified recently.",
  retrieval: "Start again at the declared start instead of continuing from the last run's watermark.",
  cache: "Capture every declared type again, even where the last refresh found nothing to change.",
};

function isRedeliverScope(value: unknown): value is RedeliverScope {
  return value === "all" || value === "metadata" || value === "payload";
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

/**
 * The trigger dialog's fields for delivery, retrieval and cache flows: force, the flow's parameter values, and for a
 * delivery flow the submission to work on, the records to scope the run to, and the part of them to send again. Which
 * fields apply follows the operation picked; reading every row of the scope again is the flow kind's own `replan`
 * operation rather than a field here. A run being repeated opens with what it was given, and keeps any other part of
 * its payload (the key slices a fan-out member took, a relanded submission) as it was.
 */
export function DeliveryTriggerFields({ flowKind, operation, initialValues, initialPayload, onChange }: TriggerFieldsProps) {
  const idPrefix = useId();
  const [force, setForce] = useState(() => initialPayload?.force === true);
  const [valuesText, setValuesText] = useState(
    () => Object.entries(initialValues).map(([name, value]) => `${name}=${value}`).join("\n"),
  );
  const [submissionId, setSubmissionId] = useState(
    () => (typeof initialPayload?.submissionId === "string" ? initialPayload.submissionId : ""),
  );
  const [recordKeysText, setRecordKeysText] = useState(() => (
    Array.isArray(initialPayload?.recordKeys)
      ? initialPayload.recordKeys.filter((key): key is string => typeof key === "string").join("\n")
      : ""
  ));
  const [redeliver, setRedeliver] = useState<RedeliverScope>(
    () => (isRedeliverScope(initialPayload?.redeliver) ? initialPayload.redeliver : "all"),
  );
  const [carried] = useState<Record<string, unknown>>(
    () => Object.fromEntries(Object.entries(initialPayload ?? {}).filter(([key]) => !OWNED_KEYS.has(key))),
  );

  const effectiveOperation = operation ?? DEFAULT_OPERATION[flowKind] ?? null;
  const deliveryKind = flowKind === "delivery";
  // Every operation that reads the ingestion tables is given the flow's parameter values: they fill the record
  // scope's predicate and the work location, so a run without them reads nothing.
  const takesValues = !deliveryKind
    || effectiveOperation === "deliver" || effectiveOperation === "plan"
    || effectiveOperation === "intake" || effectiveOperation === "replan";
  const takesSubmission = deliveryKind
    && (effectiveOperation === "deliver" || effectiveOperation === "intake" || effectiveOperation === "drain");
  const takesRecordScope = deliveryKind && (effectiveOperation === "deliver" || effectiveOperation === "verify");
  const recordKeys = useMemo(() => lines(recordKeysText), [recordKeysText]);
  const takesRedeliver = takesRecordScope && effectiveOperation === "deliver" && recordKeys.length > 0;

  const body = useMemo<TriggerBodyContribution>(() => {
    const parsed = takesValues ? parseValues(valuesText) : { values: {}, error: null };
    const trimmedSubmission = submissionId.trim();
    // Client-side mirror of the kind's own validation, so obvious mistakes are caught before the round trip; the control
    // plane validates authoritatively and its problem details still render if anything slips through.
    const error = parsed.error
      ?? (takesSubmission && trimmedSubmission !== "" && !UUID.test(trimmedSubmission)
        ? "The submission id must be a UUID."
        : takesRecordScope && recordKeys.some((key) => !UUID.test(key))
          ? "Every record key must be a UUID (one per line)."
          : null);

    const payload: Record<string, unknown> = { ...carried };
    if (force) {
      payload.force = true;
    }

    if (takesSubmission && trimmedSubmission !== "") {
      payload.submissionId = trimmedSubmission;
    }

    if (takesRecordScope && recordKeys.length > 0) {
      payload.recordKeys = recordKeys;
    }

    if (takesRedeliver) {
      payload.redeliver = redeliver;
    }

    return {
      values: Object.keys(parsed.values).length > 0 ? parsed.values : undefined,
      payload: Object.keys(payload).length > 0 ? payload : undefined,
      error,
    };
  }, [
    carried, force, recordKeys, redeliver, submissionId, takesRecordScope, takesRedeliver, takesSubmission,
    takesValues, valuesText,
  ]);

  useEffect(() => {
    onChange(body);
  }, [body, onChange]);

  return (
    <div className="flex flex-col gap-3" data-testid="trigger-kind-fields">
      <div className="flex flex-col gap-1">
        <Label className="flex items-center gap-2 text-[13px] font-normal">
          <Switch checked={force} onCheckedChange={setForce} data-testid="trigger-force" />
          Force
        </Label>
        <p className="pl-10 text-xs text-muted-foreground">
          {FORCE_HINTS[flowKind] ?? "Run past the change gates that would otherwise skip work."}
        </p>
      </div>
      {takesValues && (
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
      )}
      {takesSubmission && (
        <div className="flex flex-col gap-1.5">
          <Label htmlFor={`${idPrefix}-submission`}>
            {effectiveOperation === "drain" ? "Submission to drain" : effectiveOperation === "intake" ? "Submission" : "Re-run submission"}
          </Label>
          <Input
            id={`${idPrefix}-submission`}
            className="h-8 font-mono"
            value={submissionId}
            onChange={(event) => setSubmissionId(event.target.value)}
            data-testid="trigger-submission"
          />
          <p className="text-xs text-muted-foreground">
            {effectiveOperation === "drain"
              ? "Deliver the pending batches of this submission; empty drains every pending record of the flow."
              : "Work on one submission, with the parameters it was received with."}
          </p>
        </div>
      )}
      {takesRecordScope && (
        <div className="flex flex-col gap-1.5">
          <Label htmlFor={`${idPrefix}-records`}>
            {effectiveOperation === "verify" ? "Verify only these records" : "Redeliver these records"}
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
            {effectiveOperation === "verify"
              ? "Delivery keys to check; empty verifies the flow's delivered records."
              : "Delivery keys to send again regardless of what OSDU holds; empty delivers what changed."}
          </p>
        </div>
      )}
      {takesRedeliver && (
        <div className="flex flex-col gap-1.5">
          <Label htmlFor={`${idPrefix}-redeliver`}>Send again</Label>
          <Select value={redeliver} onValueChange={(value) => { if (isRedeliverScope(value)) { setRedeliver(value); } }}>
            <SelectTrigger id={`${idPrefix}-redeliver`} size="sm" className="h-8 w-full" data-testid="trigger-redeliver">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {REDELIVER_SCOPES.map((scope) => (
                <SelectItem key={scope.value} value={scope.value}>{scope.label}</SelectItem>
              ))}
            </SelectContent>
          </Select>
          <p className="text-xs text-muted-foreground">
            Which part of the scoped records goes to OSDU again: the record and its payload files, or one of them.
          </p>
        </div>
      )}
    </div>
  );
}
