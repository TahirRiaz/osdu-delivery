import { useMemo, useState, type FocusEvent } from "react";
import {
  Braces,
  Brackets,
  Calendar,
  ChevronsDownUp,
  ChevronsUpDown,
  CircleDot,
  Cloud,
  Cog,
  DatabaseZap,
  FileJson,
  Hash,
  Link2,
  List,
  Ruler,
  ToggleLeft,
  Type,
  type LucideIcon,
} from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Label } from "@/components/ui/label";
import { ResizableHandle, ResizablePanel, ResizablePanelGroup } from "@/components/ui/resizable";
import { Switch } from "@/components/ui/switch";
import { cn } from "@/lib/utils";
import type { DeliveryTemplateRole, DeliveryTemplateVariable } from "../../api/delivery";
import { CopyButton } from "@/components/CopyButton";
import { DetailPair } from "@/components/DetailPair";
import { FilterBar } from "@/components/FilterBar";
import { IconAction } from "@/components/IconAction";
import { SearchInput } from "@/components/SearchInput";
import { StatePill } from "@/components/StatusBadge";
import { TreeContext, TreeNode, type TreeState } from "@/components/Tree";
import { usePersistentLayout } from "@/layout/workbench/usePersistentLayout";
import { holderPath, roleLabel, shapeText, splitPath } from "./templateFormat";

/** One variable in the tree, with the variables it holds that survive the filter. */
interface VariableNode {
  variable: DeliveryTemplateVariable;
  /** True when the variable itself passes the filter; false for a holder kept only because something inside it does. */
  matched: boolean;
  children: VariableNode[];
}

/** Whether a variable matches the filter text: its path, title, description or the entity types it points to. */
function matches(variable: DeliveryTemplateVariable, term: string): boolean {
  return term === ""
    || variable.path.toLowerCase().includes(term)
    || (variable.title ?? "").toLowerCase().includes(term)
    || (variable.description ?? "").toLowerCase().includes(term)
    || variable.relationships.some((relationship) => relationship.toLowerCase().includes(term));
}

/**
 * The variables as a tree: each one under the variable that holds it, in schema order. A variable the switches hide
 * passes its children up to the nearest holder still shown, and a holder with no match inside it is dropped. The API
 * lists parents before their children, so a holder is always placed before anything it holds.
 */
function buildTree(
  variables: DeliveryTemplateVariable[],
  include: (variable: DeliveryTemplateVariable) => boolean,
  term: string,
): { roots: VariableNode[]; matched: number } {
  const placed = new Map<string, VariableNode>();
  const roots: VariableNode[] = [];
  for (const variable of variables) {
    if (!include(variable)) {
      continue;
    }

    const node: VariableNode = { variable, matched: matches(variable, term), children: [] };
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

/** Every holder on the way to a variable that matches the filter, so a match is never hidden inside a collapsed branch. */
function revealedPaths(variables: DeliveryTemplateVariable[], term: string): ReadonlySet<string> {
  const paths = new Set<string>();
  if (term === "") {
    return paths;
  }

  for (const variable of variables) {
    if (matches(variable, term)) {
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

/** A tree row's content: the shape glyph, the variable's name, its markers, and its shape at the far end. */
function VariableLabel({ node }: { node: VariableNode }) {
  const { variable } = node;
  const role = roleVisual(variable.role);
  return (
    <>
      <Glyph icon={shapeIcon(variable)} className={cn("size-3.5 shrink-0", role?.textClass ?? "text-muted-foreground")} />
      <span
        className={cn("min-w-0 truncate font-mono text-[12px]", role?.textClass, !node.matched && "opacity-60")}
        title={variable.path}
        data-testid={`templates-view-variable-${variable.path}`}
        data-role={variable.role}
      >
        {splitPath(variable.path).leaf}
      </span>
      {variable.required && (
        <span className="shrink-0 font-mono text-[12px] font-semibold text-destructive" title="Required" aria-label="Required">*</span>
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

/** Everything the template says about the selected variable. */
function VariableProperties({ variable, holds }: { variable: DeliveryTemplateVariable; holds: number }) {
  const { leaf } = splitPath(variable.path);
  const role = roleVisual(variable.role);
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
        {(variable.required || role !== null || variable.nested) && (
          <div className="flex flex-wrap items-center gap-1.5">
            {variable.required && <Badge variant="secondary" className="text-[10px]" data-testid="templates-view-required">Required</Badge>}
            {role !== null && <StatePill tone={role.tone} label={role.label} icon={role.icon} testId="templates-view-properties-role" />}
            {variable.nested && <Badge variant="outline" className="text-[10px]">nested list</Badge>}
          </div>
        )}
      </div>

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
 */
export function TemplateVariableExplorer({ variables }: { variables: DeliveryTemplateVariable[] }) {
  const [filter, setFilter] = useState("");
  const [showWritten, setShowWritten] = useState(false);
  const [showNested, setShowNested] = useState(false);
  // What the reader opened, and what they closed while a filter had revealed it; the filter's own reveal sits between.
  // The tree opens fully folded, top level included, so a record's sections read as an outline first.
  const [opened, setOpened] = useState<ReadonlySet<string>>(() => new Set());
  const [closed, setClosed] = useState<ReadonlySet<string>>(() => new Set());
  const [selectedId, setSelectedId] = useState<string | null>(
    () => variables.find((variable) => variable.role === "Mapping" && !variable.nested)?.path ?? null,
  );
  const layout = usePersistentLayout("sqlflow.templates.variables.layout");

  const term = filter.trim().toLowerCase();
  const tree = useMemo(
    () => buildTree(variables, (variable) => (showWritten || variable.role === "Mapping") && (showNested || !variable.nested), term),
    [variables, showWritten, showNested, term],
  );
  const byPath = useMemo(() => new Map(variables.map((variable) => [variable.path, variable])), [variables]);
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
  const revealed = useMemo(() => revealedPaths(variables, term), [variables, term]);

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
    <div className="flex flex-col gap-3">
      <FilterBar>
        <SearchInput
          value={filter}
          onChange={changeFilter}
          placeholder="Path, description or entity type"
          label="Filter the variables"
          testId="templates-view-variables-filter"
        />
        <Label className="flex items-center gap-2 text-[13px] font-normal">
          <Switch checked={showWritten} onCheckedChange={setShowWritten} data-testid="templates-view-show-written" />
          Show what OSDU Delivery and OSDU write
        </Label>
        <Label className="flex items-center gap-2 text-[13px] font-normal">
          <Switch checked={showNested} onCheckedChange={setShowNested} data-testid="templates-view-show-nested" />
          Show nested lists
        </Label>
        <div className="flex items-center gap-1 sm:ml-auto">
          <span className="mr-1 text-xs text-muted-foreground" data-testid="templates-view-variables-count">
            {tree.matched} of {variables.length} variables
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
                  ? <p className="px-2 py-3 text-[13px] text-muted-foreground">No variable matches the filter.</p>
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
  );
}
