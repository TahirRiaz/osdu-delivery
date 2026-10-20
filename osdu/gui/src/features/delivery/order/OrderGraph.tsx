import { useEffect, useMemo } from "react";
import { Graph, layout } from "@dagrejs/dagre";
import {
  Background,
  Controls,
  Handle,
  MarkerType,
  Position,
  ReactFlow,
  ReactFlowProvider,
  useNodesState,
  useReactFlow,
  type Edge,
  type Node,
  type NodeProps,
} from "@xyflow/react";
import { AlertTriangle } from "lucide-react";
import { cn } from "@/lib/utils";
import { useThemeMode } from "@/theme/ThemeModeContext";
import "@xyflow/react/dist/style.css";

/** One box of an order graph: a schedule, or an entity type. */
export interface OrderGraphNode {
  id: string;
  title: string;
  /** A second, quieter line: the repository, the group, a cadence. */
  subtitle?: string;
  /** Marks the box as needing a look (a schedule out of order, one in a circle), with why on hover. */
  warning?: string;
}

/** An arrow from the box that goes first to the box that goes after it. */
export interface OrderGraphLink {
  id: string;
  from: string;
  to: string;
  /** `warning` draws the arrow dashed in the warning colour: an order the timing does not keep. `quiet` draws it faint. */
  tone: "neutral" | "warning" | "quiet";
  /** What the arrow stands for, on hover. */
  title?: string;
}

const NODE_WIDTH = 230;
const NODE_HEIGHT = 52;

type BoxData = { node: OrderGraphNode; selected: boolean };

/** A box: its title and subtitle on two lines, cut at the end, with a warning glyph when it needs a look. */
function Box({ data }: NodeProps<Node<BoxData>>) {
  const { node, selected } = data;
  return (
    <div
      className={cn(
        "flex h-[52px] w-[230px] items-center gap-2 rounded-md border bg-card px-2.5 text-left shadow-xs",
        selected ? "border-primary ring-2 ring-primary/40" : "border-border",
      )}
      title={node.warning === undefined ? `${node.title}${node.subtitle ? `\n${node.subtitle}` : ""}` : `${node.title}\n${node.warning}`}
      data-testid={`order-graph-node-${node.id}`}
    >
      <Handle type="target" position={Position.Left} className="!size-1.5 !border-0 !bg-muted-foreground/50" />
      <div className="min-w-0 flex-1">
        <div className="truncate text-[12.5px] font-medium leading-5">{node.title}</div>
        {node.subtitle !== undefined && <div className="truncate text-[11px] leading-4 text-muted-foreground">{node.subtitle}</div>}
      </div>
      {node.warning !== undefined && <AlertTriangle className="size-3.5 shrink-0 text-warning" aria-label={node.warning} />}
      <Handle type="source" position={Position.Right} className="!size-1.5 !border-0 !bg-muted-foreground/50" />
      {/* An arrow pointing back leaves and enters at the bottom, so it runs under the boxes, clear of the arrows forward. */}
      <Handle id="back-out" type="source" position={Position.Bottom} style={{ left: "70%" }} className="!size-1 !border-0 !bg-transparent" />
      <Handle id="back-in" type="target" position={Position.Bottom} style={{ left: "30%" }} className="!size-1 !border-0 !bg-transparent" />
    </div>
  );
}

const nodeTypes = { box: Box };

/** Lays the boxes out left to right, every arrow pointing from what goes first to what goes after it. */
function laidOut(nodes: OrderGraphNode[], links: OrderGraphLink[], selected: string | null): Node<BoxData>[] {
  const graph = new Graph();
  graph.setGraph({ rankdir: "LR", nodesep: 18, ranksep: 70, marginx: 12, marginy: 12 });
  graph.setDefaultEdgeLabel(() => ({}));
  for (const node of nodes) {
    graph.setNode(node.id, { width: NODE_WIDTH, height: NODE_HEIGHT });
  }

  for (const link of links) {
    if (graph.hasNode(link.from) && graph.hasNode(link.to) && link.from !== link.to) {
      graph.setEdge(link.from, link.to);
    }
  }

  layout(graph);
  return nodes.map((node) => {
    const placed = graph.node(node.id);
    return {
      id: node.id,
      type: "box",
      // Dagre gives the centre; React Flow places by the top left corner.
      position: { x: (placed?.x ?? 0) - NODE_WIDTH / 2, y: (placed?.y ?? 0) - NODE_HEIGHT / 2 },
      data: { node, selected: node.id === selected },
      draggable: true,
    };
  });
}

