import { useEffect, useState } from "react";
import { Link as RouterLink } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import Box from "@mui/material/Box";
import Chip from "@mui/material/Chip";
import Link from "@mui/material/Link";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import Tab from "@mui/material/Tab";
import Tabs from "@mui/material/Tabs";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";
import AccountTreeIcon from "@mui/icons-material/AccountTree";
import KeyIcon from "@mui/icons-material/Key";
import { isApiError } from "../../api/client";
import { lineageApi } from "../../api/endpoints";
import type { LineageObjectColumn, ObjectRelationship } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { DetailPair } from "../../components/DetailPair";
import { EmptyState } from "../../components/EmptyState";
import { LineageJumpButton } from "../../components/LineageJumpButton";
import { RelativeTime } from "../../components/RelativeTime";
import { encodeNodeId } from "./nodeIds";

/** The set of key-column names (lowercase) an object's interpreted key names, for the PK marker. */
function keyColumnSet(keyColumns: string | null): Set<string> {
  return new Set(
    (keyColumns ?? "")
      .split(",")
      .map((name) => name.trim().toLowerCase())
      .filter((name) => name.length > 0),
  );
}

function columnTableColumns(keys: Set<string>): Column<LineageObjectColumn>[] {
  return [
    { id: "ordinal", header: "#", render: (row) => row.ordinal, width: 56 },
    {
      id: "name",
      header: "Name",
      render: (row) => (
        <Stack direction="row" spacing={0.75} alignItems="center">
          {keys.has(row.name.toLowerCase()) && (
            <Tooltip title="Interpreted key column">
              <KeyIcon sx={{ fontSize: 16, color: "warning.main" }} />
            </Tooltip>
          )}
          <Typography variant="body2">{row.name}</Typography>
        </Stack>
      ),
    },
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
}

/** One direction of the data-model table: the other table, the join condition, and how it was interpreted. */
function relationshipColumns(): Column<ObjectRelationship>[] {
  return [
    {
      id: "other",
      header: "Table",
      render: (row) => (
        <Link
          component={RouterLink}
          to={`/catalog?node=${encodeURIComponent(encodeNodeId({ type: "object", objectKey: row.otherObjectKey }))}`}
          variant="body2"
        >
          {[row.otherDatabase, row.otherSchema, row.otherName].filter((part) => part !== null).join(".")}
        </Link>
      ),
    },
    {
      id: "join",
      header: "Join on",
      render: (row) => (
        <Typography variant="body2" sx={{ fontFamily: "monospace", fontSize: 12, wordBreak: "break-word" }}>
          {`${row.ownColumns} = ${row.otherColumns}`}
        </Typography>
      ),
    },
    {
      id: "origin",
      header: "Interpreted from",
      render: (row) => (
        <Tooltip title={row.name ?? (row.origin === "Constraint" ? "Constraint clause in the codebase" : "Join predicates in the codebase's SQL")}>
          <Chip
            label={row.origin === "Constraint" ? "constraint" : "joins in code"}
            size="small"
            color={row.origin === "Constraint" ? "primary" : "default"}
            variant={row.origin === "Constraint" ? "filled" : "outlined"}
          />
        </Tooltip>
      ),
      width: 140,
    },
    {
      id: "occurrences",
      header: "Seen in",
      render: (row) => `${row.occurrences} script${row.occurrences === 1 ? "" : "s"}`,
      width: 110,
    },
    { id: "tier", header: "Tier", render: (row) => <Chip label={row.tier} size="small" variant="outlined" />, width: 100 },
  ];
}

/** A flow reference chip: its name (linking to the run/pipeline view) and kind. */
function FlowRef({ pipelineId, flow, kind }: { pipelineId: string; flow: string; kind: string }) {
  return (
    <Stack direction="row" spacing={0.75} alignItems="center" sx={{ minWidth: 0 }}>
      <AccountTreeIcon sx={{ fontSize: 16, color: "text.secondary" }} />
      <Link component={RouterLink} to={`/pipelines/${pipelineId}`} variant="body2">{flow}</Link>
      <Chip label={kind} size="small" variant="outlined" sx={{ height: 18, fontSize: 11 }} />
    </Stack>
  );
}

/** A file source's provenance: the pipelines that produce it (where it comes from) and the pipelines that
 * consume it, each with the tables the data lands in (where it goes). This is the "where do we source this,
 * through which pipeline, to where" answer for a file. */
function FileProvenance({ objectKey }: { objectKey: string }) {
  const flows = useQuery({
    queryKey: ["catalog-file-flows", objectKey],
    queryFn: () => lineageApi.fileFlows(objectKey),
  });

  if (flows.isPending) {
    return <Skeleton variant="rectangular" height={200} />;
  }
  if (flows.isError) {
    return isApiError(flows.error)
      ? <CorrelationError error={flows.error} />
      : <Typography color="error">{String(flows.error)}</Typography>;
  }

  const { producers, consumers } = flows.data;
  if (producers.length === 0 && consumers.length === 0) {
    return <EmptyState title="No pipelines reference this file yet." />;
  }

  return (
    <Stack spacing={2.5} data-testid="catalog-file-provenance">
      <Box>
        <Typography variant="subtitle2" sx={{ mb: 1 }}>{`Produced by (${producers.length})`}</Typography>
        {producers.length === 0 ? (
          <Typography variant="body2" color="text.secondary">
            Nothing in the catalog produces this file: it is an external source landing here.
          </Typography>
        ) : (
          <Stack spacing={0.75}>
            {producers.map((p) => <FlowRef key={p.pipelineId} pipelineId={p.pipelineId} flow={p.flow} kind={p.kind} />)}
          </Stack>
        )}
      </Box>
      <Box>
        <Typography variant="subtitle2" sx={{ mb: 1 }}>{`Consumed by (${consumers.length})`}</Typography>
        {consumers.length === 0 ? (
          <Typography variant="body2" color="text.secondary">No pipeline reads this file.</Typography>
        ) : (
          <Stack spacing={1.5}>
            {consumers.map((c) => (
              <Box key={c.pipelineId}>
                <FlowRef pipelineId={c.pipelineId} flow={c.flow} kind={c.kind} />
                <Stack direction="row" spacing={0.5} alignItems="baseline" sx={{ pl: 3, mt: 0.25, flexWrap: "wrap" }} useFlexGap>
                  <Typography variant="caption" color="text.secondary">Lands in:</Typography>
                  {c.lands.length === 0 ? (
                    <Typography variant="caption" color="text.secondary">(no table target recorded)</Typography>
                  ) : (
                    c.lands.map((l, index) => (
                      <span key={l.key}>
                        <Link
                          component={RouterLink}
                          to={`/catalog?node=${encodeURIComponent(encodeNodeId({ type: "object", objectKey: l.key }))}`}
                          variant="caption"
                        >
                          {[l.database, l.schema, l.name].filter((part) => part !== null).join(".")}
                        </Link>
                        {index < c.lands.length - 1 ? <Typography component="span" variant="caption" color="text.secondary">,</Typography> : null}
                      </span>
                    ))
                  )}
                </Stack>
              </Box>
            ))}
          </Stack>
        )}
      </Box>
    </Stack>
  );
}

/**
 * The catalog tree's details panel for a database object: the dossier across Overview / Columns / Code /
 * Relationships tabs. Relationships are the interpreted DATA MODEL (how this table joins others, read from
 * the codebase's own SQL: constraint clauses and the join predicates in views, procedures, and flow hooks),
 * not the flow lineage; tracing which flows move the data is one click away via the lineage jump.
 */
export function ObjectDetailsPanel({ objectKey }: { objectKey: string }) {
  const [tab, setTab] = useState<"overview" | "columns" | "code" | "relationships" | "pipelines">("overview");
  // Reset to Overview when the selected object changes, so a tab valid only for a file (or only for a table)
  // never lingers onto the next selection.
  useEffect(() => setTab("overview"), [objectKey]);

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

  const { object, columns, references, referencedBy } = dossier.data;
  const keys = keyColumnSet(object.keyColumns);
  const relationshipCount = references.length + referencedBy.length;
  // A file source's useful detail is its provenance (pipelines + landing), not columns/keys/joins, which it
  // has none of. A database object gets the semantic/query tabs instead.
  const isFile = object.kind === "File";

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
        {isFile
          ? <Tab label="Pipelines" value="pipelines" data-testid="catalog-tab-pipelines" />
          : [
            <Tab key="columns" label={`Columns (${columns.length})`} value="columns" data-testid="catalog-tab-columns" />,
            <Tab key="code" label="Code" value="code" data-testid="catalog-tab-code" />,
            <Tab key="relationships" label={`Relationships (${relationshipCount})`} value="relationships" data-testid="catalog-tab-relationships" />,
          ]}
      </Tabs>

      {tab === "overview" && (
        <Box sx={{ display: "grid", gap: 1.5, gridTemplateColumns: "repeat(2, minmax(0, 1fr))" }}>
          <DetailPair label="Server">{object.serverRef}</DetailPair>
          <DetailPair label="Database">{object.database ?? "-"}</DetailPair>
          <DetailPair label="Schema">{object.schema ?? "-"}</DetailPair>
          <DetailPair label="Level">{object.level ?? "-"}</DetailPair>
          <DetailPair label="Key">
            {object.keyColumns === null ? "-" : (
              <Stack direction="row" spacing={0.75} alignItems="center" flexWrap="wrap" useFlexGap>
                <KeyIcon sx={{ fontSize: 16, color: "warning.main" }} />
                <Typography variant="body2" sx={{ fontFamily: "monospace", fontSize: 12 }}>
                  {object.keyColumns}
                </Typography>
                <Tooltip
                  title={object.keyOrigin === "Constraint"
                    ? "From a PRIMARY KEY clause in the codebase"
                    : object.keyOrigin === "Declared"
                      ? "Declared by the loading flow's YAML key columns"
                      : "From the ON clause of the MERGE that loads it"}
                >
                  <Chip label={object.keyOrigin?.toLowerCase()} size="small" variant="outlined" sx={{ height: 20 }} />
                </Tooltip>
              </Stack>
            )}
          </DetailPair>
          <DetailPair label="First seen"><RelativeTime value={object.firstSeenUtc} /></DetailPair>
          <DetailPair label="Last seen"><RelativeTime value={object.lastSeenUtc} /></DetailPair>
        </Box>
      )}

      {tab === "pipelines" && <FileProvenance objectKey={object.key} />}

      {tab === "columns" && (
        <DataTable<LineageObjectColumn>
          columns={columnTableColumns(keys)}
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
        relationshipCount === 0 ? (
          <EmptyState title="No data-model relationships interpreted yet: nothing in the codebase joins this object to another table." />
        ) : (
          <Stack spacing={2}>
            <Box>
              <Typography variant="subtitle2" sx={{ mb: 1 }}>{`References (${references.length})`}</Typography>
              <DataTable<ObjectRelationship>
                columns={relationshipColumns()}
                rows={references}
                rowKey={(row) => `${row.otherObjectKey}|${row.ownColumns}|${row.origin}`}
                emptyMessage="This object references no other table."
                data-testid="catalog-object-references"
              />
            </Box>
            <Box>
              <Typography variant="subtitle2" sx={{ mb: 1 }}>{`Referenced by (${referencedBy.length})`}</Typography>
              <DataTable<ObjectRelationship>
                columns={relationshipColumns()}
                rows={referencedBy}
                rowKey={(row) => `${row.otherObjectKey}|${row.ownColumns}|${row.origin}`}
                emptyMessage="No other table references this object."
                data-testid="catalog-object-referenced-by"
              />
            </Box>
          </Stack>
        )
      )}
    </Box>
  );
}
