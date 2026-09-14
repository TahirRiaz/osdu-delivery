import { useId, useMemo, useState, type ReactNode } from "react";
import { ArrowDown, ArrowUp, CircleAlert, OctagonAlert, Plus, TriangleAlert, X } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Select, SelectContent, SelectGroup, SelectItem, SelectLabel, SelectTrigger, SelectValue,
} from "@/components/ui/select";
import { Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import type {
  DeliveryCachedType, DeliveryTemplateVariable, MappingDraft, MappingDraftCondition, MappingDraftConditionOperator,
  MappingDraftEntry, MappingDraftInput, MappingDraftIssue, MappingDraftModifier, MappingDraftModifierKind,
} from "../../api/delivery";
import {
  DECIMAL_SEPARATORS, emptyEntry, GROUP_SEPARATORS, inputsFor, KEY_NAME, knownColumns, MODIFIER_KINDS, newModifier, NO_GROUP, parseJson,
  repeaterOf, staticModeFor,
  type StaticMode,
} from "./mappingDraft";
import { shapeText } from "./templateFormat";

/** What the entry editor edits: the variable, its entry, and the holder when the target is a new key of an object with free keys. */
export interface EntryEditorTarget {
  variable: DeliveryTemplateVariable;
  entry: MappingDraftEntry | null;
  /** Set for a new key under an object with free keys (tags): the editor asks for the key's name. */
  keyHolder: DeliveryTemplateVariable | null;
  /** Why no mapping may fill the target, when the template does not let one. */
  outside: string | null;
  /** A number the page gives each opening, so every opening starts from the draft as it is. */
  session: number;
}

type Choice = "None" | MappingDraftInput;

const CHOICE_LABELS: Record<Choice, string> = {
  None: "Not filled",
  Dataset: "Dataset column",
  Repeat: "Repeat child rows",
  Cache: "Cache",
  Static: "Static value",
};

const OPERATOR_LABELS: Record<MappingDraftConditionOperator, string> = {
  is: "is",
  isNot: "is not",
  isEmpty: "is empty",
  isNotEmpty: "is not empty",
};

/** One findBy line as the form edits it: the cached field, and a dataset column or a fixed text. */
interface FindLine {
  field: string;
  mode: "column" | "text";
  value: string;
}

/** The static value in every editor at once, so switching between them keeps what was typed. */
interface StaticState {
  list: string[];
  text: string;
  number: string;
  boolean: "true" | "false";
  json: string;
}

/** A column as the draft stores it: trimmed, and without the dataset. prefix a person may type out of habit. */
function bare(text: string): string {
  return text.trim().replace(/^dataset\./, "");
}

function move<T>(items: readonly T[], index: number, delta: number): T[] {
  const to = index + delta;
  const next = [...items];
  if (to < 0 || to >= items.length) {
    return next;
  }

  [next[index], next[to]] = [next[to], next[index]];
  return next;
}

function defaultJson(variable: DeliveryTemplateVariable): string {
  if (variable.shape === "GroupList" || variable.shape === "ValueList" || variable.type === "array") {
    return "[]";
  }

  return variable.shape === "Group" || variable.shape === "Whole" ? "{}" : "\"\"";
}

function seedStatic(variable: DeliveryTemplateVariable, json: string | null): StaticState {
  const parsed = parseJson(json);
  const value = parsed.ok ? parsed.value : undefined;
  return {
    list: Array.isArray(value) ? value.map((item: unknown) => (item === null || item === undefined ? "" : String(item))) : [],
    text: typeof value === "string" ? value : "",
    number: typeof value === "number" ? String(value) : "",
    boolean: value === true ? "true" : "false",
    json: json !== null && json.trim() !== "" ? json : defaultJson(variable),
  };
}

/** The static value as JSON text from the editor in use, or why it cannot be; an empty number is no value yet. */
function staticJsonOf(mode: StaticMode, state: StaticState, variable: DeliveryTemplateVariable): { json: string | null; error: string | null } {
  switch (mode) {
    case "list":
      return { json: JSON.stringify(state.list.map((item) => item.trim()).filter((item) => item !== "")), error: null };
    case "text":
      return { json: JSON.stringify(state.text), error: null };
    case "number": {
      const text = state.number.trim();
      if (text === "") {
        return { json: null, error: null };
      }

      const number = Number(text);
      if (!Number.isFinite(number)) {
        return { json: null, error: `'${text}' is not a number.` };
      }

      if (variable.type === "integer" && !Number.isInteger(number)) {
        return { json: null, error: `'${text}' is not a whole number, and the template takes an integer.` };
      }

      return { json: JSON.stringify(number), error: null };
    }
    case "boolean":
      return { json: state.boolean, error: null };
    case "json": {
      const parsed = parseJson(state.json);
      if (!parsed.ok) {
        return { json: null, error: `The static value is not valid JSON: ${parsed.error}` };
      }

      return { json: parsed.value === undefined ? null : state.json.trim(), error: null };
    }
  }
}

function IconAction({
  label, onClick, disabled = false, testId, children,
}: { label: string; onClick: () => void; disabled?: boolean; testId: string; children: ReactNode }) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <Button variant="ghost" size="icon-xs" aria-label={label} onClick={onClick} disabled={disabled} data-testid={testId}>
          {children}
        </Button>
      </TooltipTrigger>
      <TooltipContent>{label}</TooltipContent>
    </Tooltip>
  );
}

