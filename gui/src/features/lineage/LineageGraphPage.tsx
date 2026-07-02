import { useEffect, useMemo } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import {
  Background,
  Controls,
  MarkerType,
  MiniMap,
  ReactFlow,
  useNodesState,
  type Edge as FlowEdge,
  type Node as FlowNode,
} from "@xyflow/react";
import dagre from "@dagrejs/dagre";
import Box from "@mui/material/Box";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Chip from "@mui/material/Chip";
import FormControl from "@mui/material/FormControl";
import MenuItem from "@mui/material/MenuItem";
import Paper from "@mui/material/Paper";
import Select from "@mui/material/Select";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import Typography from "@mui/material/Typography";
import { useTheme } from "@mui/material/styles";
import { isApiError } from "../../api/client";
import { lineageApi, repoApi } from "../../api/endpoints";
import type { FlowDependency } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import "@xyflow/react/dist/style.css";

const NODE_WIDTH = 200;
const NODE_HEIGHT = 56;
const GRAPH_HEIGHT = "max(calc(100vh - 220px), 480px)";

function truncate(value: string, max: number): string {
  return value.length > max ? `${value.slice(0, max)}...` : value;
}

/** Dagre left-to-right layout; dagre yields node centers, React Flow wants top-left corners. */
function layoutNodes(nodes: FlowNode[], edges: FlowEdge[]): FlowNode[] {
  const graph = new dagre.graphlib.Graph();
  graph.setDefaultEdgeLabel(() => ({}));
  graph.setGraph({ rankdir: "LR", nodesep: 40, ranksep: 120 });
  for (const node of nodes) {
    graph.setNode(node.id, { width: NODE_WIDTH, height: NODE_HEIGHT });
  }
  for (const edge of edges) {
    graph.setEdge(edge.source, edge.target);
  }
  dagre.layout(graph);
  return nodes.map((node) => {
    const position = graph.node(node.id);
    return { ...node, position: { x: position.x - NODE_WIDTH / 2, y: position.y - NODE_HEIGHT / 2 } };
  });
}

interface DependencyGraphProps {
  nodes: FlowNode[];
  edges: FlowEdge[];
  colorMode: "light" | "dark";
  onNodeClick: (pipelineId: string) => void;
}

