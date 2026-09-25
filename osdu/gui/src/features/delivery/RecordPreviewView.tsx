import { useState } from "react";
import { Link as RouterLink } from "react-router-dom";
import { CircleAlert, Download, ExternalLink, FileWarning, Info, SearchX } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { CodeView } from "@/components/CodeView";
import { DataTable, type Column } from "@/components/DataTable";
import { DetailPair } from "@/components/DetailPair";
import { IdChip } from "@/components/IdChip";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import {
  deliveryRecordRoute,
  type DeliveryPreviewAction,
  type DeliveryPreviewDecision,
  type DeliveryPreviewFile,
  type DeliveryPreviewPayloadPart,
  type DeliveryPreviewReference,
  type DeliveryPreviewSearch,
  type DeliveryPreviewStep,
  type DeliveryRecordPreview,
} from "../../api/delivery";
import { RecordStatusBadge } from "./DeliveryBadges";
import { downloadJson, envelopeFirst, fileNameOf, formatBytes } from "./osduDocument";

const ACTION_LABELS: Record<DeliveryPreviewAction, string> = {
  create: "would create",
  updateMetadata: "would update the record",
  updatePayload: "would update the payload",
  updateBoth: "would update record and payload",
  skip: "unchanged, would skip",
  hold: "would hold",
  blocked: "blocked",
};

const ACTION_TONES: Record<DeliveryPreviewAction, string> = {
  create: "bg-info/12 text-info",
  updateMetadata: "bg-info/12 text-info",
  updatePayload: "bg-info/12 text-info",
  updateBoth: "bg-info/12 text-info",
  skip: "bg-muted text-muted-foreground",
  hold: "bg-warning/15 text-warning",
  blocked: "bg-destructive/15 text-destructive",
};

const HOW_LABELS: Record<DeliveryRecordPreview["asked"]["how"], string> = {
  first: "the first record of the scope",
  "delivery key": "a delivery key",
  "osdu id": "an OSDU id the ledger holds",
  "source key": "a source key",
  "key parts": "the key's parts",
};

/** What the next run would do with the record, as a badge. */
export function PreviewActionBadge({ decision }: { decision: DeliveryPreviewDecision }) {
  const label = ACTION_LABELS[decision.action] ?? decision.action;
  return (
    <Badge variant="secondary" className={ACTION_TONES[decision.action]} title={decision.reason} data-testid="preview-action">
      {label}
    </Badge>
  );
}

const searchColumns: Column<DeliveryPreviewSearch>[] = [
  { id: "kind", header: "Kind searched", render: (row) => <TruncatedText text={row.kind} mono maxWidth={260} /> },
  { id: "field", header: "Field", render: (row) => <span className="font-mono text-[12px]">{row.field}</span> },
  { id: "value", header: "Value", render: (row) => <TruncatedText text={row.value} maxWidth={200} /> },
  { id: "outcome", header: "Outcome", render: (row) => <Badge variant="outline">{row.outcome}</Badge> },
  { id: "id", header: "Found", fill: true, render: (row) => <TruncatedText text={row.id ?? null} mono maxWidth={420} copy={row.id !== null && row.id !== undefined} /> },
];

function referenceColumns(): Column<DeliveryPreviewReference>[] {
  return [
    { id: "id", header: "OSDU id", fill: true, render: (row) => <TruncatedText text={row.id} mono maxWidth={480} copy copyTestId="copy-preview-reference" /> },
    { id: "property", header: "Property", render: (row) => <span className="font-mono text-[12px]">{row.property}</span> },
    {
      id: "holder",
      header: "In the ledger",
      render: (row) => (row.holder
        ? (
          <span className="inline-flex items-center gap-2">
            <RecordStatusBadge status={row.holder.status} testId="preview-reference-status" />
            <RouterLink
              to={deliveryRecordRoute({ flowId: row.holder.flowId, deliveryKey: row.holder.deliveryKey })}
              className="text-[12px] text-primary hover:underline"
              data-testid="preview-reference-link"
            >
              {row.holder.label ?? row.holder.sourceKey}
            </RouterLink>
          </span>
        )
        : <span className="text-[12px] text-muted-foreground">not a record of the ledger</span>),
    },
  ];
}

