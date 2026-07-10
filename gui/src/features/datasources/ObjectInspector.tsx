import { useEffect, useState } from "react";
import { Link as RouterLink } from "react-router-dom";
import Alert from "@mui/material/Alert";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import CircularProgress from "@mui/material/CircularProgress";
import Divider from "@mui/material/Divider";
import Drawer from "@mui/material/Drawer";
import IconButton from "@mui/material/IconButton";
import Stack from "@mui/material/Stack";
import Typography from "@mui/material/Typography";
import CloseIcon from "@mui/icons-material/Close";
import KeyIcon from "@mui/icons-material/VpnKey";
import OpenInNewIcon from "@mui/icons-material/OpenInNew";
import type {
  ComputeTaskRequest, DatasourceObject, IntrospectionResult, UniqueKeyReport,
} from "../../api/types";
import { CopyButton } from "../../components/CopyButton";
import { DataTable, type Column } from "../../components/DataTable";
import { Mono } from "../../components/Mono";
import { UniqueKeyReportView } from "./UniqueKeyReportView";
import { keyDetectionPath } from "./keyDetectionLink";
import { useCompute } from "./useCompute";

interface ObjectInspectorProps {
  reference: string;
  kind: string | null;
  database: string | null;
  object: DatasourceObject;
  onClose: () => void;
}

interface ColumnRow {
  name: string;
  ordinal: number;
  nativeType: string;
  isNullable: boolean;
  isIdentity: boolean;
  isPrimaryKeyMember: boolean;
}

const columnColumns: Column<ColumnRow>[] = [
  { id: "ordinal", header: "#", render: (row) => row.ordinal, width: 48 },
  {
    id: "name",
    header: "Column",
    render: (row) => (
      <Stack direction="row" spacing={0.75} alignItems="center">
        <Mono sx={{ fontWeight: row.isPrimaryKeyMember ? 700 : 400 }}>{row.name}</Mono>
        {row.isPrimaryKeyMember && <Chip size="small" variant="outlined" color="primary" label="PK" />}
        {row.isIdentity && <Chip size="small" variant="outlined" label="identity" />}
      </Stack>
    ),
  },
  { id: "type", header: "Type", render: (row) => <Mono>{row.nativeType}</Mono> },
  { id: "nullable", header: "Nullable", render: (row) => (row.isNullable ? "NULL" : "NOT NULL") },
];

/**
 * The drill-down for one live object: full introspection (columns, indexes) fetched as a compute task when
 * the drawer opens, plus on-demand unique-key detection. Everything runs on a worker node against the live
 * source; the drawer only renders task results. The dedicated Key detection page (deep-linked from the
 * section header) is the specialized surface with options, history, and the full report.
 */
