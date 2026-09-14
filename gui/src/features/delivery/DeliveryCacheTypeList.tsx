import { ShieldCheck } from "lucide-react";
import { Card } from "@/components/ui/card";
import { cn } from "@/lib/utils";
import { RichTooltip } from "../../components/RichTooltip";
import type { CachedTypeSummary } from "./cacheFormat";

/** One entry of the list: the type's name, a glyph when its changes wait for approval, and how many records it holds. */
function TypeRow({ label, records, approval, active, onClick, testId }: {
  label: string;
  records: number;
  approval: boolean;
  active: boolean;
  onClick: () => void;
  testId: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-pressed={active}
      className={cn(
        "flex h-8 w-full min-w-0 items-center gap-2 rounded-md px-2 text-left transition-colors duration-100",
        active
          ? "bg-accent text-foreground shadow-[inset_2px_0_0_var(--primary)]"
          : "text-foreground/85 hover:bg-accent/50 hover:text-foreground",
      )}
      data-testid={testId}
    >
      <span className={cn("min-w-0 flex-1 truncate font-mono text-[12.5px]", active && "font-medium")}>{label}</span>
      {approval && (
        <RichTooltip body="Changes to this type wait for approval before they reach delivered records.">
          <span className="inline-flex text-warning" aria-label="Changes wait for approval">
            <ShieldCheck className="size-3.5" />
          </span>
        </RichTooltip>
      )}
      <span className="shrink-0 font-mono text-[11px] tabular-nums text-muted-foreground">{records.toLocaleString()}</span>
    </button>
  );
}

/**
 * The cache's declared types as a list beside the working tabs: every type once, grouped by family, with how many
 * records the current version holds of it. Picking one scopes the records and the versions to it; All types lifts the
 * scope. It replaces a type table and a type dropdown that used to say the same thing in two places.
 */
export function DeliveryCacheTypeList({ types, selected, onSelect }: {
  types: CachedTypeSummary[];
  selected: string | null;
  onSelect: (type: string | null) => void;
}) {
  const total = types.reduce((sum, type) => sum + type.items, 0);
  const families: [string, CachedTypeSummary[]][] = [];
  for (const type of types) {
    const group = families.find(([family]) => family === type.family);
    if (group === undefined) {
      families.push([type.family, [type]]);
    } else {
      group[1].push(type);
    }
  }

  return (
    <Card className="gap-0 rounded-lg p-1.5 lg:sticky lg:top-0" data-testid="delivery-cache-types">
      <TypeRow
        label="All types"
        records={total}
        approval={false}
        active={selected === null}
        onClick={() => onSelect(null)}
        testId="delivery-cache-type-all"
      />
      {families.map(([family, members]) => (
        <div key={family} className="mt-2 flex flex-col">
          <div className="px-2 pb-1 text-[11px] font-medium uppercase tracking-wider text-muted-foreground">{family}</div>
          {members.map((type) => (
            <TypeRow
              key={type.name}
              label={type.name}
              records={type.items}
              approval={type.onChange === "approve"}
              active={selected === type.name}
              onClick={() => onSelect(type.name)}
              testId={`delivery-cache-type-${type.name}`}
            />
          ))}
        </div>
      ))}
    </Card>
  );
}
