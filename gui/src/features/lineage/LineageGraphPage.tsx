import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import {
  Background,
  Controls,
  MarkerType,
  MiniMap,
  Panel,
  ReactFlow,
  ReactFlowProvider,
  useNodesState,
  useReactFlow,
  type Edge as FlowEdge,
  type Node as FlowNode,
} from "@xyflow/react";
import { CheckIcon, ChevronRight, ChevronsUpDown, Download, Info, Loader2, Rows3 } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Command,
  CommandEmpty,
  CommandInput,
  CommandItem,
  CommandList,
} from "@/components/ui/command";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Separator } from "@/components/ui/separator";
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
} from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import { lineageApi } from "../../api/endpoints";
import type { LineageEdge, LineageProject, RunScope, WavePipeline } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { TriggerRunDialog } from "../runs/TriggerRunDialog";
import { seriesColor } from "../../theme/branding";
import { useThemeMode } from "../../theme/ThemeModeContext";
import "@xyflow/react/dist/style.css";

const NODE_WIDTH = 200;
const NODE_HEIGHT = 56;
const LAYER_SPACING = 150; // vertical gap between layers (rows, top to bottom); clears NODE_HEIGHT + edge/label room
const NODE_SPACING = 260;  // horizontal gap between nodes within a layer; clears NODE_WIDTH

type GraphView = "flows" | "objects";

/**
 * Reads a semantic design token's current value off the document root, so graph internals that need literal
 * colors (React Flow inline styles, the SVG export) stay in lockstep with index.css in both themes.
 */
function cssColor(name: string, fallback: string): string {
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return value !== "" ? value : fallback;
}

/** The literal accent colors the graph paints with, resolved from the semantic tokens for the active theme. */
interface GraphAccents {
  /** The focused node's fill and ring (`--primary`). */
  focus: string;
  /** Upstream (ancestor) highlight, the violet chart slot. */
  upstream: string;
  /** Downstream (descendant) highlight, the green chart slot. */
  downstream: string;
  /** The dashed outline marking cross-repo flows and object nodes. */
  outline: string;
}

function readAccents(): GraphAccents {
  return {
    focus: cssColor("--primary", "#2f6fce"),
    upstream: cssColor("--chart-7", "#4a3aa7"),
    downstream: cssColor("--chart-2", "#008300"),
    outline: cssColor("--muted-foreground", "#5b6b7f"),
  };
}

function truncate(value: string, max: number): string {
  return value.length > max ? `${value.slice(0, max)}...` : value;
}

/**
 * A top-to-bottom layered (Sugiyama) layout - the layout that makes data flow readable: sources at the top,
 * consumers below, one row per level. Levels come from a longest-path layering over the lineage edges, with
 * cycles broken first (a DFS drops the edges that close a cycle from the level calculation, so a flow that
 * reads a view derived from its own output still gets a real level instead of collapsing onto the top row).
 * Within each layer, several barycenter passes order nodes by the average position of their neighbours to
 * minimize edge crossings. Ported from the DeltaForge forge-graph hierarchical layout; independent of any
 * layout library so the level alignment is exact.
 */
function layeredLayout(nodes: FlowNode[], edges: FlowEdge[]): FlowNode[] {
  if (nodes.length === 0) {
    return nodes;
  }

  const ids = new Set(nodes.map((n) => n.id));
  const outgoing = new Map<string, string[]>();
  const incoming = new Map<string, string[]>();
  for (const id of ids) {
    outgoing.set(id, []);
    incoming.set(id, []);
  }
  for (const edge of edges) {
    if (ids.has(edge.source) && ids.has(edge.target) && edge.source !== edge.target) {
      outgoing.get(edge.source)!.push(edge.target);
      incoming.get(edge.target)!.push(edge.source);
    }
  }

  // De-duplicated adjacency for the level calculation: the objects view records the same source->target pair
  // once per flow, and a duplicate edge must not be mistaken for a cycle by the DFS below.
  const uniqueOut = new Map<string, string[]>();
  const layerInDegree = new Map<string, number>();
  for (const id of ids) {
    uniqueOut.set(id, []);
    layerInDegree.set(id, 0);
  }
  for (const [source, targets] of outgoing) {
    const seen = new Set<string>();
    for (const target of targets) {
      if (!seen.has(target)) {
        seen.add(target);
        uniqueOut.get(source)!.push(target);
        layerInDegree.set(target, layerInDegree.get(target)! + 1);
      }
    }
  }

  // ---- Layer assignment: longest path, with cycles broken first. --------------------------------------------
  // A cyclic subgraph (e.g. a flow that reads the view derived from its own output table) would leave Kahn's
  // queue undrained and pin every node of the cycle to the top row. A DFS therefore marks each edge that closes
  // back onto its own stack; those edges are excluded from the level calculation (they still draw, as upward
  // curves), so the remaining graph is a DAG and every node gets a real level. Nodes with the fewest inbound
  // edges are visited first so cycles break in the natural flow direction.
  const backTargets = new Map<string, Set<string>>();
  const state = new Map<string, number>(); // 1 = on the DFS stack, 2 = finished
  const roots = [...ids].sort((a, b) => layerInDegree.get(a)! - layerInDegree.get(b)!);
  for (const root of roots) {
    if (state.has(root)) {
      continue;
    }
    const stack: Array<{ id: string; next: number }> = [{ id: root, next: 0 }];
    state.set(root, 1);
    while (stack.length > 0) {
      const frame = stack[stack.length - 1];
      const children = uniqueOut.get(frame.id)!;
      if (frame.next < children.length) {
        const child = children[frame.next];
        frame.next += 1;
        const childState = state.get(child);
        if (childState === 1) {
          (backTargets.get(frame.id) ?? backTargets.set(frame.id, new Set()).get(frame.id)!).add(child);
          layerInDegree.set(child, layerInDegree.get(child)! - 1);
        } else if (childState === undefined) {
          state.set(child, 1);
          stack.push({ id: child, next: 0 });
        }
      } else {
        state.set(frame.id, 2);
        stack.pop();
      }
    }
  }

  // Longest-path layering over the acyclic remainder: a node sits one row below its deepest producer, so the
  // levels are consecutive from 0 and every row reads top to bottom in dependency order.
  const layer = new Map<string, number>();
  const queue: string[] = [];
  const sources: string[] = [];
  for (const id of ids) {
    if (layerInDegree.get(id) === 0) {
      layer.set(id, 0);
      queue.push(id);
      sources.push(id);
    }
  }
  for (let head = 0; head < queue.length; head++) {
    const id = queue[head];
    const level = layer.get(id)!;
    for (const child of uniqueOut.get(id)!) {
      if (backTargets.get(id)?.has(child)) {
        continue;
      }
      layer.set(child, Math.max(layer.get(child) ?? 0, level + 1));
      const rem = layerInDegree.get(child)! - 1;
      layerInDegree.set(child, rem);
      if (rem === 0) {
        queue.push(child);
      }
    }
  }

  // Pull each source down to one row above its shallowest consumer. Longest-path layering pins every
  // producer-less node to the top row, which strands a static input consumed deep in the graph (a manually
  // maintained table read by a late transform) rows away from its only consumer, reading as a misplaced
  // root. A true chain head has a consumer on the very next row, so its layer is unchanged; an isolated
  // node has no consumers and stays on the top row.
  for (const id of sources) {
    const back = backTargets.get(id);
    let minChild = Number.POSITIVE_INFINITY;
    for (const child of uniqueOut.get(id)!) {
      if (!back?.has(child)) {
        minChild = Math.min(minChild, layer.get(child) ?? 0);
      }
    }
    if (Number.isFinite(minChild)) {
      layer.set(id, Math.max(layer.get(id)!, minChild - 1));
    }
  }

  const groups = new Map<number, string[]>();
  for (const id of ids) {
    const level = layer.get(id)!;
    (groups.get(level) ?? groups.set(level, []).get(level)!).push(id);
  }
  const layers = [...groups.keys()].sort((a, b) => a - b);

  // ---- Crossing minimization: order each layer by the barycenter of its neighbours, both directions. --------
  const order = new Map<string, number>();
  for (const level of layers) {
    const group = groups.get(level)!;
    group.sort((a, b) =>
      outgoing.get(b)!.length + incoming.get(b)!.length - (outgoing.get(a)!.length + incoming.get(a)!.length));
    group.forEach((id, i) => order.set(id, i));
  }

  const barycenter = (id: string, neighbours: Map<string, string[]>, neighbourLevel: number): number => {
    const relevant = neighbours.get(id)!.filter((n) => layer.get(n) === neighbourLevel);
    if (relevant.length === 0) {
      return order.get(id) ?? 0;
    }
    return relevant.reduce((sum, n) => sum + (order.get(n) ?? 0), 0) / relevant.length;
  };

  for (let pass = 0; pass < 6; pass++) {
    for (let i = 1; i < layers.length; i++) {
      const group = groups.get(layers[i])!;
      group.sort((a, b) => barycenter(a, incoming, layers[i - 1]) - barycenter(b, incoming, layers[i - 1]));
      group.forEach((id, j) => order.set(id, j));
    }
    for (let i = layers.length - 2; i >= 0; i--) {
      const group = groups.get(layers[i])!;
      group.sort((a, b) => barycenter(a, outgoing, layers[i + 1]) - barycenter(b, outgoing, layers[i + 1]));
      group.forEach((id, j) => order.set(id, j));
    }
  }

  // ---- Positions (top-to-bottom): y by layer, x by order within the layer, each layer horizontally centered. -
  const position = new Map<string, { x: number; y: number }>();
  for (const level of layers) {
    const group = groups.get(level)!;
    const width = (group.length - 1) * NODE_SPACING;
    group.forEach((id, i) => {
      position.set(id, { x: i * NODE_SPACING - width / 2, y: level * LAYER_SPACING });
    });
  }

  return nodes.map((node) => ({ ...node, position: position.get(node.id) ?? { x: 0, y: 0 } }));
}

