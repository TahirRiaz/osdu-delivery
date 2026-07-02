import { useEffect, useState, type FormEvent } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import Stack from "@mui/material/Stack";
import Tab from "@mui/material/Tab";
import Tabs from "@mui/material/Tabs";
import TextField from "@mui/material/TextField";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";
import SearchIcon from "@mui/icons-material/Search";
import { searchApi } from "../../api/endpoints";
import type { ColumnHit, DefinitionHit, ObjectHit } from "../../api/types";
import { PagedTable, type Column } from "../../components/PagedTable";

function truncate(value: string, max: number): string {
  return value.length > max ? `${value.slice(0, max)}...` : value;
}

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
    render: (row) => (
      <Tooltip title={row.objectKey}>
        <Typography component="span" variant="body2" sx={{ fontFamily: "monospace" }}>
          {truncate(row.objectKey, 60)}
        </Typography>
      </Tooltip>
    ),
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
    id: "snippet",
    header: "Snippet",
    render: (row) => (
      <Typography component="span" variant="body2" sx={{ fontFamily: "monospace", whiteSpace: "pre-wrap" }}>
        {row.snippet}
      </Typography>
    ),
  },
];

/**
 * Global search over the lineage catalog: objects by name, columns by name, and full definition text. The
 * app-bar search box lands here with ?q=; every hit deep-links into the lineage page filtered to that name.
 */
export default function SearchPage() {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const q = (searchParams.get("q") ?? "").trim();
  const [term, setTerm] = useState(q);
  const [tab, setTab] = useState(0);

  // The app-bar search navigates here while this page is already mounted: mirror the new term into the input.
  useEffect(() => {
    setTerm(q);
  }, [q]);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const trimmed = term.trim();
    setSearchParams(trimmed === "" ? {} : { q: trimmed }, { replace: true });
  };

  const openLineage = (name: string) => navigate(`/lineage?name=${encodeURIComponent(name)}`);

  return (
    <Box data-testid="page-search">
      <Typography variant="h5" fontWeight={600} sx={{ mb: 2 }}>Search</Typography>

      <Box component="form" onSubmit={submit} sx={{ mb: 2 }}>
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

      <Tabs value={tab} onChange={(_, next: number) => setTab(next)} sx={{ mb: 2 }} data-testid="search-tabs">
        <Tab label="Objects" data-testid="search-tab-objects" />
        <Tab label="Columns" data-testid="search-tab-columns" />
        <Tab label="Definitions" data-testid="search-tab-definitions" />
      </Tabs>

      {q === "" && (
        <Box sx={{ py: 10, textAlign: "center" }}>
          <Typography color="text.secondary" data-testid="search-hint">
            Type a term to search objects, columns, and definitions
          </Typography>
        </Box>
      )}

      {q !== "" && tab === 0 && (
        <PagedTable<ObjectHit>
          queryKey={["search", "objects", q]}
          fetchPage={(page, pageSize) => searchApi.objects(q, { page, pageSize })}
          columns={objectColumns}
          rowKey={(row) => row.key}
          onRowClick={(row) => openLineage(row.name)}
          emptyMessage={`No objects match "${q}".`}
          data-testid="search-objects-table"
        />
      )}

      {q !== "" && tab === 1 && (
        <PagedTable<ColumnHit>
          queryKey={["search", "columns", q]}
          fetchPage={(page, pageSize) => searchApi.columns(q, { page, pageSize })}
          columns={columnColumns}
          rowKey={(row) => `${row.objectKey}::${row.columnName}`}
          onRowClick={(row) => openLineage(row.objectName)}
          emptyMessage={`No columns match "${q}".`}
          data-testid="search-columns-table"
        />
      )}

      {q !== "" && tab === 2 && (
        <PagedTable<DefinitionHit>
          queryKey={["search", "definitions", q]}
          fetchPage={(page, pageSize) => searchApi.definitions(q, { page, pageSize })}
          columns={definitionColumns}
          rowKey={(row) => row.key}
          onRowClick={(row) => openLineage(row.name)}
          emptyMessage={`No definitions match "${q}".`}
          data-testid="search-definitions-table"
        />
      )}
    </Box>
  );
}
