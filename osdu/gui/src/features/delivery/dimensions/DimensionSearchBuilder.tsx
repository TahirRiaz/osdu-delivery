import { useMemo, useState } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { Check, Info, Plus, Search, TriangleAlert, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Command, CommandEmpty, CommandInput, CommandItem, CommandList } from "@/components/ui/command";
import { Input } from "@/components/ui/input";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { CodeView } from "@/components/CodeView";
import { PageHeader } from "@/components/PageHeader";
import { CopyButton } from "@/components/CopyButton";
import { EmptyState } from "@/components/EmptyState";
import { RichTooltip } from "@/components/RichTooltip";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { deliveryApi, type DeliveryDimensionAttributePick, type DeliveryDimensionSearch, type DimensionAttributeCondition } from "../../../api/delivery";
import { KindText } from "../KindText";
import { ProblemView } from "../TemplateSheet";
import { counted } from "../assertions/assertionFormat";
import { DimensionAttributeFilter } from "./DimensionAttributeFilter";
import { DimensionsCrumb, StandingGlyph } from "./DimensionBadges";
import { DimensionValueText } from "./DimensionValueText";
import type { DimensionEntry } from "./dimensionFormat";

/** The values a picker lists: those most records hold that match what is typed. */
const PICKER_LIMIT = 30;

/** How long typing rests before the values or the search are asked for. */
const TYPING_DELAY_MS = 300;

/** The clauses one search holds at most, as the control plane composes it; the service allows 1024. */
const MAX_CLAUSES = 1000;

/** Picks by dimension: the ids of the values picked in each. */
export type SearchPicks = ReadonlyMap<number, ReadonlySet<number>>;

/** Attribute picks by dimension: the attribute values its keys have to hold. */
export type SearchAttributePicks = ReadonlyMap<number, readonly DimensionAttributeCondition[]>;

/** Conditions as a pick sends them: grouped by attribute, any of each one's values. */
function attributePicks(conditions: readonly DimensionAttributeCondition[]): DeliveryDimensionAttributePick[] {
  const byName = new Map<string, string[]>();
  for (const condition of conditions) {
    (byName.get(condition.name) ?? byName.set(condition.name, []).get(condition.name)!).push(condition.value);
  }

  return [...byName.entries()].map(([name, values]) => ({ name, values }));
}

/**
 * The values of one dimension to pick from: those most records hold, narrowed by what is typed (a value, or any key it
 * stands for), each with its records and a mark when it is picked. Picking one leaves the list open, so several are
 * picked in a row.
 */