function Section({
  title, hint, action, testId, children,
}: { title: string; hint?: ReactNode; action?: ReactNode; testId?: string; children?: ReactNode }) {
  return (
    <section className="flex flex-col gap-2" data-testid={testId}>
      <div className="flex min-h-7 items-center gap-2">
        <h3 className="text-[13px] font-medium">{title}</h3>
        {action !== undefined && <div className="ml-auto">{action}</div>}
      </div>
      {hint !== undefined && <p className="text-xs text-muted-foreground">{hint}</p>}
      {children}
    </section>
  );
}

const OTHER = "__other__";

interface ChoiceOption {
  value: string;
  hint?: string;
  group: string;
}

/** A select of the names the repository knows, with a way to type one it does not list. */
function ChoiceOrText({
  value, options, onChange, placeholder, testId, className,
}: { value: string; options: ChoiceOption[]; onChange: (value: string) => void; placeholder: string; testId: string; className?: string }) {
  const known = options.some((option) => option.value === value);
  const [typing, setTyping] = useState(value !== "" && !known);
  const showInput = typing || (value !== "" && !known);
  const groups = [...new Set(options.map((option) => option.group))];

  return (
    <div className={cn("flex flex-wrap items-center gap-1", className)}>
      <Select
        value={showInput ? OTHER : value}
        onValueChange={(next) => {
          if (next === OTHER) {
            setTyping(true);
            onChange("");
          } else {
            setTyping(false);
            onChange(next);
          }
        }}
      >
        <SelectTrigger size="sm" className="h-8 min-w-40" data-testid={testId}>
          <SelectValue placeholder={placeholder} />
        </SelectTrigger>
        <SelectContent>
          {groups.map((group) => (
            <SelectGroup key={group}>
              <SelectLabel>{group}</SelectLabel>
              {options.filter((option) => option.group === group).map((option) => (
                <SelectItem key={option.value} value={option.value}>
                  {option.value}
                  {option.hint !== undefined && <span className="font-mono text-[11px] text-muted-foreground">{option.hint}</span>}
                </SelectItem>
              ))}
            </SelectGroup>
          ))}
          <SelectItem value={OTHER}>Type a name</SelectItem>
        </SelectContent>
      </Select>
      {showInput && (
        <Input
          className="h-8 w-44 font-mono text-[12px]"
          placeholder={placeholder}
          value={value}
          onChange={(event) => onChange(event.target.value)}
          data-testid={`${testId}-text`}
        />
      )}
    </div>
  );
}

interface EntryFormProps {
  target: EntryEditorTarget;
  draft: MappingDraft;
  /** The types of the cache the mapping reads, offered to a cache entry; empty when no cache is picked. */
  cacheTypes: DeliveryCachedType[];
  issues: MappingDraftIssue[];
  onSave: (target: string, entry: MappingDraftEntry | null) => void;
  onClose: () => void;
}

