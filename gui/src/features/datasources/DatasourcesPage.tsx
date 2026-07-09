import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import Alert from "@mui/material/Alert";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import CircularProgress from "@mui/material/CircularProgress";
import Dialog from "@mui/material/Dialog";
import DialogActions from "@mui/material/DialogActions";
import DialogContent from "@mui/material/DialogContent";
import DialogTitle from "@mui/material/DialogTitle";
import Stack from "@mui/material/Stack";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";
import CableIcon from "@mui/icons-material/Cable";
import TravelExploreIcon from "@mui/icons-material/TravelExplore";
import { isApiError } from "../../api/client";
import { datasourceApi } from "../../api/endpoints";
import type { ConnectionTestResult, Datasource } from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { useCompute } from "./useCompute";

/** The connection-test dialog: runs the testConnection task when opened and shows the live outcome. */
function TestConnectionDialog({ datasource, onClose }: { datasource: Datasource; onClose: () => void }) {
  const test = useCompute<ConnectionTestResult>();
  const { run } = test;

  // The dialog mounts per target, so a single on-mount run tests exactly the datasource it was opened for.
  useEffect(() => {
    void run({ reference: datasource.reference, operation: "testConnection", kind: datasource.kind });
  }, [run, datasource.reference, datasource.kind]);

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="xs" data-testid="test-connection-dialog">
      <DialogTitle>Test connection</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <Mono>{datasource.reference}</Mono>
          {test.running && (
            <Stack direction="row" spacing={1.5} alignItems="center">
              <CircularProgress size={18} />
              <Typography variant="body2" color="text.secondary">
                A worker node is opening the connection...
              </Typography>
            </Stack>
          )}
          {test.error !== null && <Alert severity="error" data-testid="test-connection-error">{test.error}</Alert>}
          {test.data !== null && (
            <Alert severity="success" data-testid="test-connection-ok">
              Connected to a {test.data.kind} source
              {test.data.database !== null ? <> (database <Mono>{test.data.database}</Mono>)</> : null}
              {test.data.serverVersion !== null ? <>, server version {test.data.serverVersion}</> : null}
              {" "}in {Math.round(test.data.elapsedMs)} ms.
            </Alert>
          )}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} data-testid="test-connection-close">Close</Button>
      </DialogActions>
    </Dialog>
  );
}

/**
 * The estate's datasources: every connection reference the active pipelines declare, with live actions
 * (test the connection, browse databases/schemas/tables) that run as queued compute tasks on a worker node
 * that can actually reach the source. Inline-literal identities are listed but not browsable: a worker
 * cannot turn a hash back into a connection.
 */
export default function DatasourcesPage() {
  const navigate = useNavigate();
  const { hasScope } = useAuth();
  const canOperate = hasScope("operate");
  const [testTarget, setTestTarget] = useState<Datasource | null>(null);

  const datasources = useQuery({
    queryKey: ["datasources", "list"],
    queryFn: () => datasourceApi.list(),
  });

  const columns: Column<Datasource>[] = [
    {
      id: "reference",
      header: "Reference",
      render: (row) => <Mono sx={{ fontWeight: 600 }}>{row.reference}</Mono>,
    },
    {
      id: "kind",
      header: "Kind",
      render: (row) => (row.kind !== null
        ? <Chip size="small" variant="outlined" label={row.kind} />
        : <Typography variant="body2" color="text.secondary">unknown</Typography>),
    },
    {
      id: "usage",
      header: "Used by",
      render: (row) => (
        <Typography variant="body2">
          {row.sourcePipelines} source / {row.targetPipelines} target pipeline(s)
        </Typography>
      ),
    },
    {
      id: "resolvable",
      header: "Compute",
      render: (row) => (row.resolvable
        ? <Chip size="small" color="success" variant="outlined" label="browsable" />
        : (
          <Tooltip title="An inline connection literal is identified by hash only; a worker cannot resolve it for ad-hoc compute. Declare it as a ${...} reference to browse it.">
            <Chip size="small" variant="outlined" label="not browsable" />
          </Tooltip>
        )),
    },
    {
      id: "actions",
      header: "Actions",
      align: "right",
      render: (row) => (
        <Stack direction="row" spacing={1} justifyContent="flex-end">
          <Button
            size="small"
            startIcon={<CableIcon />}
            disabled={!canOperate || !row.resolvable}
            onClick={(e) => {
              e.stopPropagation();
              setTestTarget(row);
            }}
            data-testid="datasource-test"
          >
            Test
          </Button>
          <Button
            size="small"
            variant="outlined"
            startIcon={<TravelExploreIcon />}
            disabled={!canOperate || !row.resolvable}
            onClick={(e) => {
              e.stopPropagation();
              navigate(`/datasources/browse?ref=${encodeURIComponent(row.reference)}${row.kind !== null ? `&kind=${encodeURIComponent(row.kind)}` : ""}`);
            }}
            data-testid="datasource-browse"
          >
            Browse
          </Button>
        </Stack>
      ),
    },
  ];

  return (
    <Page data-testid="page-datasources">
      <PageHeader
        title="Datasources"
        subtitle={canOperate
          ? "The connection references the estate's pipelines declare. Browse runs live against the source on a worker node; nothing here ever carries a secret."
          : "The connection references the estate's pipelines declare. Live browse and connection tests need the operate scope."}
      />

      {datasources.isError && (isApiError(datasources.error)
        ? <CorrelationError error={datasources.error} />
        : <Typography color="error">{String(datasources.error)}</Typography>)}

      {!datasources.isError && (
        <DataTable
          columns={columns}
          rows={datasources.data}
          rowKey={(row) => row.reference}
          emptyMessage="No datasources yet: sync a repo with flows and their connection references appear here."
          data-testid="datasources-table"
        />
      )}

      {testTarget !== null && (
        <TestConnectionDialog datasource={testTarget} onClose={() => setTestTarget(null)} />
      )}
    </Page>
  );
}
