import { useMemo, useState } from "react";
import { DatabaseZap, Link2, OctagonAlert, Plus, TriangleAlert } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import type {
  DeliveryTemplateDetail, DeliveryTemplateVariable, MappingDraft, MappingDraftEntry, MappingDraftIssue,
} from "../../api/delivery";
import { DataTable, type Column } from "../../components/DataTable";
import { FilterBar } from "../../components/FilterBar";
import { GlyphRef } from "../../components/GlyphRef";
import { SearchInput } from "../../components/SearchInput";
import { TruncatedText } from "../../components/TruncatedText";
import { keyVariable, type EntryEditorTarget } from "./MappingEntryEditor";
import { entrySummary, KEY_NAME } from "./mappingDraft";
import { pathDepth, shapeText, splitPath } from "./templateFormat";

/** One row of the variables list: a variable a mapping can fill, a key under an object with free keys, or an entry the template does not let a mapping fill. */
export interface VariableRow {
  key: string;
  variable: DeliveryTemplateVariable;
  entry: MappingDraftEntry | null;
  kind: "variable" | "key" | "outside";
  /** Why no mapping may fill the row's target, for an outside row. */
  outside: string | null;
}

function isKeyOf(holder: string, target: string): boolean {
  return target.startsWith(`${holder}.`) && KEY_NAME.test(target.slice(holder.length + 1));
}

function unknownVariable(target: string): DeliveryTemplateVariable {
  return {
    path: target,
    shape: "Value",
    type: "any",
    itemType: null,
    format: null,
    required: false,
    role: "Mapping",
    relationships: [],
    pattern: null,
    unitContext: null,
    title: null,
    description: null,
    keyValueType: null,
    nested: false,
    cacheTypes: [],
  };
}

/**
 * The rows of the variables list, in template order: every variable a mapping can fill with its entry, each key entry
 * under the object with free keys it belongs to, and at the end every entry whose target the template does not let a
 * mapping fill, so nothing in the draft is out of sight.
 */
export function variableRows(detail: DeliveryTemplateDetail, draft: MappingDraft): VariableRow[] {
  const byTarget = new Map(draft.entries.map((entry) => [entry.target, entry]));
  const byPath = new Map(detail.variables.map((variable) => [variable.path, variable]));
  const shown = new Set<string>();
  const rows: VariableRow[] = [];

  for (const variable of detail.variables) {
    if (variable.role !== "Mapping" || variable.nested) {
      continue;
    }

    const entry = byTarget.get(variable.path) ?? null;
    shown.add(variable.path);
    rows.push({ key: variable.path, variable, entry, kind: "variable", outside: null });
    if (variable.keyValueType === null) {
      continue;
    }

    for (const candidate of draft.entries) {
      // A property the template names is its own row, even under an object that also takes free keys.
      if (!shown.has(candidate.target) && !byPath.has(candidate.target) && isKeyOf(variable.path, candidate.target)) {
        shown.add(candidate.target);
        rows.push({ key: candidate.target, variable: keyVariable(variable, candidate.target), entry: candidate, kind: "key", outside: null });
      }
    }
  }

  for (const entry of draft.entries) {
    if (shown.has(entry.target)) {
      continue;
    }

    shown.add(entry.target);
    const known = byPath.get(entry.target);
    let outside: string;
    if (known === undefined) {
      outside = `Template ${detail.kind} version ${detail.version} has no variable ${entry.target}.`;
    } else if (known.role === "Engine") {
      outside = `${entry.target} is written by OSDU Delivery, not by a mapping.`;
    } else if (known.role === "Osdu") {
      outside = `${entry.target} is set by OSDU when the record is stored, not by a mapping.`;
    } else {
      outside = `${entry.target} is a list inside a repeated item, which a mapping cannot fill.`;
    }

    rows.push({ key: entry.target, variable: known ?? unknownVariable(entry.target), entry, kind: "outside", outside });
  }

  return rows;
}

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
