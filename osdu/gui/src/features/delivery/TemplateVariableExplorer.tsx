import { createContext, useContext, useMemo, useState, type FocusEvent } from "react";
import {
  Braces,
  Brackets,
  Calendar,
  ChevronsDownUp,
  ChevronsUpDown,
  Circle,
  CircleCheck,
  CircleDot,
  Cloud,
  Cog,
  DatabaseZap,
  FileJson,
  Hash,
  Link2,
  List,
  OctagonAlert,
  Ruler,
  ToggleLeft,
  TriangleAlert,
  Type,
  type LucideIcon,
} from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Label } from "@/components/ui/label";
import { ResizableHandle, ResizablePanel, ResizablePanelGroup } from "@/components/ui/resizable";
import { Switch } from "@/components/ui/switch";
import { cn } from "@/lib/utils";
import type {
  CoverageState, DeliveryMappingCoverage, DeliveryTemplateRole, DeliveryTemplateVariable, MappingDraftEntry, MappingDraftIssue,
} from "../../api/delivery";
import { CopyButton } from "@/components/CopyButton";
import { DetailPair } from "@/components/DetailPair";
import { FilterBar } from "@/components/FilterBar";
import { IconAction } from "@/components/IconAction";
import { SearchInput } from "@/components/SearchInput";
import { StatePill } from "@/components/StatusBadge";
import { TreeContext, TreeNode, type TreeState } from "@/components/Tree";
import { usePersistentLayout } from "@/layout/workbench/usePersistentLayout";
import { entryText, propertyRow } from "./mappingDraft";
import { EntryDetail } from "./MappingEntryDetail";
import { holderPath, roleLabel, shapeText, splitPath } from "./templateFormat";
import { withKeyVariables } from "./variableRows";

/** One variable in the tree, with the variables it holds that survive the filter. */
interface VariableNode {
  variable: DeliveryTemplateVariable;
  /** True when the variable itself passes the filter; false for a holder kept only because something inside it does. */
  matched: boolean;
  children: VariableNode[];
}

/** A mapping laid over the template: what it fills of it, and the entries doing the filling. */
export interface MappingOverlay {
  /** What the mapping fills, variable by variable, as the coverage endpoint answers it. */
  coverage: DeliveryMappingCoverage;
  /** The document's entries, so a variable can show what fills it and not only that something does. */
  entries: MappingDraftEntry[];
}

/**
 * How the mapping being looked at reaches one variable, what fills it, and what the checks found there. A row shows the
 * state as a glyph and nothing more: the entry itself is on the row's hover and in the properties beside the tree,
 * where it can be read whole instead of crowding every row.
 */
interface CoverageView {
  state: CoverageState;
  /** The entry filling the variable itself; null for an object filled through what it holds, and for an empty one. */
  entry: MappingDraftEntry | null;
  /** The worst finding on the variable, and what it says. */
  finding: MappingDraftIssue | null;
}

/**
 * What the mapping fills, by variable, for the tree to show. Null when no mapping is being looked at, which is how the
 * Templates page reads the same tree: a template on its own, with nothing to say about who fills it.
 */
const CoverageContext = createContext<ReadonlyMap<string, CoverageView> | null>(null);

/**
 * How a variable's state reads: the word, the glyph and the tone, for a row and for the properties beside it. A
 * variable the mapping fills carries a check whether or not every record ends up with it, because it is filled either
 * way; the tone is what says a record may go without it. Only what nothing fills reads as empty.
 */
function coverageVisual(state: CoverageState): { label: string; tone: "success" | "warning" | "muted"; icon: LucideIcon; textClass: string } {
  switch (state) {
    case "Always":
      return { label: "Filled on every row", tone: "success", icon: CircleCheck, textClass: "text-success" };
    case "Sometimes":
      return { label: "Filled on some rows", tone: "warning", icon: CircleCheck, textClass: "text-warning" };
    case "Empty":
      return { label: "Not filled", tone: "muted", icon: Circle, textClass: "text-muted-foreground" };
  }
}

/**
 * Whether the overview leaves a variable out: one the mapping does not reach and no check names. A variable the
 * coverage says nothing about (what OSDU writes, a nested list) is left to the switch that shows its kind.
 */
