import { useMemo, useState, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { ArrowDown, Filter, Info, OctagonAlert, TriangleAlert } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { cn } from "@/lib/utils";
import { deliveryApi, type MappingDraftEntry, type MappingDraftInput } from "../../api/delivery";
import { DataTable, type Column } from "@/components/DataTable";
import { FilterBar } from "@/components/FilterBar";
import { GlyphRef } from "@/components/GlyphRef";
import { RichTooltip } from "@/components/RichTooltip";
import { SearchInput } from "@/components/SearchInput";
import { useClipped } from "@/components/useClipped";
import { conditionText, entrySummary, lookupLines, lookupText, modifierText } from "./mappingDraft";
import { splitPath } from "./templateFormat";
import { ProblemView } from "./TemplateSheet";

/** One property the mapping fills: where the value comes from, what is done to it, and the target it is written to. */
interface PropertyRow {
  target: string;
  /** Where the value comes from, which decides what the rest of the entry means. */
  input: MappingDraftInput;
  /** The value's origin: `dataset.log_name`, `cache.Wellbore.id`, `rows of dataset.curves`, `static "MD"`. */
  source: string;
  /** The origin without the word that names its kind, for a view that says the kind itself. */
  sourceValue: string;
  /** The cached record's lookup as one phrase, `Code/Name = dataset.elev_meas_ref`; empty when no cache is read. */
  lookup: string;
  /** One line per findBy, as the YAML writes them, for the property's own view. */
  lookupDetail: string[];
  /** The modifiers in order, one line each; empty for a value taken as it stands. */
  modifiers: string[];
  /** The modifier kinds in order, which is what the row has room for: `split, replace`. */
  modifierKinds: string;
  /** The entry's appliesWhen in one line, or null when it always applies. */
  condition: string | null;
  description: string | null;
  required: boolean;
  /** The whole line as text, with every modifier spelled out, which the hover panel shows and the filter matches. */
  detail: string;
  /** Everything the row holds, lowercased, which the filter matches against. */
  search: string;
}

function propertyRow(entry: MappingDraftEntry): PropertyRow {
  const source = entrySummary(entry);
  const lookup = lookupText(entry);
  const modifiers = entry.modifiers.map(modifierText);
  const condition = entry.appliesWhen === null ? null : conditionText(entry.appliesWhen);
  // The modifiers change the value a lookup compares, not the cached field, so they read after that value.
  const detail = [
    source,
    lookup === "" ? "" : `by ${lookup}`,
    modifiers.length === 0 ? "" : `| ${modifiers.join(" | ")}`,
    `-> ${entry.target}`,
    condition === null ? "" : `when ${condition}`,
  ].filter((part) => part !== "").join(" ");
  return {
    target: entry.target,
    input: entry.input,
    source,
    sourceValue: entry.input === "Repeat"
      ? `dataset.${entry.child ?? ""}`
      : entry.input === "Static" ? entry.static ?? "" : source,
    lookup,
    lookupDetail: lookupLines(entry),
    modifiers,
    modifierKinds: entry.modifiers.map((modifier) => modifier.kind).join(", "),
    condition,
    description: entry.description,
    required: entry.required,
    detail,
    search: [detail, entry.description ?? ""].join("\n").toLowerCase(),
  };
}

/** A formula on one line, clipped to the width its column leaves, with the whole of it on hover once anything is hidden. */
function FormulaLine({ text, title, children }: { text: string; title: string; children: ReactNode }) {
  const [ref, clipped] = useClipped(text);
  const line = (
    <span ref={ref} className="block max-w-full overflow-hidden text-ellipsis whitespace-nowrap font-mono text-[12px]">
      {children}
    </span>
  );

  return clipped ? <RichTooltip body={text} title={title} mono>{line}</RichTooltip> : line;
}

const columns: Column<PropertyRow>[] = [
  {
    id: "mapping",
    header: "From, to the property it fills",
    fill: true,
    floor: 320,
    /*
     * One line per entry, read the way the renderer reads it: the value's origin, the cached record it is found by,
     * what is done to it, then the property it lands on. The origin is muted and the property's own name carries the
     * weight, so the eye still runs down the properties although the line before each is as long as it is.
     */
    render: (row) => {
      const { parent, leaf } = splitPath(row.target);
      return (
        <span className="flex min-w-0 items-center gap-1.5" data-testid={`delivery-mapping-property-${row.target}`}>
          <FormulaLine text={row.detail} title="Mapping">
            <span className="text-muted-foreground">{row.source}</span>
            {row.lookup !== "" && <span className="text-muted-foreground">{` by ${row.lookup}`}</span>}
            {row.modifierKinds !== "" && <span className="text-muted-foreground">{` | ${row.modifierKinds}`}</span>}
            <span className="px-2 text-muted-foreground">&rarr;</span>
            <span className="text-muted-foreground">{parent}</span>
            <span className="font-medium">{leaf}</span>
            {row.condition !== null && <span className="text-muted-foreground">{` when ${row.condition}`}</span>}
          </FormulaLine>
          {!row.required && <Badge variant="outline" className="shrink-0 text-[10px]">optional</Badge>}
          {row.description !== null && row.description !== "" && <GlyphRef icon={Info} title="Description" body={row.description} />}
        </span>
      );
    },
  },
];

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
 * One property as the pipeline that fills it: the value's origin, the cached record it is found by, the modifiers in
 * the order they run, and the property they land on. A cache entry's modifiers change the value the lookup compares
 * rather than the cached field, so they are drawn inside the lookup, where they act.
 */
function PropertyDialog({ row, onClose }: { row: PropertyRow | null; onClose: () => void }) {
  // The dialog fades out showing what it showed, so the last property stays rendered while it closes.
  const [shown, setShown] = useState<PropertyRow | null>(row);
  if (row !== null && row !== shown) {
    setShown(row);
  }

  const { parent, leaf } = splitPath(shown?.target ?? "");
  const lookupSteps = shown !== null && shown.input === "Cache";

  return (
    <Dialog open={row !== null} onOpenChange={(open) => { if (!open) { onClose(); } }}>
      <DialogContent className="sm:max-w-xl" data-testid="delivery-mapping-property-detail">
        {shown !== null && (
          <>
            <DialogHeader>
              <DialogTitle className="flex flex-wrap items-center gap-2 font-mono text-[13px] break-all">
                <span>
                  <span className="font-normal text-muted-foreground">{parent}</span>
                  {leaf}
                </span>
                {!shown.required && <Badge variant="outline" className="font-sans text-[10px] font-normal">optional</Badge>}
              </DialogTitle>
              {shown.description !== null && shown.description !== "" && (
                <DialogDescription>{shown.description}</DialogDescription>
              )}
            </DialogHeader>

            <div className="flex flex-col">
              {shown.condition !== null && (
                <p
                  className="mb-3 flex items-start gap-2 rounded-md border border-warning/40 bg-warning/5 px-3 py-2 text-[12px]"
                  data-testid="delivery-mapping-property-detail-condition"
                >
                  <Filter className="mt-0.5 size-3.5 shrink-0 text-warning" />
                  <span>
                    Only when <span className="font-mono">{shown.condition}</span>. A record the condition does not hold
                    for is delivered without this property.
                  </span>
                </p>
              )}

              <Node label={SOURCE_LABEL[shown.input]} value={shown.sourceValue} testId="delivery-mapping-property-detail-source">
                {lookupSteps && (
                  <div className="mt-2 border-t pt-2" data-testid="delivery-mapping-property-detail-lookup">
                    <p className="text-[11px] text-muted-foreground">
                      found by, in order, until a cached record matches
                    </p>
                    <ul className="mt-1 flex flex-col gap-0.5">
                      {shown.lookupDetail.map((line, index) => (
                        <li key={`${index}-${line}`} className="font-mono text-[12px] break-all">{line}</li>
                      ))}
                    </ul>
                    {shown.modifiers.length > 0 && (
                      <div className="mt-2" data-testid="delivery-mapping-property-detail-modifiers">
                        <p className="text-[11px] text-muted-foreground">on the value the lookup compares, first</p>
                        <ul className="mt-1 flex flex-wrap gap-1">
                          {shown.modifiers.map((text, index) => (
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

              {!lookupSteps && shown.modifiers.length > 0
                ? (
                  <div data-testid="delivery-mapping-property-detail-modifiers">
                    {shown.modifiers.map((text, index) => <Step key={`${index}-${text}`} text={text} index={index + 1} />)}
                  </div>
                )
                : <Step />}

              <Node label="Written to" value={shown.target} target />
            </div>
          </>
        )}
      </DialogContent>
    </Dialog>
  );
}
interface MappingPropertiesViewProps {
  /** The mapping document as the catalog holds it. */
  yaml: string;
  /** Its path in the repository, which the messages name. */
  path: string;
  /** The document's content hash, so a mapping synced with new content is read again. */
  contentHash: string;
}

/**
 * What the mapping fills the template's properties with, read from the document by the same parse the mapping builder
 * opens it with: one row per property, reading from the value's origin to the target it lands on, with a lookup and
 * modifiers where there are any, and the whole of a property behind its row. Nothing is rendered and no data is read;
 * this is the document as written.
 */
export function MappingPropertiesView({ yaml, path, contentHash }: MappingPropertiesViewProps) {
  const [filter, setFilter] = useState("");
  const [onlyDerived, setOnlyDerived] = useState(false);
  const [opened, setOpened] = useState<string | null>(null);
  const parsed = useQuery({
    queryKey: ["delivery", "mapping-properties", path, contentHash],
    queryFn: () => deliveryApi.parseMapping(yaml, path),
  });

  const draft = parsed.data?.draft ?? null;
  const rows = useMemo(() => (draft === null ? [] : draft.entries.map(propertyRow)), [draft]);

  const term = filter.trim().toLowerCase();
  const shown = rows.filter(
    (row) => (!onlyDerived || row.lookup !== "" || row.modifiers.length > 0) && (term === "" || row.search.includes(term)),
  );

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-3" data-testid="delivery-mapping-properties">
      {parsed.isError && <ProblemView error={parsed.error} testId="delivery-mapping-properties-error" />}
      {(parsed.data?.issues ?? []).map((issue, index) => (
        <p
          key={`${index}-${issue.message}`}
          className={cn("flex items-start gap-1.5 text-[13px]", issue.severity === "error" ? "text-destructive" : "text-warning")}
          data-testid="delivery-mapping-properties-issue"
        >
          {issue.severity === "error"
            ? <OctagonAlert className="mt-0.5 size-3.5 shrink-0" />
            : <TriangleAlert className="mt-0.5 size-3.5 shrink-0" />}
          {issue.message}
        </p>
      ))}

      <FilterBar>
        <SearchInput
          value={filter}
          onChange={setFilter}
          placeholder="Property, source, lookup or modifier"
          label="Filter the properties"
          testId="delivery-mapping-properties-filter"
        />
        <Label className="flex items-center gap-2 text-[13px] font-normal">
          <Switch checked={onlyDerived} onCheckedChange={setOnlyDerived} data-testid="delivery-mapping-properties-only-derived" />
          Only properties with a lookup or a modifier
        </Label>
      </FilterBar>

      <p className="text-xs text-muted-foreground" data-testid="delivery-mapping-properties-count">
        {parsed.isSuccess && draft === null
          ? "The document does not parse, so the properties it fills cannot be listed."
          : `${shown.length} of ${rows.length} properties. A property of the template the mapping does not list is left out of the record.`}
      </p>

      <DataTable
        columns={columns}
        rows={parsed.isPending ? undefined : shown}
        rowKey={(row) => row.target}
        onRowClick={(row) => setOpened(row.target)}
        emptyMessage={rows.length === 0 ? "This mapping fills no property." : "No property matches the filter."}
        skeletonRows={8}
        data-testid="delivery-mapping-properties-table"
      />

      <PropertyDialog row={rows.find((row) => row.target === opened) ?? null} onClose={() => setOpened(null)} />
    </div>
  );
}