export function ObjectInspector({ reference, kind, database, object, onClose }: ObjectInspectorProps) {
  const introspect = useCompute<IntrospectionResult>();
  const detect = useCompute<UniqueKeyReport>();
  const { run: runIntrospect } = introspect;
  const [detectStarted, setDetectStarted] = useState(false);

  const base: Pick<ComputeTaskRequest, "reference" | "kind" | "database" | "schema" | "objectName"> = {
    reference,
    kind,
    database,
    schema: object.schema,
    objectName: object.name,
  };

  useEffect(() => {
    void runIntrospect({
      reference, kind, database, schema: object.schema, objectName: object.name,
      operation: "introspectObject",
    });
  }, [runIntrospect, reference, kind, database, object.schema, object.name]);

  const introspected = introspect.data?.found === true ? introspect.data.object ?? null : null;

  return (
    <Drawer anchor="right" open onClose={onClose} PaperProps={{ sx: { width: { xs: "100%", sm: 560 } } }}>
      <Stack spacing={2} sx={{ p: 2.5, overflowY: "auto" }} data-testid="object-inspector">
        <Stack direction="row" alignItems="center" justifyContent="space-between">
          <Stack direction="row" spacing={1} alignItems="center" sx={{ minWidth: 0 }}>
            <Typography variant="h6" noWrap>
              <Mono sx={{ fontSize: "inherit", fontWeight: 600 }}>{object.schema}.{object.name}</Mono>
            </Typography>
            <Chip size="small" variant="outlined" label={object.type} />
          </Stack>
          <IconButton onClick={onClose} aria-label="Close" data-testid="object-inspector-close">
            <CloseIcon />
          </IconButton>
        </Stack>

        {introspect.running && (
          <Stack direction="row" spacing={1.5} alignItems="center">
            <CircularProgress size={18} />
            <Typography variant="body2" color="text.secondary">Introspecting on a worker node...</Typography>
          </Stack>
        )}
        {introspect.error !== null && <Alert severity="error" data-testid="object-inspector-error">{introspect.error}</Alert>}
        {introspect.data?.found === false && (
          <Alert severity="warning">The object no longer exists on the source (it may have been dropped).</Alert>
        )}

        {introspected !== null && (
          <>
            <Stack direction="row" spacing={1} alignItems="center" justifyContent="space-between">
              <Typography variant="subtitle2">Columns ({introspected.columns.length})</Typography>
              {introspected.columns.length > 0 && (
                <CopyButton
                  label="Copy columns"
                  text={() => introspected.columns.map((c) => c.name).join(", ")}
                  testId="copy-column-names"
                />
              )}
            </Stack>
            <DataTable
              columns={columnColumns}
              rows={introspected.columns}
              rowKey={(row) => row.name}
              emptyMessage="The object exposes no columns."
              data-testid="object-columns-table"
            />

            {introspected.indexes.length > 0 && (
              <>
                <Typography variant="subtitle2">Indexes ({introspected.indexes.length})</Typography>
                <Stack spacing={0.75}>
                  {introspected.indexes.map((index) => (
                    <Stack key={index.name} direction="row" spacing={1} alignItems="center" flexWrap="wrap" useFlexGap>
                      <Mono>{index.name}</Mono>
                      <Typography variant="body2" color="text.secondary">
                        ({index.keyColumns.join(", ")})
                      </Typography>
                      {index.isPrimaryKey && <Chip size="small" variant="outlined" color="primary" label="primary key" />}
                      {index.isUnique && !index.isPrimaryKey && <Chip size="small" variant="outlined" label="unique" />}
                      {index.isClustered && <Chip size="small" variant="outlined" label="clustered" />}
                      {index.isColumnStore && <Chip size="small" variant="outlined" label="columnstore" />}
                    </Stack>
                  ))}
                </Stack>
              </>
            )}

            <Divider />

            <Stack direction="row" spacing={1} alignItems="center" justifyContent="space-between">
              <Typography variant="subtitle2">Unique key detection</Typography>
              <Stack direction="row" spacing={1}>
                <Button
                  size="small"
                  startIcon={<OpenInNewIcon />}
                  component={RouterLink}
                  to={keyDetectionPath({ reference, kind, database, schema: object.schema, objectName: object.name })}
                  data-testid="open-key-detection"
                >
                  Open in Key detection
                </Button>
                <Button
                  size="small"
                  variant="outlined"
                  startIcon={detect.running ? <CircularProgress size={14} /> : <KeyIcon />}
                  disabled={detect.running || (kind !== null && kind !== "MSSQL" && kind !== "AZDB")}
                  onClick={() => {
                    setDetectStarted(true);
                    void detect.run({ ...base, operation: "detectUniqueKey" });
                  }}
                  data-testid="detect-unique-key"
                >
                  {detect.running ? "Profiling..." : "Detect unique key"}
                </Button>
              </Stack>
            </Stack>
            {kind !== null && kind !== "MSSQL" && kind !== "AZDB" && (
              <Typography variant="body2" color="text.secondary">
                Detection profiles with T-SQL, so it is available for SQL Server and Azure SQL sources only.
              </Typography>
            )}
            {detect.error !== null && <Alert severity="error" data-testid="detect-error">{detect.error}</Alert>}
            {detectStarted && detect.data !== null && (
              <UniqueKeyReportView report={detect.data} dense data-testid="detect-report" />
            )}
          </>
        )}
      </Stack>
    </Drawer>
  );
}