function inOverview(covered: CoverageView | undefined): boolean {
  return covered !== undefined && covered.state === "Empty" && covered.finding === null;
}

/**
 * Whether a variable is one of the gaps: nothing fills it, or a check names it. An entry that may leave a value out is
 * not a gap, because the mapping does fill that variable; what such an entry costs is said where it is required, by
 * the check that names it.
 */
function isGap(covered: CoverageView | undefined): boolean {
  return covered === undefined || covered.state === "Empty" || covered.finding !== null;
}

/**
 * Whether a variable carries the filter text: its path, title, description or the entity types it points to, and what
 * the mapping fills it with when there is one, so a source column, a cached type or a modifier finds its variables.
 */
function textMatches(variable: DeliveryTemplateVariable, term: string, covered: CoverageView | undefined): boolean {
  return term === ""
    || variable.path.toLowerCase().includes(term)
    || (variable.title ?? "").toLowerCase().includes(term)
    || (variable.description ?? "").toLowerCase().includes(term)
    || variable.relationships.some((relationship) => relationship.toLowerCase().includes(term))
    || (covered?.entry != null && entryText(covered.entry).toLowerCase().includes(term));
}

/**
 * The variables as a tree: each one under the variable that holds it, in schema order. A variable the switches hide
 * passes its children up to the nearest holder still shown, and a holder with no match inside it is dropped. The API
 * lists parents before their children, so a holder is always placed before anything it holds.
 */
function buildTree(
  variables: DeliveryTemplateVariable[],
  include: (variable: DeliveryTemplateVariable) => boolean,
  matches: (variable: DeliveryTemplateVariable) => boolean,
): { roots: VariableNode[]; matched: number } {
  const placed = new Map<string, VariableNode>();
  const roots: VariableNode[] = [];
  for (const variable of variables) {
    if (!include(variable)) {
      continue;
    }

    const node: VariableNode = { variable, matched: matches(variable), children: [] };
    placed.set(variable.path, node);
    let holder = holderPath(variable.path);
    while (holder !== null && !placed.has(holder)) {
      holder = holderPath(holder);
    }

    const parent = holder === null ? undefined : placed.get(holder);
    if (parent === undefined) {
      roots.push(node);
    } else {
      parent.children.push(node);
    }
  }

  let matched = 0;
  const prune = (nodes: VariableNode[]): VariableNode[] => nodes.flatMap((node) => {
    const children = prune(node.children);
    if (node.matched) {
      matched += 1;
    }
    return node.matched || children.length > 0 ? [{ ...node, children }] : [];
  });
  return { roots: prune(roots), matched };
}

/**
 * Every holder on the way to a variable that matches, so a match is never hidden inside a collapsed branch. Nothing is
 * revealed while the tree shows everything: a reader opening a fresh template reads its sections as an outline first.
 */
function revealedPaths(
  variables: DeliveryTemplateVariable[],
  matches: (variable: DeliveryTemplateVariable) => boolean,
  narrowed: boolean,
): ReadonlySet<string> {
  const paths = new Set<string>();
  if (!narrowed) {
    return paths;
  }

  for (const variable of variables) {
    if (matches(variable)) {
      for (let holder = holderPath(variable.path); holder !== null; holder = holderPath(holder)) {
        paths.add(holder);
      }
    }
  }
  return paths;
}

/**
 * How a variable no mapping may fill stands out from the ones a mapping fills: a glyph and a tone per writer, so a
 * variable OSDU sets reads differently from one OSDU Delivery writes at a glance. Null for a variable a mapping fills.
 */
function roleVisual(role: DeliveryTemplateRole): { label: string; tone: "info" | "warning"; icon: LucideIcon; textClass: string } | null {
  const label = roleLabel(role);
  if (label === null) {
    return null;
  }

  return role === "Osdu"
    ? { label, tone: "info", icon: Cloud, textClass: "text-info" }
    : { label, tone: "warning", icon: Cog, textClass: "text-warning" };
}