const fileColumns: Column<DeliveryPreviewFile>[] = [
  { id: "name", header: "File", fill: true, render: (row) => <TruncatedText text={row.name} mono maxWidth={360} /> },
  { id: "size", header: "Size", align: "right", render: (row) => <span className="font-mono tabular-nums" title={`${row.size.toLocaleString()} bytes`}>{formatBytes(row.size)}</span> },
  { id: "modified", header: "Modified", render: (row) => <RelativeTime value={row.modifiedUtc ?? null} /> },
  {
    id: "shape",
    header: "Parquet",
    render: (row) => (row.parquet
      ? (
        <span className="font-mono text-[12px] tabular-nums" title={row.parquet.columnNames.join(", ") + (row.parquet.columnNamesTruncated ? ", ..." : "")}>
          {`${row.parquet.rows.toLocaleString()} rows x ${row.parquet.columns} columns`}
        </span>
      )
      : <span className="text-muted-foreground">-</span>),
  },
  { id: "problem", header: "Footer", render: (row) => (row.footerProblem ? <TruncatedText text={row.footerProblem} maxWidth={320} /> : <span className="text-muted-foreground">-</span>) },
];

/** One payload part: where its files are, how many and how large, and each file listed. */
function PayloadPart({ part }: { part: DeliveryPreviewPayloadPart }) {
  return (
    <div className="flex flex-col gap-2" data-testid="preview-payload-part">
      <div className="flex flex-wrap items-baseline gap-2 text-[13px]">
        <span className="font-medium">{part.role ?? part.payload}</span>
        {part.role && <span className="font-mono text-[12px] text-muted-foreground">{part.payload}</span>}
        {part.location && <TruncatedText text={part.location} mono maxWidth={420} copy />}
        {part.problem === null || part.problem === undefined
          ? (
            <span className="text-muted-foreground">
              {`${part.totalFiles.toLocaleString()} ${part.totalFiles === 1 ? "file" : "files"}, ${formatBytes(part.totalBytes)}`}
              {part.truncated ? `; the first ${part.files.length} listed` : ""}
            </span>
          )
          : <span className="text-warning" data-testid="preview-payload-problem">{part.problem}</span>}
      </div>
      {part.files.length > 0 && (
        <DataTable columns={fileColumns} rows={part.files} rowKey={(row) => row.name} emptyMessage="No files." data-testid="preview-payload-files" />
      )}
    </div>
  );
}

/** One request of the route, in order: the service, the request, what it carries and what the delivery goes on with. */
function Step({ step }: { step: DeliveryPreviewStep }) {
  const [open, setOpen] = useState(false);
  return (
    <li className="flex flex-col gap-1 rounded-md border px-3 py-2" data-testid="preview-step">
      <div className="flex flex-wrap items-center gap-2 text-[13px]">
        <span className="font-mono text-[12px] tabular-nums text-muted-foreground">{step.order}.</span>
        <Badge variant="outline">{step.service}</Badge>
        <span className="font-mono text-[12px] break-all">{step.request}</span>
        {step.repeats && <span className="text-[12px] text-muted-foreground">{step.repeats}</span>}
      </div>
      <p className="text-[13px]">{step.what}</p>
      {step.returns && <p className="text-[12px] text-muted-foreground">{`Returns ${step.returns}.`}</p>}
      {step.body && (
        <div className="flex flex-col gap-1">
          <Button variant="link" size="sm" className="h-6 self-start px-0 text-[12px]" onClick={() => setOpen((was) => !was)} data-testid="preview-step-body-toggle">
            {open ? "Hide the request body" : "Show the request body"}
          </Button>
          {open && <CodeView value={JSON.stringify(step.body, null, 2)} language="json" height={260} data-testid="preview-step-body" />}
        </div>
      )}
    </li>
  );
}

/**
 * A record preview as the Preview tab shows it: which row it is and how it was picked, what the next run would do with it,
 * the document as the route sends it (or as rendered), the requests the route would make, the files it would upload, what
 * it refers to, and its rows as the ingestion tables hold them. A preview that found no row says why.
 */
