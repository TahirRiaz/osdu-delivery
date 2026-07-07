import { useState, type MouseEvent } from "react";
import { useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import CircularProgress from "@mui/material/CircularProgress";
import Divider from "@mui/material/Divider";
import Link from "@mui/material/Link";
import ListItemText from "@mui/material/ListItemText";
import Menu from "@mui/material/Menu";
import MenuItem from "@mui/material/MenuItem";
import Stack from "@mui/material/Stack";
import Typography from "@mui/material/Typography";
import AccountTreeIcon from "@mui/icons-material/AccountTree";
import CallSplitIcon from "@mui/icons-material/CallSplit";
import ChevronRightIcon from "@mui/icons-material/ChevronRight";
import InsertDriveFileOutlinedIcon from "@mui/icons-material/InsertDriveFileOutlined";
import LayersOutlinedIcon from "@mui/icons-material/LayersOutlined";
import TableRowsOutlinedIcon from "@mui/icons-material/TableRowsOutlined";
import { lineageApi } from "../../api/endpoints";

/**
 * What a search hit can open in the lineage graph. An `object` target resolves its repo(s) on demand (the same
 * physical object can appear in several repos' graphs, so the user picks); a `node` target already knows its repo
 * (a flow), so it opens directly; a `file` target resolves the pipeline(s) that ingest the file by matching its
 * name/path against the flow source specs in the catalog (so a file jumps to the flow that feeds it).
 */
export type LineageJumpTarget =
  | { kind: "object"; objectKey: string; objectKind: string; label: string; sublabel?: string }
  | { kind: "node"; repoId: string; repoName: string; focusId: string; label: string; sublabel?: string }
  | { kind: "file"; filePath: string; label: string; sublabel?: string };

function TargetIcon({ target }: { target: LineageJumpTarget }) {
  if (target.kind === "node") {
    return <AccountTreeIcon fontSize="small" color="primary" />;
  }
  if (target.kind === "file") {
    return <InsertDriveFileOutlinedIcon fontSize="small" color="primary" />;
  }
  const k = target.objectKind.toLowerCase();
  if (k === "view") {
    return <LayersOutlinedIcon fontSize="small" color="primary" />;
  }
  if (k === "file") {
    return <InsertDriveFileOutlinedIcon fontSize="small" color="primary" />;
  }
  if (k === "procedure" || k === "function" || k === "trigger") {
    return <CallSplitIcon fontSize="small" color="primary" />;
  }
  return <TableRowsOutlinedIcon fontSize="small" color="primary" />;
}

/**
 * The lineage jump: a labeled action that opens a small picker showing exactly what you are about to open and, for
 * an object that lives in more than one repo, every graph it appears in (the repo whose flow populates it flagged
 * "populates") so you choose which to trace. A single target still shows the picker so the destination is never a
 * surprise. Selecting an option deep-links the graph focused on that node.
 */
export function LineageJumpButton({
  target, variant = "text", fullLabel = false,
}: {
  target: LineageJumpTarget;
  variant?: "text" | "outlined";
  /** When true, the trigger reads "View lineage"; otherwise the compact "Lineage" for dense rows. */
  fullLabel?: boolean;
}) {
  const navigate = useNavigate();
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  const open = anchor !== null;

  // Resolve which repos' graphs an object appears in (writing repo first), only once the picker is opened.
  const repos = useQuery({
    queryKey: ["lineage-object-repos", target.kind === "object" ? target.objectKey : ""],
    queryFn: () => lineageApi.objectRepos(target.kind === "object" ? target.objectKey : ""),
    enabled: open && target.kind === "object",
  });

  // Resolve which pipeline(s) ingest a file by matching it against the flow source specs in the catalog.
  const filePipelines = useQuery({
    queryKey: ["file-pipelines", target.kind === "file" ? target.filePath : ""],
    queryFn: () => lineageApi.filePipelines(target.kind === "file" ? target.filePath : ""),
    enabled: open && target.kind === "file",
  });

  const openMenu = (event: MouseEvent<HTMLElement>) => {
    event.stopPropagation();
    setAnchor(event.currentTarget);
  };
  const close = () => setAnchor(null);
  const go = (repoId: string, focusId: string) => {
    close();
    navigate(`/lineage?repoId=${encodeURIComponent(repoId)}&focus=${encodeURIComponent(focusId)}`);
  };

  const objectRepos = target.kind === "object" ? (repos.data ?? []) : [];
  const multi = objectRepos.length > 1;

  return (
    <>
      <Button
        size="small"
        variant={variant}
        startIcon={<AccountTreeIcon />}
        onClick={openMenu}
        aria-label="View in lineage graph"
        data-testid="search-open-graph"
        sx={{ whiteSpace: "nowrap", flexShrink: 0 }}
      >
        {fullLabel ? "View lineage" : "Lineage"}
      </Button>

      <Menu
        anchorEl={anchor}
        open={open}
        onClose={close}
        onClick={(event) => event.stopPropagation()}
        anchorOrigin={{ vertical: "bottom", horizontal: "right" }}
        transformOrigin={{ vertical: "top", horizontal: "right" }}
        slotProps={{ paper: { sx: { minWidth: 300, maxWidth: 380, overflow: "hidden" } } }}
      >
        <Box sx={{ px: 2, py: 1.5 }} data-testid="lineage-jump-menu">
          <Typography variant="overline" color="text.secondary" sx={{ display: "block", lineHeight: 1.6 }}>
            Open in lineage graph
          </Typography>
          <Stack direction="row" spacing={1} alignItems="center" sx={{ mt: 0.25 }}>
            <TargetIcon target={target} />
            <Box sx={{ minWidth: 0 }}>
              <Typography variant="body2" fontWeight={700} noWrap>{target.label}</Typography>
              {target.sublabel !== undefined && (
                <Typography variant="caption" color="text.secondary" noWrap component="div">
                  {target.sublabel}
                </Typography>
              )}
            </Box>
          </Stack>
        </Box>
        <Divider />

        {target.kind === "node" && (
          <MenuItem data-testid="lineage-jump-option" onClick={() => go(target.repoId, target.focusId)} sx={{ py: 1 }}>
            <ListItemText primary={target.repoName} secondary="Trace this flow in the graph" />
            <ChevronRightIcon fontSize="small" color="action" />
          </MenuItem>
        )}

        {target.kind === "object" && (
          repos.isPending ? (
            <MenuItem disabled sx={{ py: 1.5 }}>
              <CircularProgress size={16} sx={{ mr: 1.5 }} /> Finding graphs…
            </MenuItem>
          ) : objectRepos.length === 0 ? (
            <Box sx={{ px: 2, py: 1.5 }} data-testid="lineage-jump-empty">
              <Typography variant="body2" color="text.secondary">
                This object has no recorded lineage yet, so it is not in any graph.
              </Typography>
              <Link
                component="button"
                type="button"
                variant="body2"
                sx={{ mt: 0.5 }}
                onClick={() => { close(); navigate("/lineage/objects"); }}
              >
                Open in the object explorer
              </Link>
            </Box>
          ) : (
            <>
              {multi && (
                <Typography variant="caption" color="text.secondary" sx={{ px: 2, py: 0.5, display: "block" }}>
                  {`Appears in ${objectRepos.length} repos — choose one`}
                </Typography>
              )}
              {objectRepos.map((repo) => (
                <MenuItem
                  key={repo.repoId}
                  data-testid="lineage-jump-option"
                  onClick={() => go(repo.repoId, target.objectKey)}
                  sx={{ py: 1 }}
                >
                  <ListItemText
                    primary={repo.repoName}
                    secondary={`${repo.edgeCount} lineage reference${repo.edgeCount === 1 ? "" : "s"}`}
                  />
                  {repo.writes && (
                    <Chip size="small" color="primary" label="populates" sx={{ mx: 1, height: 20 }} />
                  )}
                  <ChevronRightIcon fontSize="small" color="action" />
                </MenuItem>
              ))}
            </>
          )
        )}

        {target.kind === "file" && (
          filePipelines.isPending ? (
            <MenuItem disabled sx={{ py: 1.5 }}>
              <CircularProgress size={16} sx={{ mr: 1.5 }} /> Matching flows…
            </MenuItem>
          ) : (filePipelines.data?.length ?? 0) === 0 ? (
            <Box sx={{ px: 2, py: 1.5 }} data-testid="lineage-jump-empty">
              <Typography variant="body2" color="text.secondary">
                No file flow's source pattern matches this file.
              </Typography>
            </Box>
          ) : (
            <>
              {(filePipelines.data?.length ?? 0) > 1 && (
                <Typography variant="caption" color="text.secondary" sx={{ px: 2, py: 0.5, display: "block" }}>
                  {`Ingested by ${filePipelines.data!.length} flows — choose one`}
                </Typography>
              )}
              {filePipelines.data!.map((match) => (
                <MenuItem
                  key={match.pipelineId}
                  data-testid="lineage-jump-option"
                  onClick={() => go(match.repoId, match.pipelineId)}
                  sx={{ py: 1 }}
                >
                  <ListItemText
                    primary={match.pipelineName}
                    secondary={`${match.repoName} · matches ${match.pattern}`}
                  />
                  {match.pathConfirmed && (
                    <Chip size="small" color="primary" label="path match" sx={{ mx: 1, height: 20 }} />
                  )}
                  <ChevronRightIcon fontSize="small" color="action" />
                </MenuItem>
              ))}
            </>
          )
        )}
      </Menu>
    </>
  );
}
