import { useMemo, useState } from "react";
import { ChevronDown, ChevronRight, Layers } from "lucide-react";
import { Skeleton } from "@/components/ui/skeleton";
import { SearchInput } from "@/components/SearchInput";
import { cn } from "@/lib/utils";
import type { ExplorerTypes } from "../../../api/explorer";
import { sameScope, typeTree, type ExplorerScope, type GroupNode } from "./explorerModel";
import { ReadingBar } from "./ReadingBar";

/** A group with more types than this starts folded, so a partition's long reference lists do not bury the rest. */
const OPEN_UP_TO = 24;

const compact = new Intl.NumberFormat("en-US", { notation: "compact", maximumFractionDigits: 1 });

/** A count as the rail shows it: exact up to a hundred thousand, compact past it, the exact count on hover. */
function Count({ value, quiet = false }: { value: number; quiet?: boolean }) {
  return (
    <span className={cn("ml-auto shrink-0 pl-2 font-mono text-[11px] tabular-nums", quiet ? "text-muted-foreground/70" : "text-muted-foreground")} title={value.toLocaleString("en-US")}>
      {value >= 100_000 ? compact.format(value) : value.toLocaleString("en-US")}
    </span>
  );
}

/** One row of the rail: a caret where it folds, the name, the count; the whole row picks what it names. */
function Row({ depth, label, title, count, selected, open, onToggle, onSelect, mono = false, testId }: {
  depth: number;
  label: string;
  title?: string;
  count: number;
  selected: boolean;
  open?: boolean;
  onToggle?: () => void;
  onSelect: () => void;
  mono?: boolean;
  testId: string;
}) {
  return (
    <div
      className={cn("group flex h-7 min-w-0 items-center rounded-md pr-2 text-[12px] hover:bg-accent/50", selected && "bg-accent text-accent-foreground")}
      style={{ paddingLeft: 4 + depth * 14 }}
      data-state={selected ? "active" : undefined}
      data-testid={testId}
    >
      {onToggle !== undefined
        ? (
          <button
            type="button"
            className="flex size-5 shrink-0 items-center justify-center rounded-sm text-muted-foreground hover:text-foreground"
            onClick={onToggle}
            aria-expanded={open}
            aria-label={open ? `Fold ${label}` : `Unfold ${label}`}
          >
            {open ? <ChevronDown className="size-3.5" /> : <ChevronRight className="size-3.5" />}
          </button>
        )
        : <span className="size-5 shrink-0" aria-hidden />}
      <button type="button" className="flex h-full min-w-0 flex-1 items-center text-left" onClick={onSelect} title={title ?? label} aria-current={selected ? "true" : undefined}>
        <span className={cn("min-w-0 truncate", mono && "font-mono text-[11px]")}>{label}</span>
        <Count value={count} quiet={!selected} />
      </button>
    </div>
  );
}

/**
 * The kinds of the records a search finds, as a list to pick from: every type, then each group (master-data,
 * reference-data, work-product-component, ...) with its types, and a type kept in several kinds (versions, authorities)
 * with those kinds under it, each with how many records the search finds there. Picking one narrows the records to it; the
 * counts follow the text and the values typed, so the list also says where else a search finds something. A filter finds
 * a type among hundreds. Long groups start folded.
 */
