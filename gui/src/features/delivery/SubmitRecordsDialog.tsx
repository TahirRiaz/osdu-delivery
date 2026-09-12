import { useEffect, useId, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useMutation, useQuery } from "@tanstack/react-query";
import { CircleAlert, Loader2 } from "lucide-react";
import { toast } from "sonner";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle,
} from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { isApiError } from "../../api/client";
import { deliveryApi, type DeliveryInlineRecord, type DeliverySourceContract } from "../../api/delivery";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

type Mode = "form" | "json";

/** The columns the form offers: what the mapping reads from the root row, then the flow's version column. */
function formColumns(contract: DeliverySourceContract): string[] {
  const columns = [...contract.recordColumns];
  for (const column of [contract.lastModifiedColumn, contract.fingerprintColumn]) {
    if (column !== null && !columns.some((c) => c.toLowerCase() === column.toLowerCase())) {
      columns.push(column);
    }
  }

  return columns;
}

/** Where the form says the record's payload files are, when the flow streams a payload. */
type PayloadInput = { name: string; location: string; hash: string } | null;

/** A starting point for the JSON tab: one record with every column the form offers, and one row per child scope. */
function recordsTemplate(contract: DeliverySourceContract): string {
  const blank = (columns: string[]) => Object.fromEntries(columns.map((column) => [column, ""]));
  const record: DeliveryInlineRecord = { record: blank(formColumns(contract)) };
  if (contract.scopes.length > 0) {
    record.scopes = Object.fromEntries(contract.scopes.map((scope) => [scope.scope, [blank(scope.columns)]]));
  }

  if (contract.payloadName !== null) {
    record.files = { [contract.payloadName]: contract.payloadHashRequired ? { location: "", hash: "" } : "" };
  }

  return JSON.stringify([record], null, 2);
}

/**
 * The filled-in form as one record; an empty field is left out, which the drop writes as null. The payload is pointed
 * at, never uploaded: what goes with the record is where its files already sit, and the hash when the flow needs one.
 */
function formRecord(fields: Record<string, string>, payload: PayloadInput): DeliveryInlineRecord {
  const record: DeliveryInlineRecord = { record: Object.fromEntries(Object.entries(fields).filter(([, value]) => value !== "")) };
  if (payload !== null && payload.location.trim() !== "") {
    record.files = {
      [payload.name]: payload.hash.trim() === ""
        ? payload.location.trim()
        : { location: payload.location.trim(), hash: payload.hash.trim() },
    };
  }

  return record;
}

/** Parses the JSON tab: an array of records, or one record object. The first problem found is the error. */
function parseRecords(text: string): { records: DeliveryInlineRecord[]; error: string | null } {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch (error) {
    return { records: [], error: `The records are not valid JSON: ${error instanceof Error ? error.message : String(error)}` };
  }

  const items: unknown[] = Array.isArray(parsed) ? parsed : [parsed];
  if (items.length === 0) {
    return { records: [], error: "Send at least one record." };
  }

  for (const [index, item] of items.entries()) {
    const record = typeof item === "object" && item !== null && !Array.isArray(item) ? (item as Record<string, unknown>).record : undefined;
    if (typeof record !== "object" || record === null || Array.isArray(record)) {
      return { records: [], error: `records[${index}] needs a "record" object holding the root row's columns.` };
    }
  }

  return { records: items as DeliveryInlineRecord[], error: null };
}

export interface SubmitRecordsDialogProps {
  open: boolean;
  onClose: () => void;
  pipelineId: string;
  flowName: string;
}

/**
 * Source records by hand: the same submission a source system makes through `POST /api/v1/delivery/submissions` with
 * `records`. The flow's pinned mapping renders them, and the run takes them through the regular delivery: change
 * detection, the ledger, the drain and the record history. One record through a form built from the columns the mapping
 * reads, or any number (with child rows) as JSON in the shape of a mapping fixture.
 */
