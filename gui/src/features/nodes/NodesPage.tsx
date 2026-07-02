import Box from "@mui/material/Box";
import Stack from "@mui/material/Stack";
import Typography from "@mui/material/Typography";
import type { Node } from "../../api/types";
import { nodeApi } from "../../api/endpoints";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { OnlineBadge } from "../../components/StatusBadge";

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
];

/** The worker fleet: which nodes exist, which are heartbeating, and what they run. */
export default function NodesPage() {
  return (
    <Box data-testid="page-nodes">
      <Stack sx={{ mb: 2 }}>
        <Typography variant="h5">Nodes</Typography>
        <Typography variant="body2" color="text.secondary">
          A node is online when it heartbeated within the last minute; anything older shows as offline.
        </Typography>
      </Stack>

      <PagedTable
        queryKey={["nodes", "list"]}
        fetchPage={(page, pageSize) => nodeApi.list({ page, pageSize })}
        columns={columns}
        rowKey={(row) => row.name}
        pollMs={5000}
        emptyMessage="No worker nodes have registered yet."
        data-testid="nodes-table"
      />
    </Box>
  );
}
