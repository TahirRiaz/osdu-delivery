import { useCallback, useMemo } from "react";
import { useSearchParams } from "react-router-dom";
import Box from "@mui/material/Box";
import Paper from "@mui/material/Paper";
import Typography from "@mui/material/Typography";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { CatalogTree } from "./CatalogTree";
import { FlowDetailsPanel } from "./FlowDetailsPanel";
import { ObjectDetailsPanel } from "./ObjectDetailsPanel";
import { ancestorIds, decodeNodeId, encodeNodeId } from "./nodeIds";

/**
 * The catalog: an explorer-style tree over every object and flow the catalog knows (discovered from the flow
 * YAML and the lineage analysis), with a details panel per node. The selected node lives in the URL as
 * ?node=<id> so any node is deep-linkable and back/forward navigation walks the selection history.
 */
export default function CatalogPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const selectedId = searchParams.get("node");
  const selectedNode = useMemo(() => (selectedId === null ? null : decodeNodeId(selectedId)), [selectedId]);

  // The tree starts with the roots open, plus the deep-linked node's ancestor chain so it is visible.
  const initialExpanded = useMemo(() => {
    const roots = [encodeNodeId({ type: "objectsRoot" }), encodeNodeId({ type: "flowsRoot" })];
    return selectedNode === null ? roots : [...new Set([...roots, ...ancestorIds(selectedNode)])];
    // Intentionally computed once per mount from the URL at that moment; later selection changes only add
    // to the user's own expansion via the tree's controlled state.
  }, []);

  const onSelect = useCallback((id: string) => {
    setSearchParams((current) => {
      const next = new URLSearchParams(current);
      next.set("node", id);
      return next;
    });
  }, [setSearchParams]);

  return (
    <Page data-testid="page-catalog">
      <PageHeader
        title="Catalog"
        subtitle="Every object and flow discovered from the flow YAML and the lineage analysis, as a browsable tree."
      />
      <Box
        sx={{
          display: "grid",
          gap: 2,
          gridTemplateColumns: { xs: "1fr", md: "minmax(320px, 420px) minmax(0, 1fr)" },
          alignItems: "start",
        }}
      >
        <Paper variant="outlined" sx={{ p: 1.5, maxHeight: "calc(100vh - 220px)", overflow: "auto" }}>
          <CatalogTree selectedId={selectedId} onSelect={onSelect} initialExpanded={initialExpanded} />
        </Paper>
        <Paper variant="outlined" sx={{ p: 2.5, minHeight: 320 }}>
          {selectedNode === null && (
            <Typography variant="body2" color="text.secondary" data-testid="catalog-details-placeholder">
              Select an object or a flow in the tree to see its details: overview, columns, code, and the
              relationships extracted from the SQL.
            </Typography>
          )}
          {selectedNode !== null && selectedNode.type === "object" && (
            <ObjectDetailsPanel objectKey={selectedNode.objectKey} />
          )}
          {selectedNode !== null && selectedNode.type === "flow" && (
            <FlowDetailsPanel repoId={selectedNode.repoId} pipelineId={selectedNode.pipelineId} />
          )}
          {selectedNode !== null && selectedNode.type !== "object" && selectedNode.type !== "flow" && (
            <Typography variant="body2" color="text.secondary" data-testid="catalog-details-folder">
              This is a grouping level. Expand it in the tree and select an object or a flow for details.
            </Typography>
          )}
        </Paper>
      </Box>
    </Page>
  );
}
