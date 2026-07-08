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
import Autocomplete from "@mui/material/Autocomplete";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import CircularProgress from "@mui/material/CircularProgress";
import Divider from "@mui/material/Divider";
import Drawer from "@mui/material/Drawer";
import FormControl from "@mui/material/FormControl";
import GlobalStyles from "@mui/material/GlobalStyles";
import IconButton from "@mui/material/IconButton";
import Menu from "@mui/material/Menu";
import MenuItem from "@mui/material/MenuItem";
import Paper from "@mui/material/Paper";
import Select from "@mui/material/Select";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import ToggleButton from "@mui/material/ToggleButton";
import ToggleButtonGroup from "@mui/material/ToggleButtonGroup";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";
import { useTheme } from "@mui/material/styles";
import ChevronRightIcon from "@mui/icons-material/ChevronRight";
import DownloadIcon from "@mui/icons-material/Download";
import InfoOutlinedIcon from "@mui/icons-material/InfoOutlined";
import TableRowsIcon from "@mui/icons-material/TableRows";
import { isApiError } from "../../api/client";
import { lineageApi, repoApi } from "../../api/endpoints";
import type { LineageEdge, RunScope } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { TriggerRunDialog } from "../runs/TriggerRunDialog";
import { brandToken, seriesColor } from "../../theme/branding";
import "@xyflow/react/dist/style.css";

const NODE_WIDTH = 200;
const NODE_HEIGHT = 56;
const LAYER_SPACING = 150; // vertical gap between layers (rows, top to bottom); clears NODE_HEIGHT + edge/label room
const NODE_SPACING = 260;  // horizontal gap between nodes within a layer; clears NODE_WIDTH
// The canvas is full-bleed: it fills the whole content area below the fixed app bar, with the toolbar and details
// floating on top of it (no page header). The height subtracts only the app bar (56 xs / 64 md); negative margins
// on the container (see the return) cancel AppShell's main padding so the graph reaches every edge.
const CANVAS_HEIGHT = { xs: "calc(100vh - 56px)", md: "calc(100vh - 64px)" } as const;

