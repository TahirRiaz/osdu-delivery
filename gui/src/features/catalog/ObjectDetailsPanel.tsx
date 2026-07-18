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
import { lineageApi } from "../../api/endpoints";
import type { LineageEdge, LineageObjectColumn } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { DetailPair } from "../../components/DetailPair";
import { EmptyState } from "../../components/EmptyState";
import { LineageJumpButton } from "../../components/LineageJumpButton";
import { RelativeTime } from "../../components/RelativeTime";

const columnTableColumns: Column<LineageObjectColumn>[] = [
  { id: "ordinal", header: "#", render: (row) => row.ordinal, width: 56 },
  { id: "name", header: "Name", render: (row) => row.name },
  { id: "dataType", header: "Data type", render: (row) => row.dataType ?? "-" },
  {
    id: "nullable",
    header: "Nullable",
    render: (row) => (
      <Chip label={row.nullable ? "null" : "not null"} size="small" variant={row.nullable ? "outlined" : "filled"} />
    ),
  },
  { id: "tier", header: "Tier", render: (row) => <Chip label={row.tier} size="small" variant="outlined" /> },
];

/** The display order of edge relations: how the object comes to exist, then who writes and reads it. */
const RELATION_ORDER = ["Creates", "Writes", "Reads", "Requires", "Destroys"];

function relationRank(relation: string): number {
  const index = RELATION_ORDER.indexOf(relation);
  return index >= 0 ? index : RELATION_ORDER.length;
}

const edgeTableColumns: Column<LineageEdge>[] = [
  {
    id: "relation",
    header: "Relation",
    render: (row) => (
      <Chip
        label={row.relation}
        size="small"
        color={row.relation === "Writes" || row.relation === "Creates" ? "primary" : "default"}
        variant={row.relation === "Reads" ? "outlined" : "filled"}
      />
    ),
    width: 110,
  },
  {
    id: "flow",
    header: "Flow / module",
    render: (row) => {
      if (row.flow !== null && row.pipelineId !== null) {
        return (
          <Link component={RouterLink} to={`/pipelines/${row.pipelineId}`} variant="body2">
            {row.flow}
          </Link>
        );
      }
      return <Typography variant="body2">{row.viaModule ?? row.flow ?? "-"}</Typography>;
    },
  },
  {
    id: "via",
    header: "Via",
    render: (row) => (row.flow !== null && row.viaModule !== null ? row.viaModule : "-"),
  },
  { id: "tier", header: "Tier", render: (row) => <Chip label={row.tier} size="small" variant="outlined" />, width: 100 },
];

/**
 * The catalog tree's details panel for a database object: the dossier (identity, columns, and the lineage
 * edges the SQL analysis extracted) across Overview / Columns / Code / Relationships tabs, with the lineage
 * jump to trace the object in the graph.
 */
export function ObjectDetailsPanel({ objectKey }: { objectKey: string }) {
  const [tab, setTab] = useState<"overview" | "columns" | "code" | "relationships">("overview");

  const dossier = useQuery({
    queryKey: ["catalog-object-dossier", objectKey],
    queryFn: () => lineageApi.dossier(objectKey),
  });
  const script = useQuery({
    queryKey: ["catalog-object-script", objectKey],
    queryFn: () => lineageApi.script(objectKey),
    enabled: tab === "code",
  });

  if (dossier.isPending) {
    return (
      <Box>
        <Skeleton width={280} height={40} />
        <Skeleton width="100%" />
        <Skeleton width="70%" />
        <Skeleton variant="rectangular" height={280} sx={{ mt: 2 }} />
      </Box>
    );
  }
  if (dossier.isError) {
    return isApiError(dossier.error)
      ? <CorrelationError error={dossier.error} />
      : <Typography color="error">{String(dossier.error)}</Typography>;
  }

  const { object, columns, edges } = dossier.data;
  const sortedEdges = [...edges].sort((a, b) =>
    relationRank(a.relation) - relationRank(b.relation)
    || (a.flow ?? a.viaModule ?? "").localeCompare(b.flow ?? b.viaModule ?? ""));

  return (
    <Box data-testid="catalog-object-details">
      <Stack direction="row" spacing={1.5} alignItems="center" flexWrap="wrap" useFlexGap sx={{ mb: 0.5 }}>
        <Typography variant="h6" sx={{ minWidth: 0, wordBreak: "break-word" }}>{object.name}</Typography>
        <Chip label={object.kind} size="small" />
        <Box sx={{ flexGrow: 1 }} />
        <LineageJumpButton
          target={{
            kind: "object",
            objectKey: object.key,
            objectKind: object.kind,
            label: object.name,
            sublabel: [object.database, object.schema].filter((part) => part !== null).join("."),
          }}
          variant="outlined"
        />
      </Stack>
      <Typography variant="body2" sx={{ fontFamily: "monospace", wordBreak: "break-all", mb: 1.5 }}>
        {object.key}
      </Typography>

      <Tabs value={tab} onChange={(_event, value) => setTab(value)} sx={{ mb: 2 }} variant="scrollable">
        <Tab label="Overview" value="overview" data-testid="catalog-tab-overview" />
        <Tab label={`Columns (${columns.length})`} value="columns" data-testid="catalog-tab-columns" />
        <Tab label="Code" value="code" data-testid="catalog-tab-code" />
        <Tab label={`Relationships (${edges.length})`} value="relationships" data-testid="catalog-tab-relationships" />
      </Tabs>

      {tab === "overview" && (
        <Box sx={{ display: "grid", gap: 1.5, gridTemplateColumns: "repeat(2, minmax(0, 1fr))" }}>
          <DetailPair label="Server">{object.serverRef}</DetailPair>
          <DetailPair label="Database">{object.database ?? "-"}</DetailPair>
          <DetailPair label="Schema">{object.schema ?? "-"}</DetailPair>
          <DetailPair label="Level">{object.level ?? "-"}</DetailPair>
          <DetailPair label="First seen"><RelativeTime value={object.firstSeenUtc} /></DetailPair>
          <DetailPair label="Last seen"><RelativeTime value={object.lastSeenUtc} /></DetailPair>
        </Box>
      )}

      {tab === "columns" && (
        <DataTable<LineageObjectColumn>
          columns={columnTableColumns}
          rows={columns}
          rowKey={(row) => row.ordinal}
          emptyMessage="No columns recorded for this object (a connected sync fills the column dictionary)."
          data-testid="catalog-object-columns"
        />
      )}

      {tab === "code" && (
        script.isPending ? (
          <Skeleton variant="rectangular" height={320} />
        ) : script.isError ? (
          isApiError(script.error)
            ? <CorrelationError error={script.error} />
            : <Typography color="error">{String(script.error)}</Typography>
        ) : script.data.script === null ? (
          <EmptyState title="No code captured for this object yet (no lineage tier saw it created)." />
        ) : (
          <CodeView
            value={script.data.script}
            language={script.data.language === "yaml" ? "yaml" : "sql"}
            height={420}
            data-testid="catalog-object-code"
          />
        )
      )}

      {tab === "relationships" && (
        <DataTable<LineageEdge>
          columns={edgeTableColumns}
          rows={sortedEdges}
          rowKey={(row) => row.id}
          emptyMessage="No lineage edges reference this object yet."
          data-testid="catalog-object-edges"
        />
      )}
    </Box>
  );
}
