import { useMemo, useState, type FormEvent, type KeyboardEvent } from "react";
import { ArrowLeft, Check, Loader2, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { cn } from "@/lib/utils";
import {
  explorerApi, type ExplorerCondition, type ExplorerFieldInfo, type ExplorerFilter, type ExplorerPage, type ExplorerSearchRequest,
} from "../../../api/explorer";
import { isRecordReference } from "../osduDocument";
import { RecordName } from "../RecordName";
import { ExplorerAttributeList } from "./ExplorerAttributeList";
import { ExplorerErrorText } from "./ExplorerProblem";
import { nestedLabel, useExplorerFields } from "./explorerFields";
import { CONDITION_HINTS, CONDITION_LABELS, conditionsFor, fieldLabel, filterProblem, kindParts, useExplorerRead } from "./explorerModel";
import { offeredTerm, offeredTerms, termsEntityType, termTitle, termValueIndex, useSearchTerms, type OfferedTerm } from "./explorerTerms";

/** How long typing rests before the values held are asked for again. */
const TYPING_DELAY_MS = 300;

/** The values listed under the field at most. */
const SUGGESTED = 40;

/** Whether a condition compares values the records hold, which the values held under the field help pick. */
function suggests(condition: ExplorerCondition): boolean {
  return condition !== "range" && condition !== "exists" && condition !== "missing";
}

/** The field a value of `index` is typed in. */
function inputType(index: ExplorerFieldInfo["index"], condition: ExplorerCondition): string {
  if (index === "number") {
    return "number";
  }

  return index === "date" && condition === "range" ? "date" : "text";
}

/**
 * A condition on one property of the records in view, made or changed: the property (picked from the record's own, the
 * content its schema declares, and what the records hold beyond it), what it must hold (the conditions its index allows,
 * each said in a line), and the value, with the values the records in view hold there and how many hold each, narrowed as
 * the value is typed. A value listed is picked with a click; for `is one of` each click adds or drops one. Enter in the
 * value applies the condition.
 *
 * For a type in view, a column of a source system its delivery flows read (a search term, osdu/docs/search-terms.md) is
 * picked the same way, listed before the properties: its values are typed as the source holds them, the conditions are
 * those its route allows, and the values listed are those of the property its route fills or, for a route through other
 * records, of the property those records are found by. The control plane carries the values through the mapping.
 */
export function ExplorerFilterEditor({ partition, base, initial, startValue = "", applyLabel, onApply, onCancel }: {
  partition: string | null;
  /** The search in view without this condition: what the values held are counted within. */
  base: ExplorerSearchRequest;
  /** The condition changed; null to start a new one by picking the property. */
  initial: ExplorerFilter | null;
  /** The value a new condition starts with: the text typed in the search box, to be searched in a property picked here. */
  startValue?: string;
  applyLabel: string;
  onApply: (filter: ExplorerFilter) => void;
  onCancel: () => void;
}) {
  const fields = useExplorerFields(partition, base.kind, true);
  const known = fields.data?.answer.fields;
  // A place of one type offers its terms; one of many offers none, though a condition on a term carried there is named.
  const termsRead = useSearchTerms(base.kind, true, initial?.term !== undefined);
  const oneType = termsEntityType(base.kind) !== null;
  const terms = useMemo(() => (oneType ? offeredTerms(termsRead.data?.terms) : []), [oneType, termsRead.data]);
  const [field, setField] = useState<ExplorerFieldInfo | null>(() => (initial === null || initial.term !== undefined ? null : { path: initial.path, index: initial.index, nested: initial.nested ?? null }));
  // The search term the condition names instead of a property, by its id: the one changed, or one picked here.
  const [termId, setTermId] = useState<string | null>(initial?.term ?? null);
  const [chosen, setCondition] = useState<ExplorerCondition>(initial?.condition ?? "is");
  const [value, setValue] = useState(initial?.value ?? startValue);
  const [values, setValues] = useState<string[]>(initial?.values ?? []);
  const [to, setTo] = useState(initial?.to ?? "");
  // The term named, once the terms are read; one left out or no longer searchable since is not, and is picked again.
  const named = termId === null ? undefined : termsRead.data?.terms.find((candidate) => candidate.id === termId);
  const term: OfferedTerm | null = named === undefined ? null : offeredTerm(named);
  const termGone = termId !== null && term === null && (termsRead.isError || termsRead.data !== undefined || !termsRead.isFetching);
  // What the condition compares: the property picked, or the one the term's route fills.
  const target = termId === null ? field : term?.field ?? null;
  // A term's route takes the conditions it allows; one a link carries from before the route changed is its likeliest.
  const condition = term !== null && !term.route.conditions.includes(chosen) ? term.route.conditions[0] : chosen;
  // What the catalog says of the property picked: its title, description and where it was found.
  const described = field === null ? undefined : known?.find((candidate) => candidate.path === field.path);
  const many = condition === "anyOf" || condition === "noneOf";

  // A property picked starts with the condition it is likeliest asked: text by its words, a number or a date by a range.
  const pick = (picked: ExplorerFieldInfo) => {
    setTermId(null);
    setField(picked);
    setCondition(conditionsFor(picked)[0]);
  };

  // A term picked starts with the condition its route is likeliest asked.
  const pickTerm = (picked: OfferedTerm) => {
    setField(null);
    setTermId(picked.term.id);
    setCondition(picked.route.conditions[0]);
  };

  const drafted: ExplorerFilter | null = target === null ? null : {
    path: target.path,
    index: target.index,
    ...(target.nested ? { nested: target.nested } : {}),
    ...(condition !== "is" ? { condition } : {}),
    ...(many ? { values } : {}),
    ...(!many && condition !== "exists" && condition !== "missing" && value !== "" ? { value } : {}),
    ...(condition === "range" && to !== "" ? { to } : {}),
    ...(termId !== null ? { term: termId } : {}),
  };
  const problem = drafted === null ? null : filterProblem(drafted);

  const apply = (event?: FormEvent) => {
    event?.preventDefault();
    if (drafted !== null && problem === null) {
      onApply(drafted);
    }
  };

  if (termId !== null && term === null && !termGone) {
    return (
      <div className="flex items-center gap-2 px-3 py-3 text-[12px] text-muted-foreground" data-testid="explorer-filter-editor">
        <Loader2 className="size-3.5 animate-spin" />
        Reading the source columns
      </div>
    );
  }

  if (target === null) {
    return (
      <div className="flex flex-col" data-testid="explorer-filter-editor">
        {termGone && (
          <p className="border-b px-3 py-2 text-[12px] text-warning" data-testid="explorer-filter-term-gone">
            {termsRead.isError
              ? "The source columns could not be read. Pick a property to search."
              : "The source column this condition named is no longer searched. Pick a property or another column."}
          </p>
        )}
        <ExplorerAttributeList
          read={fields}
          kind={base.kind}
          onPick={pick}
          terms={terms}
          onPickTerm={pickTerm}
          placeholder={terms.length > 0 ? "Find the property or source column to search" : "Find the property to search"}
          testId="explorer-filter-attributes"
        />
      </div>
    );
  }

  const allowed = term === null ? conditionsFor(target) : term.route.conditions;
  const back = () => {
    setField(null);
    setTermId(null);
  };
  return (
    <form className="flex flex-col" onSubmit={apply} data-testid="explorer-filter-editor">
      {term === null ? (
        <div className="flex items-center gap-2 border-b px-2 py-1.5">
          <Button type="button" variant="ghost" size="icon" className="size-7" onClick={back} aria-label="Pick another property" data-testid="explorer-filter-back">
            <ArrowLeft />
          </Button>
          <span
            className="min-w-0 flex-1 truncate font-mono text-[12px]"
            title={[target.path, described?.title, described?.description].filter(Boolean).join("\n")}
            data-testid="explorer-filter-field"
          >
            {fieldLabel(target.path)}
          </span>
          {nestedLabel(target.nested) !== null && <span className="shrink-0 rounded-sm border px-1 text-[10px] text-muted-foreground">in {nestedLabel(target.nested)}</span>}
          <span className="shrink-0 text-[11px] text-muted-foreground">{target.index}</span>
        </div>
      ) : (
        <div className="flex items-center gap-2 border-b px-2 py-1.5">
          <Button type="button" variant="ghost" size="icon" className="size-7" onClick={back} aria-label="Pick another property or source column" data-testid="explorer-filter-back">
            <ArrowLeft />
          </Button>
          <span className="min-w-0 flex-1 truncate text-[13px]" title={termTitle(term)} data-testid="explorer-filter-term">
            {term.term.name}
          </span>
          <span className="max-w-[45%] shrink-0 truncate text-[11px] text-muted-foreground" title={`Searched in ${term.route.path}, ${term.route.how}`}>
            {`${term.term.system} ${term.term.columnLabel} in `}
            <span className="font-mono">{fieldLabel(term.route.path)}</span>
          </span>
        </div>
      )}
      <div className="flex flex-col gap-2 p-2.5">
        <Select value={condition} onValueChange={(next) => setCondition(next as ExplorerCondition)}>
          <SelectTrigger size="sm" className="h-8 w-full text-[13px]" data-testid="explorer-filter-condition">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {allowed.map((option) => (
              <SelectItem key={option} value={option} className="text-[13px]" data-testid="explorer-filter-condition-option">
                {CONDITION_LABELS[option]}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <p className="text-[11px] leading-4 text-muted-foreground" data-testid="explorer-filter-hint">
          {CONDITION_HINTS[condition]}
          {term !== null && condition !== "exists" && condition !== "missing" && " Typed as the source column holds it; the mapping turns it into what OSDU holds."}
        </p>
        <ValueFields
          field={term === null ? target : { ...target, index: termValueIndex(term.route) }}
          condition={condition}
          value={value}
          values={values}
          to={to}
          onValue={setValue}
          onValues={setValues}
          onTo={setTo}
        />
      </div>
      {suggests(condition) && (term === null || term.term.suggest !== null) && (
        <HeldValues
          partition={partition}
          {...heldFor(base, target, term)}
          typed={many ? "" : value}
          picked={many ? values : value === "" ? [] : [value]}
          onPick={(picked) => {
            if (many) {
              setValues((was) => (was.includes(picked) ? was.filter((other) => other !== picked) : [...was, picked]));
            } else {
              setValue(picked);
            }
          }}
        />
      )}
      <div className="flex items-center gap-2 border-t px-2.5 py-2">
        <span className="min-w-0 flex-1 truncate text-[11px] text-muted-foreground" data-testid="explorer-filter-problem">{problem ?? ""}</span>
        <Button type="button" variant="ghost" size="sm" className="h-7 px-2 text-[12px]" onClick={onCancel}>Cancel</Button>
        <Button type="submit" size="sm" className="h-7 px-2.5 text-[12px]" disabled={problem !== null} data-testid="explorer-filter-apply">
          {applyLabel}
        </Button>
      </div>
    </form>
  );
}

/**
 * Where the values to pick from are read: the property of the records in view, within the search without this condition;
 * for a term, the property its suggestions name, and for a route through other records, every record of their kind.
 */
function heldFor(base: ExplorerSearchRequest, target: ExplorerFieldInfo, term: OfferedTerm | null): { base: ExplorerSearchRequest; field: ExplorerFieldInfo; heading?: string } {
  const suggest = term?.term.suggest;
  if (suggest === undefined || suggest === null) {
    return { base, field: target };
  }

  const field = { path: suggest.path, index: suggest.index, nested: suggest.nested };
  return suggest.kind === null
    ? { base, field }
    : { base: { kind: suggest.kind, filters: [] }, field, heading: `Values of ${kindParts(suggest.kind).type} ${fieldLabel(suggest.path)}` };
}

/** The value, the values or the bounds a condition compares, typed in fields fit for the property's index. */
function ValueFields({ field, condition, value, values, to, onValue, onValues, onTo }: {
  field: ExplorerFieldInfo;
  condition: ExplorerCondition;
  value: string;
  values: string[];
  to: string;
  onValue: (value: string) => void;
  onValues: (values: string[]) => void;
  onTo: (to: string) => void;
}) {
  const [adding, setAdding] = useState("");
  const type = inputType(field.index, condition);

  if (condition === "exists" || condition === "missing") {
    return null;
  }

  if (field.index === "boolean") {
    return (
      <div className="flex gap-1.5" role="radiogroup" aria-label="The value">
        {["true", "false"].map((option) => (
          <Button
            key={option}
            type="button"
            variant={value === option ? "secondary" : "outline"}
            size="sm"
            className="h-7 flex-1 text-[12px]"
            aria-checked={value === option}
            role="radio"
            onClick={() => onValue(option)}
            data-testid="explorer-filter-boolean"
          >
            {option}
          </Button>
        ))}
      </div>
    );
  }

  if (condition === "range") {
    return (
      <div className="grid grid-cols-2 gap-1.5">
        <label className="flex flex-col gap-1 text-[11px] text-muted-foreground">
          From (included)
          <Input type={type} value={value} onChange={(event) => onValue(event.target.value)} className="h-8 text-[13px]" autoFocus data-testid="explorer-filter-from" />
        </label>
        <label className="flex flex-col gap-1 text-[11px] text-muted-foreground">
          Up to (not included)
          <Input type={type} value={to} onChange={(event) => onTo(event.target.value)} className="h-8 text-[13px]" data-testid="explorer-filter-to" />
        </label>
      </div>
    );
  }

  if (condition === "anyOf" || condition === "noneOf") {
    const add = (event: KeyboardEvent<HTMLInputElement>) => {
      if (event.key === "Enter" && adding.trim() !== "") {
        // Enter adds the value typed; with nothing typed it applies the condition, as the form does.
        event.preventDefault();
        if (!values.includes(adding.trim())) {
          onValues([...values, adding.trim()]);
        }

        setAdding("");
      }
    };

    return (
      <div className="flex flex-col gap-1.5">
        {values.length > 0 && (
          <div className="flex flex-wrap gap-1" data-testid="explorer-filter-values">
            {values.map((picked) => (
              <span key={picked} className="inline-flex max-w-full items-center gap-1 rounded-md border bg-muted/60 py-0.5 pr-1 pl-1.5 text-[12px]">
                <span className="min-w-0 truncate" title={picked}>{picked}</span>
                <button type="button" className="rounded-sm p-0.5 text-muted-foreground hover:bg-accent hover:text-foreground" onClick={() => onValues(values.filter((other) => other !== picked))} aria-label={`Drop ${picked}`}>
                  <X className="size-3" />
                </button>
              </span>
            ))}
          </div>
        )}
        <Input
          type={type}
          value={adding}
          onChange={(event) => setAdding(event.target.value)}
          onKeyDown={add}
          placeholder="Type a value and press Enter, or pick values below"
          className="h-8 text-[13px]"
          autoFocus
          data-testid="explorer-filter-add-value"
        />
      </div>
    );
  }

  return (
    <Input
      type={type}
      value={value}
      onChange={(event) => onValue(event.target.value)}
      placeholder={condition === "contains" ? "Words, such as NO 34/10" : condition === "startsWith" ? "The start, in the same case" : "The whole value"}
      className="h-8 text-[13px]"
      autoFocus
      data-testid="explorer-filter-value"
    />
  );
}

/**
 * The values the records in view hold at the property, the commonest first, each with how many records hold it: one grouping
 * of the search in view, and as a value is typed, one more of the values that start with it (exact case, as the index keeps
 * the whole value), with the values listed before that hold the text anywhere, in any case.
 */
function HeldValues({ partition, base, field, heading = "Values held here", typed, picked, onPick }: {
  partition: string | null;
  base: ExplorerSearchRequest;
  field: ExplorerFieldInfo;
  /** What the list is called: the values of the records in view, or of the records a term's route finds. */
  heading?: string;
  typed: string;
  picked: string[];
  onPick: (value: string) => void;
}) {
  const settled = useDebouncedValue(typed.trim(), TYPING_DELAY_MS);
  const facet = { path: field.path, index: field.index, ...(field.nested ? { nested: field.nested } : {}) };
  const grouped = { ...base, offset: 0, limit: 1, columns: undefined, facet };
  const all = useExplorerRead<ExplorerPage>(["held", partition, grouped], () => explorerApi.search(partition, grouped));
  // The start typed is asked of the index where it keeps the whole value: text and keywords outside a nested list.
  const narrowable = settled !== "" && (field.index === "text" || field.index === "keyword") && !field.nested;
  const narrowed = { ...grouped, filters: [...(base.filters ?? []), { path: field.path, index: field.index, condition: "startsWith" as const, value: settled }] };
  const starting = useExplorerRead<ExplorerPage>(["held", partition, narrowed], narrowable ? () => explorerApi.search(partition, narrowed) : null);

  const listed = useMemo(() => {
    const wanted = settled.toLowerCase();
    const byValue = new Map<string, number>();
    for (const bucket of [...(starting.data?.answer.facet ?? []), ...(all.data?.answer.facet ?? [])]) {
      if (bucket.value != null && (wanted === "" || bucket.value.toLowerCase().includes(wanted)) && !byValue.has(bucket.value)) {
        byValue.set(bucket.value, bucket.count);
      }
    }

    return [...byValue.entries()].sort(([a, x], [b, y]) => y - x || a.localeCompare(b)).slice(0, SUGGESTED);
  }, [all.data, starting.data, settled]);

  const refusal = all.data?.answer.refusal;
  const reading = all.isPending || (narrowable && starting.isFetching);
  return (
    <div className="flex max-h-56 min-h-0 flex-col border-t">
      <div className="flex items-center gap-1.5 px-2.5 pt-1.5 pb-1 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
        <span className="min-w-0 truncate" title={heading}>{heading}</span>
        {reading && <Loader2 className="size-3 animate-spin" aria-label="Counting the values" />}
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto px-1 pb-1" data-testid="explorer-filter-held">
        {all.isError && <ExplorerErrorText error={all.error} className="px-1.5 py-1" />}
        {refusal && <p className="px-1.5 py-1 text-[12px] text-destructive">{`The search service would not group by this property: ${refusal}`}</p>}
        {!reading && !all.isError && !refusal && listed.length === 0 && (
          <p className="px-1.5 py-1 text-[12px] text-muted-foreground">{settled === "" ? "None of these records holds a value of it." : `No value held here matches "${settled}".`}</p>
        )}
        {listed.map(([held, count]) => {
          const on = picked.includes(held);
          return (
            <button
              key={held}
              type="button"
              onClick={() => onPick(held)}
              className={cn("flex h-7 w-full min-w-0 items-center gap-2 rounded-md px-1.5 text-left text-[12px] hover:bg-accent/60", on && "bg-accent/40")}
              title={held}
              aria-pressed={on}
              data-testid="explorer-filter-held-value"
            >
              <Check className={cn("size-3.5 shrink-0", on ? "text-primary" : "invisible")} aria-hidden />
              <span className="min-w-0 flex-1 truncate">{isRecordReference(held) ? <RecordName id={held} /> : held}</span>
              <span className="shrink-0 font-mono text-[11px] tabular-nums text-muted-foreground">{count.toLocaleString("en-US")}</span>
            </button>
          );
        })}
      </div>
    </div>
  );
}