/** The canvas itself: node positions live in React Flow state so dragging works; data changes re-seed them. */
function DependencyGraph({ nodes, edges, colorMode, onNodeClick }: DependencyGraphProps) {
  const [flowNodes, setFlowNodes, onNodesChange] = useNodesState(nodes);

  useEffect(() => {
    setFlowNodes(nodes);
  }, [nodes, setFlowNodes]);

  return (
    <ReactFlow
      nodes={flowNodes}
      edges={edges}
      onNodesChange={onNodesChange}
      onNodeClick={(_, node) => onNodeClick(node.id)}
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
 * The repo-scoped dependency graph: pipelines as nodes (laid out by execution wave, left to right) and
 * flow dependencies as edges, with the wave breakdown alongside. Deep-linkable via ?repoId=.
 */
export default function LineageGraphPage() {
  const navigate = useNavigate();
  const theme = useTheme();
  const [searchParams, setSearchParams] = useSearchParams();
  const repoId = searchParams.get("repoId") ?? "";

  const repos = useQuery({
    queryKey: ["repos", "for-lineage-graph"],
    queryFn: () => repoApi.list({ page: 1, pageSize: 200 }),
  });

  const waves = useQuery({
    queryKey: ["lineage-waves", repoId],
    queryFn: () => lineageApi.waves(repoId),
    enabled: repoId !== "",
  });

  const dependencies = useQuery({
    queryKey: ["lineage-dependencies", repoId],
    enabled: repoId !== "",
    queryFn: async () => {
      const all: FlowDependency[] = [];
      let page = 1;
      for (;;) {
        const result = await lineageApi.dependencies(repoId, { page, pageSize: 200 });
        all.push(...result.items);
        if (all.length >= result.total || result.items.length === 0) {
          break;
        }
        page += 1;
      }
      return all;
    },
  });

  const changeRepo = (value: string) => {
    setSearchParams((previous) => {
      const next = new URLSearchParams(previous);
      if (value === "") {
        next.delete("repoId");
      } else {
        next.set("repoId", value);
      }
      return next;
    }, { replace: true });
  };

  const repoItems = repos.data?.items ?? [];
  const selectValue = repoItems.some((repo) => repo.id === repoId) ? repoId : "";

  const sortedWaves = useMemo(() => {
    if (!waves.data) {
      return [];
    }
    const rank = (wave: number) => (wave === -1 ? Number.MAX_SAFE_INTEGER : wave);
    return [...waves.data].sort((a, b) => rank(a.wave) - rank(b.wave));
  }, [waves.data]);

  const graph = useMemo(() => {
    if (!waves.data || !dependencies.data) {
      return null;
    }

    const nodesById = new Map<string, FlowNode>();
    for (const wave of waves.data) {
      for (const pipeline of wave.pipelines) {
        if (nodesById.has(pipeline.id)) {
          continue;
        }
        nodesById.set(pipeline.id, {
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
          style: { width: NODE_WIDTH, height: NODE_HEIGHT, padding: 8, borderRadius: 8 },
        });
      }
    }

    const edges: FlowEdge[] = [];
    for (const dependency of dependencies.data) {
      if (!nodesById.has(dependency.fromPipelineId) || !nodesById.has(dependency.toPipelineId)) {
        continue;
      }
      edges.push({
        id: String(dependency.id),
        source: dependency.fromPipelineId,
        target: dependency.toPipelineId,
        label: dependency.viaObjects === "" ? undefined : truncate(dependency.viaObjects, 60),
        markerEnd: { type: MarkerType.ArrowClosed },
      });
    }

    return { nodes: layoutNodes([...nodesById.values()], edges), edges };
  }, [waves.data, dependencies.data]);

  const queryError = [repos, waves, dependencies].find((query) => query.isError)?.error;
  const loadingGraph = repoId !== "" && (waves.isPending || dependencies.isPending);
  const hasPipelines = graph !== null && graph.nodes.length > 0;

  return (
    <Box data-testid="page-lineage-graph">
      <Stack direction="row" alignItems="center" justifyContent="space-between" sx={{ mb: 2 }} flexWrap="wrap" gap={1}>
        <Typography variant="h5" fontWeight={600}>Lineage graph</Typography>
        <FormControl size="small" sx={{ minWidth: 280 }}>
          <Select
            value={selectValue}
            onChange={(event) => changeRepo(event.target.value)}
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
            The dependency graph is computed per repo: select one above to see its pipelines, waves, and the
            objects that connect them.
          </Typography>
        </Paper>
      )}

      {loadingGraph && !queryError && (
        <Skeleton variant="rectangular" sx={{ height: 480, borderRadius: 1 }} data-testid="graph-loading" />
      )}

      {repoId !== "" && !loadingGraph && !queryError && graph !== null && !hasPipelines && (
        <Paper variant="outlined" sx={{ p: 6, textAlign: "center" }} data-testid="graph-no-lineage">
          <Typography variant="h6" color="text.secondary" gutterBottom>No lineage for this repo yet</Typography>
          <Typography variant="body2" color="text.secondary">
            No waves or pipelines were found. Sync the repo and run lineage computation, then come back.
          </Typography>
        </Paper>
      )}

      {repoId !== "" && !loadingGraph && !queryError && graph !== null && hasPipelines && (
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
            <DependencyGraph
              nodes={graph.nodes}
              edges={graph.edges}
              colorMode={theme.palette.mode}
              onNodeClick={(pipelineId) => navigate(`/pipelines/${pipelineId}`)}
            />
          </Box>
          <Card
            variant="outlined"
            sx={{ width: { xs: "100%", lg: 320 }, flexShrink: 0, maxHeight: GRAPH_HEIGHT, overflowY: "auto" }}
            data-testid="wave-list"
          >
            <CardContent>
              <Typography variant="subtitle1" fontWeight={600} gutterBottom>Execution waves</Typography>
              {sortedWaves.map((wave) => (
                <Box key={wave.wave} sx={{ mb: 1.5 }}>
                  <Typography variant="subtitle2" color="text.secondary">
                    {wave.wave === -1 ? "Unwaved (lineage not computed)" : `Wave ${wave.wave}`}
                  </Typography>
                  <Box sx={{ display: "flex", flexWrap: "wrap", gap: 0.5, mt: 0.5 }}>
                    {wave.pipelines.map((pipeline) => (
                      <Chip
                        key={pipeline.id}
                        label={pipeline.name}
                        size="small"
                        onClick={() => navigate(`/pipelines/${pipeline.id}`)}
                        data-testid={`wave-pipeline-${pipeline.id}`}
                      />
                    ))}
                  </Box>
                </Box>
              ))}
            </CardContent>
          </Card>
        </Stack>
      )}
    </Box>
  );
}