/** A built graph plus the indexes navigation needs: display names for search, per-flow colors for the legend,
 * and directed adjacency for the upstream/downstream focus traversal. */
interface BuiltGraph {
  nodes: FlowNode[];
  edges: FlowEdge[];
  names: Map<string, string>;
  flowColors: Map<string, string>;
  incoming: Map<string, string[]>;
  outgoing: Map<string, string[]>;
  /** Object keys at the depth-capped frontier with un-included downstream consumers; a client can expand these. */
  frontier: Set<string>;
  /** Pipeline id -> its repo id, so repo-aware actions (only a seed-repo flow can be run from here) can be gated. */
  repoOf: Map<string, string>;
  /** How to open the selected node elsewhere in the app. */
  openTarget: (id: string) => { label: string; to: string };
}

/** The BFS closure behind the focus mode: everything upstream (ancestors) and downstream (descendants). */
function reachability(start: string, adjacency: Map<string, string[]>): Set<string> {
  const seen = new Set<string>();
  const queue = [start];
  while (queue.length > 0) {
    const current = queue.pop()!;
    for (const next of adjacency.get(current) ?? []) {
      if (!seen.has(next)) {
        seen.add(next);
        queue.push(next);
      }
    }
  }
  seen.delete(start);
  return seen;
}

function addAdjacency(map: Map<string, string[]>, from: string, to: string): void {
  const list = map.get(from);
  if (list) {
    list.push(to);
  } else {
    map.set(from, [to]);
  }
}

