import { useEffect, useMemo, useState } from "react";
import { CircleAlert, Info, RefreshCw, ShieldCheck } from "lucide-react";
import { IconAction } from "@/components/IconAction";
import { RichTooltip } from "@/components/RichTooltip";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import {
  EXPLORER_MAX_CHECKED, explorerApi, type ExplorerAnswer, type ExplorerListValidation, type ExplorerSchemaChoice, type ExplorerSearchRequest,
  type ExplorerValidateRequest, type ExplorerValidation,
} from "../../../api/explorer";
import { whereOf } from "../../../api/validation";
import { idParts } from "../osduRecordModel";
import type { InspectorField } from "../OsduRecordInspector";
import { TaskProgress } from "../TemplateSheet";
import { ValidationCount, ValidationGlyph } from "../ValidationMark";
import { ValidationVerdictView } from "../ValidationVerdictView";
import { problemsAt } from "../validationModel";
import { ExplorerProblem } from "./ExplorerProblem";
import { counted, kindParts, useExplorerRead } from "./explorerModel";

/** Which schema a check reads: the Schema service's, or a saved template (its version, or the kind's newest). */
interface SchemaChoice {
  schema: ExplorerSchemaChoice;
  templateVersion?: string;
}

/** The schema picker of a check: OSDU's own, or a saved template, with the saved versions to pick from where they are known. */
function SchemaPicker({ choice, saved, onChoose }: { choice: SchemaChoice; saved: string[]; onChoose: (choice: SchemaChoice) => void }) {
  return (
    <span className="inline-flex flex-wrap items-center gap-2">
      <ToggleGroup
        type="single"
        value={choice.schema}
        onValueChange={(value) => { if (value === "osdu" || value === "saved") { onChoose({ schema: value }); } }}
        variant="outline"
        size="sm"
        data-testid="explorer-validate-schema"
      >
        <ToggleGroupItem value="osdu" className="h-7 px-2.5 text-xs" title="What the partition's Schema service holds for the kind" data-testid="explorer-validate-osdu">OSDU schema</ToggleGroupItem>
        <ToggleGroupItem value="saved" className="h-7 px-2.5 text-xs" title="A template saved in OSDU Delivery" data-testid="explorer-validate-saved">Saved template</ToggleGroupItem>
      </ToggleGroup>
      {choice.schema === "saved" && saved.length > 0 && (
        <Select value={choice.templateVersion ?? saved[0]} onValueChange={(templateVersion) => onChoose({ schema: "saved", templateVersion })}>
          <SelectTrigger size="sm" className="h-7 w-[190px] font-mono text-[11px]" data-testid="explorer-validate-version">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {saved.map((version, index) => (
              <SelectItem key={version} value={version} className="font-mono text-[11px]">{index === 0 ? `${version} (newest)` : version}</SelectItem>
            ))}
          </SelectContent>
        </Select>
      )}
    </span>
  );
}

/**
 * Where a check reads the record it checks: through the connection the explorer picks for a partition, or through the route
 * and credentials of the flow a record page reads the record by. `key` tells one source's answers from another's.
 */
export interface ValidationSource {
  key: readonly unknown[];
  validate: (request: ExplorerValidateRequest) => Promise<ExplorerAnswer<ExplorerValidation>>;
}

/**
 * A record OSDU holds checked against the schema of its kind (osdu/docs/reference/concepts/explorer.md, Validate), in
 * the explorer or on a record page's OSDU tab: by default what the partition's Schema service holds, which is what OSDU
 * expects of the record; or a template saved in OSDU Delivery. The problems open their element in the record. What was
 * read to make the schema, and what could not be resolved, is in the tooltip beside the schema. The answer is handed
 * up, so the record's fields can carry its marks.
 */
