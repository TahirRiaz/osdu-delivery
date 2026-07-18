import { useEffect, useState, type FormEvent, type ReactNode } from "react";
import { Link as RouterLink, useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import LinearProgress from "@mui/material/LinearProgress";
import Link from "@mui/material/Link";
import Paper from "@mui/material/Paper";
import Stack from "@mui/material/Stack";
import Tab from "@mui/material/Tab";
import Tabs from "@mui/material/Tabs";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import SearchIcon from "@mui/icons-material/Search";
import { searchApi } from "../../api/endpoints";
import { isApiError } from "../../api/client";
import type {
  AllSearchResult, ColumnHit, DefinitionHit, FileHit, FlowHit, ObjectHit, SearchCategory,
} from "../../api/types";
import { LineageJumpButton, type LineageJumpTarget } from "../../components/LineageJumpButton";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { PagedTable, type Column } from "../../components/PagedTable";
import { TruncatedText } from "../../components/TruncatedText";
import { formatBytes, parseUtc } from "../../lib/time";

/**
 * The trailing "Lineage" action column: a labeled button (not a bare icon) that opens the lineage jump picker for
 * the row. `target` returns null for rows that cannot be traced (a processed file whose run carries no repo), which
 * simply renders no action.
 */
function lineageColumn<T>(target: (row: T) => LineageJumpTarget | null): Column<T> {
  return {
    id: "lineage-graph",
    header: "Lineage",
    align: "right",
    width: 132,
    render: (row) => {
      const jump = target(row);
      return jump === null ? null : <LineageJumpButton target={jump} variant="outlined" />;
    },
  };
}

// One lineage target per hit kind. Objects/columns/code focus the object node (repo resolved on open); flows and
// files focus the flow node in the flow's own repo (a file focuses the flow that ingested it).
const objectTarget = (row: ObjectHit): LineageJumpTarget => ({
  kind: "object", objectKey: row.key, objectKind: row.kind, label: row.name,
  sublabel: [row.kind, row.schema, row.serverRef].filter(Boolean).join(" · "),
});
const columnTarget = (row: ColumnHit): LineageJumpTarget => ({
  kind: "object", objectKey: row.objectKey, objectKind: "", label: row.objectName,
  sublabel: `column: ${row.columnName}`,
});
const definitionTarget = (row: DefinitionHit): LineageJumpTarget => ({
  kind: "object", objectKey: row.key, objectKind: row.kind, label: row.name, sublabel: row.kind,
});
const flowTarget = (row: FlowHit): LineageJumpTarget => ({
  kind: "node", repoId: row.repoId, repoName: row.repoName, focusId: row.id, label: row.name,
  sublabel: `${row.kind} flow · ${row.repoName}`,
});
// A file focuses the flow that ingests it, resolved by matching the file against the flow source specs in the
// catalog (works from the file's full path when known, else its name).
const fileTarget = (row: FileHit): LineageJumpTarget => ({
  kind: "file", filePath: row.path ?? row.name, label: row.name,
  sublabel: row.flowName ? `ingested by ${row.flowName}` : undefined,
});

// Tab order; index 0 (All) is the landing view for every search.
const TABS = ["All", "Objects", "Columns", "Definitions", "Files", "Flows"] as const;
type TabIndex = 0 | 1 | 2 | 3 | 4 | 5;

const objectColumns: Column<ObjectHit>[] = [
  {
    id: "name",
    header: "Name",
    render: (row) => <Typography variant="body2" fontWeight={600}>{row.name}</Typography>,
  },
  { id: "kind", header: "Kind", render: (row) => <Chip label={row.kind} size="small" /> },
  { id: "schema", header: "Schema", render: (row) => row.schema ?? "-" },
  { id: "database", header: "Database", render: (row) => row.database ?? "-" },
  { id: "serverRef", header: "Server", render: (row) => row.serverRef },
];

const columnColumns: Column<ColumnHit>[] = [
  {
    id: "columnName",
    header: "Column",
    render: (row) => <Typography variant="body2" fontWeight={600}>{row.columnName}</Typography>,
  },
  { id: "dataType", header: "Data type", render: (row) => row.dataType ?? "-" },
  {
    id: "nullable",
    header: "Nullable",
    render: (row) => (
      <Chip label={row.nullable ? "null" : "not null"} size="small" variant={row.nullable ? "outlined" : "filled"} />
    ),
  },
  { id: "objectName", header: "Object", render: (row) => row.objectName },
  {
    id: "objectKey",
    header: "Object key",
    render: (row) => <TruncatedText text={row.objectKey} mono maxWidth={420} />,
  },
];

const definitionColumns: Column<DefinitionHit>[] = [
  {
    id: "name",
    header: "Name",
    render: (row) => <Typography variant="body2" fontWeight={600}>{row.name}</Typography>,
  },
  { id: "kind", header: "Kind", render: (row) => <Chip label={row.kind} size="small" /> },
  {
    id: "source",
    header: "Matched",
    render: (row) => <Chip label={row.source} size="small" variant="outlined" />,
  },
  {
    id: "snippet",
    header: "Snippet",
    render: (row) => <Mono sx={{ whiteSpace: "pre-wrap" }}>{row.snippet}</Mono>,
  },
];

const fileColumns: Column<FileHit>[] = [
  {
    id: "name",
    header: "File",
    render: (row) => <Typography variant="body2" fontWeight={600}>{row.name}</Typography>,
  },
  {
    id: "flowName",
    header: "Flow",
    // Direct file -> pipeline navigation: the flow name links to the pipeline that ingested the file.
    render: (row) => (
      <Link
        component={RouterLink}
        to={`/pipelines/${row.pipelineId}`}
        onClick={(event) => event.stopPropagation()}
        underline="hover"
      >
        {row.flowName}
      </Link>
    ),
  },
  { id: "rows", header: "Rows", render: (row) => row.rows.toLocaleString() },
  { id: "columns", header: "Columns", render: (row) => row.columns.toLocaleString() },
  { id: "sizeBytes", header: "Size", render: (row) => formatBytes(row.sizeBytes) },
  {
    id: "runUtc",
    header: "Run",
    render: (row) => (row.runUtc ? parseUtc(row.runUtc).toLocaleString() : "-"),
  },
  {
    id: "path",
    header: "Path",
    render: (row) => <TruncatedText text={row.path} mono maxWidth={420} />,
  },
];

const flowColumns: Column<FlowHit>[] = [
  {
    id: "name",
    header: "Flow",
    render: (row) => <Typography variant="body2" fontWeight={600}>{row.name}</Typography>,
  },
  { id: "kind", header: "Kind", render: (row) => <Chip label={row.kind} size="small" /> },
  {
    id: "matchedIn",
    header: "Matched",
    render: (row) => <Chip label={row.matchedIn} size="small" variant="outlined" />,
  },
  {
    id: "snippet",
    header: "Path / snippet",
    render: (row) => <Mono sx={{ whiteSpace: "pre-wrap" }}>{row.snippet}</Mono>,
  },
];

/**
 * Global search over the whole catalog: objects and columns by name, code (module bodies and emitted DDL),
 * processed files by name or path, and flow YAML by name, path, or body text. The app-bar search box lands here
 * with ?q=; the All tab shows a grouped preview across every surface and each dedicated tab pages one surface.
 */
export default function SearchPage() {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const q = (searchParams.get("q") ?? "").trim();
  const [term, setTerm] = useState(q);
  const [tab, setTab] = useState<TabIndex>(0);

  // The app-bar search navigates here while this page is already mounted: mirror the new term into the input.
  useEffect(() => {
    setTerm(q);
  }, [q]);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const trimmed = term.trim();
    setSearchParams(trimmed === "" ? {} : { q: trimmed }, { replace: true });
  };

  const openLineage = (name: string) => navigate(`/lineage/objects?name=${encodeURIComponent(name)}`);
  const openRun = (runId: string) => navigate(`/runs/${runId}`);
  const openPipeline = (id: string) => navigate(`/pipelines/${id}`);

  return (
    <Page data-testid="page-search">
      <PageHeader title="Search" />

      <Box component="form" onSubmit={submit}>
        <Stack direction="row" spacing={2}>
          <TextField
            label="Search term"
            size="small"
            value={term}
            onChange={(event) => setTerm(event.target.value)}
            inputProps={{ "data-testid": "search-input" }}
            sx={{ flexGrow: 1, maxWidth: 480 }}
          />
          <Button type="submit" variant="contained" startIcon={<SearchIcon />} data-testid="search-submit">
            Search
          </Button>
        </Stack>
      </Box>

      <Tabs value={tab} onChange={(_, next: TabIndex) => setTab(next)} data-testid="search-tabs">
        {TABS.map((label) => (
          <Tab key={label} label={label} data-testid={`search-tab-${label.toLowerCase()}`} />
        ))}
      </Tabs>

      {q === "" && (
        <EmptyState
          title="Type a term to search objects, columns, code, processed files, and flows"
          data-testid="search-hint"
        />
      )}

      {q !== "" && tab === 0 && (
        <AllResults
          q={q}
          onSelectTab={setTab}
          openLineage={openLineage}
          openRun={openRun}
          openPipeline={openPipeline}
        />
      )}

      {q !== "" && tab === 1 && (
        <PagedTable<ObjectHit>
          queryKey={["search", "objects", q]}
          fetchPage={(page, pageSize) => searchApi.objects(q, { page, pageSize })}
          columns={[...objectColumns, lineageColumn<ObjectHit>(objectTarget)]}
          rowKey={(row) => row.key}
          onRowClick={(row) => openLineage(row.name)}
          emptyMessage={`No objects match "${q}".`}
          data-testid="search-objects-table"
        />
      )}

      {q !== "" && tab === 2 && (
        <PagedTable<ColumnHit>
          queryKey={["search", "columns", q]}
          fetchPage={(page, pageSize) => searchApi.columns(q, { page, pageSize })}
          columns={[...columnColumns, lineageColumn<ColumnHit>(columnTarget)]}
          rowKey={(row) => `${row.objectKey}::${row.columnName}`}
          onRowClick={(row) => openLineage(row.objectName)}
          emptyMessage={`No columns match "${q}".`}
          data-testid="search-columns-table"
        />
      )}

      {q !== "" && tab === 3 && (
        <PagedTable<DefinitionHit>
          queryKey={["search", "definitions", q]}
          fetchPage={(page, pageSize) => searchApi.definitions(q, { page, pageSize })}
          columns={[...definitionColumns, lineageColumn<DefinitionHit>(definitionTarget)]}
          rowKey={(row) => row.key}
          onRowClick={(row) => openLineage(row.name)}
          emptyMessage={`No code matches "${q}".`}
          data-testid="search-definitions-table"
        />
      )}

      {q !== "" && tab === 4 && (
        <PagedTable<FileHit>
          queryKey={["search", "files", q]}
          fetchPage={(page, pageSize) => searchApi.files(q, { page, pageSize })}
          columns={[...fileColumns, lineageColumn<FileHit>(fileTarget)]}
          rowKey={(row) => `${row.runId}::${row.name}::${row.path ?? ""}`}
          onRowClick={(row) => openRun(row.runId)}
          emptyMessage={`No processed files match "${q}".`}
          data-testid="search-files-table"
        />
      )}

      {q !== "" && tab === 5 && (
        <PagedTable<FlowHit>
          queryKey={["search", "flows", q]}
          fetchPage={(page, pageSize) => searchApi.flows(q, { page, pageSize })}
          columns={[...flowColumns, lineageColumn<FlowHit>(flowTarget)]}
          rowKey={(row) => row.id}
          onRowClick={(row) => openPipeline(row.id)}
          emptyMessage={`No flows match "${q}".`}
          data-testid="search-flows-table"
        />
      )}
    </Page>
  );
}