function Canvas({ nodes, links, selected, onSelect, onOpen, testId }: OrderGraphProps) {
  const { mode } = useThemeMode();
  const laid = useMemo(() => laidOut(nodes, links, selected), [nodes, links, selected]);
  const [flowNodes, setFlowNodes, onNodesChange] = useNodesState(laid);
  const view = useReactFlow();

  useEffect(() => {
    setFlowNodes(laid);
  }, [laid, setFlowNodes]);

  // A new set of boxes is fitted into view once it is drawn; a selection inside the same set keeps the view.
  const shape = useMemo(() => nodes.map((n) => n.id).join("|"), [nodes]);
  useEffect(() => {
    const handle = window.setTimeout(() => void view.fitView({ padding: 0.15, duration: 250, maxZoom: 1 }), 0);
    return () => window.clearTimeout(handle);
  }, [shape, view]);

  const edges = useMemo<Edge[]>(() => {
    const x = new Map(laid.map((node) => [node.id, node.position.x]));
    return links.map((link) => {
      const colour = link.tone === "warning" ? "var(--warning)" : "var(--muted-foreground)";
      // An arrow pointing back (a circle) is routed around the boxes rather than drawn through them.
      const back = (x.get(link.to) ?? 0) <= (x.get(link.from) ?? 0);
      return {
        id: link.id,
        source: link.from,
        target: link.to,
        type: back ? "smoothstep" : "default",
        sourceHandle: back ? "back-out" : undefined,
        targetHandle: back ? "back-in" : undefined,
        pathOptions: back ? { offset: 22, borderRadius: 8 } : undefined,
        ariaLabel: link.title,
        markerEnd: { type: MarkerType.ArrowClosed, color: colour, width: 16, height: 16 },
        style: {
          stroke: colour,
          strokeWidth: link.tone === "warning" ? 1.75 : 1.25,
          strokeDasharray: link.tone === "warning" ? "6 4" : undefined,
          opacity: link.tone === "quiet" ? 0.45 : 1,
        },
      };
    });
  }, [links, laid]);

  return (
    <ReactFlow
      nodes={flowNodes}
      edges={edges}
      nodeTypes={nodeTypes}
      onNodesChange={onNodesChange}
      onNodeClick={(_, node) => onSelect?.(node.id)}
      onNodeDoubleClick={(_, node) => onOpen?.(node.id)}
      onPaneClick={() => onSelect?.(null)}
      colorMode={mode}
      minZoom={0.1}
      nodesConnectable={false}
      data-testid={testId}
    >
      <Background gap={20} size={1} />
      <Controls showInteractive={false} />
    </ReactFlow>
  );
}

export interface OrderGraphProps {
  nodes: OrderGraphNode[];
  links: OrderGraphLink[];
  selected: string | null;
  onSelect?: (id: string | null) => void;
  /** A double click on a box. */
  onOpen?: (id: string) => void;
  testId: string;
}

/**
 * An order drawn left to right: boxes (schedules, entity types) and arrows from what goes first to what goes after it,
 * laid out in layers so every arrow points right unless a circle forces one back. Boxes can be dragged; a click selects
 * one, a double click opens it, and a click on the empty canvas clears the selection. It fills the box it is placed in.
 */
export function OrderGraph(props: OrderGraphProps) {
  return (
    <div className="h-full w-full overflow-hidden rounded-md border bg-background">
      <ReactFlowProvider>
        <Canvas {...props} />
      </ReactFlowProvider>
    </div>
  );
}