export function ExplorerValidationView({ source, id, version, onOpenPath, onResult }: {
  source: ValidationSource;
  id: string;
  /** The version in view; null for the latest. */
  version: number | null;
  onOpenPath: (path: string) => void;
  onResult?: (result: ExplorerValidation | null) => void;
}) {
  const [choice, setChoice] = useState<SchemaChoice>({ schema: "osdu" });
  const read = useExplorerRead<ExplorerValidation>(
    ["validate", ...source.key, id, version, choice.schema, choice.templateVersion ?? null],
    () => source.validate({ targetId: id, version: version ?? undefined, schema: choice.schema, templateVersion: choice.templateVersion }),
  );
  const answer = read.data?.answer;
  useEffect(() => onResult?.(answer ?? null), [answer, onResult]);

  const schemaNotes = answer?.schema
    ? [
      `${answer.schema.kind}, content version ${answer.schema.version}, from ${answer.schema.source === "schema-service" ? "the partition's Schema service" : "a template saved in OSDU Delivery"}.`,
      answer.schema.read.length > 1 ? `Read from the Schema service to make it: ${answer.schema.read.join(", ")}.` : null,
      ...answer.schema.unresolved.map((why) => `Not resolved: ${why}.`),
    ].filter((line): line is string => line !== null)
    : [];

  return (
    <div className="flex flex-col gap-3 p-3" data-testid="explorer-validation">
      <div className="flex flex-wrap items-center gap-2 text-[12px]">
        <ShieldCheck className="size-4 text-muted-foreground" aria-hidden />
        <span className="font-medium">Checked against</span>
        <SchemaPicker choice={choice} saved={answer?.savedVersions ?? []} onChoose={setChoice} />
        {schemaNotes.length > 0 && (
          <RichTooltip title="The schema" body={schemaNotes.join("\n\n")}>
            <Info className="size-3.5 text-muted-foreground" aria-label="The schema" data-testid="explorer-validate-schema-info" />
          </RichTooltip>
        )}
        <IconAction
          label="Check again"
          icon={<RefreshCw className={read.isFetching ? "animate-spin" : undefined} />}
          variant="ghost"
          className="ml-auto size-7"
          disabled={read.isFetching}
          onClick={() => void read.refetch()}
          data-testid="explorer-validate-again"
        />
      </div>
      {read.isPending && <TaskProgress label="Checking the record against its schema" testId="explorer-validate-progress" />}
      {read.isError && <ExplorerProblem error={read.error} />}
      {answer?.problem && (
        <Alert data-testid="explorer-validate-problem">
          <CircleAlert />
          <AlertTitle>The record was not checked</AlertTitle>
          <AlertDescription>
            <span className="text-[12px]">{answer.problem}</span>
            {choice.schema === "osdu" && answer.savedVersions.length > 0 && (
              <Button variant="outline" size="sm" className="mt-1 h-7 w-fit" onClick={() => setChoice({ schema: "saved", templateVersion: answer.savedVersions[0] })} title={`The saved template of content version ${answer.savedVersions[0]}`} data-testid="explorer-validate-use-saved">
                Check against the saved template
              </Button>
            )}
          </AlertDescription>
        </Alert>
      )}
      {answer?.verdict && <ValidationVerdictView verdict={answer.verdict} guidance={answer.guidance} onOpenPath={onOpenPath} />}
    </div>
  );
}

/**
 * The mark a field of the record `id` carries when the last check of it found a problem at the field, or inside it for a
 * section: the glyph, and what is wrong in its tooltip. Only the version the check read carries marks, since another
 * version in view holds other values.
 */