interface AllResultsProps {
  q: string;
  onSelectTab: (tab: TabIndex) => void;
  openLineage: (name: string) => void;
  openRun: (runId: string) => void;
  openPipeline: (id: string) => void;
}

/** The unified landing view: one query fanned across every surface, each category previewed with a jump to its tab. */
function AllResults({ q, onSelectTab, openLineage, openRun, openPipeline }: AllResultsProps) {
  const query = useQuery({
    queryKey: ["search", "all", q],
    queryFn: () => searchApi.all(q),
  });

  if (query.isLoading) {
    return <LinearProgress data-testid="search-all-loading" />;
  }
  if (query.isError) {
    return isApiError(query.error)
      ? <CorrelationError error={query.error} />
      : <Typography color="error">{String(query.error)}</Typography>;
  }

  const data = query.data as AllSearchResult;
  const totalHits = data.objects.total + data.columns.total + data.definitions.total
    + data.files.total + data.flows.total;

  if (totalHits === 0) {
    return <EmptyState title={`Nothing matches "${q}".`} data-testid="search-all-empty" />;
  }

  return (
    <Stack spacing={2} data-testid="search-all">
      <CategorySection<ObjectHit>
        title="Objects" tab={1} category={data.objects} onSelectTab={onSelectTab}
        rowKey={(row) => row.key} onRowClick={(row) => openLineage(row.name)}
        jumpTarget={objectTarget}
        primary={(row) => row.name}
        secondary={(row) => [row.kind, row.schema, row.database, row.serverRef].filter(Boolean).join(" · ")}
      />
      <CategorySection<ColumnHit>
        title="Columns" tab={2} category={data.columns} onSelectTab={onSelectTab}
        rowKey={(row) => `${row.objectKey}::${row.columnName}`} onRowClick={(row) => openLineage(row.objectName)}
        jumpTarget={columnTarget}
        primary={(row) => row.columnName}
        secondary={(row) => [row.dataType ?? "", `on ${row.objectName}`].filter(Boolean).join(" · ")}
      />
      <CategorySection<DefinitionHit>
        title="Code" tab={3} category={data.definitions} onSelectTab={onSelectTab}
        rowKey={(row) => row.key} onRowClick={(row) => openLineage(row.name)}
        jumpTarget={definitionTarget}
        primary={(row) => `${row.name} (${row.source})`}
        secondary={(row) => row.snippet}
      />
      <CategorySection<FileHit>
        title="Files" tab={4} category={data.files} onSelectTab={onSelectTab}
        rowKey={(row) => `${row.runId}::${row.name}::${row.path ?? ""}`} onRowClick={(row) => openRun(row.runId)}
        jumpTarget={fileTarget}
        primary={(row) => row.name}
        secondary={(row) => [
          row.flowName, `${row.rows.toLocaleString()} rows`, formatBytes(row.sizeBytes),
        ].join(" · ")}
      />
      <CategorySection<FlowHit>
        title="Flows" tab={5} category={data.flows} onSelectTab={onSelectTab}
        rowKey={(row) => row.id} onRowClick={(row) => openPipeline(row.id)}
        jumpTarget={flowTarget}
        primary={(row) => row.name}
        secondary={(row) => `${row.matchedIn}: ${row.snippet}`}
      />
    </Stack>
  );
}

