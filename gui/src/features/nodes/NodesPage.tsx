import Stack from "@mui/material/Stack";
import Typography from "@mui/material/Typography";
import type { Node } from "../../api/types";
import { nodeApi } from "../../api/endpoints";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { OnlineBadge } from "../../components/StatusBadge";
import { NodeRestartButton } from "./NodeRestartButton";
import { NodeDeleteButton } from "./NodeDeleteButton";
import { WorkerPoolsPanel } from "./WorkerPoolsPanel";

const columns: Column<Node>[] = [
  {
    id: "name",
    header: "Name",
    render: (row) => <Typography variant="body2" sx={{ fontWeight: 600 }}>{row.name}</Typography>,
  },
  { id: "status", header: "Status", render: (row) => <OnlineBadge online={row.online} /> },
  { id: "version", header: "Version", render: (row) => row.version ?? "-" },
  { id: "firstSeen", header: "First seen", render: (row) => <RelativeTime value={row.firstSeenUtc} /> },
  { id: "lastSeen", header: "Last seen", render: (row) => <RelativeTime value={row.lastSeenUtc} /> },
  {
    id: "actions",
    header: "",
    render: (row) => (
      <Stack direction="row" spacing={1} sx={{ justifyContent: "flex-end" }}>
        <NodeRestartButton node={row} />
        <NodeDeleteButton node={row} />
      </Stack>
    ),
  },
];

/** The worker fleet: which nodes exist, which are heartbeating, and what they run, plus per-pool compute controls
 *  (always-on floor, manual scale, spawn) and a per-node restart. */
export default function NodesPage() {
  return (
    <Page data-testid="page-nodes">
      <PageHeader
        title="Nodes"
        subtitle="A node is online when it heartbeated within the last minute; anything older shows as offline."
      />

      <Stack spacing={3}>
        <WorkerPoolsPanel />

        <PagedTable
          queryKey={["nodes", "list"]}
          fetchPage={(page, pageSize) => nodeApi.list({ page, pageSize })}
          columns={columns}
          rowKey={(row) => row.name}
          pollMs={5000}
          emptyMessage="No worker nodes have registered yet."
          data-testid="nodes-table"
        />
      </Stack>
    </Page>
  );
}
