import { useId, useState } from "react";
import { Link as RouterLink, useNavigate } from "react-router-dom";
import { useMutation, useQuery } from "@tanstack/react-query";
import { CircleAlert, Loader2, TriangleAlert } from "lucide-react";
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
import { isApiError } from "@/api/client";
import {
  deliveryApi, type DeliveryInlineRecord, type DeliverySourceColumnUse, type DeliverySourceContract,
} from "../../api/delivery";
import { CodeView } from "@/components/CodeView";
import { CorrelationError } from "@/components/CorrelationError";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** The longest reference the API takes; checked here so the dialog says so before the request goes. */
const MaxReferenceLength = 200;

type Mode = "form" | "json";

/** The columns the form offers: what the mapping reads from the dataset row, then the flow's version column. */
function formColumns(contract: DeliverySourceContract): string[] {
  const columns = contract.columns.map((column) => column.name);
  const version = contract.lastModifiedColumn;
  if (version !== null && !columns.some((c) => c.toLowerCase() === version.toLowerCase())) {
    columns.push(version);
  }

  return columns;
}

/** The cached type a findBy use reads, as the mapping writes it (cache.Wellbore), from the entry's source or its findBy line. */
function cachedTypeOf(use: DeliverySourceColumnUse): string | null {
  const text = use.source !== null && use.source.startsWith("cache.")
    ? use.source
    : (use.findBy?.split("=")[0].trim() ?? null);
  if (text === null || !text.startsWith("cache.")) {
    return null;
  }

  const parts = text.split(".");
  return parts.length >= 2 ? `${parts[0]}.${parts[1]}` : null;
}

/**
 * What the mapping does with a column, on one line: the variables it fills, the cached records it finds, and the entries
 * whose condition it decides. A phrase appears once, however many entries share it.
 */
function describeUses(uses: DeliverySourceColumnUse[]): string {
  const phrases: string[] = [];
  for (const use of uses) {
    let phrase: string;
    if (use.role === "findBy") {
      const type = cachedTypeOf(use);
      phrase = type === null ? `finds a cached record for ${use.target}` : `finds ${type} for ${use.target}`;
    } else if (use.role === "appliesWhen") {
      phrase = `decides ${use.target}`;
    } else {
      phrase = `fills ${use.target}${use.required ? "" : " (optional)"}`;
    }

    if (use.role !== "appliesWhen" && use.modifiers.length > 0) {
      phrase += ` after ${use.modifiers.join(", ")}`;
    }

    if (!phrases.includes(phrase)) {
      phrases.push(phrase);
    }
  }

  return phrases.join("; ");
}

/** Where the form says one of the record's payloads already sits. */
interface PayloadInput {
  name: string;
  location: string;
  hash: string;
}

/** A starting point for the JSON tab: one record with every column the form offers, and one blank row per child dataset. */
function recordsTemplate(contract: DeliverySourceContract): string {
  const blank = (columns: string[]) => Object.fromEntries(columns.map((column) => [column, ""]));
  const record: DeliveryInlineRecord = { record: blank(formColumns(contract)) };
  if (contract.datasets.length > 0) {
    record.datasets = Object.fromEntries(
      contract.datasets.map((dataset) => [dataset.name, [blank(dataset.columns.map((column) => column.name))]]),
    );
  }

  if (contract.payloads.length > 0) {
    record.files = Object.fromEntries(
      contract.payloads.map((name) => [name, contract.payloadHashRequired ? { location: "", hash: "" } : ""]),
    );
  }

  return JSON.stringify([record], null, 2);
}

/**
 * The filled-in form as one record; an empty field is left out, which the flow reads as null. A payload is pointed
 * at, never uploaded: what goes with the record is where its files already sit, and the hash when the flow needs one.
 */
