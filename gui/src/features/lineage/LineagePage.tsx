import { useEffect, useState, type ReactNode } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import Divider from "@mui/material/Divider";
import Drawer from "@mui/material/Drawer";
import FormControl from "@mui/material/FormControl";
import IconButton from "@mui/material/IconButton";
import InputLabel from "@mui/material/InputLabel";
import MenuItem from "@mui/material/MenuItem";
import Select from "@mui/material/Select";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import AccountTreeIcon from "@mui/icons-material/AccountTree";
import CloseIcon from "@mui/icons-material/Close";
import { isApiError } from "../../api/client";
import { lineageApi } from "../../api/endpoints";
import type { LineageObject, LineageObjectColumn } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";

const OBJECT_KINDS = ["Table", "View", "Procedure", "Function", "Trigger", "Synonym", "File"];

/** Local debounce for free-text filters: the table re-queries 400ms after the user stops typing. */
function useDebounced(value: string, delayMs: number): string {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const handle = window.setTimeout(() => setDebounced(value), delayMs);
    return () => window.clearTimeout(handle);
  }, [value, delayMs]);
  return debounced;
}

const objectTableColumns: Column<LineageObject>[] = [
  {
    id: "name",
    header: "Name",
    render: (row) => <Typography variant="body2" fontWeight={600}>{row.name}</Typography>,
  },
  { id: "kind", header: "Kind", render: (row) => <Chip label={row.kind} size="small" /> },
  { id: "schema", header: "Schema", render: (row) => row.schema ?? "-" },
  { id: "database", header: "Database", render: (row) => row.database ?? "-" },
  { id: "serverRef", header: "Server", render: (row) => row.serverRef },
  { id: "lastSeen", header: "Last seen", render: (row) => <RelativeTime value={row.lastSeenUtc} /> },
];

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
];

function DetailRow({ label, children }: { label: string; children: ReactNode }) {
  return (
    <Box sx={{ display: "flex", gap: 1 }}>
      <Typography variant="body2" color="text.secondary" sx={{ width: 96, flexShrink: 0 }}>
        {label}
      </Typography>
      <Typography variant="body2" component="div" sx={{ minWidth: 0, wordBreak: "break-word" }}>
        {children}
      </Typography>
    </Box>
  );
}

/** The drawer body: object detail (with the SQL definition when captured) plus the paged column list. */
function ObjectDrawerContent({ objectKey }: { objectKey: string }) {
  const detail = useQuery({
    queryKey: ["lineage-object-detail", objectKey],
    queryFn: () => lineageApi.objectDetail(objectKey),
  });

  if (detail.isPending) {
    return (
      <Box sx={{ p: 3 }}>
        <Skeleton width={240} height={36} />
        <Skeleton width="100%" />
        <Skeleton width="80%" />
        <Skeleton width="60%" />
        <Skeleton variant="rectangular" height={240} sx={{ mt: 2 }} />
      </Box>
    );
  }

  if (detail.isError) {
    return (
      <Box sx={{ p: 3 }}>
        {isApiError(detail.error)
          ? <CorrelationError error={detail.error} />
          : <Typography color="error">{String(detail.error)}</Typography>}
      </Box>
    );
  }

  const data = detail.data;
  return (
    <Box sx={{ p: 3 }}>
      <Stack direction="row" spacing={1.5} alignItems="center" sx={{ mb: 1 }}>
        <Typography variant="h6" sx={{ minWidth: 0, wordBreak: "break-word" }}>{data.name}</Typography>
        <Chip label={data.kind} size="small" />
      </Stack>
      <Typography variant="body2" sx={{ fontFamily: "monospace", wordBreak: "break-all", mb: 2 }} data-testid="object-key">
        {data.key}
      </Typography>

      <Stack spacing={0.75} sx={{ mb: 2 }}>
        <DetailRow label="Server">{data.serverRef}</DetailRow>
        <DetailRow label="Database">{data.database ?? "-"}</DetailRow>
        <DetailRow label="Schema">{data.schema ?? "-"}</DetailRow>
        <DetailRow label="First seen"><RelativeTime value={data.firstSeenUtc} /></DetailRow>
        <DetailRow label="Last seen"><RelativeTime value={data.lastSeenUtc} /></DetailRow>
      </Stack>

      {data.definition !== null && (
        <Box sx={{ mb: 2 }}>
          <Typography variant="subtitle2" sx={{ mb: 1 }}>Definition</Typography>
          <CodeView value={data.definition} language="sql" height={320} data-testid="object-definition" />
        </Box>
      )}

      <Divider sx={{ mb: 2 }} />
      <Typography variant="subtitle2" sx={{ mb: 1 }}>Columns</Typography>
      <PagedTable<LineageObjectColumn>
        queryKey={["lineage-object-columns", objectKey]}
        fetchPage={(page, pageSize) => lineageApi.objectColumns(objectKey, { page, pageSize })}
        columns={columnTableColumns}
        rowKey={(row) => row.ordinal}
        emptyMessage="No columns recorded for this object."
        data-testid="object-columns-table"
      />
    </Box>
  );
}

