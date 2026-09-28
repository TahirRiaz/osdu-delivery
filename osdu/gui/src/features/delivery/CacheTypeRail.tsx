import type { ReactNode } from "react";
import { Layers, ShieldCheck } from "lucide-react";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import type { BrowsedType } from "./cacheRecordsModel";

/** One entry of the list: a type, or every type at once, with the count beside it. */
function RailItem({ label, count, selected, dim, approve = false, icon, onClick, testId, type }: {
  label: string;
  /** The number beside the label; undefined while a search's count for it is on its way. */
  count: number | undefined;
  selected: boolean;
  /** Steps the entry back: the version holds nothing of the type, or nothing of it matches the search. */
  dim: boolean;
  approve?: boolean;
  icon?: ReactNode;
  onClick: () => void;
  testId: string;
  type?: string;
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
      data-type={type}
    >
      {selected && <span className="absolute -left-1.5 h-4 w-0.5 rounded-r bg-primary" />}
      {icon}
      <span className="min-w-0 flex-1 truncate" title={label}>{label}</span>
      {approve && (
        <Tooltip>
          <TooltipTrigger asChild>
            <ShieldCheck className="size-3.5 shrink-0 text-warning" aria-label="Changes need approval" />
          </TooltipTrigger>
          <TooltipContent>Changes need approval</TooltipContent>
        </Tooltip>
      )}
      {count === undefined
        ? <Skeleton className="h-3 w-7" />
        : (
          <span
            className={cn("shrink-0 font-mono text-[11px] tabular-nums", selected ? "text-foreground" : "text-muted-foreground")}
            data-testid="delivery-cache-type-item-count"
          >
            {count.toLocaleString()}
          </span>
        )}
    </button>
  );
}

/**
 * The types of a partition's cache as a list beside the records, grouped by family the way the type picker groups them,
 * each with how many records the version being read holds of it; while a search is typed, how many of them match, with
 * the types nothing matches in stepped back. Picking a type opens its table; All types opens every type's first records.
 */
export function CacheTypeRail({ types, selected, onSelect, matches, className }: {
  types: BrowsedType[];
  /** The type in view; null for every type. */
  selected: string | null;
  onSelect: (type: string | null) => void;
  /** While a search is typed, how many records of each type match it (undefined while that count is on its way); null otherwise. */
  matches: ReadonlyMap<string, number | undefined> | null;
  className?: string;
}) {
  const count = (type: BrowsedType) => (matches === null ? type.items : matches.get(type.name));
  const counts = types.map(count);
  const total = counts.some((value) => value === undefined)
    ? undefined
    : counts.reduce<number>((sum, value) => sum + (value ?? 0), 0);

  const families: { family: string; types: BrowsedType[] }[] = [];
  for (const type of types) {
    const last = families.at(-1);
    if (last !== undefined && last.family === type.family) {
      last.types.push(type);
    } else {
      families.push({ family: type.family, types: [type] });
    }
  }

  return (
    <nav
      aria-label="Cached types"
      className={cn("flex min-h-0 flex-col overflow-hidden rounded-lg border border-border bg-card", className)}
      data-testid="delivery-cache-type-rail"
    >
      <div className="flex h-8 shrink-0 items-center justify-between border-b border-border px-3 text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
        <span>Types</span>
        <span className="normal-case tracking-normal">{matches === null ? "records" : "matches"}</span>
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto px-2 py-1.5">
        <RailItem
          label="All types"
          count={total}
          selected={selected === null}
          dim={total === 0}
          icon={<Layers className="size-4 shrink-0 opacity-75" />}
          onClick={() => onSelect(null)}
          testId="delivery-cache-type-all"
        />
        {families.map(({ family, types: members }) => (
          <section key={family} className="mt-2">
            <h3 className="flex h-6 items-center px-2 text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
              {family}
            </h3>
            {members.map((type) => {
              const value = count(type);
              return (
                <RailItem
                  key={type.name}
                  label={type.name}
                  count={value}
                  selected={selected === type.name}
                  dim={value === 0}
                  approve={type.declared?.onChange === "approve"}
                  onClick={() => onSelect(type.name)}
                  testId="delivery-cache-type-item"
                  type={type.name}
                />
              );
            })}
          </section>
        ))}
      </div>
    </nav>
  );
}