/** The glyph for a variable's shape: an object, a list of objects, a list of values, a whole value, or one value by its type. */
function shapeIcon(variable: DeliveryTemplateVariable): LucideIcon {
  switch (variable.shape) {
    case "Group":
      return Braces;
    case "GroupList":
      return Brackets;
    case "ValueList":
      return List;
    case "Whole":
      return FileJson;
    case "Value":
      if (variable.format === "date" || variable.format === "date-time") {
        return Calendar;
      }
      if (variable.type === "number" || variable.type === "integer") {
        return Hash;
      }
      if (variable.type === "boolean") {
        return ToggleLeft;
      }
      return variable.type === "string" ? Type : CircleDot;
  }
}

function Glyph({ icon: Icon, className }: { icon: LucideIcon; className: string }) {
  return <Icon className={className} />;
}

/**
 * A tree row's content: the shape glyph, the variable's name, its markers, and its shape at the far end. With a mapping
 * to measure against, the name carries how that mapping reaches the variable, and a finding on it stands out in the row.
 */
function VariableLabel({ node }: { node: VariableNode }) {
  const { variable } = node;
  const role = roleVisual(variable.role);
  const covered = useContext(CoverageContext)?.get(variable.path);
  const coverage = covered === undefined ? null : coverageVisual(covered.state);
  const finding = covered?.finding ?? null;
  return (
    <>
      <Glyph icon={shapeIcon(variable)} className={cn("size-3.5 shrink-0", role?.textClass ?? "text-muted-foreground")} />
      <span
        className={cn(
          "min-w-0 truncate font-mono text-[12px]",
          role?.textClass,
          finding?.severity === "error" && "text-destructive",
          !node.matched && "opacity-60",
        )}
        title={variable.path}
        data-testid={`templates-view-variable-${variable.path}`}
        data-role={variable.role}
      >
        {splitPath(variable.path).leaf}
      </span>
      {variable.required && (
        <span className="shrink-0 font-mono text-[12px] font-semibold text-destructive" title="Required" aria-label="Required">*</span>
      )}
      {coverage !== null && (
        <span
          role="img"
          aria-label={coverage.label}
          title={[finding?.message ?? coverage.label, ...(covered?.entry == null ? [] : [entryText(covered.entry)])].join("\n")}
          className={cn("inline-flex shrink-0", finding === null ? coverage.textClass : finding.severity === "error" ? "text-destructive" : "text-warning")}
          data-testid={`templates-view-coverage-${variable.path}`}
          data-coverage={covered?.state}
        >
          <Glyph
            icon={finding === null ? coverage.icon : finding.severity === "error" ? OctagonAlert : TriangleAlert}
            className="size-3"
          />
        </span>
      )}
      {role !== null && (
        <span
          role="img"
          aria-label={role.label}
          title={role.label}
          className={cn("inline-flex shrink-0", role.textClass)}
          data-testid={`templates-view-role-${variable.path}`}
        >
          <Glyph icon={role.icon} className="size-3" />
        </span>
      )}
      {variable.relationships.length > 0 && (
        <span className="inline-flex shrink-0 text-muted-foreground" title={`Points to ${variable.relationships.join(", ")}`}>
          <Link2 className="size-3" />
        </span>
      )}
      <span className="ml-auto shrink-0 pl-3 font-mono text-[11px] font-normal text-muted-foreground">{shapeText(variable)}</span>
    </>
  );
}

function VariableBranch({ node }: { node: VariableNode }) {
  return (
    <TreeNode id={node.variable.path} label={<VariableLabel node={node} />}>
      {node.children.length === 0
        ? undefined
        : node.children.map((child) => <VariableBranch key={child.variable.path} node={child} />)}
    </TreeNode>
  );
}

/** A list of kinds under a caption with its glyph and count: what a variable points to, or the cached types that answer it. */
function KindList({ icon, label, values, testId }: { icon: LucideIcon; label: string; values: string[]; testId: string }) {
  if (values.length === 0) {
    return null;
  }

  return (
    <DetailPair label={<span className="inline-flex items-center gap-1"><Glyph icon={icon} className="size-3" />{label} ({values.length})</span>}>
      <ul className="flex flex-col gap-0.5 font-mono text-[12px]" data-testid={testId}>
        {values.map((value, index) => <li key={`${index}:${value}`} className="break-all">{value}</li>)}
      </ul>
    </DetailPair>
  );
}

