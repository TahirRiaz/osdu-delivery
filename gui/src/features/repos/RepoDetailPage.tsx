import { useState, type ReactNode } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Chip from "@mui/material/Chip";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import Typography from "@mui/material/Typography";
import AccountTreeIcon from "@mui/icons-material/AccountTree";
import PlayArrowIcon from "@mui/icons-material/PlayArrow";
import { isApiError } from "../../api/client";
import { pipelineApi, repoApi } from "../../api/endpoints";
import type { PipelineSummary } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { ActiveBadge } from "../../components/StatusBadge";
import { TriggerRunDialog } from "../runs/TriggerRunDialog";

/** A caption/value pair for detail header cards; shared by the repo and pipeline detail pages. */
export function DetailPair({ label, children }: { label: string; children: ReactNode }) {
  return (
    <Box sx={{ minWidth: 160 }}>
      <Typography variant="caption" color="text.secondary" display="block">{label}</Typography>
      <Typography variant="body2" component="div">{children}</Typography>
    </Box>
  );
}

const pipelineColumns: Column<PipelineSummary>[] = [
  {
    id: "name",
    header: "Name",
    render: (row) => <Typography variant="body2" fontWeight={600}>{row.name}</Typography>,
  },
  { id: "kind", header: "Kind", render: (row) => <Chip size="small" label={row.kind} variant="outlined" /> },
  { id: "batch", header: "Batch", render: (row) => row.batch ?? "-" },
  { id: "wave", header: "Wave", render: (row) => (row.wave === -1 ? "-" : String(row.wave)) },
  { id: "active", header: "Active", render: (row) => <ActiveBadge active={row.active} /> },
  { id: "relativePath", header: "Path", render: (row) => row.relativePath },
];

/** One repo: its sync facts, quick actions (lineage graph, trigger run), and its pipelines. */
export default function RepoDetailPage() {
  const { repoId = "" } = useParams();
  const navigate = useNavigate();
  const [triggerOpen, setTriggerOpen] = useState(false);

  const repoQuery = useQuery({
    queryKey: ["repos", "detail", repoId],
    queryFn: () => repoApi.getById(repoId),
    enabled: repoId !== "",
  });

  if (repoQuery.isError) {
    return isApiError(repoQuery.error)
      ? <CorrelationError error={repoQuery.error} />
      : <Typography color="error">{String(repoQuery.error)}</Typography>;
  }

  const repo = repoQuery.data;

  return (
    <Stack spacing={2} data-testid="page-repo-detail">
      <Card variant="outlined">
        <CardContent>
          {repo === undefined ? (
            <Stack spacing={1}>
              <Skeleton width={280} height={36} />
              <Skeleton width="60%" />
              <Skeleton width="40%" />
            </Stack>
          ) : (
            <Stack spacing={2}>
              <Stack direction="row" spacing={2} alignItems="center" flexWrap="wrap" useFlexGap>
                <Typography variant="h5" sx={{ fontWeight: 600 }}>{repo.name}</Typography>
                <Box sx={{ flexGrow: 1 }} />
                <Button
                  variant="outlined"
                  startIcon={<AccountTreeIcon />}
                  onClick={() => navigate(`/lineage/graph?repoId=${repoId}`)}
                  data-testid="repo-lineage-graph"
                >
                  Lineage graph
                </Button>
                <Button
                  variant="contained"
                  startIcon={<PlayArrowIcon />}
                  onClick={() => setTriggerOpen(true)}
                  data-testid="open-trigger-run"
                >
                  Trigger run
                </Button>
              </Stack>
              <Stack direction="row" spacing={4} flexWrap="wrap" useFlexGap>
                <DetailPair label="Remote URL">{repo.remoteUrl ?? "-"}</DetailPair>
                <DetailPair label="Root path">{repo.rootPath ?? "-"}</DetailPair>
                <DetailPair label="First seen"><RelativeTime value={repo.firstSeenUtc} /></DetailPair>
                <DetailPair label="Last sync"><RelativeTime value={repo.lastSyncUtc} /></DetailPair>
              </Stack>
            </Stack>
          )}
        </CardContent>
      </Card>

      <Typography variant="h6">Pipelines</Typography>
      <PagedTable
        queryKey={["pipelines", "by-repo", repoId]}
        fetchPage={(page, pageSize) => pipelineApi.list({ repoId, page, pageSize })}
        columns={pipelineColumns}
        rowKey={(row) => row.id}
        onRowClick={(row) => navigate(`/pipelines/${row.id}`)}
        emptyMessage="This repo has no pipelines."
      />

      {triggerOpen && <TriggerRunDialog open onClose={() => setTriggerOpen(false)} repoId={repoId} />}
    </Stack>
  );
}