function ValuePicker({ dimensionId, name, picked, onPick }: {
  dimensionId: number;
  name: string;
  picked: ReadonlySet<number>;
  onPick: (valueId: number, value: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const [typed, setTyped] = useState("");
  const search = useDebouncedValue(typed.trim(), TYPING_DELAY_MS);
  const values = useQuery({
    queryKey: ["delivery", "dimensions", "picker", dimensionId, search],
    queryFn: () => deliveryApi.dimensionValues(dimensionId, { search, order: "records", limit: PICKER_LIMIT }),
    enabled: open,
    placeholderData: keepPreviousData,
    staleTime: 60_000,
  });
  const items = values.data?.items ?? [];

  return (
    <Popover open={open} onOpenChange={(next) => { setOpen(next); if (next) { setTyped(""); } }}>
      <PopoverTrigger asChild>
        <Button variant="outline" size="xs" data-testid="search-builder-add-value">
          <Plus />
          {picked.size === 0 ? "Pick values" : "Add"}
        </Button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-80 p-0" data-testid="search-builder-picker">
        <Command shouldFilter={false}>
          <CommandInput placeholder={`A value of ${name}, or a key`} value={typed} onValueChange={setTyped} />
          <CommandList>
            <CommandEmpty>
              {values.isPending
                ? "Reading the values..."
                : values.isError
                  ? "The values could not be read."
                  : search === "" ? `${name} holds no value.` : `No value of ${name}, nor any key, holds "${search}".`}
            </CommandEmpty>
            {items.map((item) => (
              <CommandItem
                key={item.valueId}
                value={String(item.valueId)}
                onSelect={() => onPick(item.valueId, item.value)}
                className="flex items-center gap-2"
                data-testid="search-builder-picker-value"
              >
                <Check className={picked.has(item.valueId) ? "opacity-100" : "opacity-0"} />
                <span className="min-w-0 flex-1"><DimensionValueText value={item.value} maxWidth={200} /></span>
                <span className="shrink-0 font-mono text-[11px] tabular-nums text-muted-foreground">
                  {item.recordsExact ? "" : "~"}{item.records.toLocaleString("en-US")}
                </span>
              </CommandItem>
            ))}
          </CommandList>
        </Command>
        {items.length === PICKER_LIMIT && (
          <p className="border-t px-2 py-1.5 text-[11px] text-muted-foreground">
            The {PICKER_LIMIT} values most records hold; type to find another.
          </p>
        )}
      </PopoverContent>
    </Popover>
  );
}

/**
 * One dimension of the kind: its name and what it reads, the values picked in it and the picker adding more, and the
 * attribute values its keys have to hold ("where").
 */
function DimensionPicks({ entry, picked, names, onPick, onUnpick, conditions, onConditions }: {
  entry: DimensionEntry;
  picked: ReadonlySet<number>;
  names: ReadonlyMap<number, string>;
  onPick: (valueId: number, value: string) => void;
  onUnpick: (valueId: number) => void;
  conditions: readonly DimensionAttributeCondition[];
  onConditions: (next: DimensionAttributeCondition[]) => void;
}) {
  const { dimension } = entry;
  const reads = dimension.label.length > 0 ? `${dimension.path}, labelled by ${dimension.label.at(-1)}` : dimension.path;
  return (
    <div className="flex flex-col gap-1.5 border-b border-border px-3 py-2.5 last:border-b-0" data-testid="search-builder-dimension" data-dimension={dimension.name}>
      <div className="flex min-w-0 items-center gap-2">
        <StandingGlyph standing={entry.standing} />
        <RichTooltip title={entry.flow.name} body={`Reads ${reads}${dimension.builtQuery === null ? "" : `, within ${dimension.builtQuery}`}.`} mono>
          <span className="min-w-0 truncate text-[13px] font-medium">{dimension.name}</span>
        </RichTooltip>
        <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{counted(dimension.values, "value")}</span>
        <span className="ml-auto shrink-0">
          <ValuePicker dimensionId={dimension.dimensionId!} name={dimension.name} picked={picked} onPick={onPick} />
        </span>
      </div>
      {picked.size > 0 && (
        <div className="flex flex-wrap gap-1.5" data-testid="search-builder-picked">
          {[...picked].map((valueId) => (
            <span key={valueId} className="inline-flex min-w-0 items-center gap-1 rounded-sm bg-primary/10 py-px pr-0.5 pl-1.5 text-[12px]">
              {names.has(valueId)
                ? <DimensionValueText value={names.get(valueId)!} maxWidth={220} />
                : <span className="font-mono text-[11px] text-muted-foreground">value #{valueId}</span>}
              <button
                type="button"
                onClick={() => onUnpick(valueId)}
                className="rounded-sm p-0.5 text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:bg-accent"
                aria-label="Remove the value"
                data-testid="search-builder-unpick"
              >
                <X className="size-3" />
              </button>
            </span>
          ))}
        </div>
      )}
      {dimension.attributes.length > 0 && (
        <div className="flex min-w-0 items-center gap-1.5">
          <span className="text-[11px] text-muted-foreground">where</span>
          <DimensionAttributeFilter
            dimensionId={dimension.dimensionId!}
            attributes={dimension.attributes}
            conditions={[...conditions]}
            onChange={onConditions}
            testId="search-builder-where"
          />
        </div>
      )}
    </div>
  );
}

/** The composed search: the query and the request to send, how many clauses it holds, each dimension's part, and what the picks left out. */
function ComposedSearch({ search }: { search: DeliveryDimensionSearch }) {
  const warnings = [
    ...search.missing.map((name) => `No value of the dimension any more: ${name}.`),
    ...search.removed.map((name) => `No build finds this value any more, so it adds nothing: ${name}.`),
    ...search.notes,
  ];
  return (
    <div className="flex flex-col gap-4" data-testid="search-builder-result">
      <div className="flex flex-wrap items-center gap-2">
        <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Query</h3>
        <RichTooltip body={`Each key compared is a clause; one search holds at most ${MAX_CLAUSES}.`}>
          <span className="font-mono text-[11px] tabular-nums text-muted-foreground" data-testid="search-builder-clauses">
            {search.clauses.toLocaleString("en-US")} of {MAX_CLAUSES.toLocaleString("en-US")} clauses
          </span>
        </RichTooltip>
        <span className="ml-auto flex items-center gap-1">
          <CopyButton label="Copy the query" text={search.query} testId="search-builder-copy-query" />
          <CopyButton label="Copy the request" text={search.request} testId="search-builder-copy-request" />
        </span>
      </div>
      <p className="max-h-48 overflow-y-auto whitespace-pre-wrap break-all rounded-md border border-border bg-card p-2 font-mono text-[12px]" data-testid="search-builder-query">
        {search.query}
      </p>

      <section className="flex flex-col gap-1.5">
        <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Finds a record holding</h3>
        <ol className="flex flex-col gap-1 text-[12.5px]" data-testid="search-builder-parts">
          {search.parts.map((part, index) => (
            <li key={part.dimensionId} className="flex min-w-0 flex-wrap items-baseline gap-1.5">
              {index > 0 && <span className="font-mono text-[11px] font-medium text-primary">AND</span>}
              <span className="font-medium">{part.dimension}</span>
              {part.attributes.length > 0
                ? (
                  <span className="text-muted-foreground" data-testid="search-builder-part-where">
                    where {part.attributes.map((a) => `${a.name} is ${a.values.join(" or ")}`).join(" and ")}:
                  </span>
                )
                : <span className="text-muted-foreground">{part.values.length === 1 ? "is" : "is one of"}</span>}
              {part.values.map((value, at) => (
                <span key={value.valueId} className="inline-flex items-baseline">
                  <DimensionValueText value={value.value} maxWidth={200} />
                  {at < part.values.length - 1 && <span className="text-muted-foreground">,</span>}
                </span>
              ))}
              <RichTooltip title={`Compares ${part.aggregateBy}`} body={part.filter} mono>
                <span className="font-mono text-[11px] tabular-nums text-muted-foreground underline decoration-dotted underline-offset-2">
                  {counted(part.keys, "key")}
                </span>
              </RichTooltip>
            </li>
          ))}
        </ol>
      </section>

      {warnings.length > 0 && (
        <ul className="flex flex-col gap-1 text-[12.5px]" data-testid="search-builder-warnings">
          {warnings.map((warning) => (
            <li key={warning} className="flex items-start gap-2">
              <TriangleAlert className="mt-0.5 size-4 shrink-0 text-warning" />
              <span className="break-words">{warning}</span>
            </li>
          ))}
        </ul>
      )}

      <section className="flex flex-col gap-1.5">
        <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">As a search request</h3>
        <CodeView value={search.request} language="json" height={120} data-testid="search-builder-request" />
      </section>
    </div>
  );
}

/**
 * The search builder: pick the kind to search, then values in any of the dimensions that read it; the control plane
 * composes the OSDU search that finds the records holding a picked value of every dimension picked in (any of the values
 * picked within one), each value standing for the exact keys the index holds. The query and the request body are ready to
 * copy; nothing is sent to OSDU from here. The kind and the picks live in the URL, so a link carries the search.
 */
export function DimensionSearchBuilder({ entries, kind, onKind, picks, onPicks, attributes, onAttributes, onBack }: {
  entries: DimensionEntry[];
  /** The kind the link names; null or a kind no built dimension reads picks the kind most dimensions read. */
  kind: string | null;
  onKind: (kind: string) => void;
  picks: SearchPicks;
  onPicks: (picks: SearchPicks) => void;
  attributes: SearchAttributePicks;
  onAttributes: (picks: SearchAttributePicks) => void;
  /** Back to every dimension. */
  onBack: () => void;
}) {
  const [typedWithin, setTypedWithin] = useState("");
  const within = useDebouncedValue(typedWithin.trim(), TYPING_DELAY_MS);
  const [picked, setPicked] = useState<ReadonlyMap<number, string>>(new Map());

  const built = useMemo(() => entries.filter((entry) => entry.dimension.dimensionId !== null && entry.dimension.current !== null), [entries]);
  const kinds = useMemo(() => {
    const counts = new Map<string, number>();
    for (const entry of built) {
      counts.set(entry.dimension.kind, (counts.get(entry.dimension.kind) ?? 0) + 1);
    }

    return [...counts.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0])).map(([name, count]) => ({ name, count }));
  }, [built]);
  const chosen = kind !== null && kinds.some((option) => option.name === kind) ? kind : kinds[0]?.name ?? null;
  const rows = built.filter((entry) => entry.dimension.kind === chosen);
  const inView = (dimensionId: number) => rows.some((row) => row.dimension.dimensionId === dimensionId);
  const own = new Map([...picks].filter(([dimensionId, values]) => values.size > 0 && inView(dimensionId)));
  const where = new Map([...attributes].filter(([dimensionId, conditions]) => conditions.length > 0 && inView(dimensionId)));
  const request = [...new Set([...own.keys(), ...where.keys()])].sort((a, b) => a - b).map((dimensionId) => ({
    dimensionId,
    valueIds: [...(own.get(dimensionId) ?? [])].sort((a, b) => a - b),
    attributes: attributePicks(where.get(dimensionId) ?? []),
  }));

  const composed = useQuery({
    queryKey: ["delivery", "dimensions", "search", chosen, request, within],
    queryFn: () => deliveryApi.dimensionSearch({ picks: request, kind: chosen, within: within === "" ? null : within }),
    enabled: chosen !== null && request.length > 0,
    placeholderData: keepPreviousData,
    retry: false,
  });

  // The names of the values picked: those picked here, and those a link named, as the composed search returns them.
  const names = useMemo(() => {
    const known = new Map(picked);
    for (const part of composed.data?.parts ?? []) {
      for (const value of part.values) {
        known.set(value.valueId, value.value);
      }
    }

    return known;
  }, [picked, composed.data]);

  const change = (dimensionId: number, valueId: number, add: boolean) => {
    const next = new Map([...own].map(([id, values]) => [id, new Set(values)]));
    const values = next.get(dimensionId) ?? new Set<number>();
    if (add) {
      values.add(valueId);
    } else {
      values.delete(valueId);
    }

    next.set(dimensionId, values);
    onPicks(next);
  };

  if (kinds.length === 0) {
    return (
      <Card className="gap-0 rounded-lg p-0">
        <EmptyState
          icon={<Search />}
          title="No dimension is built in this partition yet"
          description="A search is composed from the values of built dimensions. Run a dimension flow's pipeline, then pick values here."
          data-testid="search-builder-nothing-built"
        />
      </Card>
    );
  }

  const pickCount = [...own.values()].reduce((sum, values) => sum + values.size, 0) + [...where.values()].reduce((sum, conditions) => sum + conditions.length, 0);
  return (
    <div className="flex min-w-0 flex-col gap-3" data-testid="search-builder">
      <PageHeader
        title={(
          <span className="flex min-w-0 flex-wrap items-center gap-x-1.5 gap-y-1">
            <DimensionsCrumb onBack={onBack} />
            <span>Build a search</span>
            <RichTooltip
              title="How a search is composed"
              body="A record is found when, for every dimension a value is picked in, it holds one of that dimension's picked values. A value stands for the keys the index holds (the ids, for a reference), so the query compares exactly those, within each dimension's own query."
            >
              <Info className="size-4 text-muted-foreground" aria-label="How a search is composed" />
            </RichTooltip>
          </span>
        )}
        subtitle="Pick values in the dimensions of one kind; copy the OSDU search that finds their records. Nothing is sent to OSDU from here."
        actions={(
          <Select value={chosen ?? undefined} onValueChange={onKind}>
            <SelectTrigger size="sm" className="w-full max-w-[26rem] sm:w-auto" aria-label="Kind to search" data-testid="search-builder-kind">
              <SelectValue placeholder="A kind" />
            </SelectTrigger>
            <SelectContent>
              {kinds.map((option) => (
                <SelectItem key={option.name} value={option.name}>
                  <span className="flex min-w-0 items-center gap-2">
                    <KindText kind={option.name} />
                    <span className="font-mono text-[11px] text-muted-foreground">{counted(option.count, "dimension")}</span>
                  </span>
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
      />

      <div className="grid items-start gap-4 xl:grid-cols-[minmax(0,26rem)_minmax(0,1fr)]">
        <Card className="gap-0 overflow-hidden rounded-lg p-0">
          <div className="flex h-9 items-center gap-2 border-b border-border px-3 text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
            <span>Dimensions</span>
            {pickCount > 0 && (
              <Button
                variant="ghost"
                size="xs"
                className="ml-auto normal-case tracking-normal"
                onClick={() => { onPicks(new Map()); onAttributes(new Map()); }}
                data-testid="search-builder-clear"
              >
                <X />
                Clear {counted(pickCount, "pick")}
              </Button>
            )}
          </div>
          {rows.map((entry) => (
            <DimensionPicks
              key={entry.ref}
              entry={entry}
              picked={own.get(entry.dimension.dimensionId!) ?? new Set()}
              names={names}
              onPick={(valueId, value) => {
                setPicked((current) => new Map(current).set(valueId, value));
                change(entry.dimension.dimensionId!, valueId, !(own.get(entry.dimension.dimensionId!)?.has(valueId) ?? false));
              }}
              onUnpick={(valueId) => change(entry.dimension.dimensionId!, valueId, false)}
              conditions={where.get(entry.dimension.dimensionId!) ?? []}
              onConditions={(next) => onAttributes(new Map([...where, [entry.dimension.dimensionId!, next]]))}
            />
          ))}
          <div className="flex flex-col gap-1 border-t border-border px-3 py-2.5">
            <label htmlFor="search-builder-within" className="text-[12px] text-muted-foreground">Narrowed further by a query of your own</label>
            <Input
              id="search-builder-within"
              value={typedWithin}
              onChange={(event) => setTypedWithin(event.target.value)}
              placeholder="createTime:[2024-01-01 TO *]"
              className="h-8 font-mono text-[12px]"
              data-testid="search-builder-within"
            />
          </div>
        </Card>

        <Card className="min-w-0 gap-0 rounded-lg p-4">
          {request.length === 0
            ? (
              <p className="flex items-center gap-2 text-[13px] text-muted-foreground" data-testid="search-builder-empty">
                <Search className="size-4 shrink-0" />
                Pick a value in any dimension, or an attribute its keys hold, to compose its search.
              </p>
            )
            : composed.isError
              ? <ProblemView error={composed.error} testId="search-builder-error" />
              : composed.data === undefined
                ? <Skeleton className="h-48 w-full" />
                : <ComposedSearch search={composed.data} />}
        </Card>
      </div>
    </div>
  );
}
