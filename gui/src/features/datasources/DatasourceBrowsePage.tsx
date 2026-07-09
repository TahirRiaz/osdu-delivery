import { useCallback, useEffect, useState } from "react";
import { Link as RouterLink, useSearchParams } from "react-router-dom";
import Alert from "@mui/material/Alert";
import Autocomplete from "@mui/material/Autocomplete";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import FormControlLabel from "@mui/material/FormControlLabel";
import LinearProgress from "@mui/material/LinearProgress";
import Stack from "@mui/material/Stack";
import Switch from "@mui/material/Switch";
import TablePagination from "@mui/material/TablePagination";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import ArrowBackIcon from "@mui/icons-material/ArrowBack";
import LockPersonIcon from "@mui/icons-material/LockPerson";
import type {
  DatasourceDatabase, DatasourceObject, DatasourceObjectPage, DatasourceSchema,
} from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { DataTable, type Column } from "../../components/DataTable";
import { EmptyState } from "../../components/EmptyState";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { ObjectInspector } from "./ObjectInspector";
import { useCompute } from "./useCompute";

const objectColumns: Column<DatasourceObject>[] = [
  {
    id: "name",
    header: "Object",
    render: (row) => <Mono sx={{ fontWeight: 600 }}>{row.schema}.{row.name}</Mono>,
  },
  { id: "type", header: "Type", render: (row) => <Chip size="small" variant="outlined" label={row.type} /> },
  {
    id: "rows",
    header: "Approx. rows",
    align: "right",
    render: (row) => (row.type === "Table" ? row.approxRows.toLocaleString() : "-"),
  },
];

/**
 * The live browser for one datasource: databases, then schemas, then a filtered, paged table/view listing,
 * each fetched as a compute task executed by a worker node that can reach the source (the browser never
 * connects to anything itself). Clicking an object opens the live introspection drawer.
 */