export function RecordPreviewView({ preview }: { preview: DeliveryRecordPreview }) {
  const [form, setForm] = useState<"sent" | "rendered">("sent");
  const asked = preview.asked;
  const askedText = asked.key
    ? `'${asked.key}', read as ${HOW_LABELS[asked.how] ?? asked.how}`
    : `${HOW_LABELS.first}${asked.scopeRecords !== null && asked.scopeRecords !== undefined ? ` (${asked.scopeRecords.toLocaleString()} in scope)` : ""}`;
  const values = Object.entries(asked.values ?? {});

  if (!preview.found) {
    return (
      <Alert data-testid="preview-not-found">
        <SearchX />
        <AlertTitle>Nothing to preview</AlertTitle>
        <AlertDescription>
          <p data-testid="preview-reason">{preview.reason}</p>
          <p className="text-[12px] text-muted-foreground">
            {`Asked for ${askedText}`}
            {values.length > 0 ? `, with ${values.map(([name, value]) => `${name}=${value}`).join(", ")}` : ""}
            {asked.keyColumns.length > 0 ? `. The records are keyed by ${asked.keyColumns.join(", ")}.` : "."}
          </p>
          {asked.passedOverWhy.length > 0 && (
            <ul className="list-disc pl-5 text-[12px] text-muted-foreground">
              {asked.passedOverWhy.map((why) => <li key={why}>{why}</li>)}
            </ul>
          )}
        </AlertDescription>
      </Alert>
    );
  }

  const source = preview.source!;
  const decision = preview.decision!;
  const document = preview.document ?? null;
  const ledger = decision.ledger ?? null;
  const shown = document === null ? null : form === "sent" && document.sent ? document.sent : document.rendered ?? null;
  const recordLink = ledger !== null && source.deliveryKey
    ? deliveryRecordRoute({ flowId: preview.flowId, deliveryKey: source.deliveryKey })
    : null;
  const datasets = Object.entries(source.datasets ?? {});

  return (
    <div className="flex flex-col gap-3" data-testid="preview-result">
      <Card className="gap-3 rounded-lg p-4" data-testid="preview-header">
        <div className="flex flex-wrap items-center gap-2">
          <h2 className="min-w-0 break-words text-base font-semibold" data-testid="preview-title">{source.label ?? source.sourceKey}</h2>
          <PreviewActionBadge decision={decision} />
          {document?.held && <Badge variant="secondary" className="bg-warning/15 text-warning" data-testid="preview-held">held</Badge>}
          {ledger?.sameDocument === true && <Badge variant="outline" data-testid="preview-same">same document as delivered</Badge>}
          {ledger?.sameDocument === false && <Badge variant="outline" className="border-info/40 text-info" data-testid="preview-differs">differs from what was delivered</Badge>}
          <div className="ml-auto flex flex-wrap items-center gap-2">
            {recordLink !== null && (
              <Button variant="outline" size="sm" asChild data-testid="preview-open-record">
                <RouterLink to={recordLink}>
                  <ExternalLink />
                  Open record
                </RouterLink>
              </Button>
            )}
            <Button
              variant="outline"
              size="sm"
              onClick={() => downloadJson(fileNameOf("preview", preview.flow, preview.interface, source.sourceKey), preview)}
              data-testid="preview-download"
            >
              <Download />
              Download JSON
            </Button>
          </div>
        </div>
        <div className="flex flex-wrap gap-2">
          {document?.targetId && <IdChip label="osdu" value={document.targetId} display={document.targetId} testId="preview-target" copyTestId="copy-preview-target" />}
          {source.deliveryKey && <IdChip label="key" value={source.deliveryKey} testId="preview-delivery-key" copyTestId="copy-preview-delivery-key" />}
        </div>
        <div className="grid grid-cols-1 gap-x-6 gap-y-2 sm:grid-cols-2 xl:grid-cols-3">
          <DetailPair label="Source key"><TruncatedText text={source.sourceKey} mono maxWidth={320} copy copyTestId="copy-preview-source-key" /></DetailPair>
          <DetailPair label="Picked as"><span className="text-[13px]" data-testid="preview-asked">{askedText}</span></DetailPair>
          <DetailPair label="Received from">
            {source.originFile
              ? (
                <span className="inline-flex flex-wrap items-baseline gap-1">
                  <TruncatedText text={source.originFile} mono maxWidth={240} copy />
                  {source.originRow !== null && source.originRow !== undefined && <span className="text-[12px] text-muted-foreground">row <span className="font-mono">{source.originRow}</span></span>}
                </span>
              )
              : <span className="text-muted-foreground">not recorded</span>}
          </DetailPair>
          <DetailPair label="Next run"><span className="text-[13px]" data-testid="preview-reason">{decision.reason}</span></DetailPair>
          <DetailPair label="In the ledger">
            {ledger === null
              ? <span className="text-muted-foreground">no record yet</span>
              : (
                <span className="inline-flex flex-wrap items-center gap-2">
                  <RecordStatusBadge status={ledger.status} testId="preview-ledger-status" />
                  {ledger.targetVersion !== null && ledger.targetVersion !== undefined && <span className="font-mono text-[12px]">v{ledger.targetVersion}</span>}
                  {ledger.lastDeliveredUtc && <RelativeTime value={ledger.lastDeliveredUtc} />}
                </span>
              )}
          </DetailPair>
          <DetailPair label="Mapping"><span className="font-mono text-[12px]">{preview.inputs.mapping}</span></DetailPair>
          <DetailPair label="Kind"><TruncatedText text={preview.inputs.kind} mono maxWidth={320} /></DetailPair>
          <DetailPair label="Cache">
            {preview.inputs.cachePartition
              ? <span className="font-mono text-[12px]">{`${preview.inputs.cachePartition} ${preview.inputs.cacheVersion ?? ""}`.trim()}</span>
              : <span className="text-muted-foreground">none read</span>}
          </DetailPair>
          <DetailPair label="Route">
            <span className="text-[13px]">
              <span className="font-mono">{preview.route.protocol}</span>
              {preview.route.ddms ? `: ${preview.route.ddms}` : ""}
            </span>
          </DetailPair>
          {values.length > 0 && (
            <DetailPair label="Scope">
              <span className="font-mono text-[12px]">{values.map(([name, value]) => `${name}=${value}`).join(", ")}</span>
            </DetailPair>
          )}
        </div>
      </Card>

      {asked.passedOver > 0 && (
        <Alert data-testid="preview-passed-over">
          <Info />
          <AlertDescription>
            <span>{`${asked.passedOver.toLocaleString()} ${asked.passedOver === 1 ? "row" : "rows"} before this one cannot render, and ${asked.passedOver === 1 ? "was" : "were"} passed over:`}</span>
            <ul className="list-disc pl-5 text-[12px]">{asked.passedOverWhy.map((why) => <li key={why}>{why}</li>)}</ul>
          </AlertDescription>
        </Alert>
      )}
      {document !== null && document.holds.length > 0 && (
        <Alert variant="destructive" data-testid="preview-holds">
          <CircleAlert />
          <AlertTitle>A delivery would hold this record</AlertTitle>
          <AlertDescription>
            <ul className="list-disc pl-5">{document.holds.map((hold) => <li key={hold}>{hold}</li>)}</ul>
          </AlertDescription>
        </Alert>
      )}
      {document === null && preview.noDocument && (
        <Alert data-testid="preview-no-document">
          <FileWarning />
          <AlertTitle>The row renders no document</AlertTitle>
          <AlertDescription>{preview.noDocument}</AlertDescription>
        </Alert>
      )}
      {preview.issues.length > 0 && (
        <Alert data-testid="preview-issues">
          <Info />
          <AlertTitle>The preflight's warnings</AlertTitle>
          <AlertDescription>
            <ul className="list-disc pl-5">{preview.issues.map((issue) => <li key={issue}>{issue}</li>)}</ul>
          </AlertDescription>
        </Alert>
      )}

      <Tabs defaultValue={document !== null ? "document" : "source"}>
        <TabsList data-testid="preview-tabs">
          <TabsTrigger value="document" disabled={document === null} data-testid="preview-tab-document">Document</TabsTrigger>
          <TabsTrigger value="steps" disabled={preview.steps.length === 0} data-testid="preview-tab-steps">
            Steps
            {preview.steps.length > 0 && <Badge variant="secondary" className="ml-1">{preview.steps.length}</Badge>}
          </TabsTrigger>
          <TabsTrigger value="payload" data-testid="preview-tab-payload">
            Payload
            {preview.payload.length > 0 && <Badge variant="secondary" className="ml-1">{preview.payload.reduce((sum, part) => sum + part.totalFiles, 0)}</Badge>}
          </TabsTrigger>
          <TabsTrigger value="references" data-testid="preview-tab-references">
            References
            {preview.referenceCount > 0 && <Badge variant="secondary" className="ml-1">{preview.referenceCount}</Badge>}
          </TabsTrigger>
          <TabsTrigger value="source" data-testid="preview-tab-source">Source row</TabsTrigger>
        </TabsList>

        <TabsContent value="document">
          {document !== null && (
            <div className="flex flex-col gap-3">
              <div className="flex flex-wrap items-center gap-3 text-[12px] text-muted-foreground">
                {document.sent
                  ? (
                    <ToggleGroup type="single" size="sm" variant="outline" value={form} onValueChange={(next) => { if (next === "sent" || next === "rendered") { setForm(next); } }} data-testid="preview-form">
                      <ToggleGroupItem value="sent" data-testid="preview-form-sent">As the route sends it</ToggleGroupItem>
                      <ToggleGroupItem value="rendered" data-testid="preview-form-rendered">As the mapping renders it</ToggleGroupItem>
                    </ToggleGroup>
                  )
                  : <span>The route sends the document as the mapping renders it.</span>}
                <span className="font-mono">{`${document.characters.toLocaleString()} characters`}</span>
                <span className="font-mono" title="The hash of the rendered document, which the ledger compares to decide what is sent">{`hash ${document.metadataHash.slice(0, 16)}`}</span>
                {document.cacheValues > 0 && <span>{`${document.cacheValues.toLocaleString()} values read from the cache`}</span>}
              </div>
              {document.omitted
                ? <Alert data-testid="preview-document-omitted"><Info /><AlertDescription>{document.omitted}</AlertDescription></Alert>
                : shown !== null && <CodeView value={JSON.stringify(envelopeFirst(shown), null, 2)} language="json" height={520} data-testid="preview-document" />}
              {form === "sent" && document.placeholders.length > 0 && (
                <div className="flex flex-col gap-1" data-testid="preview-placeholders">
                  <h3 className="text-[13px] font-medium">Values the platform gives when the record is sent</h3>
                  <ul className="flex flex-col gap-1 text-[12px]">
                    {document.placeholders.map((placeholder) => (
                      <li key={placeholder.path}>
                        <span className="font-mono">{placeholder.path}</span>
                        <span className="text-muted-foreground">{`: ${placeholder.standsFor}`}</span>
                      </li>
                    ))}
                  </ul>
                </div>
              )}
              {document.searches.length > 0 && (
                <div className="flex flex-col gap-1">
                  <h3 className="text-[13px] font-medium">What the render found by searching the platform</h3>
                  <DataTable columns={searchColumns} rows={document.searches} rowKey={(row) => `${row.kind}|${row.field}|${row.value}`} emptyMessage="No searches." data-testid="preview-searches" />
                </div>
              )}
            </div>
          )}
        </TabsContent>

        <TabsContent value="steps">
          <div className="flex flex-col gap-3">
            <p className="text-[12px] text-muted-foreground">
              The requests a delivery of this record makes, in order, as the flow's route makes them. Nothing was sent.
            </p>
            <ol className="flex flex-col gap-2" data-testid="preview-steps">
              {preview.steps.map((step) => <Step key={step.order} step={step} />)}
            </ol>
            {preview.notes.length > 0 && (
              <ul className="flex flex-col gap-1 text-[12px] text-muted-foreground" data-testid="preview-notes">
                {preview.notes.map((note) => <li key={note}>{note}</li>)}
              </ul>
            )}
          </div>
        </TabsContent>

        <TabsContent value="payload">
          {preview.payload.length === 0
            ? <p className="text-[13px] text-muted-foreground" data-testid="preview-no-payload">A delivery of this record uploads no files: the route sends the document alone, or the row names none.</p>
            : <div className="flex flex-col gap-4">{preview.payload.map((part) => <PayloadPart key={`${part.role ?? ""}|${part.payload}`} part={part} />)}</div>}
        </TabsContent>

        <TabsContent value="references">
          <div className="flex flex-col gap-2">
            <p className="text-[12px] text-muted-foreground">
              {preview.referenceCount > preview.references.length
                ? `The document refers to ${preview.referenceCount} records; the first ${preview.references.length} are looked up in the ledger.`
                : "The records the document refers to, from the relationships of the template its mapping pins. A record of the ledger that is not delivered yet is one a delivery waits for."}
            </p>
            <DataTable
              columns={referenceColumns()}
              rows={preview.references}
              rowKey={(row) => `${row.id}|${row.property}`}
              emptyMessage="The document refers to no other record."
              data-testid="preview-references"
            />
          </div>
        </TabsContent>

        <TabsContent value="source">
          <div className="flex flex-col gap-3">
            {source.omitted && <Alert><Info /><AlertDescription>{source.omitted}</AlertDescription></Alert>}
            <div className="flex flex-col gap-1">
              <h3 className="text-[13px] font-medium">The record row</h3>
              <CodeView value={JSON.stringify(source.row, null, 2)} language="json" height={240} data-testid="preview-source-row" />
            </div>
            {datasets.map(([name, rows]) => (
              <div key={name} className="flex flex-col gap-1">
                <h3 className="text-[13px] font-medium">
                  {name}
                  <span className="ml-2 font-normal text-muted-foreground">
                    {rows.truncated ? `the first ${rows.rows.length} of ${rows.total.toLocaleString()} rows` : `${rows.total.toLocaleString()} ${rows.total === 1 ? "row" : "rows"}`}
                  </span>
                </h3>
                <CodeView value={JSON.stringify(rows.rows, null, 2)} language="json" height={200} data-testid="preview-source-dataset" />
              </div>
            ))}
          </div>
        </TabsContent>
      </Tabs>
    </div>
  );
}
