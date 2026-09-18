import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Info, OctagonAlert, TriangleAlert } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { cn } from "@/lib/utils";
import { deliveryApi, type MappingDraftEntry } from "../../api/delivery";
import { DataTable, type Column } from "@/components/DataTable";
import { FilterBar } from "@/components/FilterBar";
import { GlyphRef } from "@/components/GlyphRef";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import { HeadClippedText } from "./HeadClippedText";
import { conditionText, entrySummary, lookupLines, modifierText } from "./mappingDraft";
import { splitPath } from "./templateFormat";
import { ProblemView } from "./TemplateSheet";

/** One property the mapping fills: its target in the template, where the value comes from, and what is done to it. */
interface PropertyRow {
  target: string;
  /** The value's origin in one line, as the YAML writes it: `dataset.log_name`, `cache.Wellbore.id`, `static MD`. */
  source: string;
  /** The findBy lines of a cache entry, one per line; empty for an entry that reads no cache. */
  lookup: string;
  /** The modifiers in order, one per line; empty for a value taken as it stands. */
  modifiers: string;
  /** The entry's appliesWhen in one line, or null when it always applies. */
  condition: string | null;
  description: string | null;
  required: boolean;
  /** Everything the row holds, lowercased, which the filter matches against. */
  search: string;
}

function propertyRow(entry: MappingDraftEntry): PropertyRow {
  const source = entrySummary(entry);
  const lookup = lookupLines(entry).join("\n");
  const modifiers = entry.modifiers.map(modifierText).join("\n");
  const condition = entry.appliesWhen === null ? null : conditionText(entry.appliesWhen);
  return {
    target: entry.target,
    source,
    lookup,
    modifiers,
    condition,
    description: entry.description,
    required: entry.required,
    search: [entry.target, source, lookup, modifiers, condition ?? "", entry.description ?? ""].join("\n").toLowerCase(),
  };
}

const columns: Column<PropertyRow>[] = [
  {
    id: "target",
    header: "Property",
    fill: true,
    floor: 200,
    // The end of the path tells the properties apart, so the section it sits in gives way first.
    render: (row) => {
      const { parent, leaf } = splitPath(row.target);
      return (
        <span className="flex min-w-0 items-center gap-1.5" data-testid={`delivery-mapping-property-${row.target}`}>
          <HeadClippedText head={parent} body={leaf} title="Property" className="font-mono text-[12px]" />
          {!row.required && <Badge variant="outline" className="shrink-0 text-[10px]">optional</Badge>}
          {row.description !== null && row.description !== "" && <GlyphRef icon={Info} title="Description" body={row.description} />}
        </span>
      );
    },
  },
  {
    id: "source",
    header: "Source",
    fill: true,
    floor: 170,
    // A condition decides whether the value is written at all, so it reads under the source it applies to.
    render: (row) => (
      <div className="flex min-w-0 flex-col" data-testid={`delivery-mapping-source-${row.target}`}>
        <TruncatedText text={row.source} mono maxWidth={1200} title="Source" className="min-w-0" />
        {row.condition !== null && (
          <TruncatedText
            text={`only when ${row.condition}`}
            mono
            maxWidth={1200}
            title="Applies when"
            className="min-w-0 text-[11px] text-muted-foreground"
          />
        )}
      </div>
    ),
  },
  {
    id: "lookup",
    header: "Lookup",
    fill: true,
    floor: 170,
    // One findBy per line: the clipped line shows the first, and the hover panel the whole lookup.
    render: (row) => (
      <TruncatedText text={row.lookup} mono maxWidth={1200} title="Lookup" className="min-w-0 text-[11px] text-muted-foreground" />
    ),
  },
  {
    id: "modifiers",
    header: "Modifiers",
    fill: true,
    floor: 150,
    render: (row) => (
      <TruncatedText text={row.modifiers} mono maxWidth={1200} title="Modifiers" className="min-w-0 text-[11px] text-muted-foreground" />
    ),
  },
];

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
 * opens it with: the target path, the source value behind it, the cached record a cache entry is found by, and the
 * modifiers the value passes through. Nothing is rendered and no data is read; this is the document as written.
 */
export function MappingPropertiesView({ yaml, path, contentHash }: MappingPropertiesViewProps) {
  const [filter, setFilter] = useState("");
  const [onlyDerived, setOnlyDerived] = useState(false);
  const parsed = useQuery({
    queryKey: ["delivery", "mapping-properties", path, contentHash],
    queryFn: () => deliveryApi.parseMapping(yaml, path),
  });

  const draft = parsed.data?.draft ?? null;
  const rows = useMemo(() => (draft === null ? [] : draft.entries.map(propertyRow)), [draft]);

  const term = filter.trim().toLowerCase();
  const shown = rows.filter(
    (row) => (!onlyDerived || row.lookup !== "" || row.modifiers !== "") && (term === "" || row.search.includes(term)),
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
        emptyMessage={rows.length === 0 ? "This mapping fills no property." : "No property matches the filter."}
        skeletonRows={8}
        data-testid="delivery-mapping-properties-table"
      />
    </div>
  );
}
