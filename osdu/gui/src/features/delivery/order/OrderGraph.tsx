import { useEffect, useMemo, useRef, type ReactNode } from "react";
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
  type FitViewOptions,
  type Node,
  type NodeProps,
} from "@xyflow/react";
import { AlertTriangle, X } from "lucide-react";
import { Button } from "@/components/ui/button";
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

/** What the panel sliding in from the left says about the selected box. */
export interface OrderGraphDetail {
  title: string;
  /** A quieter line under the title. */
  subtitle?: string;
  /** Buttons at the end of the panel's heading, before its close button. */
  actions?: ReactNode;
  body: ReactNode;
  testId: string;
}

const NODE_WIDTH = 230;
const NODE_HEIGHT = 52;
/** The panel's width; on a narrow graph it takes at most 85% of it, so some of the graph always shows beside it. */
const PANEL_WIDTH = 380;
/** What the view keeps between the panel's edge and a box it brings into view. */
const PANEL_GAP = 24;
const FIT: FitViewOptions = { padding: 0.15, duration: 250, maxZoom: 1 };
const FIT_BESIDE_PANEL: FitViewOptions = {
  padding: { top: "24px", right: "24px", bottom: "24px", left: `${PANEL_WIDTH + PANEL_GAP}px` },
  duration: 250,
  maxZoom: 1,
};

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
function laidOut(nodes: OrderGraphNode[], links: OrderGraphLink[]): Node<BoxData>[] {
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
      data: { node, selected: false },
      draggable: true,
    };
  });
}

/** A section of the panel: a quiet heading, with a count or a name beside it kept as written, over its content. */
export function OrderPanelSection({ title, note, children }: { title: string; note?: string | number; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-2 border-t px-3 py-3 first:border-t-0">
      <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
        {title}
        {note !== undefined && <span className="ml-1.5 font-mono tabular-nums normal-case">{note}</span>}
      </h3>
      {children}
    </section>
  );
}

/** The selected box's detail, over the left of the graph: its heading and close button, and a body that scrolls. */
function DetailPanel({ detail, onClose }: { detail: OrderGraphDetail; onClose: () => void }) {
  return (
    <aside
      className="absolute inset-y-0 left-0 z-10 flex max-w-[85%] flex-col border-r bg-card shadow-lg duration-200 animate-in fade-in-0 slide-in-from-left-8"
      style={{ width: PANEL_WIDTH }}
      aria-label={detail.title}
      data-testid={detail.testId}
    >
      <header className="flex shrink-0 items-start gap-2 border-b px-3 py-2">
        <div className="min-w-0 flex-1">
          <h2 className="truncate text-[13px] font-semibold leading-5" title={detail.title}>{detail.title}</h2>
          {detail.subtitle !== undefined && (
            <p className="truncate text-[11.5px] leading-4 text-muted-foreground" title={detail.subtitle}>{detail.subtitle}</p>
          )}
        </div>
        {detail.actions}
        <Button variant="ghost" size="icon-xs" onClick={onClose} aria-label="Close" data-testid={`${detail.testId}-close`}>
          <X />
        </Button>
      </header>
      <div className="min-h-0 flex-1 overflow-y-auto">{detail.body}</div>
    </aside>
  );
}

function Canvas({ nodes, links, selected, detail, onSelect, onOpen, testId }: OrderGraphProps) {
  const { mode } = useThemeMode();
  const laid = useMemo(() => laidOut(nodes, links), [nodes, links]);
  const [flowNodes, setFlowNodes, onNodesChange] = useNodesState(laid);
  const view = useReactFlow();
  const frame = useRef<HTMLDivElement>(null);
  const fitted = useRef<string | null>(null);
  const open = detail !== undefined && detail !== null;

  useEffect(() => {
    setFlowNodes(laid);
  }, [laid, setFlowNodes]);

  // The selection is drawn on the boxes as they stand, so a box dragged aside stays where it was put.
  const shown = useMemo(
    () => flowNodes.map((node) => (node.data.selected === (node.id === selected) ? node : { ...node, data: { ...node.data, selected: node.id === selected } })),
    [flowNodes, selected],
  );

  // A new set of boxes is fitted into view once it is drawn, beside the panel while it is open; a selection inside the
  // same set, and the panel opening or closing, keep the view.
  const shape = useMemo(() => nodes.map((n) => n.id).join("|"), [nodes]);
  useEffect(() => {
    if (fitted.current === shape) {
      return;
    }

    const handle = window.setTimeout(() => {
      fitted.current = shape;
      void view.fitView(open ? FIT_BESIDE_PANEL : FIT);
    }, 0);
    return () => window.clearTimeout(handle);
  }, [shape, open, view]);

  // The box the panel describes is moved clear of the panel when it would sit under it, and back into view when it is
  // past the graph's right edge.
  useEffect(() => {
    if (!open || selected === null) {
      return;
    }

    const handle = window.setTimeout(() => {
      const node = view.getInternalNode(selected);
      const pane = frame.current;
      if (node === undefined || pane === null) {
        return;
      }

      const { x, y, zoom } = view.getViewport();
      const width = Math.min(PANEL_WIDTH, pane.clientWidth * 0.85);
      const left = node.internals.positionAbsolute.x * zoom + x;
      const right = left + (node.measured.width ?? NODE_WIDTH) * zoom;
      const shift = left < width + PANEL_GAP ? width + PANEL_GAP - left
        : right > pane.clientWidth - PANEL_GAP ? Math.max(width + PANEL_GAP - left, pane.clientWidth - PANEL_GAP - right) : 0;
      if (shift !== 0) {
        void view.setViewport({ x: x + shift, y, zoom }, { duration: 250 });
      }
    }, 0);
    return () => window.clearTimeout(handle);
  }, [open, selected, view]);

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
    <div
      ref={frame}
      className="relative h-full w-full"
      onKeyDown={(event) => {
        if (event.key === "Escape" && open) {
          event.stopPropagation();
          onSelect?.(null);
        }
      }}
    >
      <ReactFlow
        nodes={shown}
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
        {/* Top right, clear of the panel that slides in from the left. */}
        <Controls position="top-right" showInteractive={false} fitViewOptions={open ? FIT_BESIDE_PANEL : FIT} />
      </ReactFlow>
      {open && <DetailPanel detail={detail} onClose={() => onSelect?.(null)} />}
    </div>
  );
}

export interface OrderGraphProps {
  nodes: OrderGraphNode[];
  links: OrderGraphLink[];
  selected: string | null;
  /** The selected box's detail, shown in a panel sliding in from the left over part of the graph; null hides it. */
  detail?: OrderGraphDetail | null;
  onSelect?: (id: string | null) => void;
  /** A double click on a box. */
  onOpen?: (id: string) => void;
  testId: string;
}

/**
 * An order drawn left to right: boxes (schedules, entity types) and arrows from what goes first to what goes after it,
 * laid out in layers so every arrow points right unless a circle forces one back. Boxes can be dragged; a click selects
 * one and slides its detail in from the left, over part of the graph, a double click opens it, and a click on the empty
 * canvas, the panel's close button or Escape clears the selection. It fills the box it is placed in.
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