export function ValidationFieldMark({ result, id, field }: { result: ExplorerValidation | null; id: string; field: InspectorField }) {
  if (field.level !== 0 || result === null || result.targetId !== id) {
    return null;
  }

  const shown = field.trail[field.level]?.record;
  if (shown === null || shown === undefined || (result.version !== null && shown.version !== result.version)) {
    return null;
  }

  const section = field.node.kind === "object" || field.node.kind === "array";
  const problems = result.verdict?.problems ?? [];
  const found = problemsAt(problems, field.node.path, section);
  if (found.length === 0) {
    return null;
  }

  // Each problem with how to fix it, where the check said; the guidance lists one guide per problem, in the verdict's order.
  const advice = (problem: (typeof problems)[number]) => {
    const guide = result.guidance?.problems[problems.indexOf(problem)];
    return guide !== undefined && guide.path === problem.path && guide.advice !== null ? `\nFix: ${guide.advice}` : "";
  };
  const body = found.slice(0, 8).map((p) => `${whereOf(p)} (${p.rule}): ${p.message}${advice(p)}`).join("\n\n")
    + (found.length > 8 ? `\n\nand ${found.length - 8} more` : "");
  return (
    <RichTooltip title={`${found.length} problem${found.length === 1 ? "" : "s"} the schema finds here`} body={body}>
      <span className="inline-flex size-5 items-center justify-center" data-testid="explorer-validation-mark">
        <ValidationGlyph outcome="invalid" />
      </span>
    </RichTooltip>
  );
}

/**
 * The records a search finds checked against their schemas, up to 1,000 (osdu/docs/reference/concepts/explorer.md,
 * Validate): how many came to each outcome, the rules broken most often with the records that break them and an example
 * of each, and each record with its first problem; a record opens in the explorer. A search matching more records than
 * are read says so: the counts are of the first ones, and a whole kind is an assertion flow's conforms test.
 */