function escapeXml(value: string): string {
  return value.replace(/[&<>"']/g, (ch) =>
    ch === "&" ? "&amp;" : ch === "<" ? "&lt;" : ch === ">" ? "&gt;" : ch === "\"" ? "&quot;" : "&apos;");
}

/**
 * Serialize the built graph to a standalone SVG from the computed layout: rounded node cards with a colored accent
 * bar, bezier edges with arrowheads and relation labels. Built from the graph model rather than React Flow's DOM,
 * so the export is a clean vector (no HTML foreignObject) that opens in any SVG editor, in the current theme's
 * colors. Returns "" for an empty graph.
 */
function buildLineageSvg(
  graph: BuiltGraph,
  palette: { text: string; textMuted: string; paper: string; border: string },
): string {
  if (graph.nodes.length === 0) {
    return "";
  }
  const PAD = 48;
  const xs = graph.nodes.map((node) => node.position.x);
  const ys = graph.nodes.map((node) => node.position.y);
  const minX = Math.min(...xs);
  const minY = Math.min(...ys);
  const width = Math.round(Math.max(...xs) - minX + NODE_WIDTH + PAD * 2);
  const height = Math.round(Math.max(...ys) - minY + NODE_HEIGHT + PAD * 2);
  const ox = PAD - minX;
  const oy = PAD - minY;

  const nodeById = new Map(graph.nodes.map((node) => [node.id, node]));
  const strokeOf = (color: unknown) => (typeof color === "string" ? color : palette.border);
  const markerId = (color: string) =>
    `arrow-${[...color].reduce((hash, ch) => (hash * 31 + ch.charCodeAt(0)) >>> 0, 0).toString(36)}`;

  const colors = new Set<string>();
  for (const edge of graph.edges) {
    colors.add(strokeOf(edge.style?.stroke));
  }
  const defs = [...colors]
    .map((color) =>
      `<marker id="${markerId(color)}" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">`
      + `<path d="M0,0 L10,5 L0,10 z" fill="${color}"/></marker>`)
    .join("");

  const edgeSvg = graph.edges.map((edge) => {
    const from = nodeById.get(edge.source);
    const to = nodeById.get(edge.target);
    if (!from || !to) {
      return "";
    }
    const stroke = strokeOf(edge.style?.stroke);
    const sx = from.position.x + ox + NODE_WIDTH / 2;
    const sy = from.position.y + oy + NODE_HEIGHT;
    const tx = to.position.x + ox + NODE_WIDTH / 2;
    const ty = to.position.y + oy;
    const my = (sy + ty) / 2;
    const path = `<path d="M${sx},${sy} C${sx},${my} ${tx},${my} ${tx},${ty}" fill="none" stroke="${stroke}" stroke-width="1.5" marker-end="url(#${markerId(stroke)})"/>`;
    const label = typeof edge.label === "string" ? edge.label : "";
    if (label === "") {
      return path;
    }
    const lx = (sx + tx) / 2;
    const boxWidth = label.length * 6.5 + 12;
    return path
      + `<rect x="${lx - boxWidth / 2}" y="${my - 9}" width="${boxWidth}" height="18" rx="4" fill="${palette.paper}" stroke="${palette.border}"/>`
      + `<text x="${lx}" y="${my + 4}" text-anchor="middle" font-size="11" fill="${palette.textMuted}">${escapeXml(label)}</text>`;
  }).join("");

  const nodeSvg = graph.nodes.map((node) => {
    const x = node.position.x + ox;
    const y = node.position.y + oy;
    const accent = graph.flowColors.get(node.id) ?? palette.border;
    const name = graph.names.get(node.id) ?? node.id;
    const label = name.length > 26 ? `${name.slice(0, 25)}…` : name;
    return `<rect x="${x}" y="${y}" width="${NODE_WIDTH}" height="${NODE_HEIGHT}" rx="8" fill="${palette.paper}" stroke="${palette.border}"/>`
      + `<rect x="${x}" y="${y}" width="4" height="${NODE_HEIGHT}" rx="2" fill="${accent}"/>`
      + `<text x="${x + 14}" y="${y + NODE_HEIGHT / 2 + 4}" font-size="13" font-weight="600" fill="${palette.text}">${escapeXml(label)}</text>`;
  }).join("");

  return `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" `
    + `font-family="Inter, system-ui, -apple-system, sans-serif">`
    + `<rect x="0" y="0" width="100%" height="100%" fill="${palette.paper}"/>`
    + `<defs>${defs}</defs>${edgeSvg}${nodeSvg}</svg>`;
}

/** The two-line label inside a graph node: name over a muted caption, both truncated to the card. */
function NodeLabel({ name, caption }: { name: string; caption: string }) {
  return (
    <div className="overflow-hidden text-left">
      <div className="truncate text-[13px] font-semibold">{name}</div>
      <div className="truncate text-[11px] text-muted-foreground">{caption}</div>
    </div>
  );
}

interface FocusState {
  id: string;
  upstream: Set<string>;
  downstream: Set<string>;
}

interface CanvasProps {
  graph: BuiltGraph;
  focus: FocusState | null;
  colorMode: "light" | "dark";
  accents: GraphAccents;
  /** Bumps when the node search picks a node, so the canvas centers on it. */
  centerRequest: { id: string; nonce: number; zoom?: number } | null;
  onFocus: (id: string | null) => void;
  onOpen: (id: string) => void;
  /** Right-click on a node: opens the node context menu at the pointer. */
  onNodeContextMenu: (id: string, position: { x: number; y: number }) => void;
  /** Overlay content rendered inside the flow pane (the floating details panel). */
  children?: ReactNode;
}

/**
 * The canvas: positions live in React Flow state (dragging works, layout re-seeds on data change), while the
 * focus styling is applied in place so a focus change never resets positions the user arranged. Single click
 * focuses (upstream/downstream highlight); double click opens the node's page; clicking the pane clears focus.
 */
function GraphCanvas({ graph, focus, colorMode, accents, centerRequest, onFocus, onOpen, onNodeContextMenu, children }: CanvasProps) {
  const [flowNodes, setFlowNodes, onNodesChange] = useNodesState(graph.nodes);
  const view = useReactFlow();

  useEffect(() => {
    setFlowNodes(graph.nodes);
  }, [graph, setFlowNodes]);

  useEffect(() => {
    if (centerRequest === null) {
      return;
    }
    const node = view.getNodes().find((candidate) => candidate.id === centerRequest.id);
    if (node) {
      void view.setCenter(node.position.x + NODE_WIDTH / 2, node.position.y + NODE_HEIGHT / 2, {
        // A deep-link jump requests a firm zoom so the landed-on node dominates the view; the in-graph search and
        // wave chips omit it and keep the user's current zoom (never zooming out below 1).
        zoom: centerRequest.zoom ?? Math.max(view.getZoom(), 1),
        duration: 500,
      });
    }
  }, [centerRequest, view]);

  const styledNodes = useMemo(() => {
    if (focus === null) {
      return flowNodes.map((node) => ({
        ...node,
        className: undefined,
        style: { ...node.style, opacity: 1, boxShadow: undefined, zIndex: undefined },
      }));
    }
    return flowNodes.map((node) => {
      const isFocus = node.id === focus.id;
      const isUpstream = focus.upstream.has(node.id);
      const isDownstream = focus.downstream.has(node.id);
      const related = isFocus || isUpstream || isDownstream;
      const accent = isFocus ? accents.focus : isUpstream ? accents.upstream : accents.downstream;
      return {
        ...node,
        // The focused node gets a bold ring + outer glow (and the one-shot pulse via the class) so it clearly
        // stands out from its upstream/downstream neighbours; related nodes get a thin accent ring; the rest dim.
        className: isFocus ? "sf-focus-node" : undefined,
        style: {
          ...node.style,
          opacity: related ? 1 : 0.15,
          zIndex: isFocus ? 10 : undefined,
          // The focused node is filled solid with the primary color (its label text is forced to the contrast
          // color via the .sf-focus-node rule) plus a ring and outer glow, so it is unmistakable; related nodes
          // keep a thin accent ring, the rest dim.
          backgroundColor: isFocus ? accent : undefined,
          boxShadow: isFocus
            ? `0 0 0 3px ${accent}, 0 0 18px 5px ${accent}`
            : related ? `0 0 0 2px ${accent}` : undefined,
        },
      };
    });
  }, [flowNodes, focus, accents]);

  const styledEdges = useMemo(() => {
    if (focus === null) {
      return graph.edges;
    }
    const closure = new Set<string>([focus.id, ...focus.upstream, ...focus.downstream]);
    return graph.edges.map((edge) => {
      const related = closure.has(edge.source) && closure.has(edge.target);
      return {
        ...edge,
        style: { ...edge.style, opacity: related ? 1 : 0.08 },
        labelStyle: { ...edge.labelStyle, opacity: related ? 1 : 0.08 },
      };
    });
  }, [graph.edges, focus]);

  return (
    <ReactFlow
      nodes={styledNodes}
      edges={styledEdges}
      onNodesChange={onNodesChange}
      onNodeClick={(_, node) => onFocus(node.id)}
      onNodeDoubleClick={(_, node) => onOpen(node.id)}
      onNodeContextMenu={(event, node) => {
        event.preventDefault();
        onNodeContextMenu(node.id, { x: event.clientX, y: event.clientY });
      }}
      onPaneClick={() => onFocus(null)}
      colorMode={colorMode}
      fitView
      minZoom={0.05}
      nodesConnectable={false}
      nodesDraggable
    >
      <Background />
      <Controls />
      <MiniMap pannable zoomable />
      {children}
    </ReactFlow>
  );
}

/**
 * The in-toolbar node search: an always-visible input (cmdk) whose match list drops down while a query is
 * typed. Picking an option focuses and centers that node; the options carry role="option" for accessibility.
 */
function NodeSearch({ options, onPick }: {
  options: { id: string; name: string }[];
  onPick: (id: string) => void;
}) {
  const [query, setQuery] = useState("");
  const open = query.trim() !== "";
  return (
    <Command className="relative w-56 overflow-visible rounded-md border border-input bg-transparent **:data-[slot=command-input-wrapper]:h-8 **:data-[slot=command-input-wrapper]:border-b-0">
      <CommandInput
        value={query}
        onValueChange={setQuery}
        placeholder="Find a node"
        aria-label="Find a node"
        data-testid="graph-node-search"
        className="h-8 py-0 text-[13px]"
      />
      {open && (
        <CommandList
          className="absolute top-full left-0 z-20 mt-1 max-h-72 w-72 overflow-y-auto rounded-md border border-border bg-popover shadow-md"
          // Keep the input focused while an option is clicked, so the list is not dismissed mid-click.
          onMouseDown={(event) => event.preventDefault()}
        >
          <CommandEmpty>No matching node.</CommandEmpty>
          {options.map((option) => (
            <CommandItem
              key={option.id}
              value={option.name}
              onSelect={() => {
                setQuery("");
                onPick(option.id);
              }}
              className="font-mono text-[12px]"
            >
              {option.name}
            </CommandItem>
          ))}
        </CommandList>
      )}
    </Command>
  );
}

/**
 * The searchable (repo, project) scope picker: a combobox trigger showing the current selection, with the
 * full project list (repo, flow count) filterable in a popover. Clearing returns to the pick-a-project state.
 */
function ProjectSelect({ items, selected, onSelect }: {
  items: LineageProject[];
  selected: LineageProject | null;
  onSelect: (option: LineageProject | null) => void;
}) {
  const [open, setOpen] = useState(false);
  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button
          variant="outline"
          size="sm"
          role="combobox"
          aria-expanded={open}
          aria-label="Select a project"
          data-testid="graph-project-select"
          className="h-8 min-w-[240px] justify-between font-normal"
        >
          {selected !== null
            ? <span className="truncate">{selected.repoName} / {selected.project}</span>
            : <span className="text-muted-foreground">Select a project</span>}
          <ChevronsUpDown className="text-muted-foreground" />
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="w-80 p-0">
        <Command>
          <CommandInput placeholder="Search projects" />
          <CommandList>
            <CommandEmpty>No projects found.</CommandEmpty>
            {selected !== null && (
              <CommandItem
                value="clear-project-selection"
                onSelect={() => {
                  setOpen(false);
                  onSelect(null);
                }}
                className="text-muted-foreground"
              >
                Clear selection
              </CommandItem>
            )}
            {items.map((option) => {
              const isSelected = selected !== null
                && selected.repoId === option.repoId && selected.project === option.project;
              return (
                <CommandItem
                  key={`${option.repoId}:${option.project}`}
                  value={`${option.repoName} / ${option.project}`}
                  onSelect={() => {
                    setOpen(false);
                    onSelect(option);
                  }}
                >
                  <div className="min-w-0 flex-1 overflow-hidden">
                    <div className="truncate text-[13px]">{option.project}</div>
                    <div className="truncate text-xs text-muted-foreground">
                      {option.repoName} · {option.flowCount} {option.flowCount === 1 ? "flow" : "flows"}
                    </div>
                  </div>
                  {isSelected && <CheckIcon className="shrink-0" />}
                </CommandItem>
              );
            })}
          </CommandList>
        </Command>
      </PopoverContent>
    </Popover>
  );
}

/** A legend/wave chip: secondary pill with the flow's series color as a left accent bar. */
const chipClass = "inline-flex max-w-full items-center truncate rounded-sm bg-secondary py-0.5 pl-1.5 pr-2 font-mono text-[11px] text-secondary-foreground";

/**
 * The repo-scoped lineage graph, built for two things above all: a tree-based (layered, top-to-bottom) layout
 * that reads as data flow, and navigation: click to focus a node and light up everything upstream (purple) and
 * downstream (green) of it, search to jump to a node, double click (or the panel button) to open it. Two views
 * share the canvas: Flows (pipelines AND the physical objects they read/write, so a table is a node between its
 * producer and consumers) and Objects (tables/files connected object-to-object by the flows that move data
 * between them, edges colored per flow). Deep-linkable via ?repoId= and ?view=.
 */