type GraphView = "flows" | "objects";

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
  for (const id of ids) {
    if (layerInDegree.get(id) === 0) {
      layer.set(id, 0);
      queue.push(id);
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

interface FocusState {
  id: string;
  upstream: Set<string>;
  downstream: Set<string>;
}

interface CanvasProps {
  graph: BuiltGraph;
  focus: FocusState | null;
  colorMode: "light" | "dark";
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
function GraphCanvas({ graph, focus, colorMode, centerRequest, onFocus, onOpen, onNodeContextMenu, children }: CanvasProps) {
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
      const accent = isFocus
        ? brandToken("--sf-primary")
        : isUpstream
          ? brandToken("--sf-series-2")
          : brandToken("--sf-series-7");
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
  }, [flowNodes, focus]);

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
 * The repo-scoped lineage graph, built for two things above all: a tree-based (layered, top-to-bottom) layout
 * that reads as data flow, and navigation: click to focus a node and light up everything upstream (purple) and
 * downstream (green) of it, search to jump to a node, double click (or the panel button) to open it. Two views
 * share the canvas: Flows (pipelines AND the physical objects they read/write, so a table is a node between its
 * producer and consumers) and Objects (tables/files connected object-to-object by the flows that move data
 * between them, edges colored per flow). Deep-linkable via ?repoId= and ?view=.
 */
export default function LineageGraphPage() {
  const navigate = useNavigate();
  const theme = useTheme();
  const [searchParams, setSearchParams] = useSearchParams();
  const repoId = searchParams.get("repoId") ?? "";
  const graphView: GraphView = searchParams.get("view") === "objects" ? "objects" : "flows";
  // A deep-link (from search) can target a node to focus: an object key or a pipeline id. When it arrives without
  // a repo (an object hit carries no repo, since an object is global), the repo is resolved below and filled in.
  const focusParam = searchParams.get("focus") ?? "";
  const [focus, setFocus] = useState<FocusState | null>(null);
  const [centerRequest, setCenterRequest] = useState<{ id: string; nonce: number; zoom?: number } | null>(null);
  const [panelOpen, setPanelOpen] = useState(true);
  // Right-click context menu on a node, and the node whose script is open in the drawer.
  const [nodeMenu, setNodeMenu] = useState<{ id: string; x: number; y: number } | null>(null);
  const [scriptKey, setScriptKey] = useState<string | null>(null);
  // A flow node's Run action opens the trigger dialog prefilled with that flow and the chosen scope.
  const [runDialog, setRunDialog] = useState<{ flowName: string; scope: RunScope } | null>(null);

  const scriptQuery = useQuery({
    queryKey: ["lineage-node-script", scriptKey],
    queryFn: () => lineageApi.script(scriptKey as string),
    enabled: scriptKey !== null,
  });

  const repos = useQuery({
    queryKey: ["repos", "for-lineage-graph"],
    queryFn: () => repoApi.list({ page: 1, pageSize: 200 }),
  });

  // Resolve which repo's graph to draw for a focus target that arrived without one: an object hit from search
  // carries only the global object key, so ask which repos reference it (writing repo ranked first) and adopt the
  // best. Only runs while a focus is pending and no repo is chosen yet; a pipeline focus already carries its repo.
  const focusRepos = useQuery({
    queryKey: ["lineage-object-repos", focusParam],
    queryFn: () => lineageApi.objectRepos(focusParam),
    enabled: focusParam !== "" && repoId === "",
  });

  const waves = useQuery({
    queryKey: ["lineage-waves", repoId],
    queryFn: () => lineageApi.waves(repoId),
    enabled: repoId !== "",
  });

  const objectEdges = useQuery({
    // Both views build from the object edges: the objects view as object->object data movement, the flows view
    // as pipelines and the objects they read/write (so a physical table is a node between its producer and
    // consumers). The flow->flow execution order is still shown via the waves panel.
    queryKey: ["lineage-object-edges", repoId],
    enabled: repoId !== "",
    queryFn: async () => {
      const all: LineageEdge[] = [];
      let page = 1;
      for (;;) {
        const result = await lineageApi.edges(repoId, { page, pageSize: 200 });
        all.push(...result.items);
        if (all.length >= result.total || result.items.length === 0) {
          break;
        }
        page += 1;
      }
      return all;
    },
  });

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

  // Once the focus target's repos resolve, adopt the best one (writing repo first) into ?repoId= so the graph
  // draws it; the focus param rides along and is applied once that repo's graph contains the node.
  useEffect(() => {
    if (focusParam === "" || repoId !== "") {
      return;
    }
    const best = focusRepos.data?.[0];
    if (best) {
      setParam("repoId", best.repoId);
    }
  }, [focusParam, repoId, focusRepos.data, setParam]);

  const repoItems = repos.data?.items ?? [];
  const selectValue = repoItems.some((repo) => repo.id === repoId) ? repoId : "";

  const sortedWaves = useMemo(() => {
    if (!waves.data) {
      return [];
    }
    const rank = (wave: number) => (wave === -1 ? Number.MAX_SAFE_INTEGER : wave);
    return [...waves.data].sort((a, b) => rank(a.wave) - rank(b.wave));
  }, [waves.data]);

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
    if (!waves.data || !objectEdges.data) {
      return null;
    }

    const names = new Map<string, string>();
    const flowColors = new Map<string, string>();
    const incoming = new Map<string, string[]>();
    const outgoing = new Map<string, string[]>();
    const objectNodeIds = new Set<string>();
    const pipelineIdByName = new Map<string, string>();
    const nodes: FlowNode[] = [];
    const edges: FlowEdge[] = [];
    const seenEdges = new Set<string>();

    // Stable per-pipeline accent color and a name->id map across EVERY wave first, so a pipeline keeps its color
    // whether or not the wave filter is applied (the filter only changes which nodes are drawn, not their colors).
    let colorIndex = 0;
    for (const wave of waves.data) {
      for (const pipeline of wave.pipelines) {
        if (!flowColors.has(pipeline.id)) {
          flowColors.set(pipeline.id, seriesColor(colorIndex));
          colorIndex += 1;
        }
        pipelineIdByName.set(pipeline.name, pipeline.id);
      }
    }

    // Pipeline nodes, restricted to the selected wave when the batch filter is set. Only these pipelines pull in
    // the objects they read/write below, so filtering to a wave scopes the whole graph to that batch.
    for (const wave of waves.data) {
      if (selectedWave !== null && wave.wave !== selectedWave) {
        continue;
      }
      for (const pipeline of wave.pipelines) {
        if (names.has(pipeline.id)) {
          continue;
        }
        const color = flowColors.get(pipeline.id)!;
        names.set(pipeline.id, pipeline.name);
        nodes.push({
          id: pipeline.id,
          position: { x: 0, y: 0 },
          data: {
            label: (
              <Box sx={{ overflow: "hidden", textAlign: "left" }}>
                <Typography variant="body2" fontWeight={600} noWrap component="div">{pipeline.name}</Typography>
                <Typography variant="caption" color="text.secondary" noWrap component="div">
                  {wave.wave >= 0 ? `${pipeline.kind}, wave ${wave.wave}` : pipeline.kind}
                </Typography>
              </Box>
            ),
          },
          style: {
            width: NODE_WIDTH,
            height: NODE_HEIGHT,
            padding: 8,
            borderRadius: 8,
            borderLeft: `5px solid ${color}`,
          },
        });
      }
    }

    // Classify the object edges (as in the objects view): a flow's reads/writes are data movement; a
    // module-derived read connects a VIEW to its base table; a `Requires` (a procedure a flow executes) is a
    // code dependency, not data, so it is excluded here (only the procedure's data reads/writes show).
    const nameByKey = new Map<string, string>();
    const writtenKeys = new Set<string>();
    const writeOwner = new Map<string, string>();
    const moduleReads = new Map<string, Set<string>>();
    const byFlow = new Map<string, { reads: LineageEdge[]; writes: LineageEdge[] }>();
    for (const edge of objectEdges.data) {
      nameByKey.set(edge.objectKey, edge.objectName);
      if (edge.flow) {
        const group = byFlow.get(edge.flow) ?? byFlow.set(edge.flow, { reads: [], writes: [] }).get(edge.flow)!;
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
    const viewKeys = new Set([...moduleReads.keys()].filter((key) => writtenKeys.has(key)));

    const objectKind = (key: string): string => {
      const serverRef = key.includes("|") ? key.slice(0, key.indexOf("|")) : "";
      if (serverRef === "file") {
        return "file";
      }
      return viewKeys.has(key) ? "view" : "table";
    };
    const ensureObject = (key: string) => {
      if (names.has(key)) {
        return;
      }
      objectNodeIds.add(key);
      names.set(key, nameByKey.get(key) ?? key);
      nodes.push({
        id: key,
        position: { x: 0, y: 0 },
        data: {
          label: (
            <Box sx={{ overflow: "hidden", textAlign: "left" }}>
              <Typography variant="body2" fontWeight={600} noWrap component="div">{nameByKey.get(key) ?? key}</Typography>
              <Typography variant="caption" color="text.secondary" noWrap component="div">{objectKind(key)}</Typography>
            </Box>
          ),
        },
        style: {
          width: NODE_WIDTH,
          height: NODE_HEIGHT,
          padding: 8,
          borderRadius: 20,
          border: "1px dashed",
          borderColor: brandToken("--sf-series-7"),
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

    // flow -> each table it writes (a view is skipped; it is wired to its base table below), and object -> flow
    // it reads. Every physical table a flow produces is therefore its own node between producer and consumers.
    for (const [flow, group] of byFlow) {
      const producerId = pipelineIdByName.get(flow);
      // Skip flows whose pipeline is not a drawn node (unknown flow, or filtered out by the wave selection).
      if (producerId === undefined || !names.has(producerId)) {
        continue;
      }
      const color = flowColors.get(producerId)!;
      for (const write of group.writes) {
        if (viewKeys.has(write.objectKey)) {
          continue;
        }
        ensureObject(write.objectKey);
        addEdge(producerId, write.objectKey, color, write.relation === "Creates" ? "creates" : "writes");
      }
      for (const read of group.reads) {
        ensureObject(read.objectKey);
        addEdge(read.objectKey, producerId, color, "reads");
      }
    }

    // A view node is wired to its PARENT TABLE (the module read), coloured like the flow that maintains it.
    for (const viewKey of viewKeys) {
      const owner = writeOwner.get(viewKey);
      const producerId = owner ? pipelineIdByName.get(owner) : undefined;
      // Under a wave filter, only keep views maintained by a pipeline that is actually drawn in this batch.
      if (selectedWave !== null && (producerId === undefined || !names.has(producerId))) {
        continue;
      }
      const color = producerId ? flowColors.get(producerId)! : seriesColor(colorIndex++);
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
      openTarget: (id) => (objectNodeIds.has(id)
        ? { label: "Open in explorer", to: `/lineage/objects?name=${encodeURIComponent(names.get(id) ?? id)}` }
        : { label: "Open pipeline", to: `/pipelines/${id}` }),
    };
    // The series colors are theme-scoped custom properties; rebuilding on mode change keeps them in sync.
  }, [waves.data, objectEdges.data, selectedWave, theme.palette.mode]); // eslint-disable-line react-hooks/exhaustive-deps

  // ---- Objects view: tables/files as nodes, "flow moves data from A to B" as edges, colored per flow -------------
  const objectsGraph = useMemo<BuiltGraph | null>(() => {
    if (graphView !== "objects" || !objectEdges.data) {
      return null;
    }

    const names = new Map<string, string>();
    const flowColors = new Map<string, string>();
    const incoming = new Map<string, string[]>();
    const outgoing = new Map<string, string[]>();
    const nodes: FlowNode[] = [];
    const edges: FlowEdge[] = [];
    const seenEdges = new Set<string>();

    const ensureNode = (key: string, name: string) => {
      if (names.has(key)) {
        return;
      }
      names.set(key, name);
      const serverRef = key.includes("|") ? key.slice(0, key.indexOf("|")) : "";
      nodes.push({
        id: key,
        position: { x: 0, y: 0 },
        data: {
          label: (
            <Box sx={{ overflow: "hidden", textAlign: "left" }}>
              <Typography variant="body2" fontWeight={600} noWrap component="div">{name}</Typography>
              <Typography variant="caption" color="text.secondary" noWrap component="div">
                {serverRef === "file" ? "file" : truncate(serverRef, 30)}
              </Typography>
            </Box>
          ),
        },
        style: { width: NODE_WIDTH, height: NODE_HEIGHT, padding: 8, borderRadius: 8 },
      });
    };

    // Classify every edge. A flow's own reads/writes drive the data movement (data goes from what a flow reads
    // to the tables it writes). A module-derived read (no flow, carries a viaModule) is the module body reading
    // a base object - for a VIEW this is exactly how it connects to its PARENT TABLE, which is the physical data
    // path (file -> raw table -> view -> target), not file -> view. A `Requires` edge is a code/existence
    // dependency (a procedure a flow executes), not data movement, so it is excluded.
    const byFlow = new Map<string, { reads: LineageEdge[]; writes: LineageEdge[] }>();
    const nameByKey = new Map<string, string>();
    const writtenKeys = new Set<string>();          // objects a flow writes/creates (real data targets)
    const writeOwner = new Map<string, string>();   // object -> the flow that produces it
    const moduleReads = new Map<string, Set<string>>(); // module (view/proc) -> base objects its body reads

    for (const edge of objectEdges.data) {
      nameByKey.set(edge.objectKey, edge.objectName);
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

    // A VIEW is a module (its body reads base objects) that a flow also writes/creates; a PROCEDURE is a module
    // a flow only requires (never writes), so it stays out of the data-flow view. A view's input is its base
    // table, so it is NOT wired to whatever the producing flow read (the file); it is wired to its base below.
    const viewKeys = new Set([...moduleReads.keys()].filter((key) => writtenKeys.has(key)));

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

    // 2. View derivation: base table -> view, attributed to the flow that maintains the view (same color as its
    //    other work). This is the edge that connects a view to its PARENT TABLE.
    for (const viewKey of viewKeys) {
      const owner = writeOwner.get(viewKey) ?? "";
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
      openTarget: (id) => ({
        label: "Open in explorer",
        to: `/lineage/objects?name=${encodeURIComponent(names.get(id) ?? id)}`,
      }),
    };
  }, [graphView, objectEdges.data, theme.palette.mode]); // eslint-disable-line react-hooks/exhaustive-deps

  const graph = graphView === "flows" ? flowsGraph : objectsGraph;

  // Focus and centering are per graph; switching repo or view resets them.
  useEffect(() => {
    setFocus(null);
    setCenterRequest(null);
  }, [repoId, graphView]);

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

  const queryError = [repos, waves, objectEdges, focusRepos].find((query) => query.isError)?.error;
  const loadingGraph = repoId !== "" && (waves.isPending || objectEdges.isPending);
  const hasContent = graph !== null && graph.nodes.length > 0;
  // A focus target that arrived without a repo: resolving which repo to draw, or resolved to none (the object has
  // no lineage edges, so it is in no graph).
  const resolvingFocus = focusParam !== "" && repoId === "" && focusRepos.isPending;
  const focusHasNoRepo = focusParam !== "" && repoId === ""
    && focusRepos.isSuccess && focusRepos.data.length === 0;
  const repoName = repoItems.find((repo) => repo.id === repoId)?.name ?? repoId;

  // Export the drawn graph as an SVG vector of the current layout (nodes, edges, labels) in the active theme,
  // built from the same in-memory graph so it stays a single source of truth.
  const downloadLineage = useCallback(() => {
    if (graph === null) {
      return;
    }
    const svg = buildLineageSvg(graph, {
      text: theme.palette.text.primary,
      textMuted: theme.palette.text.secondary,
      paper: theme.palette.background.paper,
      border: theme.palette.divider,
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
  }, [graph, graphView, repoId, repoName, theme.palette]);

  // The floating details panel: node focus (upstream/downstream trace + open) over the execution waves (flows)
  // or the per-flow edge legend (objects). It rides on the canvas via a React Flow <Panel> so the graph keeps the
  // full width, and collapses to a single button when the user wants the whole canvas.
  const detailsPanel = graph === null ? null : (
    <Paper
      variant="outlined"
      data-testid="graph-side-panel"
      sx={{
        width: 300,
        // Bounded to the visible canvas so a long wave list scrolls inside the panel rather than off the graph;
        // the canvas now fills the viewport below the app bar, leaving room for the React Flow panel margins.
        maxHeight: { xs: "calc(100vh - 96px)", md: "calc(100vh - 104px)" },
        display: "flex",
        flexDirection: "column",
        overflow: "hidden",
      }}
    >
      <Box
        sx={{
          display: "flex",
          alignItems: "center",
          justifyContent: "space-between",
          px: 2,
          py: 1,
          flexShrink: 0,
          borderBottom: 1,
          borderColor: "divider",
        }}
      >
        <Typography variant="subtitle2" fontWeight={700}>Details</Typography>
        <Tooltip title="Collapse panel">
          <IconButton size="small" onClick={() => setPanelOpen(false)} data-testid="graph-panel-collapse" aria-label="Collapse panel">
            <ChevronRightIcon fontSize="small" />
          </IconButton>
        </Tooltip>
      </Box>
      <Box sx={{ flex: 1, minHeight: 0, overflowY: "auto", p: 2 }}>
        {focus !== null && (
          <Box data-testid="graph-focus-panel" sx={{ mb: 2 }}>
            <Typography variant="subtitle1" fontWeight={600} noWrap>
              {graph.names.get(focus.id) ?? focus.id}
            </Typography>
            <Stack direction="row" spacing={1} sx={{ my: 1 }}>
              <Chip
                size="small"
                label={`${focus.upstream.size} upstream`}
                sx={{ bgcolor: brandToken("--sf-series-2"), color: brandToken("--sf-primary-contrast") }}
              />
              <Chip
                size="small"
                label={`${focus.downstream.size} downstream`}
                sx={{ bgcolor: brandToken("--sf-series-7"), color: brandToken("--sf-primary-contrast") }}
              />
            </Stack>
            <Stack direction="row" spacing={1} sx={{ flexWrap: "wrap", rowGap: 1 }}>
              <Button size="small" variant="contained" onClick={() => openNode(focus.id)} data-testid="graph-open-selected">
                {graph.openTarget(focus.id).label}
              </Button>
              <Button size="small" onClick={() => setScriptKey(focus.id)} data-testid="graph-view-script">
                View script
              </Button>
              <Button size="small" onClick={() => focusNode(null)} data-testid="graph-clear-focus">
                Clear
              </Button>
            </Stack>
            <Divider sx={{ mt: 2 }} />
          </Box>
        )}

        {graphView === "flows" ? (
          <>
            <Typography variant="subtitle1" fontWeight={600} gutterBottom>Execution waves</Typography>
            {sortedWaves.map((wave) => (
              <Box key={wave.wave} sx={{ mb: 1.5 }}>
                <Typography variant="subtitle2" color="text.secondary">
                  {wave.wave === -1 ? "Unwaved (lineage not computed)" : `Wave ${wave.wave}`}
                </Typography>
                <Box sx={{ display: "flex", flexWrap: "wrap", gap: 0.5, mt: 0.5 }} data-testid="wave-list">
                  {wave.pipelines.map((pipeline) => (
                    <Chip
                      key={pipeline.id}
                      label={pipeline.name}
                      size="small"
                      onClick={() => {
                        focusNode(pipeline.id);
                        setCenterRequest((previous) => ({ id: pipeline.id, nonce: (previous?.nonce ?? 0) + 1 }));
                      }}
                      sx={{
                        borderLeft: `4px solid ${graph.flowColors.get(pipeline.id) ?? "transparent"}`,
                        borderRadius: 1,
                      }}
                      data-testid={`wave-pipeline-${pipeline.id}`}
                    />
                  ))}
                </Box>
              </Box>
            ))}
          </>
        ) : (
          <>
            <Typography variant="subtitle1" fontWeight={600} gutterBottom>Flows (edge colors)</Typography>
            <Box sx={{ display: "flex", flexWrap: "wrap", gap: 0.5 }} data-testid="graph-flow-legend">
              {[...graph.flowColors.entries()].map(([flow, color]) => (
                <Chip
                  key={flow}
                  label={flow}
                  size="small"
                  sx={{ borderLeft: `4px solid ${color}`, borderRadius: 1 }}
                />
              ))}
            </Box>
          </>
        )}
      </Box>
    </Paper>
  );

  return (
    <Box
      data-testid="page-lineage-graph"
      sx={{
        position: "relative",
        // Full-bleed: negative margins cancel AppShell's main padding (2 xs / 3 md) so the canvas runs edge to
        // edge, and the height claims the whole viewport below the app bar.
        height: CANVAS_HEIGHT,
        mt: { xs: -2, md: -3 },
        mb: { xs: -2, md: -3 },
        mx: { xs: -2, md: -3 },
        minWidth: 0,
        overflow: "hidden",
        bgcolor: "background.paper",
      }}
    >
      {/* The focused node's emphasis: its label text is forced to the primary contrast color (it sits on a solid
          primary fill), and it plays a brief glow pulse when it becomes the focus so the eye lands on it. */}
      <GlobalStyles
        styles={{
          "@keyframes sfFocusPulse": {
            "0%": { filter: "drop-shadow(0 0 2px var(--sf-primary))" },
            "50%": { filter: "drop-shadow(0 0 16px var(--sf-primary))" },
            "100%": { filter: "drop-shadow(0 0 2px var(--sf-primary))" },
          },
          ".react-flow__node.sf-focus-node": { animation: "sfFocusPulse 1.3s ease-in-out 3" },
          ".react-flow__node.sf-focus-node .MuiTypography-root": {
            color: "var(--sf-primary-contrast) !important",
          },
        }}
      />
      {/* The toolbar floats over the canvas (top-left) instead of sitting in a page header, so the graph itself
          fills the whole page; the details panel floats at top-right via a React Flow <Panel>. The toolbar's max
          width leaves a clear column on the right for that panel (wider when it is open, narrow for its collapsed
          icon), so a wrapping toolbar can never slide underneath it. */}
      <Paper
        elevation={4}
        data-testid="graph-toolbar"
        sx={{
          position: "absolute",
          top: 12,
          left: 12,
          zIndex: 6,
          maxWidth: panelOpen ? "calc(100% - 340px)" : "calc(100% - 84px)",
          display: "flex",
          flexWrap: "wrap",
          alignItems: "center",
          gap: 1,
          px: 1.5,
          py: 1,
          bgcolor: "background.paper",
        }}
      >
        <Button
          variant="outlined"
          size="small"
          startIcon={<TableRowsIcon />}
          onClick={() => navigate("/lineage/objects")}
          data-testid="open-lineage-objects"
        >
          Explorer
        </Button>
        <ToggleButtonGroup
          exclusive
          size="small"
          value={graphView}
          onChange={(_, value: GraphView | null) => {
            if (value !== null) {
              setParam("view", value === "flows" ? "" : value);
            }
          }}
          data-testid="graph-view-toggle"
        >
          <ToggleButton value="flows" data-testid="graph-view-flows">Flows</ToggleButton>
          <ToggleButton value="objects" data-testid="graph-view-objects">Objects</ToggleButton>
        </ToggleButtonGroup>
        {graphView === "flows" && sortedWaves.length > 0 && (
          <FormControl size="small" sx={{ minWidth: 150 }}>
            <Select
              value={selectedWave === null ? "" : String(selectedWave)}
              onChange={(event) => setParam("wave", event.target.value)}
              displayEmpty
              inputProps={{ "aria-label": "Wave" }}
              data-testid="graph-wave-filter"
            >
              <MenuItem value=""><em>All waves</em></MenuItem>
              {sortedWaves.map((wave) => (
                <MenuItem key={wave.wave} value={String(wave.wave)}>
                  {wave.wave === -1 ? "Unwaved" : `Wave ${wave.wave}`}
                </MenuItem>
              ))}
            </Select>
          </FormControl>
        )}
        {hasContent && (
          <Autocomplete
            size="small"
            sx={{ width: 240 }}
            options={searchOptions}
            getOptionLabel={(option) => option.name}
            isOptionEqualToValue={(a, b) => a.id === b.id}
            onChange={(_, option) => {
              if (option) {
                focusNode(option.id);
                setCenterRequest((previous) => ({ id: option.id, nonce: (previous?.nonce ?? 0) + 1 }));
              }
            }}
            renderInput={(params) => (
              <TextField
                {...params}
                placeholder="Find a node"
                inputProps={{ ...params.inputProps, "data-testid": "graph-node-search" }}
              />
            )}
          />
        )}
        {hasContent && (
          <Button
            variant="outlined"
            size="small"
            startIcon={<DownloadIcon />}
            onClick={downloadLineage}
            data-testid="download-lineage"
          >
            Download SVG
          </Button>
        )}
        <FormControl size="small" sx={{ minWidth: 220 }}>
          <Select
            value={selectValue}
            onChange={(event) => setParam("repoId", event.target.value)}
            displayEmpty
            inputProps={{ "aria-label": "Repo" }}
            data-testid="graph-repo-select"
          >
            <MenuItem value=""><em>Select a repo</em></MenuItem>
            {repoItems.map((repo) => (
              <MenuItem key={repo.id} value={repo.id}>{repo.name}</MenuItem>
            ))}
          </Select>
        </FormControl>
      </Paper>

      {queryError !== undefined && (
        <Box sx={{ position: "absolute", top: 68, left: 12, right: 12, zIndex: 6, maxWidth: 640 }}>
          {isApiError(queryError)
            ? <CorrelationError error={queryError} />
            : <Typography color="error">{String(queryError)}</Typography>}
        </Box>
      )}

      {repoId === "" && !repos.isError && (
          <Box sx={{ position: "absolute", inset: 0, display: "flex", alignItems: "center", justifyContent: "center", p: 3 }} data-testid="graph-empty">
            {resolvingFocus ? (
              <Stack alignItems="center" spacing={2} data-testid="graph-resolving-focus">
                <CircularProgress size={28} />
                <Typography variant="body2" color="text.secondary">Locating the object in the lineage graph…</Typography>
              </Stack>
            ) : focusHasNoRepo ? (
              <EmptyState
                data-testid="graph-focus-no-repo"
                title="No lineage graph references this object yet"
                description="This object has no recorded lineage edges, so it does not appear in any repo's graph. Open it in the object explorer to see its definition and columns."
                action={(
                  <Button
                    variant="outlined"
                    startIcon={<TableRowsIcon />}
                    onClick={() => navigate("/lineage/objects")}
                    data-testid="graph-focus-open-explorer"
                  >
                    Open explorer
                  </Button>
                )}
              />
            ) : (
              <EmptyState
                title="Pick a repo to draw its graph"
                description="The lineage graph is computed per repo: select one above to see its pipelines and the objects that connect them. Click a node to trace what feeds it and what depends on it."
              />
            )}
          </Box>
        )}

        {loadingGraph && !queryError && (
          <Skeleton variant="rectangular" sx={{ position: "absolute", inset: 0, height: "100%" }} data-testid="graph-loading" />
        )}

        {repoId !== "" && !loadingGraph && !queryError && graph !== null && !hasContent && (
          <Box sx={{ position: "absolute", inset: 0, display: "flex", alignItems: "center", justifyContent: "center", p: 3 }} data-testid="graph-no-lineage">
            <EmptyState
              title={graphView === "flows" ? "No lineage for this repo yet" : "No object lineage for this repo yet"}
              description={graphView === "flows"
                ? "No waves or pipelines were found. Sync the repo, then come back."
                : "No object edges were recorded. Sync the repo (a connected sync adds the derived tier), then come back."}
            />
          </Box>
        )}

        {repoId !== "" && !loadingGraph && !queryError && graph !== null && hasContent && (
          <ReactFlowProvider>
            <GraphCanvas
              graph={graph}
              focus={focus}
              colorMode={theme.palette.mode}
              centerRequest={centerRequest}
              onFocus={focusNode}
              onOpen={openNode}
              onNodeContextMenu={(id, position) => setNodeMenu({ id, x: position.x, y: position.y })}
            >
              {panelOpen ? (
                <Panel position="top-right">{detailsPanel}</Panel>
              ) : (
                <Panel position="top-right">
                  <Tooltip title="Show details">
                    <IconButton
                      onClick={() => setPanelOpen(true)}
                      data-testid="graph-panel-open"
                      aria-label="Show details panel"
                      sx={{ bgcolor: "background.paper", border: 1, borderColor: "divider", "&:hover": { bgcolor: "background.paper" } }}
                    >
                      <InfoOutlinedIcon />
                    </IconButton>
                  </Tooltip>
                </Panel>
              )}
            </GraphCanvas>
          </ReactFlowProvider>
        )}

        <Menu
          open={nodeMenu !== null}
          onClose={() => setNodeMenu(null)}
          anchorReference="anchorPosition"
          anchorPosition={nodeMenu !== null ? { top: nodeMenu.y, left: nodeMenu.x } : undefined}
        >
          <MenuItem
            data-testid="node-menu-view-script"
            onClick={() => {
              if (nodeMenu !== null) {
                setScriptKey(nodeMenu.id);
              }
              setNodeMenu(null);
            }}
          >
            View script
          </MenuItem>
          <MenuItem
            onClick={() => {
              if (nodeMenu !== null) {
                focusNode(nodeMenu.id);
              }
              setNodeMenu(null);
            }}
          >
            Trace upstream / downstream
          </MenuItem>
          <MenuItem
            onClick={() => {
              if (nodeMenu !== null) {
                openNode(nodeMenu.id);
              }
              setNodeMenu(null);
            }}
          >
            Open details
          </MenuItem>
          {nodeMenu !== null && repoId !== "" && graph !== null
            && graph.openTarget(nodeMenu.id).label === "Open pipeline"
            && [
              <Divider key="run-divider" />,
              <MenuItem
                key="run-flow"
                data-testid="node-menu-run-flow"
                onClick={() => {
                  const flowName = graph.names.get(nodeMenu.id);
                  if (flowName) {
                    setRunDialog({ flowName, scope: "flow" });
                  }
                  setNodeMenu(null);
                }}
              >
                Run flow
              </MenuItem>,
              <MenuItem
                key="run-node"
                data-testid="node-menu-run-node"
                onClick={() => {
                  const flowName = graph.names.get(nodeMenu.id);
                  if (flowName) {
                    setRunDialog({ flowName, scope: "node" });
                  }
                  setNodeMenu(null);
                }}
              >
                Run flow + descendants
              </MenuItem>,
              <MenuItem
                key="run-batch"
                data-testid="node-menu-run-batch"
                onClick={() => {
                  const flowName = graph.names.get(nodeMenu.id);
                  if (flowName) {
                    setRunDialog({ flowName, scope: "batch" });
                  }
                  setNodeMenu(null);
                }}
              >
                Run batch
              </MenuItem>,
            ]}
        </Menu>

        <TriggerRunDialog
          open={runDialog !== null}
          onClose={() => setRunDialog(null)}
          repoId={repoId || undefined}
          flowName={runDialog?.flowName}
          scope={runDialog?.scope}
        />

        <Drawer
          anchor="right"
          open={scriptKey !== null}
          onClose={() => setScriptKey(null)}
          sx={{ zIndex: (t) => t.zIndex.modal }}
        >
          <Box sx={{ width: { xs: "100vw", sm: 760 }, maxWidth: "100vw", display: "flex", flexDirection: "column", height: "100%" }}>
            <Stack direction="row" alignItems="center" justifyContent="space-between" spacing={2} sx={{ p: 2, borderBottom: 1, borderColor: "divider" }}>
              <Box sx={{ minWidth: 0 }}>
                <Typography variant="subtitle1" noWrap>{scriptQuery.data?.name ?? scriptKey}</Typography>
                {scriptQuery.data && (
                  <Stack direction="row" spacing={1} sx={{ mt: 0.5 }}>
                    <Chip size="small" color="primary" label={scriptQuery.data.kind} />
                    <Chip size="small" variant="outlined" label={scriptQuery.data.language.toUpperCase()} />
                    {scriptQuery.data.source && <Chip size="small" variant="outlined" label={scriptQuery.data.source} />}
                  </Stack>
                )}
              </Box>
              <Stack direction="row" spacing={1} sx={{ flexShrink: 0 }}>
                <Button size="small" variant="contained" onClick={() => setScriptKey(null)}>Close</Button>
              </Stack>
            </Stack>
            <Box sx={{ flex: 1, minHeight: 0, p: 2 }}>
              {scriptQuery.isLoading && <Stack alignItems="center" sx={{ py: 4 }}><CircularProgress size={24} /></Stack>}
              {scriptQuery.isError && (isApiError(scriptQuery.error)
                ? <CorrelationError error={scriptQuery.error} />
                : <Typography color="error">{String(scriptQuery.error)}</Typography>)}
              {scriptQuery.data && (scriptQuery.data.script
                ? (
                  <CodeView
                    value={scriptQuery.data.script}
                    language={scriptQuery.data.language === "yaml" ? "yaml" : "sql"}
                    height="calc(100vh - 128px)"
                    data-testid="node-script-body"
                  />
                )
                : <EmptyState title="No script" description="This node has no captured script yet. For database objects, run a connected sync (--connect) so the source is read." />)}
            </Box>
          </Box>
        </Drawer>
    </Box>
  );
}
