import { useState } from "react";
import { Link as RouterLink } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import Box from "@mui/material/Box";
import Chip from "@mui/material/Chip";
import Link from "@mui/material/Link";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import Tab from "@mui/material/Tab";
import Tabs from "@mui/material/Tabs";
import Typography from "@mui/material/Typography";
import { isApiError } from "../../api/client";
import { lineageApi, pipelineApi, repoApi } from "../../api/endpoints";
import type { FlowDependency } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { DetailPair } from "../../components/DetailPair";
import { LineageJumpButton } from "../../components/LineageJumpButton";
import { RelativeTime } from "../../components/RelativeTime";

/** One direction of a flow's dependency table: the other flow and the objects that mediate the edge. */
function dependencyColumns(otherFlow: (row: FlowDependency) => { name: string; pipelineId: string }): Column<FlowDependency>[] {
  return [
    {
      id: "flow",
      header: "Flow",
      render: (row) => {
        const other = otherFlow(row);
        return (
          <Link component={RouterLink} to={`/pipelines/${other.pipelineId}`} variant="body2">
            {other.name}
          </Link>
        );
      },
    },
    {
      id: "via",
      header: "Via objects",
      render: (row) => (
        <Typography variant="body2" sx={{ wordBreak: "break-word" }}>{row.viaObjects || "-"}</Typography>
      ),
    },
  ];
}

const waitsForColumns = dependencyColumns((row) => ({ name: row.fromFlow, pipelineId: row.fromPipelineId }));
const unblocksColumns = dependencyColumns((row) => ({ name: row.toFlow, pipelineId: row.toPipelineId }));

/**
 * The catalog tree's details panel for a flow: identity and scheduling facts (kind, batch, wave, lifecycle),
 * the authored YAML, and the flow-to-flow dependencies the lineage engine derived, split into what this flow
 * waits for and what it unblocks.
 */
export function FlowDetailsPanel({ repoId, pipelineId }: { repoId: string; pipelineId: string }) {
  const [tab, setTab] = useState<"overview" | "yaml" | "dependencies">("overview");

  const pipeline = useQuery({
    queryKey: ["catalog-pipeline", pipelineId],
    queryFn: () => pipelineApi.getById(pipelineId),
  });
  const repo = useQuery({
    queryKey: ["catalog-repo", repoId],
    queryFn: () => repoApi.getById(repoId),
  });
  const dependencies = useQuery({
    queryKey: ["catalog-flow-dependencies", repoId, pipelineId],
    queryFn: () => lineageApi.dependencies(repoId, { pipelineId, pageSize: 200 }),
    enabled: tab === "dependencies",
  });

  if (pipeline.isPending) {
    return (
      <Box>
        <Skeleton width={280} height={40} />
        <Skeleton width="100%" />
        <Skeleton width="70%" />
        <Skeleton variant="rectangular" height={280} sx={{ mt: 2 }} />
      </Box>
    );
  }
  if (pipeline.isError) {
    return isApiError(pipeline.error)
      ? <CorrelationError error={pipeline.error} />
      : <Typography color="error">{String(pipeline.error)}</Typography>;
  }

  const flow = pipeline.data;
  const waitsFor = (dependencies.data?.items ?? []).filter((row) => row.toPipelineId === pipelineId);
  const unblocks = (dependencies.data?.items ?? []).filter((row) => row.fromPipelineId === pipelineId);

  return (
    <Box data-testid="catalog-flow-details">
      <Stack direction="row" spacing={1.5} alignItems="center" flexWrap="wrap" useFlexGap sx={{ mb: 0.5 }}>
        <Typography variant="h6" sx={{ minWidth: 0, wordBreak: "break-word" }}>{flow.name}</Typography>
        <Chip label={flow.kind} size="small" />
        {!flow.active && <Chip label="inactive" size="small" color="warning" variant="outlined" />}
        <Box sx={{ flexGrow: 1 }} />
        <Link component={RouterLink} to={`/pipelines/${flow.id}`} variant="body2" sx={{ whiteSpace: "nowrap" }}>
          Open pipeline page
        </Link>
        <LineageJumpButton
          target={{
            kind: "node",
            repoId: flow.repoId,
            repoName: repo.data?.name ?? flow.repoId,
            focusId: flow.id,
            label: flow.name,
            sublabel: flow.relativePath,
          }}
          variant="outlined"
        />
      </Stack>
      <Typography variant="body2" color="text.secondary" sx={{ wordBreak: "break-all", mb: 1.5 }}>
        {flow.relativePath}
      </Typography>

      <Tabs value={tab} onChange={(_event, value) => setTab(value)} sx={{ mb: 2 }} variant="scrollable">
        <Tab label="Overview" value="overview" data-testid="catalog-tab-flow-overview" />
        <Tab label="YAML" value="yaml" data-testid="catalog-tab-flow-yaml" />
        <Tab label="Dependencies" value="dependencies" data-testid="catalog-tab-flow-dependencies" />
      </Tabs>

      {tab === "overview" && (
        <Box sx={{ display: "grid", gap: 1.5, gridTemplateColumns: "repeat(2, minmax(0, 1fr))" }}>
          <DetailPair label="Repo">{repo.data?.name ?? repoId}</DetailPair>
          <DetailPair label="Batch">{flow.batch ?? "default"}</DetailPair>
          <DetailPair label="Wave">{flow.wave}</DetailPair>
          <DetailPair label="Lifecycle">{flow.lifecycle}</DetailPair>
          <DetailPair label="Execution mode">{flow.executionMode}</DetailPair>
          <DetailPair label="Active">{flow.active ? "yes" : "no"}</DetailPair>
          <DetailPair label="Source server">{flow.sourceServer ?? "-"}</DetailPair>
          <DetailPair label="Target server">{flow.targetServer ?? "-"}</DetailPair>
          <DetailPair label="First seen"><RelativeTime value={flow.firstSeenUtc} /></DetailPair>
          <DetailPair label="Last seen"><RelativeTime value={flow.lastSeenUtc} /></DetailPair>
        </Box>
      )}

      {tab === "yaml" && (
        <CodeView value={flow.yaml} language="yaml" height={420} data-testid="catalog-flow-yaml" />
      )}

      {tab === "dependencies" && (
        dependencies.isPending ? (
          <Skeleton variant="rectangular" height={200} />
        ) : dependencies.isError ? (
          isApiError(dependencies.error)
            ? <CorrelationError error={dependencies.error} />
            : <Typography color="error">{String(dependencies.error)}</Typography>
        ) : (
          <Stack spacing={2}>
            <Box>
              <Typography variant="subtitle2" sx={{ mb: 1 }}>{`Waits for (${waitsFor.length})`}</Typography>
              <DataTable<FlowDependency>
                columns={waitsForColumns}
                rows={waitsFor}
                rowKey={(row) => row.id}
                emptyMessage="Nothing upstream: this flow can start in the first wave of its group."
                data-testid="catalog-flow-waits-for"
              />
            </Box>
            <Box>
              <Typography variant="subtitle2" sx={{ mb: 1 }}>{`Unblocks (${unblocks.length})`}</Typography>
              <DataTable<FlowDependency>
                columns={unblocksColumns}
                rows={unblocks}
                rowKey={(row) => row.id}
                emptyMessage="Nothing downstream waits on this flow."
                data-testid="catalog-flow-unblocks"
              />
            </Box>
          </Stack>
        )
      )}
    </Box>
  );
}