export default function LineageGraphPage() {
  const navigate = useNavigate();
  const { mode } = useThemeMode();
  const [searchParams, setSearchParams] = useSearchParams();
  const repoId = searchParams.get("repoId") ?? "";
  const project = searchParams.get("project") ?? "";
  const graphView: GraphView = searchParams.get("view") === "objects" ? "objects" : "flows";
  // A deep-link (from search) can target a node to focus: an object key or a pipeline id. It also seeds the graph:
  // rather than a separate whole-repo drawing path, the focused node is passed as an expand seed so the same
  // project-graph closure draws that node's local upstream/downstream context.
  const focusParam = searchParams.get("focus") ?? "";
  // Frontier nodes the user expanded, held in the URL so the deeper graph is deep-linkable and survives a reload.
  const expand = useMemo(() => searchParams.getAll("expand"), [searchParams]);
  const [focus, setFocus] = useState<FocusState | null>(null);
  const [centerRequest, setCenterRequest] = useState<{ id: string; nonce: number; zoom?: number } | null>(null);
  const [panelOpen, setPanelOpen] = useState(true);
  // Right-click context menu on a node, and the node whose script is open in the sheet. A flow node has two
  // scripts: its authored YAML (the default) and, for an sp flow, the SQL of the procedure it executes
  // (view "object"); an object node has only its SQL.
  const [nodeMenu, setNodeMenu] = useState<{ id: string; x: number; y: number } | null>(null);
  const [scriptTarget, setScriptTarget] = useState<{ key: string; view?: "object" } | null>(null);
  // A flow node's Run action opens the trigger dialog prefilled with that flow and the chosen scope.
  const [runDialog, setRunDialog] = useState<{ flowName: string; scope: RunScope } | null>(null);

  // The literal accent colors, re-read when the theme flips so inline node styles track the tokens.
  const accents = useMemo(() => readAccents(), [mode]); // eslint-disable-line react-hooks/exhaustive-deps

  const scriptQuery = useQuery({
    queryKey: ["lineage-node-script", scriptTarget?.key, scriptTarget?.view ?? ""],
    queryFn: () => {
      const target = scriptTarget as { key: string; view?: "object" };
      return lineageApi.script(target.key, target.view);
    },
    enabled: scriptTarget !== null,
  });

  // Every (repo, project) pair, for the searchable scope picker. The graph is seeded from a project, not a repo.
  const projectsQuery = useQuery({
    queryKey: ["lineage-projects"],
    queryFn: () => lineageApi.projects(),
  });

  // A deep-link onto a node (an object key or pipeline id) seeds the graph on that node when no project is chosen,
  // so a search jump into lineage draws the node's own closure via the same endpoint. A pipeline deep-link may carry
  // its repoId too; that is fine (it only scopes an unused project lookup), the focus node still drives the graph.
  const focusSeed = project === "" && focusParam !== "" ? focusParam : "";
  const activeExpand = useMemo(
    () => (focusSeed === "" ? expand : [focusSeed, ...expand]),
    [focusSeed, expand],
  );
  const graphEnabled = (repoId !== "" && project !== "") || focusSeed !== "";

  // The one graph query: a project's cross-repo downstream closure (or a focused node's local context). Both the
  // Flows and Objects views build from this single payload; the walk crosses repos freely via global object keys.
  const projectGraph = useQuery({
    queryKey: ["lineage-project-graph", repoId, project, activeExpand.join("")],
    enabled: graphEnabled,
    queryFn: () => lineageApi.projectGraph({
      repoId: repoId || undefined,
      project: project || undefined,
      expand: activeExpand,
    }),
  });

  const pipelines = projectGraph.data?.pipelines;
  const objectEdgesData = projectGraph.data?.edges;
  const frontierSet = useMemo(() => new Set(projectGraph.data?.frontier ?? []), [projectGraph.data]);

  // Each flow node's kind, for the script actions: every flow offers its YAML, and an sp flow additionally
  // offers the SQL of the procedure it executes. Object keys never collide with pipeline ids (GUIDs), so this
  // map also answers "is this node a flow".
  const flowKindById = useMemo(() => {
    const map = new Map<string, string>();
    for (const pipeline of pipelines ?? []) {
      map.set(pipeline.id, pipeline.kind);
    }
    return map;
  }, [pipelines]);

  const setParam = useCallback((key: string, value: string) => {
    setSearchParams((previous) => {
      const next = new URLSearchParams(previous);
      if (value === "") {
        next.delete(key);
      } else {
        next.set(key, value);
      }
      return next;
    }, { replace: true });
  }, [setSearchParams]);

  // Pick a (repo, project) scope: set both params together and drop any prior focus/expand so the new project draws
  // from its own seed rather than inheriting the previous graph's node expansions.
  const selectProject = useCallback((option: LineageProject | null) => {
    setSearchParams((previous) => {
      const next = new URLSearchParams(previous);
      next.delete("focus");
      next.delete("expand");
      if (option === null) {
        next.delete("repoId");
        next.delete("project");
      } else {
        next.set("repoId", option.repoId);
        next.set("project", option.project);
      }
      return next;
    }, { replace: true });
  }, [setSearchParams]);

  // Expand a frontier object: append it to ?expand= so the walk continues past it, deep-linkably.
  const expandNode = useCallback((id: string) => {
    setSearchParams((previous) => {
      const next = new URLSearchParams(previous);
      if (!next.getAll("expand").includes(id)) {
        next.append("expand", id);
      }
      return next;
    }, { replace: true });
  }, [setSearchParams]);

  const projectItems = projectsQuery.data ?? [];
  const selectedProject = useMemo(
    () => projectItems.find((item) => item.repoId === repoId && item.project === project) ?? null,
    [projectItems, repoId, project],
  );

  // The waves for the flows view's batch filter and details panel: the closure's pipelines grouped by their own
  // execution wave. Waves are a per-repo plan, so across repos a shared "Wave N" only groups flows that each sit at
  // that wave in their own repo; it stays a useful batch label without pretending the number is estate-global.
  const sortedWaves = useMemo(() => {
    if (!pipelines) {
      return [] as { wave: number; pipelines: WavePipeline[] }[];
    }
    const byWave = new Map<number, WavePipeline[]>();
    for (const pipeline of pipelines) {
      const list = byWave.get(pipeline.wave) ?? byWave.set(pipeline.wave, []).get(pipeline.wave)!;
      list.push({ id: pipeline.id, name: pipeline.name, kind: pipeline.kind });
    }
    const rank = (wave: number) => (wave === -1 ? Number.MAX_SAFE_INTEGER : wave);
    return [...byWave.entries()]
      .map(([wave, group]) => ({ wave, pipelines: group }))
      .sort((a, b) => rank(a.wave) - rank(b.wave));
  }, [pipelines]);

  // The wave (batch) top filter, read from ?wave= and validated against the repo's actual waves so a stale value
  // (e.g. after switching repos) simply falls back to "all waves" instead of drawing an empty graph.
  const rawWave = searchParams.get("wave");
  const selectedWave = useMemo(() => {
    if (rawWave === null || rawWave === "") {
      return null;
    }
    const parsed = Number(rawWave);
    return sortedWaves.some((wave) => wave.wave === parsed) ? parsed : null;
  }, [rawWave, sortedWaves]);

  // ---- Flows view: pipelines AND the physical objects they move data through, all as nodes -------------------
  // A pipeline reads and writes objects, so a physical table (Fact_OrderSummary, a mart) is its OWN node sitting
  // between the flow that produces it and the flow(s) that consume it - not a label collapsed onto one edge. A
  // procedure that writes several tables therefore gets one node and one edge per table. A view is wired to its
  // base table (not the file the flow read), exactly as in the objects view.
  const flowsGraph = useMemo<BuiltGraph | null>(() => {
    if (!pipelines || !objectEdgesData) {
      return null;
    }

    const names = new Map<string, string>();
    const flowColors = new Map<string, string>();
    const repoOf = new Map<string, string>();
    const incoming = new Map<string, string[]>();
    const outgoing = new Map<string, string[]>();
    const objectNodeIds = new Set<string>();
    const nodes: FlowNode[] = [];
    const edges: FlowEdge[] = [];
    const seenEdges = new Set<string>();
    const outline = cssColor("--muted-foreground", "#5b6b7f");
    const frontierAccent = cssColor("--chart-7", "#4a3aa7");
    const objectAccent = cssColor("--chart-2", "#008300");

    // Stable per-pipeline accent color and repo across the whole closure first, keyed by the pipeline id (unique
    // across repos, unlike a flow name), so a pipeline keeps its color and repo whether or not the wave filter is
    // applied (the filter only changes which nodes are drawn).
    let colorIndex = 0;
    for (const pipeline of pipelines) {
      if (!flowColors.has(pipeline.id)) {
        flowColors.set(pipeline.id, seriesColor(colorIndex));
        colorIndex += 1;
      }
      repoOf.set(pipeline.id, pipeline.repoId);
    }

    // Pipeline nodes, restricted to the selected wave when the batch filter is set. A flow from a repo other than
    // the seed project's repo (a cross-repo downstream hop) gets a dashed outline and its repo in the caption, so
    // where data crosses a repo boundary is legible without turning the graph into a patchwork.
    const drawn = selectedWave === null
      ? pipelines
      : pipelines.filter((pipeline) => pipeline.wave === selectedWave);
    for (const pipeline of drawn) {
      if (names.has(pipeline.id)) {
        continue;
      }
      const color = flowColors.get(pipeline.id)!;
      const crossRepo = repoId !== "" && pipeline.repoId !== repoId;
      const base = pipeline.wave >= 0 ? `${pipeline.kind}, wave ${pipeline.wave}` : pipeline.kind;
      names.set(pipeline.id, pipeline.name);
      nodes.push({
        id: pipeline.id,
        position: { x: 0, y: 0 },
        data: {
          label: <NodeLabel name={pipeline.name} caption={crossRepo ? `${base} · ${pipeline.repoName}` : base} />,
        },
        style: crossRepo
          ? {
              width: NODE_WIDTH,
              height: NODE_HEIGHT,
              padding: 8,
              borderRadius: 8,
              border: "1px dashed",
              borderColor: outline,
              borderLeft: `5px solid ${color}`,
            }
          : {
              width: NODE_WIDTH,
              height: NODE_HEIGHT,
              padding: 8,
              borderRadius: 8,
              borderLeft: `5px solid ${color}`,
            },
      });
    }

    // Classify the object edges (as in the objects view): a flow's reads/writes are data movement; a
    // module-derived read connects a VIEW to its base table; a `Requires` (a procedure a flow executes) is a
    // code dependency, not data, so it is excluded here (only the procedure's data reads/writes show). Edges are
    // grouped by their pipeline id (present on every flow fact), so a name shared across repos never collides.
    const nameByKey = new Map<string, string>();
    const locationByKey = new Map<string, string>();
    const kindByKey = new Map<string, string>();
    const writtenKeys = new Set<string>();
    const writeOwner = new Map<string, string>();
    const readersByObject = new Map<string, string[]>();
    const moduleReads = new Map<string, Set<string>>();
    const byPipeline = new Map<string, { reads: LineageEdge[]; writes: LineageEdge[] }>();
    for (const edge of objectEdgesData) {
      nameByKey.set(edge.objectKey, edge.objectName);
      if (edge.objectDatabase !== null || edge.objectSchema !== null) {
        locationByKey.set(edge.objectKey, [edge.objectDatabase, edge.objectSchema].filter(Boolean).join("."));
      }
      if (edge.objectKind && !kindByKey.has(edge.objectKey)) {
        kindByKey.set(edge.objectKey, edge.objectKind);
      }
      if (edge.pipelineId) {
        const group = byPipeline.get(edge.pipelineId)
          ?? byPipeline.set(edge.pipelineId, { reads: [], writes: [] }).get(edge.pipelineId)!;
        if (edge.relation === "Reads") {
          group.reads.push(edge);
          addAdjacency(readersByObject, edge.objectKey, edge.pipelineId);
        } else if (edge.relation === "Writes" || edge.relation === "Creates") {
          group.writes.push(edge);
          writtenKeys.add(edge.objectKey);
          if (!writeOwner.has(edge.objectKey)) {
            writeOwner.set(edge.objectKey, edge.pipelineId);
          }
        }
      } else if (edge.viaModule && edge.relation === "Reads") {
        (moduleReads.get(edge.viaModule) ?? moduleReads.set(edge.viaModule, new Set()).get(edge.viaModule)!)
          .add(edge.objectKey);
      }
    }
    // A module with body reads is a view worth wiring when a pipeline maintains it (the generated transform
    // view) OR the registry knows it as a View (a DB-managed view - a fact/dim or compatibility view no
    // pipeline writes). Without the registry kind, an unwritten module could be a procedure, which is a code
    // dependency and stays out of the data-flow drawing.
    const viewKeys = new Set([...moduleReads.keys()]
      .filter((key) => writtenKeys.has(key) || kindByKey.get(key) === "View"));

    const objectKind = (key: string): string => {
      const serverRef = key.includes("|") ? key.slice(0, key.indexOf("|")) : "";
      if (serverRef === "file") {
        return "file";
      }
      const known = kindByKey.get(key);
      if (known && known !== "Unknown") {
        return known.toLowerCase();
      }
      return viewKeys.has(key) ? "view" : "table";
    };
    const ensureObject = (key: string) => {
      if (names.has(key)) {
        return;
      }
      objectNodeIds.add(key);
      names.set(key, nameByKey.get(key) ?? key);
      // The caption places the object: its kind plus where it lives (database.schema); a file has no location. A
      // frontier object (downstream was cut by the depth cap) says so, and a heavier border invites expanding it.
      const location = locationByKey.get(key);
      const isFrontier = frontierSet.has(key);
      const base = location ? `${objectKind(key)} · ${location}` : objectKind(key);
      nodes.push({
        id: key,
        position: { x: 0, y: 0 },
        data: {
          label: (
            <NodeLabel
              name={nameByKey.get(key) ?? key}
              caption={isFrontier ? `${base} · more downstream` : base}
            />
          ),
        },
        style: {
          width: NODE_WIDTH,
          height: NODE_HEIGHT,
          padding: 8,
          borderRadius: 20,
          border: isFrontier ? "2px solid" : "1px dashed",
          borderColor: isFrontier ? frontierAccent : objectAccent,
        },
      });
    };

    const addEdge = (source: string, target: string, color: string, label: string) => {
      if (source === target) {
        return;
      }
      const id = `${source}|${target}`;
      if (seenEdges.has(id)) {
        return;
      }
      seenEdges.add(id);
      edges.push({
        id,
        source,
        target,
        label,
        markerEnd: { type: MarkerType.ArrowClosed, color },
        style: { stroke: color, strokeWidth: 1.5 },
      });
      addAdjacency(outgoing, source, target);
      addAdjacency(incoming, target, source);
    };

    // pipeline -> each table it writes (a view is skipped; it is wired to its base table below), and object ->
    // pipeline it reads. Every physical table a flow produces is therefore its own node between producer and
    // consumers, and the consumers can be flows from other repos.
    for (const [pipelineId, group] of byPipeline) {
      // Skip a pipeline not drawn (filtered out by the wave selection); its edges wait for that batch.
      if (!names.has(pipelineId)) {
        continue;
      }
      const color = flowColors.get(pipelineId) ?? seriesColor(colorIndex++);
      for (const write of group.writes) {
        if (viewKeys.has(write.objectKey)) {
          continue;
        }
        ensureObject(write.objectKey);
        addEdge(pipelineId, write.objectKey, color, write.relation === "Creates" ? "creates" : "writes");
      }
      for (const read of group.reads) {
        ensureObject(read.objectKey);
        addEdge(read.objectKey, pipelineId, color, "reads");
      }
    }

    // A view node is wired to its PARENT TABLE (the module read): coloured like the flow that maintains it, or
    // with the neutral object accent for a DB-managed view (a fact/dim or compatibility view no pipeline
    // writes), whose wiring is what connects it into the chain instead of dangling as a root.
    for (const viewKey of viewKeys) {
      const producerId = writeOwner.get(viewKey);
      // Under a wave filter, only keep views with a drawn neighbour: the maintaining pipeline for an owned
      // view, any drawn reader for a DB-managed one.
      if (selectedWave !== null) {
        const producerDrawn = producerId !== undefined && names.has(producerId);
        const readerDrawn = (readersByObject.get(viewKey) ?? []).some((id) => names.has(id));
        if (!producerDrawn && !readerDrawn) {
          continue;
        }
      }
      const color = producerId ? (flowColors.get(producerId) ?? seriesColor(colorIndex++)) : objectAccent;
      ensureObject(viewKey);
      for (const baseKey of moduleReads.get(viewKey) ?? []) {
        ensureObject(baseKey);
        addEdge(baseKey, viewKey, color, "view");
      }
    }

    return {
      nodes: layeredLayout(nodes, edges),
      edges,
      names,
      flowColors,
      incoming,
      outgoing,
      frontier: frontierSet,
      repoOf,
      openTarget: (id) => (objectNodeIds.has(id)
        ? { label: "Open in explorer", to: `/lineage/objects?name=${encodeURIComponent(names.get(id) ?? id)}` }
        : { label: "Open pipeline", to: `/pipelines/${id}` }),
    };
    // The accent colors are theme-scoped custom properties; rebuilding on mode change keeps them in sync.
  }, [pipelines, objectEdgesData, frontierSet, selectedWave, repoId, mode]); // eslint-disable-line react-hooks/exhaustive-deps

  // ---- Objects view: tables/files as nodes, "flow moves data from A to B" as edges, colored per flow -------------
  const objectsGraph = useMemo<BuiltGraph | null>(() => {
    if (graphView !== "objects" || !objectEdgesData) {
      return null;
    }

    const names = new Map<string, string>();
    const flowColors = new Map<string, string>();
    const incoming = new Map<string, string[]>();
    const outgoing = new Map<string, string[]>();
    const nodes: FlowNode[] = [];
    const edges: FlowEdge[] = [];
    const seenEdges = new Set<string>();
    const frontierAccent = cssColor("--chart-7", "#4a3aa7");

    const ensureNode = (key: string, name: string) => {
      if (names.has(key)) {
        return;
      }
      names.set(key, name);
      const serverRef = key.includes("|") ? key.slice(0, key.indexOf("|")) : "";
      // The caption places the object: database.schema when the registry knows it, else the server reference
      // (a file just says "file"). A frontier object (downstream cut by the depth cap) says so and gets a heavier
      // border, inviting the user to expand it.
      const isFrontier = frontierSet.has(key);
      const base = serverRef === "file"
        ? "file"
        : locationByKey.get(key) ?? truncate(serverRef, 30);
      nodes.push({
        id: key,
        position: { x: 0, y: 0 },
        data: {
          label: <NodeLabel name={name} caption={isFrontier ? `${base} · more downstream` : base} />,
        },
        style: isFrontier
          ? { width: NODE_WIDTH, height: NODE_HEIGHT, padding: 8, borderRadius: 8, border: "2px solid", borderColor: frontierAccent }
          : { width: NODE_WIDTH, height: NODE_HEIGHT, padding: 8, borderRadius: 8 },
      });
    };

    // Classify every edge. A flow's own reads/writes drive the data movement (data goes from what a flow reads
    // to the tables it writes). A module-derived read (no flow, carries a viaModule) is the module body reading
    // a base object - for a VIEW this is exactly how it connects to its PARENT TABLE, which is the physical data
    // path (file -> raw table -> view -> target), not file -> view. A `Requires` edge is a code/existence
    // dependency (a procedure a flow executes), not data movement, so it is excluded.
    const byFlow = new Map<string, { reads: LineageEdge[]; writes: LineageEdge[] }>();
    const nameByKey = new Map<string, string>();
    const locationByKey = new Map<string, string>(); // object -> "database.schema" from the global registry
    const kindByKey = new Map<string, string>();    // object -> its catalog kind (Table, View, ...)
    const writtenKeys = new Set<string>();          // objects a flow writes/creates (real data targets)
    const writeOwner = new Map<string, string>();   // object -> the flow that produces it
    const moduleReads = new Map<string, Set<string>>(); // module (view/proc) -> base objects its body reads

    for (const edge of objectEdgesData) {
      nameByKey.set(edge.objectKey, edge.objectName);
      if (edge.objectDatabase !== null || edge.objectSchema !== null) {
        locationByKey.set(edge.objectKey, [edge.objectDatabase, edge.objectSchema].filter(Boolean).join("."));
      }
      if (edge.objectKind && !kindByKey.has(edge.objectKey)) {
        kindByKey.set(edge.objectKey, edge.objectKind);
      }
      if (edge.flow) {
        let group = byFlow.get(edge.flow);
        if (!group) {
          group = { reads: [], writes: [] };
          byFlow.set(edge.flow, group);
        }
        if (edge.relation === "Reads") {
          group.reads.push(edge);
        } else if (edge.relation === "Writes" || edge.relation === "Creates") {
          group.writes.push(edge);
          writtenKeys.add(edge.objectKey);
          if (!writeOwner.has(edge.objectKey)) {
            writeOwner.set(edge.objectKey, edge.flow);
          }
        }
      } else if (edge.viaModule && edge.relation === "Reads") {
        (moduleReads.get(edge.viaModule) ?? moduleReads.set(edge.viaModule, new Set()).get(edge.viaModule)!)
          .add(edge.objectKey);
      }
    }

    // A VIEW is a module (its body reads base objects) that a flow writes/creates (the generated transform
    // view) OR that the registry knows as a View (a DB-managed fact/dim or compatibility view no flow writes).
    // A PROCEDURE is a module a flow only requires, so it stays out of the data-flow view. A view's input is
    // its base table, so it is NOT wired to whatever the producing flow read (the file); it is wired below.
    const viewKeys = new Set([...moduleReads.keys()]
      .filter((key) => writtenKeys.has(key) || kindByKey.get(key) === "View"));

    let colorIndex = 0;
    const flowColor = (flow: string): string => {
      let color = flowColors.get(flow);
      if (color === undefined) {
        color = seriesColor(colorIndex);
        colorIndex += 1;
        flowColors.set(flow, color);
      }
      return color;
    };

    const addEdge = (source: string, target: string, flow: string, color: string) => {
      if (source === target) {
        return;
      }
      const id = `${flow}|${source}|${target}`;
      if (seenEdges.has(id)) {
        return;
      }
      seenEdges.add(id);
      ensureNode(source, nameByKey.get(source) ?? source);
      ensureNode(target, nameByKey.get(target) ?? target);
      edges.push({
        id,
        source,
        target,
        label: truncate(flow, 40),
        markerEnd: { type: MarkerType.ArrowClosed, color },
        style: { stroke: color, strokeWidth: 1.5 },
      });
      addAdjacency(outgoing, source, target);
      addAdjacency(incoming, target, source);
    };

    // 1. Data movement: each object a flow reads -> each real table it writes. A view target is skipped here;
    //    its data comes from its base table (added in step 2), so it is never wired to the file the flow read.
    for (const [flow, group] of byFlow) {
      const color = flowColor(flow);
      for (const read of group.reads) {
        for (const write of group.writes) {
          if (viewKeys.has(write.objectKey)) {
            continue;
          }
          addEdge(read.objectKey, write.objectKey, flow, color);
        }
      }
    }

    // 2. View derivation: base table -> view. Attributed to the flow that maintains the view (same color as
    //    its other work) when one exists; a DB-managed view (no writing flow) is wired with a plain "view"
    //    label so it still connects to its parent table instead of dangling as a root.
    for (const viewKey of viewKeys) {
      const owner = writeOwner.get(viewKey) ?? "view";
      const color = flowColor(owner);
      for (const baseKey of moduleReads.get(viewKey) ?? []) {
        addEdge(baseKey, viewKey, owner, color);
      }
    }

    return {
      nodes: layeredLayout(nodes, edges),
      edges,
      names,
      flowColors,
      incoming,
      outgoing,
      frontier: frontierSet,
      repoOf: new Map<string, string>(),
      openTarget: (id) => ({
        label: "Open in explorer",
        to: `/lineage/objects?name=${encodeURIComponent(names.get(id) ?? id)}`,
      }),
    };
  }, [graphView, objectEdgesData, frontierSet, mode]); // eslint-disable-line react-hooks/exhaustive-deps

  const graph = graphView === "flows" ? flowsGraph : objectsGraph;

  // Focus and centering are per graph; switching project or view resets them. Expanding a frontier node does not
  // (it grows the same graph), so ?expand= is deliberately not a dependency here.
  useEffect(() => {
    setFocus(null);
    setCenterRequest(null);
  }, [repoId, project, graphView]);

  const focusNode = useCallback((id: string | null) => {
    if (id === null || graph === null) {
      setFocus(null);
      return;
    }
    setFocus({
      id,
      upstream: reachability(id, graph.incoming),
      downstream: reachability(id, graph.outgoing),
    });
  }, [graph]);

  // Apply a deep-linked focus once the drawn graph actually contains the node (waves/edges have loaded and, for an
  // object hit, the resolved repo has been adopted). Applied once per focus value so the user's later interaction
  // (clicking elsewhere, switching view) is not overridden.
  const appliedFocus = useRef<string | null>(null);
  useEffect(() => {
    if (focusParam === "" || graph === null || appliedFocus.current === focusParam) {
      return;
    }
    if (!graph.names.has(focusParam)) {
      return;
    }
    appliedFocus.current = focusParam;
    focusNode(focusParam);
    // A deep-link jump zooms in firmly on the landed-on node so it dominates the view (the highlight then makes it
    // unmistakable); in-graph search and wave chips keep the user's current zoom instead.
    setCenterRequest((previous) => ({ id: focusParam, nonce: (previous?.nonce ?? 0) + 1, zoom: 1.9 }));
  }, [focusParam, graph, focusNode]);

  const openNode = useCallback((id: string) => {
    if (graph !== null) {
      navigate(graph.openTarget(id).to);
    }
  }, [graph, navigate]);

  const searchOptions = useMemo(
    () => (graph === null ? [] : [...graph.names.entries()].map(([id, name]) => ({ id, name }))
      .sort((a, b) => a.name.localeCompare(b.name))),
    [graph],
  );

  const queryError = [projectsQuery, projectGraph].find((query) => query.isError)?.error;
  const loadingGraph = graphEnabled && projectGraph.isLoading;
  const hasContent = graph !== null && graph.nodes.length > 0;
  // A deep-link onto a node whose closure is still loading, or that came back empty (the node has no lineage, so it
  // is in no graph). Both are gated on a focus seed being what drives the graph (no project chosen).
  const resolvingFocus = focusSeed !== "" && projectGraph.isLoading;
  const focusHasNoRepo = focusSeed !== "" && projectGraph.isSuccess
    && (projectGraph.data?.pipelines.length ?? 0) === 0;
  const repoName = selectedProject?.repoName ?? repoId;

  // Export the drawn graph as an SVG vector of the current layout (nodes, edges, labels) in the active theme,
  // built from the same in-memory graph so it stays a single source of truth.
  const downloadLineage = useCallback(() => {
    if (graph === null) {
      return;
    }
    const svg = buildLineageSvg(graph, {
      text: cssColor("--foreground", "#1d2733"),
      textMuted: cssColor("--muted-foreground", "#5b6b7f"),
      paper: cssColor("--card", "#ffffff"),
      border: cssColor("--border", "#dfe5ee"),
    });
    if (svg === "") {
      return;
    }
    const safeName = (repoName || repoId || "graph").replace(/[^\w.-]+/g, "_");
    const blob = new Blob([svg], { type: "image/svg+xml" });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = `lineage-${safeName}-${graphView}.svg`;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
  }, [graph, graphView, repoId, repoName]);

  // Whether the context-menu node is a runnable flow: a pipeline in the seed repo (only a seed-repo flow can be
  // launched from here; cross-repo nodes and objects get the navigation actions only).
  const menuCanRun = nodeMenu !== null && repoId !== "" && graph !== null
    && graph.openTarget(nodeMenu.id).label === "Open pipeline"
    && graph.repoOf.get(nodeMenu.id) === repoId;

  // The floating details panel: node focus (upstream/downstream trace + open) over the execution waves (flows)
  // or the per-flow edge legend (objects). It rides on the canvas via a React Flow <Panel> so the graph keeps the
  // full width, and collapses to a single button when the user wants the whole canvas.
  const detailsPanel = graph === null ? null : (
    <div
      data-testid="graph-side-panel"
      className="flex max-h-[calc(100vh-140px)] w-[300px] flex-col overflow-hidden rounded-lg border border-border bg-card shadow-sm"
    >
      <div className="flex shrink-0 items-center justify-between border-b border-border px-3 py-1.5">
        <span className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Details</span>
        <Tooltip>
          <TooltipTrigger asChild>
            <Button
              variant="ghost"
              size="icon-xs"
              onClick={() => setPanelOpen(false)}
              data-testid="graph-panel-collapse"
              aria-label="Collapse panel"
            >
              <ChevronRight />
            </Button>
          </TooltipTrigger>
          <TooltipContent>Collapse panel</TooltipContent>
        </Tooltip>
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto p-3">
        {focus !== null && (
          <div data-testid="graph-focus-panel" className="mb-3">
            <div className="truncate font-mono text-[13px] font-semibold">
              {graph.names.get(focus.id) ?? focus.id}
            </div>
            <div className="my-2 flex items-center gap-1.5">
              <Badge variant="secondary" className="gap-1.5">
                <span
                  aria-hidden
                  className="size-2 rounded-full"
                  style={{ backgroundColor: accents.upstream }}
                />
                {focus.upstream.size} upstream
              </Badge>
              <Badge variant="secondary" className="gap-1.5">
                <span
                  aria-hidden
                  className="size-2 rounded-full"
                  style={{ backgroundColor: accents.downstream }}
                />
                {focus.downstream.size} downstream
              </Badge>
            </div>
            <div className="flex flex-wrap items-center gap-1.5">
              <Button size="xs" onClick={() => openNode(focus.id)} data-testid="graph-open-selected">
                {graph.openTarget(focus.id).label}
              </Button>
              <Button variant="ghost" size="xs" onClick={() => setScriptKey(focus.id)} data-testid="graph-view-script">
                View script
              </Button>
              <Button variant="ghost" size="xs" onClick={() => focusNode(null)} data-testid="graph-clear-focus">
                Clear
              </Button>
            </div>
            <Separator className="mt-3" />
          </div>
        )}

        {graphView === "flows" ? (
          <>
            <h3 className="mb-1.5 text-[13px] font-medium">Execution waves</h3>
            {sortedWaves.map((wave) => (
              <div key={wave.wave} className="mb-3">
                <div className="text-xs text-muted-foreground">
                  {wave.wave === -1 ? "Unwaved (lineage not computed)" : `Wave ${wave.wave}`}
                </div>
                <div className="mt-1 flex flex-wrap gap-1" data-testid="wave-list">
                  {wave.pipelines.map((pipeline) => (
                    <button
                      type="button"
                      key={pipeline.id}
                      onClick={() => {
                        focusNode(pipeline.id);
                        setCenterRequest((previous) => ({ id: pipeline.id, nonce: (previous?.nonce ?? 0) + 1 }));
                      }}
                      className={cn(chipClass, "cursor-pointer transition-colors hover:bg-accent")}
                      style={{ borderLeft: `3px solid ${graph.flowColors.get(pipeline.id) ?? "transparent"}` }}
                      data-testid={`wave-pipeline-${pipeline.id}`}
                    >
                      {pipeline.name}
                    </button>
                  ))}
                </div>
              </div>
            ))}
          </>
        ) : (
          <>
            <h3 className="mb-1.5 text-[13px] font-medium">Flows (edge colors)</h3>
            <div className="flex flex-wrap gap-1" data-testid="graph-flow-legend">
              {[...graph.flowColors.entries()].map(([flow, color]) => (
                <span key={flow} className={chipClass} style={{ borderLeft: `3px solid ${color}` }}>
                  {flow}
                </span>
              ))}
            </div>
          </>
        )}
      </div>
    </div>
  );

  return (
    <div
      data-testid="page-lineage-graph"
      // Full-bleed: negative margins cancel the editor's content padding so the canvas runs edge to edge, and
      // the height claims the viewport below the fixed chrome (title bar 36px + tab strip 35px + status bar 22px).
      className="relative -m-4 h-[calc(100vh-93px)] min-w-0 overflow-hidden bg-card md:-m-6"
    >
      {/* The focused node's emphasis: its label text is forced to the primary contrast color (it sits on a solid
          primary fill), and it plays a brief glow pulse when it becomes the focus so the eye lands on it. */}
      <style>{`
        @keyframes sf-focus-pulse {
          0% { filter: drop-shadow(0 0 2px var(--primary)); }
          50% { filter: drop-shadow(0 0 16px var(--primary)); }
          100% { filter: drop-shadow(0 0 2px var(--primary)); }
        }
        .react-flow__node.sf-focus-node { animation: sf-focus-pulse 1.3s ease-in-out 3; }
        .react-flow__node.sf-focus-node div { color: var(--primary-foreground) !important; }
      `}</style>
      {/* The toolbar floats over the canvas (top-left) instead of sitting in a page header, so the graph itself
          fills the whole page; the details panel floats at top-right via a React Flow <Panel>. The toolbar's max
          width leaves a clear column on the right for that panel (wider when it is open, narrow for its collapsed
          icon), so a wrapping toolbar can never slide underneath it. */}
      <div
        data-testid="graph-toolbar"
        className={cn(
          "absolute top-3 left-3 z-10 flex flex-wrap items-center gap-2 rounded-lg border border-border bg-card p-2 shadow-sm",
          panelOpen ? "max-w-[calc(100%-340px)]" : "max-w-[calc(100%-84px)]",
        )}
      >
        <Tooltip>
          <TooltipTrigger asChild>
            <Button
              variant="ghost"
              size="icon-sm"
              onClick={() => navigate("/lineage/objects")}
              aria-label="Open explorer"
              data-testid="open-lineage-objects"
            >
              <Rows3 />
            </Button>
          </TooltipTrigger>
          <TooltipContent>Open explorer</TooltipContent>
        </Tooltip>
        {hasContent && (
          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                variant="ghost"
                size="icon-sm"
                onClick={downloadLineage}
                aria-label="Download SVG"
                data-testid="download-lineage"
              >
                <Download />
              </Button>
            </TooltipTrigger>
            <TooltipContent>Download SVG</TooltipContent>
          </Tooltip>
        )}
        <ToggleGroup
          type="single"
          variant="outline"
          size="sm"
          value={graphView}
          onValueChange={(value) => {
            if (value !== "") {
              setParam("view", value === "flows" ? "" : value);
            }
          }}
          aria-label="Graph view"
          data-testid="graph-view-toggle"
        >
          <ToggleGroupItem value="flows" data-testid="graph-view-flows" className="h-8 px-3 text-xs">
            Flows
          </ToggleGroupItem>
          <ToggleGroupItem value="objects" data-testid="graph-view-objects" className="h-8 px-3 text-xs">
            Objects
          </ToggleGroupItem>
        </ToggleGroup>
        {graphView === "flows" && sortedWaves.length > 0 && (
          <Select
            value={selectedWave === null ? "all" : String(selectedWave)}
            onValueChange={(value) => setParam("wave", value === "all" ? "" : value)}
          >
            <SelectTrigger size="sm" className="h-8 w-[150px]" aria-label="Wave" data-testid="graph-wave-filter">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All waves</SelectItem>
              {sortedWaves.map((wave) => (
                <SelectItem key={wave.wave} value={String(wave.wave)}>
                  {wave.wave === -1 ? "Unwaved" : `Wave ${wave.wave}`}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
        {hasContent && (
          <NodeSearch
            options={searchOptions}
            onPick={(id) => {
              focusNode(id);
              setCenterRequest((previous) => ({ id, nonce: (previous?.nonce ?? 0) + 1 }));
            }}
          />
        )}
        <ProjectSelect items={projectItems} selected={selectedProject} onSelect={selectProject} />
      </div>

      {queryError !== undefined && (
        <div className="absolute top-16 left-3 right-3 z-10 max-w-2xl">
          {isApiError(queryError)
            ? <CorrelationError error={queryError} />
            : <p className="text-[13px] text-destructive">{String(queryError)}</p>}
        </div>
      )}

      {!graphEnabled && !projectsQuery.isError && (
        <div className="absolute inset-0 flex items-center justify-center p-6" data-testid="graph-empty">
          <EmptyState
            title="Pick a project to draw its map"
            description="The lineage graph is seeded from a project (a repo's root folder): pick one above to see its flows, the objects they read and write, and everything downstream, even across repos. Click a node to trace what feeds it and what depends on it."
          />
        </div>
      )}

      {resolvingFocus && !queryError && (
        <div className="absolute inset-0 flex items-center justify-center p-6" data-testid="graph-resolving-focus-box">
          <div className="flex flex-col items-center gap-3" data-testid="graph-resolving-focus">
            <Loader2 className="size-7 animate-spin text-muted-foreground" />
            <p className="text-[13px] text-muted-foreground">Locating the node in the lineage graph...</p>
          </div>
        </div>
      )}

      {loadingGraph && !resolvingFocus && !queryError && (
        <Skeleton className="absolute inset-0 h-full rounded-none" data-testid="graph-loading" />
      )}

      {graphEnabled && !loadingGraph && !queryError && graph !== null && !hasContent && (
        <div className="absolute inset-0 flex items-center justify-center p-6" data-testid="graph-no-lineage">
          {focusSeed !== "" || focusHasNoRepo ? (
            <EmptyState
              data-testid="graph-focus-no-repo"
              title="No lineage references this node yet"
              description="This node has no recorded lineage edges, so nothing feeds it and nothing depends on it. Open it in the object explorer to see its definition and columns."
              action={(
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => navigate("/lineage/objects")}
                  data-testid="graph-focus-open-explorer"
                >
                  <Rows3 />
                  Open explorer
                </Button>
              )}
            />
          ) : (
            <EmptyState
              title="No lineage for this project yet"
              description={graphView === "flows"
                ? "This project has no active flows, or its repo has not been synced. Sync the repo, then come back."
                : "No object edges were recorded for this project. Sync the repo (a connected sync adds the derived tier), then come back."}
            />
          )}
        </div>
      )}

      {graphEnabled && !loadingGraph && !queryError && graph !== null && hasContent && (
        <ReactFlowProvider>
          <GraphCanvas
            graph={graph}
            focus={focus}
            colorMode={mode}
            accents={accents}
            centerRequest={centerRequest}
            onFocus={focusNode}
            onOpen={openNode}
            onNodeContextMenu={(id, position) => setNodeMenu({ id, x: position.x, y: position.y })}
          >
            {panelOpen ? (
              <Panel position="top-right">{detailsPanel}</Panel>
            ) : (
              <Panel position="top-right">
                <Tooltip>
                  <TooltipTrigger asChild>
                    <Button
                      variant="outline"
                      size="icon-sm"
                      onClick={() => setPanelOpen(true)}
                      data-testid="graph-panel-open"
                      aria-label="Show details panel"
                      className="bg-card shadow-sm"
                    >
                      <Info />
                    </Button>
                  </TooltipTrigger>
                  <TooltipContent>Show details</TooltipContent>
                </Tooltip>
              </Panel>
            )}
          </GraphCanvas>
        </ReactFlowProvider>
      )}

      <DropdownMenu
        open={nodeMenu !== null}
        onOpenChange={(open) => {
          if (!open) {
            setNodeMenu(null);
          }
        }}
      >
        {/* An invisible anchor pinned to the right-click position, so the menu opens at the pointer. */}
        <DropdownMenuTrigger asChild>
          <span
            aria-hidden
            className="fixed size-px"
            style={{ top: nodeMenu?.y ?? 0, left: nodeMenu?.x ?? 0 }}
          />
        </DropdownMenuTrigger>
        <DropdownMenuContent align="start" className="w-56">
          <DropdownMenuItem
            data-testid="node-menu-view-script"
            onSelect={() => {
              if (nodeMenu !== null) {
                setScriptKey(nodeMenu.id);
              }
            }}
          >
            View script
          </DropdownMenuItem>
          <DropdownMenuItem
            onSelect={() => {
              if (nodeMenu !== null) {
                focusNode(nodeMenu.id);
              }
            }}
          >
            Trace upstream / downstream
          </DropdownMenuItem>
          {nodeMenu !== null && graph !== null && graph.frontier.has(nodeMenu.id) && (
            <DropdownMenuItem
              data-testid="node-menu-expand"
              onSelect={() => {
                if (nodeMenu !== null) {
                  expandNode(nodeMenu.id);
                }
              }}
            >
              Expand downstream
            </DropdownMenuItem>
          )}
          <DropdownMenuItem
            onSelect={() => {
              if (nodeMenu !== null) {
                openNode(nodeMenu.id);
              }
            }}
          >
            Open details
          </DropdownMenuItem>
          {menuCanRun && graph !== null && nodeMenu !== null && (
            <>
              <DropdownMenuSeparator />
              <DropdownMenuItem
                data-testid="node-menu-run-flow"
                onSelect={() => {
                  const flowName = graph.names.get(nodeMenu.id);
                  if (flowName) {
                    setRunDialog({ flowName, scope: "flow" });
                  }
                }}
              >
                Run flow
              </DropdownMenuItem>
              <DropdownMenuItem
                data-testid="node-menu-run-node"
                onSelect={() => {
                  const flowName = graph.names.get(nodeMenu.id);
                  if (flowName) {
                    setRunDialog({ flowName, scope: "node" });
                  }
                }}
              >
                Run flow + descendants
              </DropdownMenuItem>
              <DropdownMenuItem
                data-testid="node-menu-run-batch"
                onSelect={() => {
                  const flowName = graph.names.get(nodeMenu.id);
                  if (flowName) {
                    setRunDialog({ flowName, scope: "batch" });
                  }
                }}
              >
                Run batch
              </DropdownMenuItem>
            </>
          )}
        </DropdownMenuContent>
      </DropdownMenu>

      <TriggerRunDialog
        open={runDialog !== null}
        onClose={() => setRunDialog(null)}
        repoId={repoId || undefined}
        flowName={runDialog?.flowName}
        scope={runDialog?.scope}
      />

      <Sheet
        open={scriptKey !== null}
        onOpenChange={(open) => {
          if (!open) {
            setScriptKey(null);
          }
        }}
      >
        {/* Focus stays outside Monaco so Escape reaches the sheet, not the editor. */}
        <SheetContent side="right" className="w-full gap-0 sm:max-w-[760px]" onOpenAutoFocus={(event) => event.preventDefault()}>
          <SheetHeader className="border-b border-border pr-10">
            <SheetTitle className="truncate font-mono text-sm">
              {scriptQuery.data?.name ?? scriptKey ?? ""}
            </SheetTitle>
            <SheetDescription className="sr-only">
              The captured script behind the selected lineage node.
            </SheetDescription>
            {scriptQuery.data && (
              <div className="flex flex-wrap items-center gap-1.5">
                <Badge>{scriptQuery.data.kind}</Badge>
                <Badge variant="outline">{scriptQuery.data.language.toUpperCase()}</Badge>
                {scriptQuery.data.source && <Badge variant="outline">{scriptQuery.data.source}</Badge>}
              </div>
            )}
          </SheetHeader>
          <div className="min-h-0 flex-1 overflow-y-auto p-4">
            {scriptQuery.isLoading && (
              <div className="flex justify-center py-8">
                <Loader2 className="size-5 animate-spin text-muted-foreground" />
              </div>
            )}
            {scriptQuery.isError && (isApiError(scriptQuery.error)
              ? <CorrelationError error={scriptQuery.error} />
              : <p className="text-[13px] text-destructive">{String(scriptQuery.error)}</p>)}
            {scriptQuery.data && (scriptQuery.data.script
              ? (
                <CodeView
                  value={scriptQuery.data.script}
                  language={scriptQuery.data.language === "yaml" ? "yaml" : "sql"}
                  height="calc(100vh - 190px)"
                  data-testid="node-script-body"
                />
              )
              : <EmptyState title="No script" description="This node has no captured script yet. For database objects, run a connected sync (--connect) so the source is read." />)}
          </div>
        </SheetContent>
      </Sheet>
    </div>
  );
}