export function SubmitRecordsDialog({ open, onClose, pipelineId, flowName }: SubmitRecordsDialogProps) {
  const navigate = useNavigate();
  const idPrefix = useId();
  const [mode, setMode] = useState<Mode>("form");
  const [fields, setFields] = useState<Record<string, string>>({});
  const [jsonText, setJsonText] = useState<string | null>(null);
  const [parameters, setParameters] = useState<Record<string, string>>({});
  const [preview, setPreview] = useState(false);
  const [force, setForce] = useState(false);
  const [submissionId, setSubmissionId] = useState("");
  const [payloadLocation, setPayloadLocation] = useState("");
  const [payloadHash, setPayloadHash] = useState("");

  const contract = useQuery({
    queryKey: ["delivery", "source-contract", pipelineId],
    queryFn: () => deliveryApi.sourceContract(pipelineId),
    enabled: open,
  });

  // What has been dropped off already, so a record can point at an upload instead of a hand-typed path.
  const dropOffs = useQuery({
    queryKey: ["delivery", "dropoffs", "complete"],
    queryFn: () => deliveryApi.dropOffs({ status: "complete", limit: 25 }),
    enabled: open,
  });

  useEffect(() => {
    if (open) {
      setMode("form");
      setFields({});
      setJsonText(null);
      setParameters({});
      setPreview(false);
      setForce(false);
      setSubmissionId("");
      setPayloadLocation("");
      setPayloadHash("");
    }
  }, [open]);

  const submit = useMutation({
    mutationFn: deliveryApi.submit,
    onSuccess: (accepted, request) => {
      onClose();
      const count = request.records?.length ?? 0;
      if (accepted.replayed) {
        toast.success(`These records were already accepted as submission ${accepted.submissionId?.slice(0, 8) ?? ""}; showing the run that took them.`);
      } else if (request.operation === "plan") {
        toast.success(`Preview of ${count} record${count === 1 ? "" : "s"} queued for ${accepted.flowName}.`);
      } else {
        toast.success(`${count} record${count === 1 ? "" : "s"} queued for delivery by ${accepted.flowName}.`);
      }

      navigate(`/runs/${accepted.runId}`);
    },
    onError: (error) => {
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error));
    },
  });

  const c = contract.data;
  const columns = c ? formColumns(c) : [];
  const json = jsonText ?? (c ? recordsTemplate(c) : "[]");
  const formBlank = Object.values(fields).every((value) => value === "");
  const payload: PayloadInput = c !== undefined && c.payloadName !== null
    ? { name: c.payloadName, location: payloadLocation, hash: payloadHash }
    : null;

  let records: DeliveryInlineRecord[] = [];
  let error: string | null = null;
  if (c !== undefined) {
    if (mode === "form") {
      records = [formRecord(fields, payload)];
      const emptyKey = c.naturalKey.find((column) => (fields[column] ?? "") === "");
      if (formBlank) {
        error = "Fill in the record's columns.";
      } else if (emptyKey !== undefined) {
        error = `The natural key column '${emptyKey}' is empty; the record's OSDU id is derived from it.`;
      } else if (payload !== null && payload.location.trim() === "") {
        error = `The flow streams the payload '${payload.name}', so the record says where its files are.`;
      } else if (payload !== null && c.payloadHashRequired && payload.hash.trim() === "") {
        error = `The flow decides payload changes by content hash, so the record carries the hash of its '${payload.name}' files.`;
      }
    } else {
      ({ records, error } = parseRecords(json));
    }

    const missingParameter = c.parameters.find((p) => p.required && p.default === null && (parameters[p.name] ?? "").trim() === "");
    if (error === null && missingParameter !== undefined) {
      error = `The flow parameter '${missingParameter.name}' is required.`;
    }

    if (error === null && submissionId.trim() !== "" && !UUID.test(submissionId.trim())) {
      error = "The submission id must be a UUID.";
    }

    if (error === null && records.length > c.maxRecords) {
      error = `One submission carries at most ${c.maxRecords} records; deliver a larger set as a drop.`;
    }
  }

  // An untouched form is not an error yet; it only keeps the submit button disabled.
  const shownError = mode === "form" && formBlank ? null : error;
  const canSubmit = c !== undefined && c.acceptsRecords && error === null && !submit.isPending;

  const changeMode = (next: string) => {
    if (next === "json" && jsonText === null && !formBlank) {
      setJsonText(JSON.stringify([formRecord(fields, payload)], null, 2));
    }

    setMode(next === "json" ? "json" : "form");
  };

  const send = () => {
    const values = Object.fromEntries(Object.entries(parameters).filter(([, value]) => value.trim() !== ""));
    submit.mutate({
      pipelineId,
      records,
      parameters: Object.keys(values).length > 0 ? values : null,
      operation: preview ? "plan" : "deliver",
      force,
      submissionId: submissionId.trim() === "" ? null : submissionId.trim(),
    });
  };

  return (
    <Sheet open={open} onOpenChange={(next) => { if (!next && !submit.isPending) { onClose(); } }}>
      <SheetContent className="w-full gap-0 sm:max-w-2xl" data-testid="submit-records-dialog">
        <SheetHeader>
          <SheetTitle>Submit records</SheetTitle>
          <SheetDescription>
            Send {flowName} source records the way a source system does through the API. The flow&apos;s mapping renders them
            into OSDU documents, and the run delivers them like any drop: change detection, the ledger and the record
            history all apply.
          </SheetDescription>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-4 overflow-y-auto px-4 pb-4">
          {contract.isError && (isApiError(contract.error)
            ? <CorrelationError error={contract.error} />
            : <p className="text-[13px] text-destructive">{String(contract.error)}</p>)}
          {submit.isError && isApiError(submit.error) && <CorrelationError error={submit.error} />}
          {c === undefined && !contract.isError && <Skeleton className="h-48 w-full rounded-lg" />}
          {c !== undefined && !c.acceptsRecords && (
            <Alert variant="destructive" data-testid="submit-records-refusal">
              <CircleAlert />
              <AlertDescription>{c.recordsRefusal}</AlertDescription>
            </Alert>
          )}
          {c !== undefined && c.acceptsRecords && (
            <>
              <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
                <Badge variant="outline" data-testid="submit-records-mapping">{c.mappingReference}</Badge>
                <Badge variant="outline">{c.protocol}</Badge>
                <span>Up to {c.maxRecords.toLocaleString()} records per submission.</span>
              </div>
              {c.mappingProblem !== null && (
                <Alert data-testid="submit-records-mapping-problem">
                  <CircleAlert />
                  <AlertDescription>{c.mappingProblem} The columns cannot be listed, so write the records in the JSON tab.</AlertDescription>
                </Alert>
              )}
              {c.parameters.length > 0 && (
                <div className="flex flex-col gap-2">
                  <h3 className="text-[13px] font-medium">Flow parameters</h3>
                  {c.parameters.map((p) => (
                    <div key={p.name} className="flex flex-col gap-1">
                      <Label htmlFor={`${idPrefix}-param-${p.name}`} className="font-mono text-[12px]">
                        {p.name}{p.required && p.default === null ? " (required)" : ""}
                      </Label>
                      <Input
                        id={`${idPrefix}-param-${p.name}`}
                        className="h-8 font-mono"
                        placeholder={p.default ?? ""}
                        value={parameters[p.name] ?? ""}
                        onChange={(event) => { const value = event.target.value; setParameters((current) => ({ ...current, [p.name]: value })); }}
                        data-testid={`submit-records-param-${p.name}`}
                      />
                      {p.description !== null && <p className="text-xs text-muted-foreground">{p.description}</p>}
                    </div>
                  ))}
                </div>
              )}
              <Tabs value={mode} onValueChange={changeMode}>
                <TabsList>
                  <TabsTrigger value="form" disabled={columns.length === 0} data-testid="submit-records-tab-form">One record</TabsTrigger>
                  <TabsTrigger value="json" data-testid="submit-records-tab-json">JSON</TabsTrigger>
                </TabsList>
                <TabsContent value="form" className="flex flex-col gap-3 pt-2">
                  {c.scopes.length > 0 && (
                    <p className="text-xs text-muted-foreground">
                      The mapping also reads child rows ({c.scopes.map((s) => s.scope).join(", ")}). A record with child rows is
                      written in the JSON tab.
                    </p>
                  )}
                  <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                    {columns.map((column) => (
                      <div key={column} className="flex flex-col gap-1">
                        <Label htmlFor={`${idPrefix}-field-${column}`} className="flex items-center gap-1.5 font-mono text-[12px]">
                          {column}
                          {c.naturalKey.includes(column) && <Badge variant="secondary" className="h-4 px-1 text-[10px]">key</Badge>}
                          {column === c.lastModifiedColumn && <Badge variant="secondary" className="h-4 px-1 text-[10px]">last modified</Badge>}
                          {column === c.fingerprintColumn && <Badge variant="secondary" className="h-4 px-1 text-[10px]">fingerprint</Badge>}
                        </Label>
                        <div className="flex gap-1">
                          <Input
                            id={`${idPrefix}-field-${column}`}
                            className="h-8 font-mono"
                            value={fields[column] ?? ""}
                            onChange={(event) => { const value = event.target.value; setFields((current) => ({ ...current, [column]: value })); }}
                            data-testid={`submit-records-field-${column}`}
                          />
                          {column === c.lastModifiedColumn && (
                            <Button
                              variant="outline"
                              size="sm"
                              className="h-8"
                              onClick={() => setFields((current) => ({ ...current, [column]: new Date().toISOString() }))}
                              data-testid="submit-records-now"
                            >
                              Now
                            </Button>
                          )}
                        </div>
                      </div>
                    ))}
                  </div>
                  <p className="text-xs text-muted-foreground">
                    An empty field is sent as absent, and the drop writes it as null. A record whose last-modified moment is not
                    later than the version already delivered is skipped, so a change needs a later one.
                  </p>
                  {c.payloadName !== null && (
                    <div className="flex flex-col gap-3 rounded-lg border border-border p-3" data-testid="submit-records-payload">
                      <div className="flex flex-col gap-1">
                        <h3 className="text-[13px] font-medium">Payload files: {c.payloadName}</h3>
                        <p className="text-xs text-muted-foreground">
                          Where the files already sit. Nothing is uploaded here: the node opens the location with its own identity
                          when the run delivers, and again on every retry.
                        </p>
                      </div>
                      {(dropOffs.data ?? []).length > 0 && (
                        <div className="flex flex-col gap-1">
                          <Label htmlFor={`${idPrefix}-payload-dropoff`}>Use a drop-off</Label>
                          <select
                            id={`${idPrefix}-payload-dropoff`}
                            className="h-8 rounded-md border border-input bg-transparent px-2 text-[13px]"
                            value=""
                            onChange={(event) => { if (event.target.value !== "") { setPayloadLocation(event.target.value); } }}
                            data-testid="submit-records-payload-dropoff"
                          >
                            <option value="">Pick an upload to fill the location</option>
                            {(dropOffs.data ?? []).map((d) => (
                              <option key={d.dropOffId} value={d.location}>
                                {d.label ?? d.dropOffId.slice(0, 8)} ({d.fileCount} file{d.fileCount === 1 ? "" : "s"})
                              </option>
                            ))}
                          </select>
                        </div>
                      )}
                      <div className="flex flex-col gap-1">
                        <Label htmlFor={`${idPrefix}-payload-location`} className="font-mono text-[12px]">location (required)</Label>
                        <Input
                          id={`${idPrefix}-payload-location`}
                          className="h-8 font-mono"
                          placeholder={c.payloadRoots[0] ?? "the folder or glob the files are in"}
                          value={payloadLocation}
                          onChange={(event) => setPayloadLocation(event.target.value)}
                          data-testid="submit-records-payload-location"
                        />
                        {c.payloadRoots.length > 0 && (
                          <p className="text-xs text-muted-foreground">
                            This flow reads files under <span className="font-mono">{c.payloadRoots.join(", ")}</span>.
                          </p>
                        )}
                      </div>
                      <div className="flex flex-col gap-1">
                        <Label htmlFor={`${idPrefix}-payload-hash`} className="font-mono text-[12px]">
                          hash {c.payloadHashRequired ? "(required)" : "(optional)"}
                        </Label>
                        <Input
                          id={`${idPrefix}-payload-hash`}
                          className="h-8 font-mono"
                          value={payloadHash}
                          onChange={(event) => setPayloadHash(event.target.value)}
                          data-testid="submit-records-payload-hash"
                        />
                        <p className="text-xs text-muted-foreground">
                          {c.payloadHashRequired
                            ? "The flow decides payload changes by content hash, so each record carries the hash of its files."
                            : "The flow watches the files' modified times, so a hash is sent only when the source has one."}
                        </p>
                      </div>
                    </div>
                  )}
                </TabsContent>
                <TabsContent value="json" className="flex flex-col gap-2 pt-2">
                  <CodeView value={json} language="json" height={360} readOnly={false} onChange={setJsonText} data-testid="submit-records-json" />
                  <p className="text-xs text-muted-foreground">
                    An array of records, each {"{ \"record\": { column: value }, \"scopes\": { scope: [ rows ] } }"}: the shape of a
                    mapping fixture. Values are strings, numbers, booleans or null.
                    {c.payloadName !== null && ` Each record also carries "files": { "${c.payloadName}": "where its files are" }, or { "location": ..., "hash": ... } to send the hash with it.`}
                  </p>
                </TabsContent>
              </Tabs>
              <div className="flex flex-col gap-2">
                <Label className="flex items-center gap-2 text-[13px] font-normal">
                  <Switch checked={preview} onCheckedChange={setPreview} data-testid="submit-records-preview" />
                  Preview only: render and plan the records, send nothing
                </Label>
                <Label className="flex items-center gap-2 text-[13px] font-normal">
                  <Switch checked={force} onCheckedChange={setForce} data-testid="submit-records-force" />
                  Force: plan past the change gates
                </Label>
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor={`${idPrefix}-id`}>Submission id (optional)</Label>
                <Input
                  id={`${idPrefix}-id`}
                  className="h-8 font-mono"
                  placeholder="a new id when left empty"
                  value={submissionId}
                  onChange={(event) => setSubmissionId(event.target.value)}
                  data-testid="submit-records-id"
                />
                <p className="text-xs text-muted-foreground">
                  The idempotency key. The same records under the same id again answer with the run that took them; different
                  records under it are refused.
                </p>
              </div>
              {shownError !== null && <p className="text-xs font-medium text-destructive" data-testid="submit-records-error">{shownError}</p>}
            </>
          )}
        </div>
        <SheetFooter className="flex-row justify-end gap-2 border-t border-border">
          <Button variant="ghost" size="sm" onClick={onClose} disabled={submit.isPending}>Cancel</Button>
          <Button size="sm" disabled={!canSubmit} onClick={send} data-testid="submit-records-submit">
            {submit.isPending && <Loader2 className="animate-spin" />}
            {preview ? "Preview" : "Submit"}
          </Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}
