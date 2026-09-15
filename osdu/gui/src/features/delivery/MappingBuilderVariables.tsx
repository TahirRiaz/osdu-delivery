import { useMemo, useState } from "react";
import { DatabaseZap, Link2, OctagonAlert, Plus, TriangleAlert } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import type { DeliveryTemplateVariable, MappingDraftIssue } from "../../api/delivery";
import { DataTable, type Column } from "@/components/DataTable";
import { FilterBar } from "@/components/FilterBar";
import { GlyphRef } from "@/components/GlyphRef";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import type { EntryEditorTarget } from "./MappingEntryEditor";
import { entrySummary } from "./mappingDraft";
import { pathDepth, shapeText, splitPath } from "./templateFormat";
import { keyVariable, type VariableRow } from "./variableRows";

interface MappingBuilderVariablesProps {
  rows: VariableRow[];
  issues: MappingDraftIssue[];
  /** Opens the entry editor on a row, or on a new key of an object with free keys. */
  onOpen: (target: Omit<EntryEditorTarget, "session">) => void;
  /** Gives a variable a cache entry read from its first fitting cached type. */
  onUseCache: (variable: DeliveryTemplateVariable) => void;
}

/** The template's variables with what the draft fills them with: pick one to edit its entry. */
export function MappingBuilderVariables({ rows, issues, onOpen, onUseCache }: MappingBuilderVariablesProps) {
  const [filter, setFilter] = useState("");
  const [onlyEntries, setOnlyEntries] = useState(false);

  // The worst finding of the last check per target, so a row can say it needs attention.
  const findings = useMemo(() => {
    const worst = new Map<string, "error" | "warning">();
    for (const issue of issues) {
      if (issue.target !== null && worst.get(issue.target) !== "error") {
        worst.set(issue.target, issue.severity);
      }
    }

    return worst;
  }, [issues]);

  const term = filter.trim().toLowerCase();
  const shown = rows.filter((row) => (!onlyEntries || row.entry !== null)
    && (term === ""
      || row.key.toLowerCase().includes(term)
      || (row.variable.title ?? "").toLowerCase().includes(term)
      || (row.variable.description ?? "").toLowerCase().includes(term)
      || row.variable.relationships.some((relationship) => relationship.toLowerCase().includes(term))
      || (row.entry !== null && entrySummary(row.entry).toLowerCase().includes(term))));
  const filled = rows.filter((row) => row.entry !== null).length;

  const columns: Column<VariableRow>[] = [
    {
      id: "variable",
      header: "Variable",
      render: (row) => {
        const { parent, leaf } = splitPath(row.key);
        return (
          <span
            className="inline-flex items-center gap-1.5"
            style={{ paddingLeft: pathDepth(row.key) * 14 }}
            data-testid={`mapping-builder-variable-${row.key}`}
          >
            <span className="font-mono text-[12px]">
              <span className="text-muted-foreground">{parent}</span>
              <span className="font-medium">{leaf}</span>
            </span>
            {row.kind === "variable" && row.variable.required && <Badge variant="secondary" className="text-[10px]">Required</Badge>}
            {row.kind === "key" && <Badge variant="outline" className="text-[10px]">key</Badge>}
            {row.kind === "outside" && (
              <Badge variant="outline" className="border-destructive/40 text-[10px] text-destructive">not fillable</Badge>
            )}
          </span>
        );
      },
    },
    { id: "shape", header: "Shape", render: (row) => <span className="font-mono text-[12px]">{shapeText(row.variable)}</span> },
    {
      id: "relationships",
      header: "Points to",
      render: (row) => (
        <GlyphRef
          icon={Link2}
          title="Points to"
          body={row.variable.relationships.join("\n")}
          label={String(row.variable.relationships.length)}
          mono
        />
      ),
    },
    {
      id: "entry",
      header: "Entry",
      fill: true,
      floor: 180,
      render: (row) => {
        const finding = findings.get(row.key);
        return (
          <span className="flex min-w-0 items-center gap-1.5" data-testid={`mapping-builder-entry-summary-${row.key}`}>
            {finding === "error" && <OctagonAlert className="size-3.5 shrink-0 text-destructive" aria-label="The check found an error" />}
            {finding === "warning" && <TriangleAlert className="size-3.5 shrink-0 text-warning" aria-label="The check found a warning" />}
            {row.entry === null
              ? <span className="text-muted-foreground">Not filled</span>
              : <TruncatedText text={entrySummary(row.entry)} mono maxWidth={1200} className="min-w-0" />}
            {row.entry !== null && row.entry.prefilled && (
              <Badge variant="secondary" className="shrink-0 bg-info/15 text-[10px] text-info" data-testid="mapping-builder-prefilled">
                Prefilled
                <span className="@max-3xl/table:sr-only"> from cache</span>
              </Badge>
            )}
          </span>
        );
      },
    },
    {
      id: "actions",
      header: "",
      align: "right",
      render: (row) => (
        <span className="inline-flex items-center gap-1">
          {row.kind === "variable" && row.entry === null && row.variable.cacheTypes.length > 0 && (
            <Button
              variant="outline"
              size="xs"
              onClick={(event) => { event.stopPropagation(); onUseCache(row.variable); }}
              data-testid={`mapping-builder-use-cache-${row.key}`}
            >
              <DatabaseZap />
              <span className="@max-3xl/table:sr-only">Use cache</span>
            </Button>
          )}
          {row.kind === "variable" && row.variable.keyValueType !== null && (
            <Button
              variant="outline"
              size="xs"
              onClick={(event) => {
                event.stopPropagation();
                onOpen({ variable: keyVariable(row.variable, `${row.key}.`), entry: null, keyHolder: row.variable, outside: null });
              }}
              data-testid={`mapping-builder-add-key-${row.key}`}
            >
              <Plus />
              <span className="@max-3xl/table:sr-only">Add a key</span>
            </Button>
          )}
        </span>
      ),
    },
  ];

  return (
    <Card className="gap-3 rounded-lg p-4" data-testid="mapping-builder-variables-panel">
      <div className="flex flex-wrap items-baseline gap-2">
        <h2 className="text-[13px] font-medium">Variables</h2>
        <span className="text-xs text-muted-foreground" data-testid="mapping-builder-variables-count">
          {filled} of {rows.length} have an entry. A variable without one is left out of the record.
        </span>
      </div>
      <FilterBar>
        <SearchInput
          value={filter}
          onChange={setFilter}
          placeholder="Path, description or entry"
          label="Filter the variables"
          testId="mapping-builder-variables-filter"
        />
        <Label className="flex items-center gap-2 text-[13px] font-normal">
          <Switch checked={onlyEntries} onCheckedChange={setOnlyEntries} data-testid="mapping-builder-only-entries" />
          Only variables with an entry
        </Label>
      </FilterBar>
      <DataTable
        columns={columns}
        rows={shown}
        rowKey={(row) => row.key}
        onRowClick={(row) => onOpen({ variable: row.variable, entry: row.entry, keyHolder: null, outside: row.outside })}
        emptyMessage={onlyEntries && term === "" ? "No variable has an entry yet." : "No variable matches the filter."}
        skeletonRows={8}
        data-testid="mapping-builder-variables"
      />
    </Card>
  );
}