export function ExplorerValidateDialog({ partition, request, open, onOpenChange, onOpenRecord }: {
  partition: string | null;
  request: ExplorerSearchRequest;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onOpenRecord: (id: string) => void;
}) {
  const [schema, setSchema] = useState<ExplorerSchemaChoice>("osdu");
  const search = useMemo(() => ({ ...request, offset: undefined, limit: undefined, facet: undefined }), [request]);
  const read = useExplorerRead<ExplorerListValidation>(
    ["validate-list", partition, search, schema],
    open ? () => explorerApi.validateList(partition, { search, max: EXPLORER_MAX_CHECKED, schema }) : null,
  );
  const answer = read.data?.answer;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="flex max-h-[85vh] flex-col gap-3 overflow-hidden sm:max-w-5xl" data-testid="explorer-validate-list">
        <DialogHeader>
          <DialogTitle>Validate these records</DialogTitle>
          <DialogDescription>
            {`The first ${EXPLORER_MAX_CHECKED.toLocaleString("en-US")} records the search finds, read from OSDU and checked against the schema of their kind.`}
          </DialogDescription>
        </DialogHeader>
        <div className="flex flex-wrap items-center gap-2 text-[12px]">
          <span className="font-medium">Checked against</span>
          <SchemaPicker choice={{ schema }} saved={[]} onChoose={(choice) => setSchema(choice.schema)} />
          <IconAction label="Check again" icon={<RefreshCw className={read.isFetching ? "animate-spin" : undefined} />} variant="ghost" className="ml-auto size-7" disabled={read.isFetching} onClick={() => void read.refetch()} data-testid="explorer-validate-list-again" />
        </div>
        {read.isPending && <TaskProgress label="Reading the records from OSDU and checking them" testId="explorer-validate-list-progress" />}
        {read.isError && <ExplorerProblem error={read.error} />}
        {answer && (
          <div className="flex min-h-0 flex-1 flex-col gap-3 overflow-y-auto pr-1">
            <div className="flex flex-wrap items-center gap-x-4 gap-y-1" data-testid="explorer-validate-list-counts">
              <span className="text-[12px]">
                <span className="font-mono tabular-nums">{answer.read.toLocaleString("en-US")}</span>
                <span className="text-muted-foreground">{` of ${counted(answer.matched, "record")} checked`}</span>
              </span>
              <ValidationCount outcome="valid" count={answer.valid} />
              <ValidationCount outcome="invalid" count={answer.invalid} />
              <ValidationCount outcome="unverified" count={answer.unverified} />
              {answer.notChecked > 0 && <ValidationCount outcome="notValidated" count={answer.notChecked} />}
            </div>
            {answer.cut && (
              <p className="text-[12px] text-muted-foreground" data-testid="explorer-validate-list-cut">
                The search finds more records than a check reads, so these counts are of the first ones. Narrow the search, or check a whole kind with an assertion flow's conforms test.
              </p>
            )}
            {[...answer.notes, ...answer.unavailable.map((u) => `${counted(u.records, "record")} of ${u.kind} not checked: ${u.why}`), ...(answer.notFound.length > 0 ? [`${counted(answer.notFound.length, "record")} the search found, storage did not return.`] : [])].map((note) => (
              <p key={note} className="text-[12px] text-muted-foreground" data-testid="explorer-validate-list-note">{note}</p>
            ))}
            {answer.rules.length > 0 && (
              <section className="rounded-md border" data-testid="explorer-validate-list-rules">
                <header className="border-b px-3 py-1.5 text-[12px] font-medium">Rules broken most often</header>
                <div className="grid grid-cols-[minmax(0,1.4fr)_auto_auto_minmax(0,2.4fr)]">
                  {answer.rules.map((rule) => (
                    <div key={`${rule.at}:${rule.rule}`} className="col-span-full grid min-w-0 grid-cols-subgrid items-start gap-3 border-b px-3 py-1.5 text-[12px] last:border-b-0" data-testid="explorer-validate-list-rule">
                      <span className="min-w-0 truncate font-mono text-[11px]" title={rule.at || "the record"}>{rule.at || "the record"}</span>
                      <span className="font-mono text-[11px] text-muted-foreground">{rule.rule}</span>
                      <span className="whitespace-nowrap text-right font-mono tabular-nums" title={`${rule.problems.toLocaleString("en-US")} problem(s) in all`}>{counted(rule.records, "record")}</span>
                      <span className="flex min-w-0 flex-col gap-0.5">
                        <span>
                          <span className="break-words">{rule.exampleMessage}</span>
                          <button type="button" className="ml-2 text-primary hover:underline" onClick={() => onOpenRecord(rule.exampleId)} title={rule.exampleId} data-testid="explorer-validate-list-example">
                            {idParts(rule.exampleId).unique}
                          </button>
                        </span>
                        {rule.expected !== null && (
                          <span className="text-muted-foreground" data-testid="explorer-validate-list-expected">
                            {"Takes "}
                            <span className="text-foreground">{rule.expected}</span>
                          </span>
                        )}
                        {rule.advice !== null && (
                          <span data-testid="explorer-validate-list-advice">
                            <span className="font-medium">{"Fix: "}</span>
                            {rule.advice}
                          </span>
                        )}
                      </span>
                    </div>
                  ))}
                </div>
              </section>
            )}
            <section className="rounded-md border" data-testid="explorer-validate-list-records">
              <header className="border-b px-3 py-1.5 text-[12px] font-medium">Records</header>
              {answer.records.map((record) => (
                <button
                  key={record.id}
                  type="button"
                  onClick={() => onOpenRecord(record.id)}
                  className="grid w-full min-w-0 cursor-pointer grid-cols-[auto_minmax(0,1.2fr)_minmax(0,0.8fr)_minmax(0,2.4fr)] items-center gap-3 border-b px-3 py-1 text-left text-[12px] last:border-b-0 hover:bg-accent/50"
                  title={record.id}
                  data-testid="explorer-validate-list-record"
                >
                  <ValidationGlyph outcome={record.outcome} />
                  <span className="min-w-0 truncate text-primary">{idParts(record.id).unique}</span>
                  <span className="min-w-0 truncate text-muted-foreground">{record.kind === null ? "" : kindParts(record.kind).type}</span>
                  <span className="min-w-0 truncate text-muted-foreground">{record.first ?? ""}</span>
                </button>
              ))}
            </section>
          </div>
        )}
      </DialogContent>
    </Dialog>
  );
}
