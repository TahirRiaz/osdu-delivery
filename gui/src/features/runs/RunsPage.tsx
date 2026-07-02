import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import FormControl from "@mui/material/FormControl";
import InputLabel from "@mui/material/InputLabel";
import MenuItem from "@mui/material/MenuItem";
import Select from "@mui/material/Select";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import ToggleButton from "@mui/material/ToggleButton";
import ToggleButtonGroup from "@mui/material/ToggleButtonGroup";
import Typography from "@mui/material/Typography";
import type { RunStatus, RunSummary } from "../../api/types";
import { runApi } from "../../api/endpoints";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { RunStatusBadge } from "../../components/StatusBadge";
import { formatDurationSeconds } from "../../lib/time";
import { TriggerRunDialog } from "./TriggerRunDialog";

const statuses: RunStatus[] = ["queued", "running", "succeeded", "failed", "cancelled"];
const kinds = ["all", "file", "ing", "exp", "sp", "inv", "hc", "scm", "batch"];

const columns: Column<RunSummary>[] = [
  { id: "status", header: "Status", render: (row) => <RunStatusBadge status={row.status} /> },
  {
    id: "flow",
    header: "Flow",
    render: (row) => <Typography variant="body2" sx={{ fontWeight: 600 }}>{row.flowName}</Typography>,
  },
  { id: "kind", header: "Kind", render: (row) => row.flowKind },
  {
    id: "enqueued",
    header: "Enqueued",
    render: (row) => <RelativeTime value={row.enqueuedUtc ?? row.writtenUtc} />,
  },
  {
    id: "duration",
    header: "Duration",
    render: (row) => (row.durationSeconds != null ? formatDurationSeconds(row.durationSeconds) : "-"),
  },
  { id: "rowsLoaded", header: "Rows loaded", align: "right", render: (row) => row.rowsLoaded ?? "-" },
  { id: "pool", header: "Pool", render: (row) => row.targetPool ?? "-" },
  {
    id: "commit",
    header: "Commit",
    render: (row) => <span style={{ fontFamily: "monospace" }}>{row.commitSha?.slice(0, 10) ?? "-"}</span>,
  },
];

/** The run inbox: live-polled list with status/flow/kind filters and the entry point for triggering runs. */
export default function RunsPage() {
  const navigate = useNavigate();
  const [triggerOpen, setTriggerOpen] = useState(false);
  const [status, setStatus] = useState<RunStatus | null>(null);
  const [kind, setKind] = useState("all");
  const [flowNameInput, setFlowNameInput] = useState("");
  const [flowName, setFlowName] = useState("");

  useEffect(() => {
    const handle = window.setTimeout(() => setFlowName(flowNameInput.trim()), 400);
    return () => window.clearTimeout(handle);
  }, [flowNameInput]);

  return (
    <Box data-testid="page-runs">
      <Stack direction="row" alignItems="center" justifyContent="space-between" sx={{ mb: 2 }}>
        <Typography variant="h5">Runs</Typography>
        <Button variant="contained" onClick={() => setTriggerOpen(true)} data-testid="open-trigger-run">
          Trigger run
        </Button>
      </Stack>

      <Stack direction="row" spacing={2} useFlexGap flexWrap="wrap" alignItems="center" sx={{ mb: 2 }}>
        <ToggleButtonGroup
          size="small"
          exclusive
          value={status}
          onChange={(_, value: RunStatus | null) => setStatus(value)}
          aria-label="Filter by status"
        >
          {statuses.map((s) => (
            <ToggleButton key={s} value={s} data-testid={`filter-status-${s}`}>{s}</ToggleButton>
          ))}
        </ToggleButtonGroup>
        <TextField
          size="small"
          label="Flow name"
          value={flowNameInput}
          onChange={(e) => setFlowNameInput(e.target.value)}
          inputProps={{ "data-testid": "filter-flow-name" }}
        />
        <FormControl size="small" sx={{ minWidth: 120 }}>
          <InputLabel id="filter-kind-label">Kind</InputLabel>
          <Select
            labelId="filter-kind-label"
            label="Kind"
            value={kind}
            onChange={(e) => setKind(e.target.value)}
            data-testid="filter-kind"
          >
            {kinds.map((k) => (
              <MenuItem key={k} value={k}>{k}</MenuItem>
            ))}
          </Select>
        </FormControl>
      </Stack>

      <PagedTable
        queryKey={["runs", "list", status, flowName, kind]}
        fetchPage={(page, pageSize) =>
          runApi.list({
            status: status ?? undefined,
            flowName: flowName === "" ? undefined : flowName,
            flowKind: kind === "all" ? undefined : kind,
            page,
            pageSize,
          })}
        columns={columns}
        rowKey={(row) => row.runId}
        onRowClick={(row) => navigate(`/runs/${row.runId}`)}
        pollMs={5000}
        emptyMessage="No runs match the current filters."
        data-testid="runs-table"
      />

      <TriggerRunDialog open={triggerOpen} onClose={() => setTriggerOpen(false)} />
    </Box>
  );
}