export default function DatasourceBrowsePage() {
  const [params] = useSearchParams();
  const reference = params.get("ref") ?? "";
  const kind = params.get("kind");
  const { hasScope } = useAuth();
  const canOperate = hasScope("operate");

  const databases = useCompute<{ databases: DatasourceDatabase[] }>();
  const schemas = useCompute<{ schemas: DatasourceSchema[] }>();
  const objects = useCompute<DatasourceObjectPage>();
  const { run: runDatabases } = databases;
  const { run: runSchemas } = schemas;
  const { run: runObjects } = objects;

  const [database, setDatabase] = useState<string | null>(null);
  const [schema, setSchema] = useState<string | null>(null);
  const [nameLike, setNameLike] = useState("");
  const [appliedNameLike, setAppliedNameLike] = useState("");
  const [includeViews, setIncludeViews] = useState(true);
  const [includeSystem, setIncludeSystem] = useState(false);
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = useState(50);
  const [selected, setSelected] = useState<DatasourceObject | null>(null);

  const usable = reference !== "" && canOperate;

  // Databases load once per datasource; schemas reload when the database scope changes.
  useEffect(() => {
    if (usable) {
      void runDatabases({ reference, kind, operation: "listDatabases" });
    }
  }, [usable, runDatabases, reference, kind]);

  useEffect(() => {
    if (usable) {
      void runSchemas({ reference, kind, database, operation: "listSchemas" });
    }
  }, [usable, runSchemas, reference, kind, database]);

  const loadObjects = useCallback(
    (targetPage: number, targetPageSize: number, filter: string) => {
      if (!usable) {
        return;
      }

      void runObjects({
        reference,
        kind,
        database,
        schema,
        nameLike: filter === "" ? null : filter,
        includeViews,
        includeSystem,
        offset: targetPage * targetPageSize,
        limit: targetPageSize,
        operation: "listObjects",
      });
    },
    [usable, runObjects, reference, kind, database, schema, includeViews, includeSystem],
  );

  // The object listing tracks every scope/filter change; paging state resets when the scope changes so a page
  // index from the previous scope is never applied to a new one.
  useEffect(() => {
    setPage(0);
    loadObjects(0, pageSize, appliedNameLike);
    // pageSize changes go through the pagination handlers below (which also refetch), so it is not a dep here.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [loadObjects, appliedNameLike]);

  if (reference === "") {
    return (
      <Page data-testid="page-datasource-browse">
        <PageHeader title="Browse datasource" />
        <EmptyState
          title="No datasource selected"
          description="Open this page from the Datasources list so it knows which reference to browse."
          action={<Button component={RouterLink} to="/datasources" startIcon={<ArrowBackIcon />}>Datasources</Button>}
        />
      </Page>
    );
  }

  if (!canOperate) {
    return (
      <Page data-testid="page-datasource-browse">
        <PageHeader title="Browse datasource" />
        <EmptyState
          icon={<LockPersonIcon />}
          title="Operate scope required"
          description="Browsing runs live queries against the source through a worker node, so it needs the operate scope. Ask an administrator for the operator role."
        />
      </Page>
    );
  }

  return (
    <Page data-testid="page-datasource-browse">
      <PageHeader
        title={(
          <Stack direction="row" spacing={1.5} alignItems="center" flexWrap="wrap" useFlexGap>
            <span>Browse</span>
            <Mono sx={{ fontSize: "inherit" }}>{reference}</Mono>
            {kind !== null && <Chip size="small" variant="outlined" label={kind} />}
          </Stack>
        )}
        subtitle="Everything on this page executes as a compute task on a worker node that can reach the source."
        actions={(
          <Button component={RouterLink} to="/datasources" startIcon={<ArrowBackIcon />} data-testid="browse-back">
            Datasources
          </Button>
        )}
      />

      <Stack direction={{ xs: "column", md: "row" }} spacing={2} alignItems={{ md: "center" }} flexWrap="wrap" useFlexGap>
        <Autocomplete
          sx={{ minWidth: 240 }}
          size="small"
          options={databases.data?.databases.map((d) => d.name) ?? []}
          value={database}
          onChange={(_, value) => {
            setDatabase(value);
            setSchema(null);
          }}
          loading={databases.running}
          renderInput={(inputParams) => (
            <TextField
              {...inputParams}
              label="Database"
              placeholder="connection default"
              inputProps={{ ...inputParams.inputProps, "data-testid": "browse-database" }}
            />
          )}
        />
        <Autocomplete
          sx={{ minWidth: 200 }}
          size="small"
          options={schemas.data?.schemas.map((s) => s.name) ?? []}
          value={schema}
          onChange={(_, value) => setSchema(value)}
          loading={schemas.running}
          renderInput={(inputParams) => (
            <TextField
              {...inputParams}
              label="Schema"
              placeholder="all schemas"
              inputProps={{ ...inputParams.inputProps, "data-testid": "browse-schema" }}
            />
          )}
        />
        <TextField
          size="small"
          label="Name filter"
          value={nameLike}
          onChange={(e) => setNameLike(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              setAppliedNameLike(nameLike.trim());
            }
          }}
          onBlur={() => setAppliedNameLike(nameLike.trim())}
          inputProps={{ "data-testid": "browse-name-filter" }}
        />
        <FormControlLabel
          control={(
            <Switch
              checked={includeViews}
              onChange={(e) => setIncludeViews(e.target.checked)}
              data-testid="browse-include-views"
            />
          )}
          label="Views"
        />
        <FormControlLabel
          control={(
            <Switch
              checked={includeSystem}
              onChange={(e) => setIncludeSystem(e.target.checked)}
              data-testid="browse-include-system"
            />
          )}
          label="System objects"
        />
      </Stack>

      {databases.error !== null && <Alert severity="error" data-testid="browse-databases-error">{databases.error}</Alert>}
      {schemas.error !== null && databases.error === null && (
        <Alert severity="error" data-testid="browse-schemas-error">{schemas.error}</Alert>
      )}
      {objects.error !== null && <Alert severity="error" data-testid="browse-objects-error">{objects.error}</Alert>}

      {objects.running && <LinearProgress data-testid="browse-loading" />}

      <DataTable
        columns={objectColumns}
        rows={objects.running && objects.data === null ? undefined : objects.data?.items ?? []}
        rowKey={(row) => `${row.schema}.${row.name}`}
        onRowClick={(row) => setSelected(row)}
        emptyMessage={objects.error !== null
          ? "The listing failed; fix the error above and adjust the scope."
          : "No tables or views match this scope."}
        data-testid="browse-objects-table"
        footer={(
          <TablePagination
            component="div"
            count={Number(objects.data?.total ?? 0)}
            page={page}
            onPageChange={(_, next) => {
              setPage(next);
              loadObjects(next, pageSize, appliedNameLike);
            }}
            rowsPerPage={pageSize}
            onRowsPerPageChange={(event) => {
              const nextSize = Number.parseInt(event.target.value, 10);
              setPageSize(nextSize);
              setPage(0);
              loadObjects(0, nextSize, appliedNameLike);
            }}
            rowsPerPageOptions={[25, 50, 100, 200]}
          />
        )}
      />
      {objects.data !== null && (
        <Typography variant="caption" color="text.secondary">
          {objects.data.items.length} of {objects.data.total.toLocaleString()} object(s) in scope.
        </Typography>
      )}

      {selected !== null && (
        <ObjectInspector
          reference={reference}
          kind={kind}
          database={database}
          object={selected}
          onClose={() => setSelected(null)}
        />
      )}
    </Page>
  );
}