interface CategorySectionProps<T> {
  title: string;
  tab: TabIndex;
  category: SearchCategory<T>;
  onSelectTab: (tab: TabIndex) => void;
  rowKey: (row: T) => string;
  onRowClick: (row: T) => void;
  /** The lineage jump for each preview row; return null for a row that cannot be traced (renders no action). */
  jumpTarget?: (row: T) => LineageJumpTarget | null;
  primary: (row: T) => ReactNode;
  secondary: (row: T) => string;
}

/** One grouped block in the All view: a titled count, the top preview rows, and a "see all" jump to the tab. */
function CategorySection<T>({
  title, tab, category, onSelectTab, rowKey, onRowClick, jumpTarget, primary, secondary,
}: CategorySectionProps<T>) {
  if (category.total === 0) {
    return null;
  }

  const remaining = category.total - category.items.length;
  return (
    <Paper variant="outlined" sx={{ p: 2 }} data-testid={`search-all-${title.toLowerCase()}`}>
      <Stack direction="row" alignItems="baseline" spacing={1} sx={{ mb: 1 }}>
        <Typography variant="subtitle1" fontWeight={700}>{title}</Typography>
        <Chip label={category.total.toLocaleString()} size="small" />
        <Box sx={{ flexGrow: 1 }} />
        <Link component="button" type="button" variant="body2" onClick={() => onSelectTab(tab)}>
          See all
        </Link>
      </Stack>
      <Stack divider={<Box sx={{ borderTop: 1, borderColor: "divider" }} />}>
        {category.items.map((row) => {
          const jump = jumpTarget?.(row) ?? null;
          return (
            <Box key={rowKey(row)} sx={{ display: "flex", alignItems: "center", gap: 1 }}>
              <Box
                onClick={() => onRowClick(row)}
                sx={{ flexGrow: 1, minWidth: 0, py: 0.75, cursor: "pointer", "&:hover": { bgcolor: "action.hover" } }}
              >
                <Typography variant="body2" fontWeight={600}>{primary(row)}</Typography>
                <Mono sx={{ display: "block", color: "text.secondary", whiteSpace: "pre-wrap" }}>
                  {secondary(row)}
                </Mono>
              </Box>
              {jump !== null && <LineageJumpButton target={jump} />}
            </Box>
          );
        })}
      </Stack>
      {remaining > 0 && (
        <Typography variant="caption" color="text.secondary" sx={{ mt: 1, display: "block" }}>
          {`and ${remaining.toLocaleString()} more`}
        </Typography>
      )}
    </Paper>
  );
}
