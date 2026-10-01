import { Fragment } from "react";
import { ChevronRight } from "lucide-react";
import { Card } from "@/components/ui/card";
import { DataTable } from "@/components/DataTable";
import { DetailPair } from "@/components/DetailPair";
import { KindText } from "../KindText";
import { fieldText, type DimensionEntry } from "./dimensionFormat";

/**
 * The steps that clean each key (or its label) into its value, as a pipeline a value runs through left to right; with no
 * step, the label, or the key itself, is the value.
 */
function CleanSteps({ steps, labelled }: { steps: string[]; labelled: boolean }) {
  if (steps.length === 0) {
    return (
      <p className="text-[13px] text-muted-foreground">
        {labelled ? "None: each key's label is its value, trimmed." : "None: each key is its own value, trimmed."}
      </p>
    );
  }

  return (
    <ol className="flex flex-wrap items-center gap-1.5" data-testid="dimension-clean-steps">
      <li className="font-mono text-[12px] text-muted-foreground">{labelled ? "label" : "key"}</li>
      {steps.map((step, index) => (
        <Fragment key={`${index}:${step}`}>
          <ChevronRight className="size-3.5 text-muted-foreground" aria-hidden />
          <li className="rounded-sm border border-border bg-secondary/60 px-1.5 py-0.5 font-mono text-[12px] dark:bg-input/40" data-testid="dimension-clean-step">
            {step}
          </li>
        </Fragment>
      ))}
      <ChevronRight className="size-3.5 text-muted-foreground" aria-hidden />
      <li className="font-mono text-[12px] text-muted-foreground">value</li>
    </ol>
  );
}

/**
 * Where a key's label is read: from the record the key names, through each step's reference to the next record, to the
 * text the last step holds; or that each key is its own value.
 */
function LabelSteps({ steps, end = "label" }: { steps: string[]; end?: string }) {
  if (steps.length === 0) {
    return <p className="text-[13px] text-muted-foreground">None: each key is cleaned into its value itself.</p>;
  }

  return (
    <ol className="flex flex-wrap items-center gap-1.5" data-testid="dimension-label-steps">
      <li className="font-mono text-[12px] text-muted-foreground">the record the key names</li>
      {steps.map((step, index) => (
        <Fragment key={`${index}:${step}`}>
          <ChevronRight className="size-3.5 text-muted-foreground" aria-hidden />
          <li className="rounded-sm border border-border bg-secondary/60 px-1.5 py-0.5 font-mono text-[12px] dark:bg-input/40" data-testid="dimension-label-step">
            {step}
          </li>
        </Fragment>
      ))}
      <ChevronRight className="size-3.5 text-muted-foreground" aria-hidden />
      <li className="font-mono text-[12px] text-muted-foreground">{end}</li>
    </ol>
  );
}

/**
 * How the flow declares a dimension, and how its builds settled it: the kind, query and path it reads, how the index
 * stores the field and the aggregation that reads it, where a key's label is read, the steps that clean each into its
 * value, whether values' records are counted exactly, the most keys it reads, and the kinds its last build read with the
 * template of each.
 */
export function DimensionDefinition({ entry }: { entry: DimensionEntry }) {
  const { dimension, flow } = entry;
  const kinds = dimension.current?.kinds ?? [];
  return (
    <div className="flex flex-col gap-4" data-testid="dimension-definition">
      <Card className="gap-4 rounded-lg p-4">
        <div className="grid gap-4 [grid-template-columns:repeat(auto-fill,minmax(220px,1fr))]">
          <DetailPair label="Kind"><KindText kind={dimension.kind} /></DetailPair>
          <DetailPair label="Path"><span className="break-all font-mono text-[12px]">{dimension.path}</span></DetailPair>
          <DetailPair label="Query">
            <span className="break-all font-mono text-[12px]">{dimension.query ?? "every record of the kind"}</span>
          </DetailPair>
          {dimension.builtQuery !== null && dimension.builtQuery !== dimension.query && (
            <DetailPair label="Query as built"><span className="break-all font-mono text-[12px]">{dimension.builtQuery}</span></DetailPair>
          )}
          <DetailPair label="Field">
            {dimension.field === null
              ? <span className="text-muted-foreground">settled by its first build</span>
              : <span>{fieldText(dimension.field)}</span>}
          </DetailPair>
          {dimension.field !== null && (
            <DetailPair label="Aggregated as"><span className="break-all font-mono text-[12px]">{dimension.field.aggregateBy}</span></DetailPair>
          )}
          <DetailPair label="Records per value">
            {dimension.countRecords ? "counted exactly, a search per value" : "exact where a record holds one key, else summed"}
          </DetailPair>
          <DetailPair label="Most keys read"><span className="font-mono tabular-nums">{dimension.maxValues.toLocaleString("en-US")}</span></DetailPair>
          <DetailPair label="Flow"><span className="font-mono text-[12px]">{flow.name}</span></DetailPair>
          <DetailPair label="Partition"><span className="font-mono text-[12px]">{flow.partition ?? "the one its data-partition-id header names"}</span></DetailPair>
        </div>
        <div className="flex flex-col gap-1.5">
          <span className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Label</span>
          <LabelSteps steps={dimension.label} />
          {dimension.label.length > 0 && (
            <p className="text-[12px] text-muted-foreground" data-testid="dimension-unlabelled">
              A key without a label is valued{" "}
              {dimension.unlabelled === null
                ? "by the code its id ends with."
                : <span className="font-mono text-foreground">{dimension.unlabelled}</span>}
            </p>
          )}
        </div>
        {dimension.attributes.length > 0 && (
          <div className="flex flex-col gap-1.5" data-testid="dimension-attributes">
            <span className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Attributes</span>
            {dimension.attributes.map((attribute) => (
              <div key={attribute.name} className="flex flex-wrap items-center gap-x-2 gap-y-1">
                <span className="w-28 shrink-0 text-[13px] font-medium">{attribute.name}</span>
                {attribute.collect === null
                  ? <LabelSteps steps={attribute.steps} end={attribute.name} />
                  : (
                    <span className="text-[12px] text-muted-foreground" data-testid="dimension-attribute-collect">
                      collected from each key&apos;s records at <span className="font-mono text-foreground">{attribute.collect}</span>
                    </span>
                  )}
              </div>
            ))}
          </div>
        )}
        <div className="flex flex-col gap-1.5">
          <span className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Clean</span>
          <CleanSteps steps={dimension.clean} labelled={dimension.label.length > 0} />
        </div>
      </Card>

      {kinds.length > 0 && (
        <section className="flex flex-col gap-2">
          <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Kinds its last build read</h3>
          <DataTable
            columns={[
              { id: "kind", header: "Kind", fill: true, floor: 240, render: (row) => <KindText kind={row.kind} /> },
              { id: "records", header: "Records", align: "right", render: (row) => <span className="font-mono text-[12px] tabular-nums">{row.records.toLocaleString("en-US")}</span> },
              { id: "template", header: "Template read", render: (row) => <span className="font-mono text-[12px] text-muted-foreground">{row.template ?? "none saved"}</span> },
            ]}
            rows={kinds}
            rowKey={(row) => row.kind}
            emptyMessage="No kind was read."
            data-testid="dimension-definition-kinds"
          />
        </section>
      )}
    </div>
  );
}