/**
 * The lineage catalog: every object the analyzer has seen, filterable by name, kind, and server, with a
 * detail drawer (definition + columns) per object. The search page deep-links here with ?name=.
 */
export default function LineagePage() {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const [name, setName] = useState(searchParams.get("name") ?? "");
  const [kind, setKind] = useState("all");
  const [serverRef, setServerRef] = useState("");
  const [selectedKey, setSelectedKey] = useState<string | null>(null);

  const debouncedName = useDebounced(name, 400);
  const debouncedServerRef = useDebounced(serverRef, 400);

  return (
    <Box data-testid="page-lineage">
      <Stack direction="row" alignItems="center" justifyContent="space-between" sx={{ mb: 2 }}>
        <Typography variant="h5" fontWeight={600}>Lineage</Typography>
        <Button
          variant="outlined"
          startIcon={<AccountTreeIcon />}
          onClick={() => navigate("/lineage/graph")}
          data-testid="open-lineage-graph"
        >
          Graph view
        </Button>
      </Stack>

      <Stack direction={{ xs: "column", sm: "row" }} spacing={2} sx={{ mb: 2 }}>
        <TextField
          label="Name"
          size="small"
          value={name}
          onChange={(event) => setName(event.target.value)}
          inputProps={{ "data-testid": "filter-object-name" }}
          sx={{ minWidth: 220 }}
        />
        <FormControl size="small" sx={{ minWidth: 180 }}>
          <InputLabel id="lineage-kind-label">Kind</InputLabel>
          <Select
            labelId="lineage-kind-label"
            label="Kind"
            value={kind}
            onChange={(event) => setKind(event.target.value)}
            data-testid="filter-object-kind"
          >
            <MenuItem value="all">All kinds</MenuItem>
            {OBJECT_KINDS.map((option) => (
              <MenuItem key={option} value={option}>{option}</MenuItem>
            ))}
          </Select>
        </FormControl>
        <TextField
          label="Server ref"
          size="small"
          value={serverRef}
          onChange={(event) => setServerRef(event.target.value)}
          inputProps={{ "data-testid": "filter-server-ref" }}
          sx={{ minWidth: 220 }}
        />
      </Stack>

      <PagedTable<LineageObject>
        queryKey={["lineage-objects", debouncedName, kind, debouncedServerRef]}
        fetchPage={(page, pageSize) => lineageApi.objects({
          name: debouncedName === "" ? undefined : debouncedName,
          kind: kind === "all" ? undefined : kind,
          serverRef: debouncedServerRef === "" ? undefined : debouncedServerRef,
          page,
          pageSize,
        })}
        columns={objectTableColumns}
        rowKey={(row) => row.key}
        onRowClick={(row) => setSelectedKey(row.key)}
        emptyMessage="No lineage objects match the current filters."
        data-testid="lineage-objects-table"
      />

      <Drawer
        anchor="right"
        open={selectedKey !== null}
        onClose={() => setSelectedKey(null)}
        data-testid="object-drawer"
        PaperProps={{ sx: { width: { xs: "100%", sm: 560 } } }}
      >
        <Box sx={{ display: "flex", justifyContent: "flex-end", px: 1, pt: 1 }}>
          <IconButton onClick={() => setSelectedKey(null)} aria-label="Close object details" data-testid="object-drawer-close">
            <CloseIcon />
          </IconButton>
        </Box>
        {selectedKey !== null && <ObjectDrawerContent objectKey={selectedKey} />}
      </Drawer>
    </Box>
  );
}
