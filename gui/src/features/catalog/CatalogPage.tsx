import { useCallback, useMemo } from "react";
import { useSearchParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useSnackbar } from "notistack";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Paper from "@mui/material/Paper";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";
import SyncIcon from "@mui/icons-material/Sync";
import { repoSourceApi } from "../../api/endpoints";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { CatalogTree } from "./CatalogTree";
import { FlowDetailsPanel } from "./FlowDetailsPanel";
import { ObjectDetailsPanel } from "./ObjectDetailsPanel";
import { ancestorIds, decodeNodeId, encodeNodeId } from "./nodeIds";

const SOURCE_FETCH_CAP = 200;
const RECOMPUTE_POLL_MS = 2000;
const RECOMPUTE_TIMEOUT_MS = 120000;

const delay = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

/**
 * The catalog: an explorer-style tree over every object and flow the catalog knows (discovered from the flow
 * YAML and the lineage analysis), with a details panel per node. The selected node lives in the URL as
 * ?node=<id> so any node is deep-linkable and back/forward navigation walks the selection history.
 */
export default function CatalogPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const selectedId = searchParams.get("node");
  const selectedNode = useMemo(() => (selectedId === null ? null : decodeNodeId(selectedId)), [selectedId]);

  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();

  // The catalog is built by the managed sync of the registered git sources, so "recompute" forces each enabled
  // source to re-sync now (a full lineage recompute, including the object body/column enrichment), then waits for
  // the background sync to finish before refreshing the tree and details from the freshly-synced catalog.
  const sourcesQuery = useQuery({
    queryKey: ["repo-sources", "list", SOURCE_FETCH_CAP],
    queryFn: () => repoSourceApi.list({ page: 1, pageSize: SOURCE_FETCH_CAP }),
  });

  const recompute = useMutation({
    mutationFn: async () => {
      const sources = (sourcesQuery.data?.items ?? []).filter((source) => source.enabled);
      if (sources.length === 0) {
        throw new Error("No enabled git source to recompute. Register or enable one on the Repos page first.");
      }

      const before = new Map(sources.map((source) => [source.id, source.lastSyncUtc]));
      await Promise.all(sources.map((source) => repoSourceApi.syncNow(source.id)));

      // Each source's last-sync stamp advances once its sync (success or failure) completes; wait for all of them
      // so the refresh below reads the recomputed catalog rather than the stale one, bounded by a timeout.
      const deadline = Date.now() + RECOMPUTE_TIMEOUT_MS;
      while (Date.now() < deadline) {
        await delay(RECOMPUTE_POLL_MS);
        const latest = await repoSourceApi.list({ page: 1, pageSize: SOURCE_FETCH_CAP });
        const settled = sources.every((source) => {
          const now = latest.items.find((item) => item.id === source.id)?.lastSyncUtc ?? null;
          return now !== null && now !== before.get(source.id);
        });
        if (settled) {
          break;
        }
      }

      return sources.length;
    },
    onSuccess: (count) => {
      enqueueSnackbar(`Recompute complete for ${count} source${count === 1 ? "" : "s"}.`, { variant: "success" });
      void queryClient.invalidateQueries();
    },
    onError: (error) =>
      enqueueSnackbar(error instanceof Error ? error.message : String(error), { variant: "error" }),
  });

  // The tree starts with the roots open, plus the deep-linked node's ancestor chain so it is visible.
  const initialExpanded = useMemo(() => {
    const roots = [
      encodeNodeId({ type: "databasesRoot" }),
      encodeNodeId({ type: "sourcesRoot" }),
      encodeNodeId({ type: "flowsRoot" }),
    ];
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
        actions={(
          <Tooltip title="Re-sync every enabled git source now and recompute lineage, filling object code and columns from the run history. Refreshes when the sync completes.">
            <span>
              <Button
                variant="outlined"
                startIcon={<SyncIcon fontSize="small" />}
                onClick={() => recompute.mutate()}
                disabled={recompute.isPending || sourcesQuery.data === undefined}
                data-testid="catalog-recompute"
              >
                {recompute.isPending ? "Recomputing…" : "Recompute lineage"}
              </Button>
            </span>
          </Tooltip>
        )}
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
