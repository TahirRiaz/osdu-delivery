import { useId, useState, type ReactNode } from "react";
import { ChevronDown, Plus, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import type { DimensionCompose, DimensionDraft, DimensionDraftAttribute, DimensionDraftIssue } from "../../../../api/explorer";
import { ROLE_GLYPHS } from "../../dimensions/blueprintModel";
import { PLAIN_STEPS, lastProperty } from "./dimensionDraft";

/** What each clean step does, in a few words, as the documentation says it. */
const STEP_HELP: Record<string, string> = {
  trim: "white space at both ends removed",
  collapseSpaces: "every run of white space one space",
  upper: "upper case",
  lower: "lower case",
  nfc: "Unicode composed form",
  nfkc: "Unicode compatibility form",
  foldSeparators: "letters and digits kept, every run of anything else one hyphen, lower case",
};

/**
 * The table the dimension writes, as it is being built: the key's column and the value's, then a column per attribute, each
 * with what it is read from and what the example key holds there, and a name a person can change; under it, how a key's
 * value is cleaned, the value of what is not read, and the less common settings.
 */
export function BuilderColumns({ draft, compose, issues, recordKind, onChange, onPoint }: {
  draft: DimensionDraft;
  compose: DimensionCompose | undefined;
  /** The exact kind of the record shown, which reading one version comes back to. */
  recordKind: string | null;
  issues: DimensionDraftIssue[];
  onChange: (next: DimensionDraft) => void;
  onPoint: (target: string | null) => void;
}) {
  const example = compose?.example ?? null;
  const dimension = compose?.dimension ?? null;
  const problemOf = (target: string) => issues.find((issue) => issue.target === target && issue.severity === "error")?.message ?? null;
  const setAttribute = (index: number, next: DimensionDraftAttribute | null) => onChange({
    ...draft,
    attributes: next === null ? draft.attributes.filter((_, i) => i !== index) : draft.attributes.map((a, i) => (i === index ? next : a)),
  });
  const valueOf = (name: string) => (example?.attributes ?? []).filter((a) => a.name === name);

  if (draft.path === null || draft.path === "") {
    return (
      <p className="px-4 py-3 text-[12.5px] text-muted-foreground" data-testid="builder-columns-empty">
        The table takes shape as the key is picked: a column for the key, one for its value, and one per attribute.
      </p>
    );
  }

  return (
    <div className="flex min-h-0 flex-col gap-3 overflow-auto px-3 py-2" data-testid="builder-columns">
      <div className="flex items-baseline gap-2">
        <h3 className="text-[13px] font-semibold">The table</h3>
        <span className="font-mono text-[12px] text-muted-foreground" data-testid="builder-table-name">{compose?.table ?? ""}</span>
      </div>
      <table className="w-full table-fixed border-collapse text-[12.5px]">
        <colgroup>
          <col className="w-[36%]" />
          <col className="w-[34%]" />
          <col />
          <col className="w-8" />
        </colgroup>
        <thead>
          <tr className="text-left text-[11.5px] text-muted-foreground">
            <th className="pb-1 font-normal">Column</th>
            <th className="pb-1 font-normal">Read from</th>
            <th className="pb-1 font-normal">Example</th>
            <th />
          </tr>
        </thead>
        <tbody>
          <ColumnRow
            glyph="key"
            name={draft.keyColumn ?? ""}
            placeholder={dimension?.keyColumn ?? lastProperty(draft.path)}
            onName={(name) => onChange({ ...draft, keyColumn: name === "" ? null : name })}
            problem={problemOf("columns.key")}
            from={<PathText path={draft.path} />}
            example={example?.key ?? null}
            target="path"
            onPoint={onPoint}
            testId="builder-column-key"
          />
          <ColumnRow
            glyph="label"
            name={draft.valueColumn ?? ""}
            placeholder={dimension?.valueColumn ?? (draft.label.length > 0 ? lastProperty(draft.label[draft.label.length - 1]) : draft.name || "value")}
            onName={(name) => onChange({ ...draft, valueColumn: name === "" ? null : name })}
            problem={problemOf("columns.value")}
            from={draft.label.length > 0 ? <Chain steps={draft.label} /> : <span className="text-muted-foreground">the key itself, cleaned</span>}
            example={example === null ? null : example.value ?? `left out: ${example.leftOut ?? "no value"}`}
            target="label"
            onPoint={onPoint}
            onRemove={draft.label.length > 0 ? () => onChange({ ...draft, label: [] }) : undefined}
            removeLabel="Read no value: the key is its own value"
            testId="builder-column-value"
          />
          {draft.attributes.map((attribute, index) => {
            const held = valueOf(attribute.name);
            return (
              <ColumnRow
                key={index}
                glyph={attribute.collect !== null ? "collect" : "attribute"}
                name={attribute.name}
                onName={(name) => setAttribute(index, { ...attribute, name })}
                problem={problemOf(`attributes.${attribute.name}`)}
                from={attribute.collect !== null
                  ? <span><span className="text-muted-foreground">collected from </span><PathText path={attribute.collect} /></span>
                  : <Chain steps={attribute.steps ?? []} />}
                example={held.length === 0 ? null : held.map((a) => (a.records === null || attribute.collect === null ? a.value : `${a.value} (${a.records.toLocaleString("en-US")})`)).join(", ")}
                target={`attributes.${attribute.name}`}
                onPoint={onPoint}
                onRemove={() => setAttribute(index, null)}
                removeLabel={`Remove ${attribute.name}`}
                testId="builder-column-attribute"
              />
            );
          })}
        </tbody>
      </table>
      <p className="text-[11.5px] text-muted-foreground">
        Every dimension's table also holds <span className="font-mono">id</span>, <span className="font-mono">partition</span>, <span className="font-mono">key_id</span>,{" "}
        <span className="font-mono">records</span>{example?.records !== null && example?.records !== undefined ? ` (${example.records.toLocaleString("en-US")} for the example)` : ""} and{" "}
        <span className="font-mono">filter</span>.
      </p>

      <ValueSettings draft={draft} onChange={onChange} problem={problemOf("unlabelled") ?? problemOf("clean")} />
      <MoreSettings draft={draft} recordKind={recordKind} onChange={onChange} problemOf={problemOf} />
    </div>
  );
}

function ColumnRow({ glyph, name, placeholder, onName, problem, from, example, target, onPoint, onRemove, removeLabel, testId }: {
  glyph: "key" | "label" | "attribute" | "collect";
  name: string;
  placeholder?: string;
  onName: (name: string) => void;
  problem: string | null;
  from: ReactNode;
  example: string | null;
  target: string;
  onPoint: (target: string | null) => void;
  onRemove?: () => void;
  removeLabel?: string;
  testId: string;
}) {
  const Icon = ROLE_GLYPHS[glyph].icon;
  return (
    <tr className="border-t border-border/60 align-top" onMouseEnter={() => onPoint(target)} onMouseLeave={() => onPoint(null)} data-testid={testId}>
      <td className="py-1.5 pr-2">
        <div className="flex items-center gap-1.5">
          <RichTooltip body={ROLE_GLYPHS[glyph].label}>
            <Icon className="size-3.5 shrink-0 text-muted-foreground" aria-label={ROLE_GLYPHS[glyph].label} />
          </RichTooltip>
          <Input
            value={name}
            placeholder={placeholder}
            onChange={(event) => onName(event.target.value)}
            className={cn("h-7 font-mono text-[12px]", problem !== null && "border-destructive")}
            aria-invalid={problem !== null}
            aria-label="Column name"
            data-testid={`${testId}-name`}
          />
        </div>
        {problem !== null && <p className="mt-1 text-[11.5px] leading-4 text-destructive" data-testid={`${testId}-problem`}>{problem}</p>}
      </td>
      <td className="min-w-0 py-1.5 pr-2">{from}</td>
      <td className="min-w-0 py-1.5 pr-1">
        {example === null
          ? <span className="text-muted-foreground/70">-</span>
          : (
            <RichTooltip body={example} mono>
              <span className="block truncate font-mono text-[12px]" data-testid={`${testId}-example`}>{example}</span>
            </RichTooltip>
          )}
      </td>
      <td className="py-1">
        {onRemove !== undefined && (
          <Button variant="ghost" size="icon-xs" onClick={onRemove} aria-label={removeLabel} data-testid={`${testId}-remove`}>
            <Trash2 />
          </Button>
        )}
      </td>
    </tr>
  );
}

/** A path as a page shows it: its filters quieter than the properties it reads. */
function PathText({ path }: { path: string }) {
  return (
    <RichTooltip body={path} mono>
      <span className="block truncate font-mono text-[12px]">
        {path.split(/(\[[^\]]*\])/).map((part, index) => (
          <span key={index} className={part.startsWith("[") ? "text-muted-foreground" : undefined}>{part}</span>
        ))}
      </span>
    </RichTooltip>
  );
}

