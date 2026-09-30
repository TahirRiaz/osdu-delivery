import { Fragment } from "react";
import { ChevronRight } from "lucide-react";
import { Card } from "@/components/ui/card";
import { DataTable } from "@/components/DataTable";
import { DetailPair } from "@/components/DetailPair";
import { KindText } from "../KindText";
import { fieldText, type DimensionEntry } from "./dimensionFormat";

/**
 * The steps that clean each original, as a pipeline a value runs through left to right; an original the steps leave as it
 * is its own member.
 */
function CleanSteps({ steps }: { steps: string[] }) {
  if (steps.length === 0) {
    return <p className="text-[13px] text-muted-foreground">None: every original is its own member, exactly as the index holds it.</p>;
  }

  return (
    <ol className="flex flex-wrap items-center gap-1.5" data-testid="dimension-clean-steps">
      <li className="font-mono text-[12px] text-muted-foreground">original</li>
      {steps.map((step, index) => (
        <Fragment key={`${index}:${step}`}>
          <ChevronRight className="size-3.5 text-muted-foreground" aria-hidden />
          <li className="rounded-sm border border-border bg-secondary/60 px-1.5 py-0.5 font-mono text-[12px] dark:bg-input/40" data-testid="dimension-clean-step">
            {step}
          </li>
        </Fragment>
      ))}
      <ChevronRight className="size-3.5 text-muted-foreground" aria-hidden />
      <li className="font-mono text-[12px] text-muted-foreground">member</li>
    </ol>
  );
}

/**
 * How the flow declares a dimension, and how its builds settled it: the kind, query and path it reads, how the index
 * stores the field and the aggregation that reads it, the steps that clean each original, whether members' records are
 * counted exactly, the most values it reads, and the kinds its last build read with the template of each.
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
          <DetailPair label="Records per member">
            {dimension.countRecords ? "counted exactly, a search per member" : "exact where a record holds one value, else summed"}
          </DetailPair>
          <DetailPair label="Most values read"><span className="font-mono tabular-nums">{dimension.maxValues.toLocaleString("en-US")}</span></DetailPair>
          <DetailPair label="Flow"><span className="font-mono text-[12px]">{flow.name}</span></DetailPair>
          <DetailPair label="Partition"><span className="font-mono text-[12px]">{flow.partition ?? "the one its data-partition-id header names"}</span></DetailPair>
        </div>
        <div className="flex flex-col gap-1.5">
          <span className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Clean</span>
          <CleanSteps steps={dimension.clean} />
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