function formRecord(fields: Record<string, string>, payloads: readonly PayloadInput[]): DeliveryInlineRecord {
  const record: DeliveryInlineRecord = { record: Object.fromEntries(Object.entries(fields).filter(([, value]) => value !== "")) };
  const files = payloads.filter((payload) => payload.location.trim() !== "");
  if (files.length > 0) {
    record.files = Object.fromEntries(files.map((payload) => [
      payload.name,
      payload.hash.trim() === ""
        ? payload.location.trim()
        : { location: payload.location.trim(), hash: payload.hash.trim() },
    ]));
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
      return { records: [], error: `records[${index}] needs a "record" object holding the dataset row's columns.` };
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
  const [reference, setReference] = useState("");
  const [payloadFiles, setPayloadFiles] = useState<Record<string, { location: string; hash: string }>>({});

  const contract = useQuery({
    queryKey: ["delivery", "source-contract", pipelineId],
    queryFn: () => deliveryApi.sourceContract(pipelineId),
    enabled: open,
  });

  // Every opening starts from an empty form.
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setMode("form");
      setFields({});
      setJsonText(null);
      setParameters({});
      setPreview(false);
      setForce(false);
      setSubmissionId("");
      setPayloadFiles({});
    }
  }

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
  const columnInfo = new Map((c?.columns ?? []).map((column) => [column.name, column]));
  const json = jsonText ?? (c ? recordsTemplate(c) : "[]");
  const formBlank = Object.values(fields).every((value) => value === "");
  const payloads: PayloadInput[] = (c?.payloads ?? []).map((name) => ({
    name,
    location: payloadFiles[name]?.location ?? "",
    hash: payloadFiles[name]?.hash ?? "",
  }));

  let records: DeliveryInlineRecord[] = [];
  let error: string | null = null;
  if (c !== undefined) {
    if (mode === "form") {
      records = [formRecord(fields, payloads)];
      const emptyKey = c.key.find((column) => (fields[column] ?? "") === "");
      const withoutLocation = payloads.find((payload) => payload.location.trim() === "");
      const withoutHash = c.payloadHashRequired ? payloads.find((payload) => payload.hash.trim() === "") : undefined;
      if (formBlank) {
        error = "Fill in the record's columns.";
      } else if (emptyKey !== undefined) {
        error = `The key column '${emptyKey}' is empty; the record's OSDU id is derived from it.`;
      } else if (withoutLocation !== undefined) {
        error = `The flow streams the payload '${withoutLocation.name}', so the record says where its files are.`;
      } else if (withoutHash !== undefined) {
        error = `The flow decides payload changes by content hash, so the record carries the hash of its '${withoutHash.name}' files.`;
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
      error = `One submission carries at most ${c.maxRecords} records; send a larger set as several submissions.`;
    }

    if (error === null && reference.trim().length > MaxReferenceLength) {
      error = `The reference is at most ${MaxReferenceLength} characters; it names the submission in your system, so a name is what belongs there.`;
    }
  }

  // An untouched form is not an error yet; it only keeps the submit button disabled.
  const shownError = mode === "form" && formBlank ? null : error;
  const canSubmit = c !== undefined && c.acceptsRecords && error === null && !submit.isPending;

  const changeMode = (next: string) => {
    if (next === "json" && jsonText === null && !formBlank) {
      setJsonText(JSON.stringify([formRecord(fields, payloads)], null, 2));
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
      reference: reference.trim() === "" ? null : reference.trim(),
    });
  };

  return (
    <Sheet open={open} onOpenChange={(next) => { if (!next && !submit.isPending) { onClose(); } }}>
      <SheetContent className="w-full gap-0 sm:max-w-2xl" data-testid="submit-records-dialog">
        <SheetHeader>
          <SheetTitle>Submit records</SheetTitle>
          <SheetDescription>
            Send {flowName} source records the way a source system does through the API. The flow&apos;s mapping renders them
            into OSDU documents, and the run delivers them like any other submission: change detection, the ledger and the record
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
                {c.template !== null && (
                  <Badge
                    asChild
                    variant="outline"
                    className={c.template.saved ? "font-mono" : "border-warning/50 bg-warning/10 font-mono text-warning"}
                  >
                    <RouterLink
                      to={c.template.saved
                        ? `/delivery/templates?${new URLSearchParams({ kind: c.template.kind, version: c.template.version }).toString()}`
                        : "/delivery/templates"}
                      data-testid="submit-records-template"
                      data-saved={c.template.saved ? "true" : "false"}
                    >
                      {!c.template.saved && <TriangleAlert />}
                      <span>{c.template.kind}</span>
                      <span className={c.template.saved ? "text-muted-foreground" : undefined}>{c.template.version}</span>
                      {!c.template.saved && <span className="font-sans">not saved</span>}
                    </RouterLink>
                  </Badge>
                )}
                <Badge variant="outline">{c.protocol}</Badge>
                <span>Up to {c.maxRecords.toLocaleString()} records per submission.</span>
              </div>
              {c.mappingProblem !== null && (
                <Alert data-testid="submit-records-mapping-problem">
                  <CircleAlert />
                  <AlertDescription>
                    <p>
                      {c.mappingProblem}
                      {c.columns.length === 0 && " The columns cannot be listed, so write the records in the JSON tab."}
                    </p>
                  </AlertDescription>
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
                  {c.datasets.length > 0 && (
                    <div className="flex flex-col gap-1 text-xs text-muted-foreground" data-testid="submit-records-datasets">
                      <p>The mapping also reads child datasets. A record with child rows is written in the JSON tab.</p>
                      <ul className="flex flex-col gap-0.5">
                        {c.datasets.map((dataset) => (
                          <li key={dataset.name} data-testid={`submit-records-dataset-${dataset.name}`}>
                            <span className="font-mono text-foreground">{dataset.name}</span>
                            {dataset.fills.length > 0
                              ? (
                                <>
                                  {" fills "}
                                  <span className="font-mono">
                                    {dataset.fills.map((fill) => `${fill.target}${fill.required ? "" : " (optional)"}`).join(", ")}
                                  </span>
                                </>
                              )
                              : " is read by the mapping"}
                            {dataset.columns.length > 0 && (
                              <>
                                {", with the columns "}
                                <span className="font-mono">{dataset.columns.map((column) => column.name).join(", ")}</span>
                              </>
                            )}
                          </li>
                        ))}
                      </ul>
                    </div>
                  )}
                  <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                    {columns.map((column) => (
                      <div key={column} className="flex flex-col gap-1">
                        <Label htmlFor={`${idPrefix}-field-${column}`} className="flex items-center gap-1.5 font-mono text-[12px]">
                          {column}
                          {c.key.includes(column) && <Badge variant="secondary" className="h-4 px-1 text-[10px]">key</Badge>}
                          {columnInfo.get(column)?.label === true && <Badge variant="secondary" className="h-4 px-1 text-[10px]">label</Badge>}
                          {column === c.lastModifiedColumn && <Badge variant="secondary" className="h-4 px-1 text-[10px]">last modified</Badge>}
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
                        {(columnInfo.get(column)?.uses.length ?? 0) > 0 && (
                          <p className="text-[11px] leading-snug text-muted-foreground" data-testid={`submit-records-uses-${column}`}>
                            {describeUses(columnInfo.get(column)?.uses ?? [])}
                          </p>
                        )}
                      </div>
                    ))}
                  </div>
                  <p className="text-xs text-muted-foreground">
                    An empty field is sent as absent, which the flow reads as null. A record whose last-modified moment is not
                    later than the version already delivered is skipped, so a change needs a later one.
                  </p>
                  {payloads.map((payload) => (
                    <div
                      key={payload.name}
                      className="flex flex-col gap-3 rounded-lg border border-border p-3"
                      data-testid={`submit-records-payload-${payload.name}`}
                    >
                      <div className="flex flex-col gap-1">
                        <h3 className="text-[13px] font-medium">Payload files: {payload.name}</h3>
                        <p className="text-xs text-muted-foreground">
                          Where the files already sit. Nothing is uploaded here: the node opens the location with its own identity
                          when the run delivers, and again on every retry.
                        </p>
                      </div>
                      <div className="flex flex-col gap-1">
                        <Label htmlFor={`${idPrefix}-payload-location-${payload.name}`} className="font-mono text-[12px]">location (required)</Label>
                        <Input
                          id={`${idPrefix}-payload-location-${payload.name}`}
                          className="h-8 font-mono"
                          placeholder={c.payloadRoots[0] ?? "the folder or glob the files are in"}
                          value={payload.location}
                          onChange={(event) => {
                            const location = event.target.value;
                            setPayloadFiles((current) => ({ ...current, [payload.name]: { location, hash: payload.hash } }));
                          }}
                          data-testid={`submit-records-payload-location-${payload.name}`}
                        />
                        {c.payloadRoots.length > 0 && (
                          <p className="text-xs text-muted-foreground">
                            This flow reads files under <span className="font-mono">{c.payloadRoots.join(", ")}</span>.
                          </p>
                        )}
                      </div>
                      <div className="flex flex-col gap-1">
                        <Label htmlFor={`${idPrefix}-payload-hash-${payload.name}`} className="font-mono text-[12px]">
                          hash {c.payloadHashRequired ? "(required)" : "(optional)"}
                        </Label>
                        <Input
                          id={`${idPrefix}-payload-hash-${payload.name}`}
                          className="h-8 font-mono"
                          value={payload.hash}
                          onChange={(event) => {
                            const hash = event.target.value;
                            setPayloadFiles((current) => ({ ...current, [payload.name]: { location: payload.location, hash } }));
                          }}
                          data-testid={`submit-records-payload-hash-${payload.name}`}
                        />
                        <p className="text-xs text-muted-foreground">
                          {c.payloadHashRequired
                            ? "The flow decides payload changes by content hash, so each record carries the hash of its files."
                            : "The flow watches the files' modified times, so a hash is sent only when the source has one."}
                        </p>
                      </div>
                    </div>
                  ))}
                </TabsContent>
                <TabsContent value="json" className="flex flex-col gap-2 pt-2">
                  <CodeView value={json} language="json" height={360} readOnly={false} onChange={setJsonText} data-testid="submit-records-json" />
                  <p className="text-xs text-muted-foreground">
                    An array of records, each {"{ \"record\": { column: value }, \"datasets\": { childDataset: [ rows ] } }"}: the shape of a
                    mapping fixture. Values are strings, numbers, booleans or null.
                    {c.payloads.length > 0 && ` Each record also carries "files", naming ${c.payloads.join(", ")}: "where its files are", or { "location": ..., "hash": ... } to send the hash with it.`}
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
              <div className="flex flex-col gap-1.5">
                <Label htmlFor={`${idPrefix}-reference`}>Reference (optional)</Label>
                <Input
                  id={`${idPrefix}-reference`}
                  className="h-8"
                  placeholder="what you call this: a filename, a ticket, a job id"
                  value={reference}
                  onChange={(event) => setReference(event.target.value)}
                  data-testid="submit-records-reference"
                />
                <p className="text-xs text-muted-foreground">
                  Your own name for this submission. It is stored and searchable and changes nothing about the delivery, so a
                  submission can be found later by the name the sending system knows it by. It is part of what a reused
                  submission id has to match.
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