/** The records a value is read through, step by step: each record named by a property, the last property read. */
function Chain({ steps }: { steps: string[] }) {
  if (steps.length === 1) {
    return <PathText path={steps[0]} />;
  }

  return (
    <RichTooltip title="Read through" body={steps.join("\n")} mono>
      <span className="block truncate text-[12px]">
        {steps.map((step, index) => (
          <span key={index}>
            {index > 0 && <span className="px-1 text-muted-foreground">&gt;</span>}
            <span className={cn("font-mono", step.includes("[") && "underline decoration-dotted underline-offset-2")}>{lastProperty(step)}</span>
          </span>
        ))}
      </span>
    </RichTooltip>
  );
}

/** How a key's value is cleaned, and what a key holds where nothing is read. */
function ValueSettings({ draft, onChange, problem }: { draft: DimensionDraft; onChange: (next: DimensionDraft) => void; problem: string | null }) {
  const ids = useId();
  const plain = new Set(draft.clean.filter((c) => c.step !== "replace").map((c) => c.step));
  const toggle = (step: string) => onChange({
    ...draft,
    clean: plain.has(step) ? draft.clean.filter((c) => c.step !== step) : [...draft.clean, { step, pattern: null, with: null }],
  });
  const replaces = draft.clean.map((c, index) => ({ c, index })).filter(({ c }) => c.step === "replace");
  const reads = draft.label.length > 0 || draft.attributes.some((a) => a.collect === null);

  return (
    <section className="flex flex-col gap-2 border-t border-border/60 pt-2" data-testid="builder-value-settings">
      <div className="flex flex-col gap-1">
        <Label className="text-[12px]">Clean each value</Label>
        <div className="flex flex-wrap gap-1">
          {PLAIN_STEPS.map((step) => (
            <RichTooltip key={step} body={STEP_HELP[step]}>
              <button
                type="button"
                onClick={() => toggle(step)}
                className={cn(
                  "rounded border px-1.5 py-0.5 font-mono text-[11.5px]",
                  plain.has(step) ? "border-primary/60 bg-primary/10 text-foreground" : "border-border text-muted-foreground hover:text-foreground",
                )}
                aria-pressed={plain.has(step)}
                data-testid={`builder-clean-${step}`}
              >
                {step}
              </button>
            </RichTooltip>
          ))}
          <button
            type="button"
            onClick={() => onChange({ ...draft, clean: [...draft.clean, { step: "replace", pattern: "", with: "" }] })}
            className="inline-flex items-center gap-1 rounded border border-dashed border-border px-1.5 py-0.5 text-[11.5px] text-muted-foreground hover:text-foreground"
            data-testid="builder-clean-replace"
          >
            <Plus className="size-3" aria-hidden />
            replace
          </button>
        </div>
        {replaces.map(({ c, index }) => (
          <div key={index} className="flex items-center gap-1.5" data-testid="builder-clean-replace-row">
            <Input
              value={c.pattern ?? ""}
              placeholder="pattern (a regular expression)"
              onChange={(event) => onChange({ ...draft, clean: draft.clean.map((s, i) => (i === index ? { ...s, pattern: event.target.value } : s)) })}
              className="h-7 font-mono text-[12px]"
              aria-label="Pattern"
            />
            <Input
              value={c.with ?? ""}
              placeholder="replaced by"
              onChange={(event) => onChange({ ...draft, clean: draft.clean.map((s, i) => (i === index ? { ...s, with: event.target.value } : s)) })}
              className="h-7 font-mono text-[12px]"
              aria-label="Replacement"
            />
            <Button variant="ghost" size="icon-xs" aria-label="Remove the replacement" onClick={() => onChange({ ...draft, clean: draft.clean.filter((_, i) => i !== index) })}>
              <Trash2 />
            </Button>
          </div>
        ))}
        {draft.clean.length > 1 && <p className="text-[11.5px] text-muted-foreground">Applied in the order picked: {draft.clean.map((c) => c.step).join(", then ")}.</p>}
      </div>
      {reads && (
        <div className="flex flex-col gap-1">
          <Label htmlFor={`${ids}-unlabelled`} className="text-[12px]">Where nothing is read</Label>
          <Input
            id={`${ids}-unlabelled`}
            value={draft.unlabelled ?? ""}
            placeholder="the code the key's id ends with"
            onChange={(event) => onChange({ ...draft, unlabelled: event.target.value === "" ? null : event.target.value })}
            className="h-7 max-w-72 text-[12px]"
            data-testid="builder-unlabelled"
          />
          <p className="text-[11.5px] text-muted-foreground">The value, and every attribute, of a key whose record holds nothing there; a drop-down lists such keys under it.</p>
        </div>
      )}
      {problem !== null && <p className="text-[11.5px] text-destructive">{problem}</p>}
    </section>
  );
}