/** Everything the template says about the selected variable, and how the mapping being looked at fills it. */
function VariableProperties({ variable, holds }: { variable: DeliveryTemplateVariable; holds: number }) {
  const { leaf } = splitPath(variable.path);
  const role = roleVisual(variable.role);
  const covered = useContext(CoverageContext)?.get(variable.path);
  const coverage = covered === undefined ? null : coverageVisual(covered.state);
  return (
    <div className="flex flex-col gap-4" data-testid="templates-view-properties-variable">
      <div className="flex flex-col gap-1.5">
        <div className="flex min-w-0 items-center gap-1">
          <span className={cn("min-w-0 break-all font-mono text-[14px] font-semibold", role?.textClass)}>{leaf}</span>
          <CopyButton iconOnly label="Copy the path" text={variable.path} testId="templates-view-properties-copy" />
        </div>
        <span className="break-all font-mono text-[12px] text-muted-foreground" data-testid="templates-view-properties-path">
          {variable.path}
        </span>
        {(variable.required || role !== null || variable.nested || coverage !== null) && (
          <div className="flex flex-wrap items-center gap-1.5">
            {variable.required && <Badge variant="secondary" className="text-[10px]" data-testid="templates-view-required">Required</Badge>}
            {coverage !== null && (
              <StatePill tone={coverage.tone} label={coverage.label} icon={coverage.icon} testId="templates-view-properties-coverage" />
            )}
            {/* A holder reads from what it holds, so its own entry being optional is said here, where it is not lost. */}
            {covered?.entry != null && !covered.entry.required && (
              <Badge variant="outline" className="text-[10px]" data-testid="templates-view-properties-optional">optional</Badge>
            )}
            {role !== null && <StatePill tone={role.tone} label={role.label} icon={role.icon} testId="templates-view-properties-role" />}
            {variable.nested && <Badge variant="outline" className="text-[10px]">nested list</Badge>}
          </div>
        )}
      </div>

      {covered?.finding != null && (
        <p
          className={cn("flex items-start gap-1.5 text-[13px]", covered.finding.severity === "error" ? "text-destructive" : "text-warning")}
          data-testid="templates-view-properties-finding"
        >
          {covered.finding.severity === "error"
            ? <OctagonAlert className="mt-0.5 size-3.5 shrink-0" />
            : <TriangleAlert className="mt-0.5 size-3.5 shrink-0" />}
          {covered.finding.message}
        </p>
      )}

      {(variable.title !== null || variable.description !== null) && (
        <div className="flex flex-col gap-1">
          {variable.title !== null && variable.title !== leaf && <div className="text-[13px] font-medium">{variable.title}</div>}
          {variable.description !== null && (
            <p className="whitespace-pre-wrap text-[13px] text-muted-foreground" data-testid="templates-view-properties-description">
              {variable.description}
            </p>
          )}
        </div>
      )}

      <div className="grid grid-cols-2 gap-x-4 gap-y-3">
        <DetailPair label="Shape"><span className="font-mono text-[12px]">{shapeText(variable)}</span></DetailPair>
        <DetailPair label="Written by">{role?.label ?? "A mapping fills it"}</DetailPair>
        {variable.format !== null && <DetailPair label="Format"><span className="font-mono text-[12px]">{variable.format}</span></DetailPair>}
        {variable.keyValueType !== null && (
          <DetailPair label="Free keys of"><span className="font-mono text-[12px]">{variable.keyValueType}</span></DetailPair>
        )}
        {holds > 0 && <DetailPair label="Holds">{holds} variable{holds === 1 ? "" : "s"}</DetailPair>}
        {covered !== undefined && covered.entry === null && (
          <DetailPair label="Filled by">
            {covered.state === "Empty" ? "No entry of the mapping" : "The entries filling what it holds"}
          </DetailPair>
        )}
        {covered?.entry != null && (
          <div className="col-span-2" data-testid="templates-view-properties-entry">
            <DetailPair label="Filled by">
              <EntryDetail row={propertyRow(covered.entry)} target={false} />
            </DetailPair>
          </div>
        )}
        {variable.unitContext !== null && (
          <div className="col-span-2">
            <DetailPair label={<span className="inline-flex items-center gap-1"><Ruler className="size-3" />Unit context</span>}>
              <span className="break-all font-mono text-[12px]">{variable.unitContext}</span>
            </DetailPair>
          </div>
        )}
        {variable.pattern !== null && (
          <div className="col-span-2">
            <DetailPair label="Pattern"><span className="break-all font-mono text-[12px]">{variable.pattern}</span></DetailPair>
          </div>
        )}
      </div>

      {variable.nested && (
        <p className="text-xs text-muted-foreground">A list inside a repeated item: listed for reference, a mapping cannot fill it.</p>
      )}
      <KindList icon={Link2} label="Points to" values={variable.relationships} testId="templates-view-properties-relationships" />
      <KindList icon={DatabaseZap} label="Cached types" values={variable.cacheTypes} testId="templates-view-properties-cache-types" />
    </div>
  );
}