export function ExplorerTypeRail({ types, loading, error, scope, onScope }: {
  types: ExplorerTypes | undefined;
  loading: boolean;
  error: string | null;
  /** The place picked; null while the reader has picked none. */
  scope: ExplorerScope | null;
  onScope: (scope: ExplorerScope) => void;
}) {
  const [filter, setFilter] = useState("");
  const groups = useMemo(() => typeTree(types?.kinds ?? []), [types]);
  const [toggled, setToggled] = useState<ReadonlySet<string>>(() => new Set());
  const term = filter.trim().toLowerCase();
  const shown = useMemo((): GroupNode[] => (term === ""
    ? groups
    : groups
      .map((group) => ({
        ...group,
        types: group.types.filter((type) => type.entityType.toLowerCase().includes(term) || type.kinds.some((kind) => kind.kind.toLowerCase().includes(term))),
      }))
      .filter((group) => group.types.length > 0)), [groups, term]);

  // A group is open as it starts (short groups, and the one holding what is picked) unless the reader toggled it; a filter opens them all.
  const startsOpen = (group: GroupNode) => group.types.length <= OPEN_UP_TO
    || (scope?.level === "type" && group.types.some((type) => type.entityType === scope.entityType))
    || (scope?.level === "kind" && group.types.some((type) => type.kinds.some((kind) => kind.kind === scope.kind)));
  const picked = (candidate: ExplorerScope) => scope !== null && sameScope(scope, candidate);
  const isOpen = (key: string, starts: boolean) => term !== "" || (toggled.has(key) ? !starts : starts);
  const toggle = (key: string) => setToggled((was) => {
    const next = new Set(was);
    if (next.has(key)) {
      next.delete(key);
    } else {
      next.add(key);
    }

    return next;
  });

  return (
    <div className="relative flex h-full min-h-0 flex-col" data-testid="explorer-types">
      {loading && <ReadingBar label="Counting the types in OSDU" />}
      <div className="border-b p-2">
        <SearchInput value={filter} onChange={setFilter} placeholder="Filter types" label="Filter the types" className="w-full sm:w-full" testId="explorer-types-filter" />
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto p-1" data-testid="explorer-types-list">
        {error !== null && <p className="px-2 py-1 text-[12px] text-destructive" data-testid="explorer-types-error">{error}</p>}
        {types === undefined && loading && Array.from({ length: 12 }, (_, index) => <Skeleton key={index} className="mx-1 my-1.5 h-4 rounded" style={{ width: `${55 + ((index * 17) % 40)}%` }} />)}
        {types !== undefined && (
          <>
            <div
              className={cn("flex h-7 items-center gap-1.5 rounded-md px-2 text-[12px] hover:bg-accent/50", scope?.level === "all" && "bg-accent text-accent-foreground")}
              data-state={scope?.level === "all" ? "active" : undefined}
              data-testid="explorer-type-all"
            >
              <button type="button" className="flex h-full min-w-0 flex-1 items-center gap-1.5 text-left font-medium" onClick={() => onScope({ level: "all" })}>
                <Layers className="size-3.5 shrink-0 text-muted-foreground" />
                <span className="truncate">All types</span>
                <Count value={types.total} quiet={scope?.level !== "all"} />
              </button>
            </div>
            {shown.map((group) => {
              const groupScope: ExplorerScope = { level: "group", group: group.group };
              const open = isOpen(group.group, startsOpen(group));
              return (
                <div key={group.group}>
                  <Row
                    depth={0}
                    label={group.group}
                    count={group.count}
                    selected={picked(groupScope)}
                    open={open}
                    onToggle={() => toggle(group.group)}
                    onSelect={() => onScope(groupScope)}
                    testId="explorer-type-group"
                  />
                  {open && group.types.map((type) => {
                    const typeScope: ExplorerScope = { level: "type", entityType: type.entityType };
                    const versions = type.kinds.length > 1;
                    const typeOpen = versions && isOpen(type.entityType, type.kinds.some((kind) => scope?.level === "kind" && kind.kind === scope.kind));
                    return (
                      <div key={type.entityType}>
                        <Row
                          depth={1}
                          label={type.type}
                          title={type.entityType}
                          count={type.count}
                          selected={picked(typeScope)}
                          open={versions ? typeOpen : undefined}
                          onToggle={versions ? () => toggle(type.entityType) : undefined}
                          onSelect={() => onScope(typeScope)}
                          testId="explorer-type"
                        />
                        {typeOpen && type.kinds.map((kind) => (
                          <Row
                            key={kind.kind}
                            depth={2}
                            label={`${kind.parts.version} ${kind.parts.authority}:${kind.parts.source}`}
                            title={kind.kind}
                            count={kind.count}
                            selected={scope?.level === "kind" && scope.kind === kind.kind}
                            onSelect={() => onScope({ level: "kind", kind: kind.kind })}
                            mono
                            testId="explorer-type-kind"
                          />
                        ))}
                      </div>
                    );
                  })}
                </div>
              );
            })}
            {shown.length === 0 && (
              <p className="px-2 py-3 text-[12px] text-muted-foreground" data-testid="explorer-types-none">
                {term === "" ? "Nothing here holds what the search asks." : "No type matches the filter."}
              </p>
            )}
          </>
        )}
      </div>
      {types !== undefined && types.listed < types.total && (
        <div
          className="border-t px-3 py-1.5 text-[11px] text-muted-foreground"
          title="The search service names a limited number of kinds in one answer; narrow the search to see the rest."
          data-testid="explorer-types-partial"
        >
          {`${(types.total - types.listed).toLocaleString("en-US")} records in kinds not listed`}
        </div>
      )}
    </div>
  );
}
