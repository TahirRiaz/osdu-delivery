import { type ReactNode } from "react";
import { ArrowDown, Filter } from "lucide-react";
import { cn } from "@/lib/utils";
import type { MappingDraftInput } from "../../api/delivery";
import type { PropertyRow } from "./mappingDraft";

/** What the source of a value is called, so the node says the kind and the value says only itself. */
const SOURCE_LABEL: Record<MappingDraftInput, string> = {
  Dataset: "Dataset column",
  Repeat: "One item per row of",
  Cache: "Cached record",
  Static: "Static value",
};

/** One end of the pipeline: where the value comes from, or the property it lands on. */
function Node({
  label, value, target = false, children, testId,
}: { label: string; value: string; target?: boolean; children?: ReactNode; testId?: string }) {
  return (
    <div
      className={cn("rounded-md border px-3 py-2", target ? "border-primary/40 bg-primary/5" : "bg-muted/40")}
      data-testid={testId}
    >
      <p className="text-[11px] text-muted-foreground">{label}</p>
      <p className="font-mono text-[12px] break-all">{value}</p>
      {children}
    </div>
  );
}

/** One step down the pipeline: the arrow, and what happens on the way when something does. */
function Step({ text, index }: { text?: string; index?: number }) {
  return (
    <div className="flex items-center gap-2 py-1 pl-3">
      <ArrowDown className="size-3.5 shrink-0 text-muted-foreground" />
      {text !== undefined && (
        <span className="rounded border bg-background px-1.5 py-0.5 font-mono text-[11px]">
          {index !== undefined && <span className="mr-1.5 text-muted-foreground">{index}</span>}
          {text}
        </span>
      )}
    </div>
  );
}

/**
 * One entry as the pipeline that fills its property: the value's origin, the cached record it is found by, the
 * modifiers in the order they run, and the property they land on. A cache entry's modifiers change the value the
 * lookup compares rather than the cached field, so they are drawn inside the lookup, where they act.
 *
 * `target` is left out where the view already says which property this fills, such as beside a tree of them.
 */
export function EntryDetail({ row, target = true }: { row: PropertyRow; target?: boolean }) {
  const lookupSteps = row.input === "Cache";
  const steps = !lookupSteps && row.modifiers.length > 0
    ? (
      <div data-testid="delivery-mapping-property-detail-modifiers">
        {row.modifiers.map((text, index) => <Step key={`${index}-${text}`} text={text} index={index + 1} />)}
      </div>
    )
    : target ? <Step /> : null;

  return (
    <div className="flex flex-col" data-testid="delivery-mapping-entry-detail">
      {row.condition !== null && (
        <p
          className="mb-3 flex items-start gap-2 rounded-md border border-warning/40 bg-warning/5 px-3 py-2 text-[12px]"
          data-testid="delivery-mapping-property-detail-condition"
        >
          <Filter className="mt-0.5 size-3.5 shrink-0 text-warning" />
          <span>
            Only when <span className="font-mono">{row.condition}</span>. A record the condition does not hold
            for is delivered without this property.
          </span>
        </p>
      )}

      <Node label={SOURCE_LABEL[row.input]} value={row.sourceValue} testId="delivery-mapping-property-detail-source">
        {lookupSteps && (
          <div className="mt-2 border-t pt-2" data-testid="delivery-mapping-property-detail-lookup">
            <p className="text-[11px] text-muted-foreground">
              found by, in order, until a cached record matches
            </p>
            <ul className="mt-1 flex flex-col gap-0.5">
              {row.lookupDetail.map((line, index) => (
                <li key={`${index}-${line}`} className="font-mono text-[12px] break-all">{line}</li>
              ))}
            </ul>
            {row.modifiers.length > 0 && (
              <div className="mt-2" data-testid="delivery-mapping-property-detail-modifiers">
                <p className="text-[11px] text-muted-foreground">on the value the lookup compares, first</p>
                <ul className="mt-1 flex flex-wrap gap-1">
                  {row.modifiers.map((text, index) => (
                    <li key={`${index}-${text}`} className="rounded border bg-background px-1.5 py-0.5 font-mono text-[11px]">
                      <span className="mr-1.5 text-muted-foreground">{index + 1}</span>
                      {text}
                    </li>
                  ))}
                </ul>
              </div>
            )}
          </div>
        )}
      </Node>

      {steps}
      {target && <Node label="Written to" value={row.target} target />}
    </div>
  );
}
