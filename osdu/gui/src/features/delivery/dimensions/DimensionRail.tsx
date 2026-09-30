import { useMemo, useState, type ReactNode } from "react";
import { LayoutGrid, Search } from "lucide-react";
import { SearchInput } from "@/components/SearchInput";
import { cn } from "@/lib/utils";
import { StandingGlyph } from "./DimensionBadges";
import { SEARCH_REF, type DimensionEntry } from "./dimensionFormat";

/** How a count reads in the rail: whole below ten thousand, then in thousands or millions, so a column of them stays narrow. */
function compact(count: number): string {
  return count < 10_000
    ? count.toLocaleString("en-US")
    : new Intl.NumberFormat("en-US", { notation: "compact", maximumFractionDigits: 1 }).format(count);
}

/** One entry of the rail: every dimension at once, the search builder, or one dimension with where it stands and how many values it holds. */
function RailItem({ label, count, selected, dim, lead, onClick, testId, dimension }: {
  label: string;
  count: number | null;
  selected: boolean;
  dim: boolean;
  lead: ReactNode;
  onClick: () => void;
  testId: string;
  dimension?: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-current={selected ? "true" : undefined}
      className={cn(
        "relative flex h-7 w-full items-center gap-2 rounded-md px-2 text-left text-[13px] outline-none transition-colors duration-120 focus-visible:bg-accent/60",
        selected ? "bg-accent font-medium text-foreground" : "text-foreground/90 hover:bg-accent/60",
        dim && !selected && "text-muted-foreground/70",
      )}
      data-testid={testId}
      data-dimension={dimension}
    >
      {selected && <span className="absolute -left-1.5 h-4 w-0.5 rounded-r bg-primary" />}
      {lead}
      <span className="min-w-0 flex-1 truncate" title={label}>{label}</span>
      {count !== null && (
        <span className={cn("shrink-0 font-mono text-[11px] tabular-nums", selected ? "text-foreground" : "text-muted-foreground")}>
          {compact(count)}
        </span>
      )}
    </button>
  );
}

/**
 * The dimensions of the partition as a list beside the page, grouped by the flow that declares them, each with where it
 * stands (a glyph in its status colour) and how many values it holds. A filter over the names narrows it when a partition
 * holds many. Picking a dimension opens it; Every dimension opens the overview of them all, and Build a search the search
 * builder, which picks values across a kind's dimensions.
 */
export function DimensionRail({ entries, selected, onSelect, className }: {
  entries: DimensionEntry[];
  /** The dimension in view, by its link name; null for the overview, and SEARCH_REF for the search builder. */
  selected: string | null;
  onSelect: (ref: string | null) => void;
  className?: string;
}) {
  const [term, setTerm] = useState("");
  const lowered = term.trim().toLowerCase();
  const groups = useMemo(() => {
    const shown = lowered === ""
      ? entries
      : entries.filter((entry) => entry.dimension.name.toLowerCase().includes(lowered)
        || entry.dimension.path.toLowerCase().includes(lowered)
        || entry.flow.name.toLowerCase().includes(lowered));
    const byFlow: { flow: string; entries: DimensionEntry[] }[] = [];
    for (const entry of shown) {
      const last = byFlow.at(-1);
      if (last !== undefined && last.flow === entry.flow.name) {
        last.entries.push(entry);
      } else {
        byFlow.push({ flow: entry.flow.name, entries: [entry] });
      }
    }

    return byFlow;
  }, [entries, lowered]);
  const values = entries.reduce((sum, entry) => sum + entry.dimension.values, 0);

  return (
    <nav
      aria-label="Dimensions"
      className={cn("flex min-h-0 flex-col overflow-hidden rounded-lg border border-border bg-card", className)}
      data-testid="dimension-rail"
    >
      <div className="flex h-8 shrink-0 items-center justify-between border-b border-border px-3 text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
        <span>Dimensions</span>
        <span className="normal-case tracking-normal">values</span>
      </div>
      {entries.length > 8 && (
        <div className="shrink-0 border-b border-border p-2">
          <SearchInput value={term} onChange={setTerm} placeholder="Name, path or flow" label="Filter dimensions" className="sm:w-full" testId="dimension-rail-search" />
        </div>
      )}
      <div className="min-h-0 flex-1 overflow-y-auto px-2 py-1.5">
        <RailItem
          label="Every dimension"
          count={values}
          selected={selected === null}
          dim={false}
          lead={<LayoutGrid className="size-4 shrink-0 opacity-75" />}
          onClick={() => onSelect(null)}
          testId="dimension-rail-all"
        />
        <RailItem
          label="Build a search"
          count={null}
          selected={selected === SEARCH_REF}
          dim={false}
          lead={<Search className="size-4 shrink-0 opacity-75" />}
          onClick={() => onSelect(SEARCH_REF)}
          testId="dimension-rail-search-builder"
        />
        {groups.map((group) => (
          <section key={group.flow} className="mt-2">
            <h3 className="flex h-6 items-center truncate px-2 font-mono text-[11px] text-muted-foreground" title={group.flow}>
              {group.flow}
            </h3>
            {group.entries.map((entry) => (
              <RailItem
                key={entry.ref}
                label={entry.dimension.name}
                count={entry.dimension.dimensionId === null ? null : entry.dimension.values}
                selected={selected === entry.ref}
                dim={entry.standing === "notBuilt" || entry.standing === "undeclared"}
                lead={<StandingGlyph standing={entry.standing} />}
                onClick={() => onSelect(entry.ref)}
                testId="dimension-rail-item"
                dimension={entry.dimension.name}
              />
            ))}
          </section>
        ))}
        {groups.length === 0 && <p className="px-2 py-3 text-xs text-muted-foreground">No dimension matches the filter.</p>}
      </div>
    </nav>
  );
}
