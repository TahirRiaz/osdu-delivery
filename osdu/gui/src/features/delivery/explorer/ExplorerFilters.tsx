import { useState } from "react";
import { Columns3, ListFilter, TriangleAlert, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import type { ExplorerFilter, ExplorerSearchRequest } from "../../../api/explorer";
import { isRecordReference } from "../osduDocument";
import { RecordName } from "../RecordName";
import { ExplorerFilterEditor } from "./ExplorerFilterEditor";
import { CONDITION_LABELS, conditionValueText, fieldLabel, filterSentence, sameFilter } from "./explorerModel";
import { offeredTerm, termOf, termTitle, useSearchTerms } from "./explorerTerms";

/** The width of the condition editor: room for a long property path and a value beside its count. */
const EDITOR_WIDTH = "w-[420px]";

/**
 * Filter: a new condition on one property or source column of the records in view, made in the editor (the attribute,
 * what it must hold, the value).
 */
export function ExplorerAddFilter({ partition, request, onAdd }: {
  partition: string | null;
  request: ExplorerSearchRequest & { filters: ExplorerFilter[] };
  onAdd: (filter: ExplorerFilter) => void;
}) {
  const [open, setOpen] = useState(false);
  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button variant="outline" size="sm" className="h-8 gap-1.5 px-2.5 text-[13px]" title="Narrow the records by what one property holds: its words, its value, a range, or whether it holds one" data-testid="explorer-add-filter">
          <ListFilter />
          Filter
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className={`${EDITOR_WIDTH} p-0`} data-testid="explorer-add-filter-panel">
        {open && (
          <ExplorerFilterEditor
            partition={partition}
            base={request}
            initial={null}
            applyLabel="Add"
            onApply={(filter) => { onAdd(filter); setOpen(false); }}
            onCancel={() => setOpen(false)}
          />
        )}
      </PopoverContent>
    </Popover>
  );
}

/**
 * The conditions the records are narrowed to, one chip each, read as a sentence (`WellboreName starts with NO 34/10`): a
 * click opens the editor to change it, the cross drops it, and Clear all drops them all. Every condition holds, together
 * with the search typed. A condition on a source column (a search term) says the column's name, marked as one, with its
 * system and the property it is searched in on hover; one whose column is no longer searched says so.
 */
export function ExplorerFilterChips({ partition, request, onFilters }: {
  partition: string | null;
  request: ExplorerSearchRequest & { filters: ExplorerFilter[] };
  onFilters: (filters: ExplorerFilter[]) => void;
}) {
  const [editing, setEditing] = useState<number | null>(null);
  const terms = useSearchTerms(request.kind, request.filters.some((filter) => filter.term !== undefined), true);
  if (request.filters.length === 0) {
    return null;
  }

  return (
    <div className="flex flex-wrap items-center gap-1.5 border-b px-3 py-1.5" data-testid="explorer-filters">
      {request.filters.map((filter, index) => {
        const others = request.filters.filter((_, at) => at !== index);
        const value = conditionValueText(filter);
        const condition = filter.condition ?? "is";
        const named = termOf(filter, terms.data?.terms);
        const offered = named === undefined ? null : offeredTerm(named);
        // A term named but no longer searched (deleted, or no mapping reads it): the search says why it was refused.
        const lost = filter.term !== undefined && offered === null && (terms.data !== undefined || terms.isError);
        const name = named?.name;
        const said = filterSentence(filter, name);
        return (
          <span key={`${index}:${filter.path}`} className="inline-flex max-w-full items-center rounded-md border bg-muted/60 text-[12px]" data-testid="explorer-filter">
            <Popover open={editing === index} onOpenChange={(open) => setEditing(open ? index : null)}>
              <PopoverTrigger asChild>
                <button
                  type="button"
                  className="inline-flex min-w-0 items-center gap-1 rounded-l-md py-0.5 pl-2 pr-1 text-left hover:bg-accent/60"
                  title={[
                    said,
                    offered === null ? null : termTitle(offered),
                    lost ? "The source column this condition names is no longer searched: change the condition, or drop it." : null,
                    "Click to change it.",
                  ].filter(Boolean).join("\n\n")}
                  data-testid="explorer-filter-edit"
                  data-term={filter.term}
                >
                  {filter.term !== undefined && (lost
                    ? <TriangleAlert className="size-3 shrink-0 text-warning" aria-label="No longer searched" />
                    : <Columns3 className="size-3 shrink-0 text-muted-foreground" aria-label="A source column" />)}
                  {/* The spaces between the parts are the chip's words when it is read or copied; a flex row draws none of them. */}
                  <span className={name === undefined ? "shrink-0 font-mono text-[11px] text-muted-foreground" : "shrink-0 text-muted-foreground"} data-testid="explorer-filter-name">
                    {name ?? fieldLabel(filter.path)}
                  </span>
                  {" "}
                  <span className="shrink-0 text-muted-foreground">{condition === "range" ? "is" : CONDITION_LABELS[condition]}</span>
                  {value !== "" && " "}
                  {value !== "" && (
                    <span className="min-w-0 max-w-[320px] truncate" data-testid="explorer-filter-value-text">
                      {condition !== "anyOf" && condition !== "noneOf" && condition !== "range" && isRecordReference(value) ? <RecordName id={value} /> : value}
                    </span>
                  )}
                </button>
              </PopoverTrigger>
              <PopoverContent align="start" className={`${EDITOR_WIDTH} p-0`} data-testid="explorer-filter-edit-panel">
                {editing === index && (
                  <ExplorerFilterEditor
                    partition={partition}
                    base={{ ...request, filters: others }}
                    initial={filter}
                    applyLabel="Apply"
                    onApply={(changed) => {
                      setEditing(null);
                      onFilters(request.filters.map((kept, at) => (at === index ? changed : kept)).filter((kept, at, all) => all.findIndex((other) => sameFilter(other, kept)) === at));
                    }}
                    onCancel={() => setEditing(null)}
                  />
                )}
              </PopoverContent>
            </Popover>
            <button
              type="button"
              className="mr-0.5 rounded-sm p-0.5 text-muted-foreground hover:bg-accent hover:text-foreground"
              onClick={() => onFilters(others)}
              aria-label={`Drop the condition ${said}`}
              data-testid="explorer-filter-drop"
            >
              <X className="size-3" />
            </button>
          </span>
        );
      })}
      {request.filters.length > 1 && (
        <button type="button" className="px-1 text-[12px] text-muted-foreground hover:text-foreground hover:underline" onClick={() => onFilters([])} data-testid="explorer-filters-clear">
          Clear all
        </button>
      )}
    </div>
  );
}
