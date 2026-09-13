import type { Node } from "../../api/types";
import { nodeApi } from "../../api/endpoints";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { OnlineBadge } from "../../components/StatusBadge";
import { TruncatedText } from "../../components/TruncatedText";
import { NodeRestartButton } from "./NodeRestartButton";
import { NodeDeleteButton } from "./NodeDeleteButton";
import { PurgeOfflineNodesButton } from "./PurgeOfflineNodesButton";
import { WorkerPoolsPanel } from "./WorkerPoolsPanel";

const columns: Column<Node>[] = [
  {
    id: "name",
    header: "Name",
    fill: true,
    floor: 140,
    render: (row) => <TruncatedText text={row.name} mono maxWidth={1200} className="font-medium" />,
  },
  { id: "status", header: "Status", render: (row) => <OnlineBadge online={row.online} /> },
  { id: "version", header: "Version", render: (row) => <Mono>{row.version ?? "-"}</Mono> },
  {
    id: "lastSeen",
    header: "Last seen",
    // When a node first appeared qualifies how long it has been seen, so it follows the last sighting and gives way
    // first in a narrow table.
    render: (row) => (
      <span className="inline-flex items-baseline gap-1.5">
        <RelativeTime value={row.lastSeenUtc} />
        <span className="inline-flex items-baseline gap-1.5 text-muted-foreground @max-3xl/table:sr-only">
          since
          <RelativeTime value={row.firstSeenUtc} />
        </span>
      </span>
    ),
  },
  {
    id: "actions",
    header: "",
    render: (row) => (
      <div className="flex items-center justify-end gap-2">
        <NodeRestartButton node={row} />
        <NodeDeleteButton node={row} />
      </div>
    ),
  },
];

/** The worker fleet: which nodes exist, which are heartbeating, and what they run, plus per-pool compute controls
 *  (always-on floor, manual scale, spawn), a per-node restart, and a purge of the offline entries the fleet
 *  accumulates as pods come and go. */
export default function NodesPage() {
  return (
    <Page data-testid="page-nodes">
      <PageHeader
        title="Nodes"
        subtitle="A node is online when it heartbeated within the last minute; anything older shows as offline."
        actions={<PurgeOfflineNodesButton />}
      />

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
    </Page>
  );
}