function EntryForm({ target, draft, cacheTypes, issues, onSave, onClose }: EntryFormProps) {
  const ids = useId();
  const { variable, entry, keyHolder, outside } = target;
  const known = useMemo(() => knownColumns(draft), [draft]);

  const allowed: MappingDraftInput[] = outside !== null ? ["Dataset", "Repeat", "Cache", "Static"] : inputsFor(variable);
  if (entry !== null && !allowed.includes(entry.input)) {
    allowed.push(entry.input);
  }

  const typedMode = staticModeFor(variable, null);
  const initialStatic = entry?.input === "Static" ? entry.static : null;

  const [choice, setChoice] = useState<Choice>(entry?.input ?? (keyHolder !== null ? allowed[0] : "None"));
  const [keyName, setKeyName] = useState("");
  const [column, setColumn] = useState(entry?.column ?? "");
  const [child, setChild] = useState(entry?.child ?? "");
  const [cacheType, setCacheType] = useState(entry?.cacheType ?? variable.cacheTypes[0] ?? "");
  const [cacheField, setCacheField] = useState(entry?.cacheField ?? "id");
  const [findBy, setFindBy] = useState<FindLine[]>(() => (entry?.findBy ?? []).map((find): FindLine => (
    find.literal !== null && find.literal !== ""
      ? { field: find.field, mode: "text", value: find.literal }
      : { field: find.field, mode: "column", value: find.column ?? "" })));
  const [ignoreSeparators, setIgnoreSeparators] = useState(entry?.ignoreSeparators ?? false);
  const [modifiers, setModifiers] = useState<MappingDraftModifier[]>(entry?.modifiers ?? []);
  const [conditionOn, setConditionOn] = useState(entry !== null && entry.appliesWhen !== null);
  const [condition, setCondition] = useState<MappingDraftCondition>(entry?.appliesWhen ?? { column: "", operator: "is", text: "" });
  const [required, setRequired] = useState(entry?.required ?? true);
  const [description, setDescription] = useState(entry?.description ?? "");
  const [jsonMode, setJsonMode] = useState(() => typedMode === "json" || staticModeFor(variable, initialStatic) === "json");
  const [staticState, setStaticState] = useState<StaticState>(() => seedStatic(variable, initialStatic));

  const mode: StaticMode = jsonMode ? "json" : typedMode;
  const staticResult = choice === "Static" ? staticJsonOf(mode, staticState, variable) : { json: null, error: null };
  const newTarget = keyHolder !== null ? `${keyHolder.path}.${keyName.trim()}` : variable.path;
  const repeater = repeaterOf(newTarget);
  const repeaterChild = repeater === null
    ? null
    : draft.entries.find((candidate) => candidate.target === repeater && candidate.input === "Repeat")?.child ?? null;
  const repoTypes = cacheTypes;
  const typeFields = repoTypes.find((type) => type.name === cacheType.trim())?.fields ?? [];
  const fieldOptions: ChoiceOption[] = [...new Set([...typeFields, "id"])].map((field) => ({ value: field, group: "Cached fields" }));
  const typeOptions: ChoiceOption[] = [
    ...variable.cacheTypes.map((name) => ({
      value: name,
      hint: repoTypes.find((type) => type.name === name)?.entityType,
      group: "Types this variable can be read from",
    })),
    ...repoTypes.filter((type) => !variable.cacheTypes.includes(type.name)).map((type) => ({
      value: type.name,
      hint: type.entityType,
      group: "Other cached types",
    })),
  ];

  let keyError: string | null = null;
  if (keyHolder !== null) {
    const name = keyName.trim();
    if (name === "") {
      keyError = "Name the key.";
    } else if (!KEY_NAME.test(name)) {
      keyError = "A key name holds no dots, brackets or spaces.";
    } else if (draft.entries.some((candidate) => candidate.target === newTarget)) {
      keyError = `${newTarget} already has an entry.`;
    }
  }

  const error = keyError ?? staticResult.error;
  const columnsList = `${ids}-columns`;
  const childrenList = `${ids}-children`;

  const choose = (next: Choice) => {
    setChoice(next);
    if (next !== "Cache") {
      return;
    }

    const type = cacheType.trim() === "" ? variable.cacheTypes[0] ?? "" : cacheType;
    if (type !== cacheType) {
      setCacheType(type);
    }

    if (findBy.length === 0) {
      const fields = repoTypes.find((candidate) => candidate.name === type)?.fields ?? [];
      setFindBy([{ field: fields[0] ?? "id", mode: "column", value: "" }]);
    }
  };

  const updateModifier = (index: number, patch: Partial<MappingDraftModifier>) =>
    setModifiers((current) => current.map((modifier, i) => (i === index ? { ...modifier, ...patch } : modifier)));

  const updateLine = (index: number, patch: Partial<FindLine>) =>
    setFindBy((current) => current.map((line, i) => (i === index ? { ...line, ...patch } : line)));

  const toggleJson = (on: boolean) => {
    if (on) {
      const typed = staticJsonOf(typedMode, staticState, variable);
      setStaticState((current) => ({ ...current, json: typed.json ?? current.json }));
      setJsonMode(true);
      return;
    }

    // Back to the typed editor only when it can show what the JSON holds.
    if (staticModeFor(variable, staticState.json) !== "json") {
      setStaticState(seedStatic(variable, staticState.json));
      setJsonMode(false);
    }
  };

  const save = () => {
    if (error !== null) {
      return;
    }

    if (choice === "None") {
      onSave(variable.path, null);
      return;
    }

    const next: MappingDraftEntry = {
      ...(entry ?? emptyEntry(newTarget, choice)),
      target: newTarget,
      input: choice,
      column: choice === "Dataset" ? bare(column) : null,
      child: choice === "Repeat" ? bare(child) : null,
      cacheType: choice === "Cache" ? cacheType.trim() : null,
      cacheField: choice === "Cache" ? cacheField.trim() : null,
      findBy: choice === "Cache"
        ? findBy.map((line) => (line.mode === "text"
          ? { field: line.field.trim(), column: null, literal: line.value }
          : { field: line.field.trim(), column: bare(line.value), literal: null }))
        : [],
      modifiers: choice === "Dataset" || choice === "Cache"
        ? modifiers.map((modifier) => (modifier.kind === "date" && (modifier.text ?? "").trim() === "" ? { ...modifier, text: null } : modifier))
        : [],
      appliesWhen: conditionOn
        ? {
          column: bare(condition.column),
          operator: condition.operator,
          text: condition.operator === "is" || condition.operator === "isNot" ? condition.text ?? "" : null,
        }
        : null,
      required: choice === "Static" ? true : required,
      ignoreSeparators: choice === "Cache" && ignoreSeparators,
      static: choice === "Static" ? staticResult.json : null,
      description: description.trim() === "" ? null : description.trim(),
      prefilled: false,
    };
    onSave(newTarget, next);
  };

  const facts = [
    shapeText(variable),
    variable.format !== null ? `format ${variable.format}` : null,
    variable.required ? "required by the template" : null,
    variable.relationships.length > 0 ? `points to ${variable.relationships.join(", ")}` : null,
    variable.unitContext !== null ? `unit context ${variable.unitContext}` : null,
  ].filter((fact): fact is string => fact !== null);

  return (
    <>
      <SheetHeader>
        <SheetTitle className="break-all pr-6 font-mono text-[14px]" data-testid="mapping-builder-entry-target">
          {keyHolder !== null ? `${keyHolder.path}.<key>` : variable.path}
        </SheetTitle>
        <SheetDescription>{facts.join(", ")}.</SheetDescription>
      </SheetHeader>
      <div className="flex flex-1 flex-col gap-5 overflow-y-auto px-4 pb-4">
        <datalist id={columnsList}>
          {known.columns.map((name) => <option key={name} value={name} />)}
        </datalist>
        <datalist id={childrenList}>
          {known.children.map((name) => <option key={name} value={name} />)}
        </datalist>
        {variable.description !== null && <p className="text-xs text-muted-foreground">{variable.description}</p>}
        {outside !== null && (
          <Alert data-testid="mapping-builder-entry-outside">
            <CircleAlert />
            <AlertDescription><p>{outside} Remove the entry, or keep it and let the check report it.</p></AlertDescription>
          </Alert>
        )}
        {issues.length > 0 && (
          <div className="flex flex-col gap-1 rounded-md border border-border bg-muted/30 p-2" data-testid="mapping-builder-entry-issues">
            {issues.map((issue, index) => (
              <p
                key={`${issue.severity}-${index}`}
                className={cn("flex items-start gap-1.5 text-xs", issue.severity === "error" ? "text-destructive" : "text-warning")}
              >
                {issue.severity === "error" ? <OctagonAlert className="mt-px size-3.5 shrink-0" /> : <TriangleAlert className="mt-px size-3.5 shrink-0" />}
                {issue.message}
              </p>
            ))}
            <p className="text-[11px] text-muted-foreground">From the last check. It runs again when you save.</p>
          </div>
        )}

        {keyHolder !== null && (
          <Section title="Key" hint={`A key under ${keyHolder.path}, which takes any key with a ${keyHolder.keyValueType ?? "string"} value.`}>
            <div className="flex items-center gap-1">
              <span className="font-mono text-[12px] text-muted-foreground">{keyHolder.path}.</span>
              <Input
                className="h-8 w-60 font-mono"
                placeholder="DeliveredBy"
                value={keyName}
                onChange={(event) => setKeyName(event.target.value)}
                data-testid="mapping-builder-entry-key"
              />
            </div>
          </Section>
        )}

        <Section title="Where the value comes from">
          <ToggleGroup
            type="single"
            variant="outline"
            size="sm"
            value={choice}
            onValueChange={(next) => { if (next !== "") { choose(next as Choice); } }}
            className="flex-wrap"
            data-testid="mapping-builder-entry-input"
          >
            {keyHolder === null && (
              <ToggleGroupItem value="None" data-testid="mapping-builder-entry-input-none">{CHOICE_LABELS.None}</ToggleGroupItem>
            )}
            {allowed.map((input) => (
              <ToggleGroupItem key={input} value={input} data-testid={`mapping-builder-entry-input-${input.toLowerCase()}`}>
                {CHOICE_LABELS[input]}
              </ToggleGroupItem>
            ))}
          </ToggleGroup>
          {choice === "None" && (
            <p className="text-xs text-muted-foreground">
              A variable without an entry is left out of the record.
              {variable.required && " The template requires this one, so the check reports it until it is filled."}
            </p>
          )}
        </Section>

        {choice === "Dataset" && (
          <Section
            title="Dataset column"
            hint={repeater === null
              ? "A column of the dataset row, written without dataset."
              : `Inside ${repeater}, a column of its child rows is written ${repeaterChild ?? "child"}.column; a plain column reads the parent row.`}
          >
            <Input
              list={columnsList}
              className="h-8 font-mono"
              placeholder={repeater === null ? "log_name" : `${repeaterChild ?? "child"}.column`}
              value={column}
              onChange={(event) => setColumn(event.target.value)}
              data-testid="mapping-builder-entry-column"
            />
          </Section>
        )}

        {choice === "Repeat" && (
          <Section
            title="Child dataset"
            hint={`One item of ${variable.path} per row of this child dataset. Fill the items' properties with their own entries, such as ${variable.path}[].Name.`}
          >
            <Input
              list={childrenList}
              className="h-8 font-mono"
              placeholder="curves"
              value={child}
              onChange={(event) => setChild(event.target.value)}
              data-testid="mapping-builder-entry-child"
            />
          </Section>
        )}

        {choice === "Cache" && (
          <>
            <Section
              title="Cached record"
              hint={repoTypes.length === 0
                ? "The repository's cache holds no types, so the check cannot confirm the type or its fields."
                : "The cached type to read, and the field to read from it. id reads the record's OSDU id in the form relationships use."}
            >
              <div className="flex flex-wrap items-center gap-2">
                <ChoiceOrText
                  value={cacheType}
                  options={typeOptions}
                  onChange={setCacheType}
                  placeholder="Cached type"
                  testId="mapping-builder-entry-cache-type"
                />
                <span className="font-mono text-[12px] text-muted-foreground">.</span>
                <Input
                  className="h-8 w-44 font-mono"
                  placeholder="id"
                  value={cacheField}
                  onChange={(event) => setCacheField(event.target.value)}
                  data-testid="mapping-builder-entry-cache-field"
                />
              </div>
            </Section>
            <Section
              title="Find the record by"
              hint="Tried in order. The first line that finds a record wins, and several matching records hold the record."
              action={(
                <Button
                  variant="outline"
                  size="xs"
                  onClick={() => setFindBy((current) => [...current, { field: typeFields[0] ?? "id", mode: "column", value: "" }])}
                  data-testid="mapping-builder-entry-findby-add"
                >
                  <Plus />
                  Add a line
                </Button>
              )}
              testId="mapping-builder-entry-findby"
            >
              {findBy.length === 0 && <p className="text-xs text-destructive">Add at least one line, or no cached record can be found.</p>}
              {findBy.map((line, index) => (
                <div
                  key={index}
                  className="flex flex-wrap items-center gap-1.5 rounded-md border border-border p-2"
                  data-testid={`mapping-builder-entry-findby-${index}`}
                >
                  <ChoiceOrText
                    value={line.field}
                    options={fieldOptions}
                    onChange={(field) => updateLine(index, { field })}
                    placeholder="Cached field"
                    testId={`mapping-builder-entry-findby-field-${index}`}
                  />
                  <span className="font-mono text-[12px] text-muted-foreground">=</span>
                  <Select value={line.mode} onValueChange={(next) => updateLine(index, { mode: next === "text" ? "text" : "column" })}>
                    <SelectTrigger size="sm" className="h-8 w-36" data-testid={`mapping-builder-entry-findby-mode-${index}`}>
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="column">dataset column</SelectItem>
                      <SelectItem value="text">fixed text</SelectItem>
                    </SelectContent>
                  </Select>
                  <Input
                    list={line.mode === "column" ? columnsList : undefined}
                    className="h-8 min-w-40 flex-1 font-mono text-[12px]"
                    placeholder={line.mode === "column" ? (repeater === null ? "column" : `${repeaterChild ?? "child"}.column`) : "KellyBushing"}
                    value={line.value}
                    onChange={(event) => updateLine(index, { value: event.target.value })}
                    data-testid={`mapping-builder-entry-findby-value-${index}`}
                  />
                  <IconAction label="Move up" onClick={() => setFindBy((current) => move(current, index, -1))} disabled={index === 0} testId={`mapping-builder-entry-findby-up-${index}`}>
                    <ArrowUp />
                  </IconAction>
                  <IconAction label="Move down" onClick={() => setFindBy((current) => move(current, index, 1))} disabled={index === findBy.length - 1} testId={`mapping-builder-entry-findby-down-${index}`}>
                    <ArrowDown />
                  </IconAction>
                  <IconAction label="Remove this line" onClick={() => setFindBy((current) => current.filter((_, i) => i !== index))} testId={`mapping-builder-entry-findby-remove-${index}`}>
                    <X />
                  </IconAction>
                </div>
              ))}
              <Label className="flex items-center gap-2 text-[13px] font-normal">
                <Switch checked={ignoreSeparators} onCheckedChange={setIgnoreSeparators} data-testid="mapping-builder-entry-ignore-separators" />
                Also try with punctuation and spacing folded away (for names, never for codes)
              </Label>
            </Section>
          </>
        )}

        {choice === "Static" && (
          <Section
            title="Static value"
            hint="{param.name} tokens in text are replaced with the flow's parameter values."
            action={typedMode !== "json" ? (
              <Label className="flex items-center gap-2 text-xs font-normal">
                <Switch
                  size="sm"
                  checked={jsonMode}
                  onCheckedChange={toggleJson}
                  disabled={jsonMode && staticModeFor(variable, staticState.json) === "json"}
                  data-testid="mapping-builder-entry-static-json-mode"
                />
                Write as JSON
              </Label>
            ) : undefined}
          >
            {mode === "list" && (
              <div className="flex flex-col gap-1.5" data-testid="mapping-builder-entry-static-list">
                {staticState.list.length === 0 && <p className="text-xs text-muted-foreground">No values yet.</p>}
                {staticState.list.map((item, index) => (
                  <div key={index} className="flex items-center gap-1">
                    <Input
                      className="h-8 font-mono"
                      value={item}
                      onChange={(event) => {
                        const value = event.target.value;
                        setStaticState((current) => ({ ...current, list: current.list.map((old, i) => (i === index ? value : old)) }));
                      }}
                      data-testid={`mapping-builder-entry-static-item-${index}`}
                    />
                    <IconAction
                      label="Remove this value"
                      onClick={() => setStaticState((current) => ({ ...current, list: current.list.filter((_, i) => i !== index) }))}
                      testId={`mapping-builder-entry-static-remove-${index}`}
                    >
                      <X />
                    </IconAction>
                  </div>
                ))}
                <Button
                  variant="outline"
                  size="xs"
                  className="self-start"
                  onClick={() => setStaticState((current) => ({ ...current, list: [...current.list, ""] }))}
                  data-testid="mapping-builder-entry-static-add"
                >
                  <Plus />
                  Add a value
                </Button>
              </div>
            )}
            {mode === "text" && (
              <Input
                className="h-8 font-mono"
                value={staticState.text}
                onChange={(event) => { const value = event.target.value; setStaticState((current) => ({ ...current, text: value })); }}
                data-testid="mapping-builder-entry-static"
              />
            )}
            {mode === "number" && (
              <Input
                className="h-8 w-48 font-mono"
                inputMode="decimal"
                placeholder={variable.type === "integer" ? "a whole number" : "a number"}
                value={staticState.number}
                onChange={(event) => { const value = event.target.value; setStaticState((current) => ({ ...current, number: value })); }}
                data-testid="mapping-builder-entry-static"
              />
            )}
            {mode === "boolean" && (
              <Select
                value={staticState.boolean}
                onValueChange={(next) => setStaticState((current) => ({ ...current, boolean: next === "true" ? "true" : "false" }))}
              >
                <SelectTrigger size="sm" className="h-8 w-32" data-testid="mapping-builder-entry-static"><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="true">true</SelectItem>
                  <SelectItem value="false">false</SelectItem>
                </SelectContent>
              </Select>
            )}
            {mode === "json" && (
              <Textarea
                className="field-sizing-fixed h-40 resize-y font-mono text-[12px]"
                spellCheck={false}
                value={staticState.json}
                onChange={(event) => { const value = event.target.value; setStaticState((current) => ({ ...current, json: value })); }}
                aria-invalid={staticResult.error !== null}
                data-testid="mapping-builder-entry-static"
              />
            )}
          </Section>
        )}

        {(choice === "Dataset" || choice === "Cache") && (
          <Section
            title="Modifiers"
            hint={choice === "Cache"
              ? "On a cache input, modifiers change the dataset value before it is compared. Cached values are never modified."
              : "Applied to the dataset value, top to bottom."}
            action={(
              <Select value="" onValueChange={(kind) => setModifiers((current) => [...current, newModifier(kind as MappingDraftModifierKind)])}>
                <SelectTrigger size="sm" className="h-7 w-40" data-testid="mapping-builder-entry-modifier-add">
                  <SelectValue placeholder="Add a modifier" />
                </SelectTrigger>
                <SelectContent>
                  {MODIFIER_KINDS.map((kind) => <SelectItem key={kind} value={kind}>{kind}</SelectItem>)}
                </SelectContent>
              </Select>
            )}
            testId="mapping-builder-entry-modifiers"
          >
            {modifiers.length === 0 && <p className="text-xs text-muted-foreground">No modifiers.</p>}
            {modifiers.map((modifier, index) => (
              <div key={index} className="flex flex-col gap-2 rounded-md border border-border p-2" data-testid={`mapping-builder-entry-modifier-${index}`}>
                <div className="flex items-center gap-1">
                  <span className="font-mono text-[12px] font-medium">{modifier.kind}</span>
                  <div className="ml-auto flex items-center gap-0.5">
                    <IconAction label="Move up" onClick={() => setModifiers((current) => move(current, index, -1))} disabled={index === 0} testId={`mapping-builder-entry-modifier-up-${index}`}>
                      <ArrowUp />
                    </IconAction>
                    <IconAction label="Move down" onClick={() => setModifiers((current) => move(current, index, 1))} disabled={index === modifiers.length - 1} testId={`mapping-builder-entry-modifier-down-${index}`}>
                      <ArrowDown />
                    </IconAction>
                    <IconAction label="Remove this modifier" onClick={() => setModifiers((current) => current.filter((_, i) => i !== index))} testId={`mapping-builder-entry-modifier-remove-${index}`}>
                      <X />
                    </IconAction>
                  </div>
                </div>
                {modifier.kind === "split" && (
                  <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
                    <span>separator</span>
                    <Input
                      className="h-7 w-20 font-mono text-[12px]"
                      value={modifier.separator ?? ""}
                      onChange={(event) => updateModifier(index, { separator: event.target.value })}
                      data-testid={`mapping-builder-entry-modifier-separator-${index}`}
                    />
                    <span>keep part</span>
                    <Input
                      className="h-7 w-16 font-mono text-[12px]"
                      inputMode="numeric"
                      value={modifier.part === null ? "" : String(modifier.part)}
                      onChange={(event) => {
                        const part = Number.parseInt(event.target.value, 10);
                        updateModifier(index, { part: Number.isNaN(part) ? null : part });
                      }}
                      data-testid={`mapping-builder-entry-modifier-part-${index}`}
                    />
                    <span>counting from one; a single space splits on any run of whitespace</span>
                  </div>
                )}
                {modifier.kind === "replace" && (
                  <div className="flex flex-col gap-1">
                    {(modifier.replacements ?? []).map((pair, pairIndex) => (
                      <div key={pairIndex} className="flex items-center gap-1">
                        <Input
                          className="h-7 font-mono text-[12px]"
                          placeholder="incoming value"
                          value={pair.from}
                          onChange={(event) => updateModifier(index, {
                            replacements: (modifier.replacements ?? []).map((old, i) => (i === pairIndex ? { ...old, from: event.target.value } : old)),
                          })}
                          data-testid={`mapping-builder-entry-modifier-from-${index}-${pairIndex}`}
                        />
                        <span className="text-xs text-muted-foreground">becomes</span>
                        <Input
                          className="h-7 font-mono text-[12px]"
                          placeholder="what it becomes"
                          value={pair.to}
                          onChange={(event) => updateModifier(index, {
                            replacements: (modifier.replacements ?? []).map((old, i) => (i === pairIndex ? { ...old, to: event.target.value } : old)),
                          })}
                          data-testid={`mapping-builder-entry-modifier-to-${index}-${pairIndex}`}
                        />
                        <IconAction
                          label="Remove this pair"
                          onClick={() => updateModifier(index, { replacements: (modifier.replacements ?? []).filter((_, i) => i !== pairIndex) })}
                          testId={`mapping-builder-entry-modifier-pair-remove-${index}-${pairIndex}`}
                        >
                          <X />
                        </IconAction>
                      </div>
                    ))}
                    <Button
                      variant="ghost"
                      size="xs"
                      className="self-start"
                      onClick={() => updateModifier(index, { replacements: [...(modifier.replacements ?? []), { from: "", to: "" }] })}
                      data-testid={`mapping-builder-entry-modifier-pair-add-${index}`}
                    >
                      <Plus />
                      Add a pair
                    </Button>
                    <p className="text-xs text-muted-foreground">A value the pairs do not list is left as it is.</p>
                  </div>
                )}
                {modifier.kind === "equals" && (
                  <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
                    <span>true when the value is</span>
                    <Input
                      className="h-7 w-48 font-mono text-[12px]"
                      value={modifier.text ?? ""}
                      onChange={(event) => updateModifier(index, { text: event.target.value })}
                      data-testid={`mapping-builder-entry-modifier-text-${index}`}
                    />
                    <span>(trimmed, any case), false otherwise</span>
                  </div>
                )}
                {modifier.kind === "date" && (
                  <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
                    <span>format</span>
                    <Input
                      className="h-7 w-48 font-mono text-[12px]"
                      placeholder="dd.MM.yyyy"
                      value={modifier.text ?? ""}
                      onChange={(event) => updateModifier(index, { text: event.target.value })}
                      data-testid={`mapping-builder-entry-modifier-format-${index}`}
                    />
                    <span>empty reads ISO 8601</span>
                  </div>
                )}
                {modifier.kind === "number" && (
                  <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
                    <span>decimals after</span>
                    <Select value={modifier.decimalSeparator ?? "."} onValueChange={(value) => updateModifier(index, { decimalSeparator: value })}>
                      <SelectTrigger size="sm" className="h-7 w-28" data-testid={`mapping-builder-entry-modifier-decimal-${index}`}>
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        {DECIMAL_SEPARATORS.map((option) => <SelectItem key={option.value} value={option.value}>{option.label}</SelectItem>)}
                      </SelectContent>
                    </Select>
                    <span>digit groups split by</span>
                    <Select
                      value={modifier.groupSeparator ?? NO_GROUP}
                      onValueChange={(value) => updateModifier(index, { groupSeparator: value === NO_GROUP ? null : value })}
                    >
                      <SelectTrigger size="sm" className="h-7 w-32" data-testid={`mapping-builder-entry-modifier-group-${index}`}>
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        {GROUP_SEPARATORS.map((option) => <SelectItem key={option.value} value={option.value}>{option.label}</SelectItem>)}
                      </SelectContent>
                    </Select>
                    {modifier.groupSeparator !== null && modifier.groupSeparator === (modifier.decimalSeparator ?? ".") && (
                      <span className="text-destructive">the two separators must differ</span>
                    )}
                  </div>
                )}
              </div>
            ))}
          </Section>
        )}

        {choice !== "None" && (
          <Section
            title="When it applies"
            hint="When the condition is false, the variable is left out for that row. It never holds a record."
          >
            <Label className="flex items-center gap-2 text-[13px] font-normal">
              <Switch checked={conditionOn} onCheckedChange={setConditionOn} data-testid="mapping-builder-entry-applies" />
              Only when a condition holds
            </Label>
            {conditionOn && (
              <div className="flex flex-wrap items-center gap-1.5">
                <span className="font-mono text-[12px] text-muted-foreground">dataset.</span>
                <Input
                  list={columnsList}
                  className="h-8 w-48 font-mono"
                  placeholder="depth_coding"
                  value={condition.column}
                  onChange={(event) => { const value = event.target.value; setCondition((current) => ({ ...current, column: value })); }}
                  data-testid="mapping-builder-entry-applies-column"
                />
                <Select
                  value={condition.operator}
                  onValueChange={(next) => setCondition((current) => ({ ...current, operator: next as MappingDraftConditionOperator }))}
                >
                  <SelectTrigger size="sm" className="h-8 w-36" data-testid="mapping-builder-entry-applies-operator"><SelectValue /></SelectTrigger>
                  <SelectContent>
                    {(Object.keys(OPERATOR_LABELS) as MappingDraftConditionOperator[]).map((operator) => (
                      <SelectItem key={operator} value={operator}>{OPERATOR_LABELS[operator]}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                {(condition.operator === "is" || condition.operator === "isNot") && (
                  <Input
                    className="h-8 w-44 font-mono"
                    placeholder="REGULAR"
                    value={condition.text ?? ""}
                    onChange={(event) => { const value = event.target.value; setCondition((current) => ({ ...current, text: value })); }}
                    data-testid="mapping-builder-entry-applies-text"
                  />
                )}
              </div>
            )}
          </Section>
        )}

        {choice !== "None" && choice !== "Static" && (
          <Section
            title="Required"
            hint={variable.required
              ? "The template requires this variable, so its entry has to stay required."
              : "Off leaves the variable out when the value is empty or the cache has no match, instead of holding the record."}
          >
            <Label className="flex items-center gap-2 text-[13px] font-normal">
              <Switch checked={required} onCheckedChange={setRequired} data-testid="mapping-builder-entry-required" />
              Hold the record when there is no value
            </Label>
          </Section>
        )}

        {choice !== "None" && (
          <Section title="Description">
            <Input
              className="h-8"
              placeholder="Why the value comes from here (optional)"
              value={description}
              onChange={(event) => setDescription(event.target.value)}
              data-testid="mapping-builder-entry-description"
            />
          </Section>
        )}
      </div>
      <SheetFooter className="flex-row flex-wrap items-center justify-end gap-2 border-t border-border">
        {error !== null && (
          <p className="mr-auto text-xs font-medium text-destructive" data-testid="mapping-builder-entry-error">{error}</p>
        )}
        <Button variant="ghost" size="sm" onClick={onClose} data-testid="mapping-builder-entry-cancel">Cancel</Button>
        <Button size="sm" onClick={save} disabled={error !== null} data-testid="mapping-builder-entry-save">
          {choice === "None" && entry !== null ? "Remove entry" : "Save entry"}
        </Button>
      </SheetFooter>
    </>
  );
}

interface MappingEntryEditorProps {
  /** What to edit; null closes the editor. */
  target: EntryEditorTarget | null;
  draft: MappingDraft;
  /** The types of the cache the mapping reads, offered to a cache entry; empty when no cache is picked. */
  cacheTypes: DeliveryCachedType[];
  /** Every issue of the last check; the editor shows the ones about its target. */
  issues: MappingDraftIssue[];
  /** Puts the entry for a target into the draft, or removes the target's entry when the entry is null. */
  onSave: (target: string, entry: MappingDraftEntry | null) => void;
  onClose: () => void;
}

/**
 * The entry editor: where one variable's value comes from (a dataset column, a child dataset's rows, a cached record or a
 * static value), with its modifiers, condition, requiredness and description. It edits a copy, and Save puts the entry
 * into the draft, so Cancel leaves the draft as it was.
 */
export function MappingEntryEditor({ target, draft, cacheTypes, issues, onSave, onClose }: MappingEntryEditorProps) {
  // The sheet slides out showing what it showed, so the last target stays rendered while it closes.
  const [shown, setShown] = useState<EntryEditorTarget | null>(target);
  if (target !== null && target !== shown) {
    setShown(target);
  }

  return (
    <Sheet open={target !== null} onOpenChange={(open) => { if (!open) { onClose(); } }}>
      <SheetContent className="w-full gap-0 sm:max-w-2xl" data-testid="mapping-builder-entry-editor">
        {shown !== null && (
          <EntryForm
            key={shown.session}
            target={shown}
            draft={draft}
            cacheTypes={cacheTypes}
            issues={shown.keyHolder === null ? issues.filter((issue) => issue.target === shown.variable.path) : []}
            onSave={onSave}
            onClose={onClose}
          />
        )}
      </SheetContent>
    </Sheet>
  );
}