/** The settings most dimensions leave as they are: the description, the versions read, the query, counting and the most keys. */
function MoreSettings({ draft, recordKind, onChange, problemOf }: {
  draft: DimensionDraft;
  recordKind: string | null;
  onChange: (next: DimensionDraft) => void;
  problemOf: (target: string) => string | null;
}) {
  const ids = useId();
  const [open, setOpen] = useState(false);
  const segments = draft.kind.split(":");
  const everyVersion = segments.length === 4 && segments[3] === "*";
  return (
    <Collapsible open={open} onOpenChange={setOpen} className="border-t border-border/60 pt-2">
      <CollapsibleTrigger className="flex items-center gap-1 text-[12px] font-medium" data-testid="builder-more">
        <ChevronDown className={cn("size-3.5 transition-transform", !open && "-rotate-90")} aria-hidden />
        More settings
      </CollapsibleTrigger>
      <CollapsibleContent className="flex flex-col gap-3 pt-2">
        <div className="flex flex-col gap-1">
          <Label htmlFor={`${ids}-description`} className="text-[12px]">Description</Label>
          <Textarea
            id={`${ids}-description`}
            value={draft.description ?? ""}
            onChange={(event) => onChange({ ...draft, description: event.target.value === "" ? null : event.target.value })}
            className="field-sizing-fixed h-16 resize-y text-[12px]"
            placeholder="What the dimension is for."
            data-testid="builder-description"
          />
        </div>
        {segments.length === 4 && (
          <div className="flex items-center gap-2">
            <Switch
              id={`${ids}-versions`}
              checked={everyVersion}
              onCheckedChange={(checked) => {
                if (checked) {
                  onChange({ ...draft, kind: [...segments.slice(0, 3), "*"].join(":") });
                } else if (recordKind !== null) {
                  onChange({ ...draft, kind: recordKind });
                }
              }}
              disabled={everyVersion && recordKind === null}
              data-testid="builder-every-version"
            />
            <Label htmlFor={`${ids}-versions`} className="text-[12px]">
              Read every version of the kind{everyVersion ? "" : `, not ${segments[3]} alone`}
            </Label>
          </div>
        )}
        <div className="flex flex-col gap-1">
          <Label htmlFor={`${ids}-query`} className="text-[12px]">Narrow the records read</Label>
          <Input
            id={`${ids}-query`}
            value={draft.query ?? ""}
            onChange={(event) => onChange({ ...draft, query: event.target.value === "" ? null : event.target.value })}
            className="h-7 font-mono text-[12px]"
            placeholder="a query, such as createTime:[2024-01-01 TO *]; {partition} is the partition built in"
            data-testid="builder-query"
          />
          {problemOf("query") !== null && <p className="text-[11.5px] text-destructive">{problemOf("query")}</p>}
        </div>
        <div className="flex items-center gap-2">
          <Switch id={`${ids}-count`} checked={draft.countRecords} onCheckedChange={(countRecords) => onChange({ ...draft, countRecords })} data-testid="builder-count" />
          <Label htmlFor={`${ids}-count`} className="text-[12px]">Count each value's records exactly, where a record holds several</Label>
        </div>
        <div className="flex flex-col gap-1">
          <Label htmlFor={`${ids}-max`} className="text-[12px]">Most keys</Label>
          <Input
            id={`${ids}-max`}
            type="number"
            min={1}
            value={draft.maxValues ?? ""}
            onChange={(event) => {
              const value = Number.parseInt(event.target.value, 10);
              onChange({ ...draft, maxValues: Number.isSafeInteger(value) ? value : null });
            }}
            className="h-7 w-40 text-[12px]"
            placeholder="1000000"
            data-testid="builder-max"
          />
          {problemOf("maxValues") !== null && <p className="text-[11.5px] text-destructive">{problemOf("maxValues")}</p>}
        </div>
      </CollapsibleContent>
    </Collapsible>
  );
}
