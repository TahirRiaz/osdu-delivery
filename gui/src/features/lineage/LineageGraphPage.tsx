import { useCallback, useEffect, useMemo, useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import {
  Background,
  Controls,
  MarkerType,
  MiniMap,
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
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Chip from "@mui/material/Chip";
import Divider from "@mui/material/Divider";
import FormControl from "@mui/material/FormControl";
import MenuItem from "@mui/material/MenuItem";
import Paper from "@mui/material/Paper";
import Select from "@mui/material/Select";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import ToggleButton from "@mui/material/ToggleButton";
import ToggleButtonGroup from "@mui/material/ToggleButtonGroup";
import Typography from "@mui/material/Typography";
import { useTheme } from "@mui/material/styles";
import { isApiError } from "../../api/client";
import { lineageApi, repoApi } from "../../api/endpoints";
import type { LineageEdge } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { brandToken, seriesColor } from "../../theme/branding";
import "@xyflow/react/dist/style.css";

const NODE_WIDTH = 200;
const NODE_HEIGHT = 56;
const LAYER_SPACING = 150; // vertical gap between layers (rows, top to bottom); clears NODE_HEIGHT + edge/label room
const NODE_SPACING = 260;  // horizontal gap between nodes within a layer; clears NODE_WIDTH
const GRAPH_HEIGHT = "max(calc(100vh - 260px), 480px)";

type GraphView = "flows" | "objects";

function truncate(value: string, max: number): string {
  return value.length > max ? `${value.slice(0, max)}...` : value;
}

/**
 * A top-to-bottom layered (Sugiyama) layout - the layout that makes data flow readable: sources at the top,
 * consumers below, one row per level. The level ("layer") is the authoritative one when supplied via
 * <c>layerOf</c> - for the flows view that is the pipeline's execution WAVE, exactly the level the backend
 * already computes by topological sort - and otherwise falls back to a longest-path layering derived from the
 * edges (the objects view, which has no waves). Within each layer, several barycenter passes order nodes by the
 * average position of their neighbours to minimize edge crossings. Ported from the DeltaForge forge-graph
 * hierarchical layout; independent of any layout library so the wave alignment is exact.
 */
function layeredLayout(nodes: FlowNode[], edges: FlowEdge[], layerOf?: Map<string, number>): FlowNode[] {
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

  // ---- Layer assignment: the supplied levels (waves) when present, else longest-path via Kahn. --------------
  const layer = new Map<string, number>();
  if (layerOf && [...ids].some((id) => layerOf.has(id))) {
    for (const id of ids) {
      layer.set(id, layerOf.get(id) ?? 0);
    }
  } else {
    const remaining = new Map<string, number>();
    for (const id of ids) {
      remaining.set(id, incoming.get(id)!.length);
    }
    const queue: string[] = [];
    for (const id of ids) {
      if (remaining.get(id) === 0) {
        layer.set(id, 0);
        queue.push(id);
      }
    }
    for (let head = 0; head < queue.length; head++) {
      const id = queue[head];
      const level = layer.get(id)!;
      for (const child of outgoing.get(id)!) {
        layer.set(child, Math.max(layer.get(child) ?? 0, level + 1));
        const rem = remaining.get(child)! - 1;
        remaining.set(child, rem);
        if (rem <= 0) {
          queue.push(child);
        }
      }
    }
    for (const id of ids) {
      if (!layer.has(id)) {
        layer.set(id, 0); // a node in a cycle the queue never drained: pin to the first layer
      }
    }
  }

  // Compress sparse/negative levels (e.g. an unwaved -1, or waves 0,2,4) to consecutive 0,1,2,... so the
  // columns are evenly spaced.
  const uniqueLevels = [...new Set(layer.values())].sort((a, b) => a - b);
  const compress = new Map(uniqueLevels.map((v, i) => [v, i]));
  for (const [id, v] of layer) {
    layer.set(id, compress.get(v)!);
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
  centerRequest: { id: string; nonce: number } | null;
  onFocus: (id: string | null) => void;
  onOpen: (id: string) => void;
}

/**
 * The canvas: positions live in React Flow state (dragging works, layout re-seeds on data change), while the
 * focus styling is applied in place so a focus change never resets positions the user arranged. Single click
 * focuses (upstream/downstream highlight); double click opens the node's page; clicking the pane clears focus.
 */
function GraphCanvas({ graph, focus, colorMode, centerRequest, onFocus, onOpen }: CanvasProps) {
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
        zoom: Math.max(view.getZoom(), 1),
        duration: 400,
      });
    }
  }, [centerRequest, view]);

  const styledNodes = useMemo(() => {
    if (focus === null) {
      return flowNodes.map((node) => ({ ...node, style: { ...node.style, opacity: 1, boxShadow: undefined } }));
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
        style: {
          ...node.style,
          opacity: related ? 1 : 0.15,
          boxShadow: related ? `0 0 0 2px ${accent}` : undefined,
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
      onPaneClick={() => onFocus(null)}
      colorMode={colorMode}
      fitView
      minZoom={0.05}
      nodesConnectable={false}
      nodesDraggable
    >
      <Background />
      <Controls />
      <MiniMap />
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
  const [focus, setFocus] = useState<FocusState | null>(null);
  const [centerRequest, setCenterRequest] = useState<{ id: string; nonce: number } | null>(null);

  const repos = useQuery({
    queryKey: ["repos", "for-lineage-graph"],
    queryFn: () => repoApi.list({ page: 1, pageSize: 200 }),
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

  const repoItems = repos.data?.items ?? [];
  const selectValue = repoItems.some((repo) => repo.id === repoId) ? repoId : "";

  const sortedWaves = useMemo(() => {
    if (!waves.data) {
      return [];
    }
    const rank = (wave: number) => (wave === -1 ? Number.MAX_SAFE_INTEGER : wave);
    return [...waves.data].sort((a, b) => rank(a.wave) - rank(b.wave));
  }, [waves.data]);

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
    let colorIndex = 0;

    // Pipeline nodes (one accent color each).
    for (const wave of waves.data) {
      for (const pipeline of wave.pipelines) {
        if (names.has(pipeline.id)) {
          continue;
        }
        const color = seriesColor(colorIndex);
        colorIndex += 1;
        names.set(pipeline.id, pipeline.name);
        pipelineIdByName.set(pipeline.name, pipeline.id);
        flowColors.set(pipeline.id, color);
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
      if (producerId === undefined) {
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
        ? { label: "Open in explorer", to: `/lineage?name=${encodeURIComponent(names.get(id) ?? id)}` }
        : { label: "Open pipeline", to: `/pipelines/${id}` }),
    };
    // The series colors are theme-scoped custom properties; rebuilding on mode change keeps them in sync.
  }, [waves.data, objectEdges.data, theme.palette.mode]); // eslint-disable-line react-hooks/exhaustive-deps

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
        to: `/lineage?name=${encodeURIComponent(names.get(id) ?? id)}`,
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

  const queryError = [repos, waves, objectEdges].find((query) => query.isError)?.error;
  const loadingGraph = repoId !== "" && (waves.isPending || objectEdges.isPending);
  const hasContent = graph !== null && graph.nodes.length > 0;

  return (
    <Box data-testid="page-lineage-graph">
      <Stack direction="row" alignItems="center" spacing={2} sx={{ mb: 2 }} flexWrap="wrap" useFlexGap>
        <Typography variant="h5" fontWeight={600} sx={{ flexGrow: 1 }}>Lineage graph</Typography>
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
        {hasContent && (
          <Autocomplete
            size="small"
            sx={{ width: 260 }}
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
        <FormControl size="small" sx={{ minWidth: 240 }}>
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
      </Stack>

      {queryError !== undefined && (
        <Box sx={{ mb: 2 }}>
          {isApiError(queryError)
            ? <CorrelationError error={queryError} />
            : <Typography color="error">{String(queryError)}</Typography>}
        </Box>
      )}

      {repoId === "" && !repos.isError && (
        <Paper variant="outlined" sx={{ p: 6, textAlign: "center" }} data-testid="graph-empty">
          <Typography variant="h6" color="text.secondary" gutterBottom>Pick a repo to draw its graph</Typography>
          <Typography variant="body2" color="text.secondary">
            The lineage graph is computed per repo: select one above to see its pipelines and the objects that
            connect them. Click a node to trace what feeds it and what depends on it.
          </Typography>
        </Paper>
      )}

      {loadingGraph && !queryError && (
        <Skeleton variant="rectangular" sx={{ height: 480, borderRadius: 1 }} data-testid="graph-loading" />
      )}

      {repoId !== "" && !loadingGraph && !queryError && graph !== null && !hasContent && (
        <Paper variant="outlined" sx={{ p: 6, textAlign: "center" }} data-testid="graph-no-lineage">
          <Typography variant="h6" color="text.secondary" gutterBottom>
            {graphView === "flows" ? "No lineage for this repo yet" : "No object lineage for this repo yet"}
          </Typography>
          <Typography variant="body2" color="text.secondary">
            {graphView === "flows"
              ? "No waves or pipelines were found. Sync the repo, then come back."
              : "No object edges were recorded. Sync the repo (a connected sync adds the derived tier), then come back."}
          </Typography>
        </Paper>
      )}

      {repoId !== "" && !loadingGraph && !queryError && graph !== null && hasContent && (
        <Stack direction={{ xs: "column", lg: "row" }} spacing={2}>
          <Box
            sx={{
              flexGrow: 1,
              minWidth: 0,
              height: GRAPH_HEIGHT,
              border: 1,
              borderColor: "divider",
              borderRadius: 1,
              overflow: "hidden",
            }}
            data-testid="lineage-graph-canvas"
          >
            <ReactFlowProvider>
              <GraphCanvas
                graph={graph}
                focus={focus}
                colorMode={theme.palette.mode}
                centerRequest={centerRequest}
                onFocus={focusNode}
                onOpen={openNode}
              />
            </ReactFlowProvider>
          </Box>
          <Card
            variant="outlined"
            sx={{ width: { xs: "100%", lg: 320 }, flexShrink: 0, maxHeight: GRAPH_HEIGHT, overflowY: "auto" }}
            data-testid="graph-side-panel"
          >
            <CardContent>
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
                  <Stack direction="row" spacing={1}>
                    <Button size="small" variant="contained" onClick={() => openNode(focus.id)} data-testid="graph-open-selected">
                      {graph.openTarget(focus.id).label}
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
            </CardContent>
          </Card>
        </Stack>
      )}
    </Box>
  );
}
