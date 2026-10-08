import { useState } from "react";
import { ListFilter, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import type { ExplorerFilter, ExplorerSearchRequest } from "../../../api/explorer";
import { isRecordReference } from "../osduDocument";
import { RecordName } from "../RecordName";
import { ExplorerFilterEditor } from "./ExplorerFilterEditor";
import { CONDITION_LABELS, conditionValueText, fieldLabel, filterSentence, sameFilter } from "./explorerModel";

/** The width of the condition editor: room for a long property path and a value beside its count. */
const EDITOR_WIDTH = "w-[420px]";

/**
 * Filter: a new condition on one property of the records in view, made in the editor (the property, what it must hold,
 * the value). Opened from here, or by the search box with the text typed there, to be searched in a property picked.
 */
export function ExplorerAddFilter({ partition, request, open, startValue, onOpenChange, onAdd }: {
  partition: string | null;
  request: ExplorerSearchRequest & { filters: ExplorerFilter[] };
  open: boolean;
  /** The text a condition opened by the search box starts with. */
  startValue: string;
  onOpenChange: (open: boolean) => void;
  onAdd: (filter: ExplorerFilter) => void;
}) {
  return (
    <Popover open={open} onOpenChange={onOpenChange}>
      <PopoverTrigger asChild>
        <Button variant="outline" size="sm" className="h-8 gap-1.5 px-2.5 text-[13px]" title="Narrow the records by what one property holds: its words, its value, a range, or whether it holds one" data-testid="explorer-add-filter">
          <ListFilter />
          Filter
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className={`${EDITOR_WIDTH} p-0`} data-testid="explorer-add-filter-panel">
        {open && (
          <ExplorerFilterEditor
            // A new ask (the search box's text) starts the editor afresh.
            key={startValue}
            partition={partition}
            base={request}
            initial={null}
            startValue={startValue}
            applyLabel="Add"
            onApply={(filter) => { onAdd(filter); onOpenChange(false); }}
            onCancel={() => onOpenChange(false)}
          />
        )}
      </PopoverContent>
    </Popover>
  );
}

/**
 * The conditions the records are narrowed to, one chip each, read as a sentence (`WellboreName starts with NO 34/10`): a
 * click opens the editor to change it, the cross drops it, and Clear all drops them all. Every condition holds, together
 * with the search typed.
 */
export function ExplorerFilterChips({ partition, request, onFilters }: {
  partition: string | null;
  request: ExplorerSearchRequest & { filters: ExplorerFilter[] };
  onFilters: (filters: ExplorerFilter[]) => void;
}) {
  const [editing, setEditing] = useState<number | null>(null);
  if (request.filters.length === 0) {
    return null;
  }

  return (
    <div className="flex flex-wrap items-center gap-1.5 border-b px-3 py-1.5" data-testid="explorer-filters">
      {request.filters.map((filter, index) => {
        const others = request.filters.filter((_, at) => at !== index);
        const value = conditionValueText(filter);
        const condition = filter.condition ?? "is";
        return (
          <span key={`${index}:${filter.path}`} className="inline-flex max-w-full items-center rounded-md border bg-muted/60 text-[12px]" data-testid="explorer-filter">
            <Popover open={editing === index} onOpenChange={(open) => setEditing(open ? index : null)}>
              <PopoverTrigger asChild>
                <button
                  type="button"
                  className="inline-flex min-w-0 items-center gap-1 rounded-l-md py-0.5 pl-2 pr-1 text-left hover:bg-accent/60"
                  title={`${filterSentence(filter)}\nClick to change it.`}
                  data-testid="explorer-filter-edit"
                >
                  {/* The spaces between the parts are the chip's words when it is read or copied; a flex row draws none of them. */}
                  <span className="shrink-0 font-mono text-[11px] text-muted-foreground">{fieldLabel(filter.path)}</span>
                  {" "}
                  <span className="shrink-0 text-muted-foreground">{condition === "range" ? "is" : CONDITION_LABELS[condition]}</span>
                  {value !== "" && " "}
                  {value !== "" && (
                    <span className="min-w-0 max-w-[320px] truncate" data-testid="explorer-filter-value-text">
                      {condition !== "anyOf" && condition !== "range" && isRecordReference(value) ? <RecordName id={value} /> : value}
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
              aria-label={`Drop the condition ${filterSentence(filter)}`}
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