/**
 * A template's variables as the record's tree beside a properties panel: each variable under the one that holds it, the
 * filter and the switches narrowing the tree, and whatever the template says about the selected variable on the right.
 * Given a `mapping` laid over it, every row also says what that mapping fills the variable with and what the checks
 * found there, and the tree opens on what is filled and what the schema requires, which is the overview an author
 * reads first; without one the tree is the template on its own.
 */
export function TemplateVariableExplorer({ variables, mapping }: { variables: DeliveryTemplateVariable[]; mapping?: MappingOverlay }) {
  const [filter, setFilter] = useState("");
  const [showMinted, setShowMinted] = useState(false);
  const [showNested, setShowNested] = useState(false);
  const [showRequired, setShowRequired] = useState(false);
  const [showGaps, setShowGaps] = useState(false);
  const [showMissing, setShowMissing] = useState(false);
  const [showEverything, setShowEverything] = useState(false);
  // What the reader opened, and what they closed while a filter had revealed it; the filter's own reveal sits between.
  // The tree opens fully folded, top level included, so a record's sections read as an outline first.
  const [opened, setOpened] = useState<ReadonlySet<string>>(() => new Set());
  const [closed, setClosed] = useState<ReadonlySet<string>>(() => new Set());
  const [selectedId, setSelectedId] = useState<string | null>(
    () => variables.find((variable) => variable.role === "Mapping" && !variable.nested)?.path ?? null,
  );
  const layout = usePersistentLayout("sqlflow.templates.variables.layout");

  // What the mapping fills, by variable: the entry filling it, and the worst finding on it, an error over a warning.
  const covered = useMemo(() => {
    if (mapping === undefined) {
      return null;
    }

    const worst = new Map<string, MappingDraftIssue>();
    for (const issue of mapping.coverage.issues) {
      if (issue.target !== null && worst.get(issue.target)?.severity !== "error") {
        worst.set(issue.target, issue);
      }
    }

    const byTarget = new Map(mapping.entries.map((entry) => [entry.target, entry]));
    return new Map<string, CoverageView>(
      mapping.coverage.variables.map((variable) => [
        variable.target,
        {
          state: variable.state,
          entry: variable.direct ? byTarget.get(variable.target) ?? null : null,
          finding: worst.get(variable.target) ?? null,
        },
      ]),
    );
  }, [mapping]);

  // An entry filling a free key of an object that takes them is a row of its own, under the object holding it.
  const listed = useMemo(
    () => (mapping === undefined ? variables : withKeyVariables(variables, mapping.entries)),
    [variables, mapping],
  );

  // What the whole mapping comes to, whatever the tree is narrowed to: the variables it fills on every row, and the
  // required ones a finding names, which is the count an author is looking for.
  const counts = useMemo(() => {
    if (mapping === undefined) {
      return null;
    }

    return {
      filled: mapping.coverage.variables.filter((variable) => variable.state !== "Empty").length,
      gaps: new Set(mapping.coverage.issues.map((issue) => issue.target).filter((target) => target !== null)).size,
    };
  }, [mapping]);

  const term = filter.trim().toLowerCase();
  // What a mapping opens on: everything it fills, and every required variable a check names, which is the required
  // ones the record actually needs. A property required inside an object nothing fills is not one of those: the record
  // holds no such object, and listing it would bury the overview. The rest of the template is a switch away.
  // Show missing and Show unfilled both ask about the whole template, so they step outside the overview rather than
  // narrowing what it has already left out.
  const overview = covered !== null && !showEverything && !showGaps && !showMissing;
  const narrowed = term !== "" || showRequired || overview || ((showGaps || showMissing) && covered !== null);
  const matches = useMemo(
    () => (variable: DeliveryTemplateVariable) => (!showRequired || variable.required)
      && (!showMissing || covered === null || covered.get(variable.path)?.finding != null)
      && (!showGaps || covered === null || isGap(covered.get(variable.path)))
      && (!overview || !inOverview(covered?.get(variable.path)))
      && textMatches(variable, term, covered?.get(variable.path)),
    [covered, overview, showGaps, showMissing, showRequired, term],
  );
  const tree = useMemo(
    () => buildTree(
      listed,
      (variable) => (showMinted || variable.role === "Mapping") && (showNested || !variable.nested),
      matches,
    ),
    [listed, showMinted, showNested, matches],
  );
  const byPath = useMemo(() => new Map(listed.map((variable) => [variable.path, variable])), [listed]);
  const holds = useMemo(() => {
    const counts = new Map<string, number>();
    for (const variable of variables) {
      const holder = holderPath(variable.path);
      if (holder !== null) {
        counts.set(holder, (counts.get(holder) ?? 0) + 1);
      }
    }
    return counts;
  }, [variables]);
  const revealed = useMemo(() => revealedPaths(listed, matches, narrowed), [listed, matches, narrowed]);

  const treeState = useMemo<TreeState>(() => {
    const expanded = new Set(opened);
    for (const path of revealed) {
      if (!closed.has(path)) {
        expanded.add(path);
      }
    }

    const setOpen = (id: string, open: boolean) => {
      setOpened((current) => {
        const next = new Set(current);
        if (open) {
          next.add(id);
        } else {
          next.delete(id);
        }
        return next;
      });
      setClosed((current) => {
        const next = new Set(current);
        if (open) {
          next.delete(id);
        } else {
          next.add(id);
        }
        return next;
      });
    };

    return {
      expanded,
      toggle: (id) => setOpen(id, !expanded.has(id)),
      setOpen,
      selectedId,
      select: setSelectedId,
    };
  }, [opened, closed, revealed, selectedId]);

  const changeFilter = (next: string) => {
    setFilter(next);
    // A new filter reveals its own matches afresh; what was closed under the previous one no longer applies.
    setClosed(new Set());
  };

  // Narrowing the tree reveals its own matches the way filter text does, so an earlier close no longer applies.
  const changeRequired = (next: boolean) => {
    setShowRequired(next);
    setClosed(new Set());
  };

  const changeGaps = (next: boolean) => {
    setShowGaps(next);
    setClosed(new Set());
  };

  const changeMissing = (next: boolean) => {
    setShowMissing(next);
    setClosed(new Set());
  };

  const changeEverything = (next: boolean) => {
    setShowEverything(next);
    setClosed(new Set());
  };

  const expandAll = () => {
    setOpened(new Set(holds.keys()));
    setClosed(new Set());
  };

  const collapseAll = () => {
    setOpened(new Set());
    setClosed(new Set(holds.keys()));
  };

  // The tree takes one tab stop: focus arriving from outside moves to the selected row, or the first row.
  const enterTree = (event: FocusEvent<HTMLDivElement>) => {
    if (event.target !== event.currentTarget || event.currentTarget.contains(event.relatedTarget)) {
      return;
    }
    const rows = [...event.currentTarget.querySelectorAll<HTMLElement>("[data-tree-row]")];
    (rows.find((row) => row.getAttribute("aria-selected") === "true") ?? rows[0])?.focus();
  };

  const selected = selectedId === null ? undefined : byPath.get(selectedId);

  return (
    <CoverageContext.Provider value={covered}>
      <div className="flex flex-col gap-3">
        <FilterBar>
          <SearchInput
            value={filter}
            onChange={changeFilter}
            placeholder="Path, description or entity type"
            label="Filter the variables"
            testId="templates-view-variables-filter"
          />
          <Label className="flex items-center gap-2 text-[13px] font-normal" title="Variables no mapping fills: OSDU Delivery writes them, or OSDU sets them">
            <Switch checked={showMinted} onCheckedChange={setShowMinted} data-testid="templates-view-show-minted" />
            Show minted
          </Label>
          <Label className="flex items-center gap-2 text-[13px] font-normal" title="Lists inside a repeated item, which a mapping cannot fill">
            <Switch checked={showNested} onCheckedChange={setShowNested} data-testid="templates-view-show-nested" />
            Show nested
          </Label>
          <Label className="flex items-center gap-2 text-[13px] font-normal" title="Only the variables the schema requires, under the variables that hold them">
            <Switch checked={showRequired} onCheckedChange={changeRequired} data-testid="templates-view-show-required" />
            Show required
          </Label>
          {covered !== null && (
            <Label
              className="flex items-center gap-2 text-[13px] font-normal"
              title="Only what the schema requires of this record and the mapping does not fill on every row"
            >
              <Switch checked={showMissing} onCheckedChange={changeMissing} data-testid="templates-view-show-missing" />
              Show missing
            </Label>
          )}
          {covered !== null && (
            <Label className="flex items-center gap-2 text-[13px] font-normal" title="Only the variables nothing fills, and the ones a check names">
              <Switch checked={showGaps} onCheckedChange={changeGaps} data-testid="templates-view-show-gaps" />
              Show unfilled
            </Label>
          )}
          {covered !== null && (
            <Label
              className="flex items-center gap-2 text-[13px] font-normal"
              title="The whole template, not only what the mapping fills and what the schema requires"
            >
              <Switch checked={showEverything} onCheckedChange={changeEverything} data-testid="templates-view-show-everything" />
              Show everything
            </Label>
          )}
          <div className="flex items-center gap-1 sm:ml-auto">
            <span className="mr-1 text-xs text-muted-foreground" data-testid="templates-view-variables-count">
              {tree.matched} of {listed.length} variables{counts === null ? "" : `, ${counts.filled} filled, ${counts.gaps} required missing`}
            </span>
            <IconAction label="Expand all" icon={<ChevronsUpDown />} onClick={expandAll} data-testid="templates-view-expand-all" />
            <IconAction label="Collapse all" icon={<ChevronsDownUp />} onClick={collapseAll} data-testid="templates-view-collapse-all" />
          </div>
        </FilterBar>
        <div
          className="h-[min(640px,calc(100vh-300px))] min-h-[360px] overflow-hidden rounded-lg border"
          data-testid="templates-view-variables"
        >
          <ResizablePanelGroup orientation="horizontal" defaultLayout={layout.defaultLayout} onLayoutChanged={layout.onLayoutChanged}>
            {/* The properties side gets the larger share: a variable's description is the longest thing either side shows. */}
            <ResizablePanel id="template-variables-tree" defaultSize="45" minSize="30">
              <div
                role="tree"
                aria-label="Variables"
                tabIndex={0}
                onFocus={enterTree}
                className="h-full overflow-y-auto overflow-x-hidden p-1 focus:outline-none"
                data-testid="templates-view-variables-tree"
              >
                <TreeContext.Provider value={treeState}>
                  {tree.roots.length === 0
                    ? (
                      <p className="px-2 py-3 text-[13px] text-muted-foreground" data-testid="templates-view-variables-empty">
                        {showMissing && term === ""
                          ? "Nothing the schema requires of this record is missing: every required variable the record reaches is filled on every row."
                          : "No variable matches the filter."}
                      </p>
                    )
                    : tree.roots.map((node) => <VariableBranch key={node.variable.path} node={node} />)}
                </TreeContext.Provider>
              </div>
            </ResizablePanel>
            <ResizableHandle withHandle />
            <ResizablePanel id="template-variables-properties" defaultSize="55" minSize="30">
              <div className="h-full overflow-y-auto p-3" data-testid="templates-view-properties">
                {selected === undefined
                  ? <p className="text-[13px] text-muted-foreground">Select a variable to see its properties.</p>
                  : <VariableProperties variable={selected} holds={holds.get(selected.path) ?? 0} />}
              </div>
            </ResizablePanel>
          </ResizablePanelGroup>
        </div>
      </div>
    </CoverageContext.Provider>
  );
}
