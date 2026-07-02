import { useNavigate } from "react-router-dom";
import Stack from "@mui/material/Stack";
import Typography from "@mui/material/Typography";
import { repoApi } from "../../api/endpoints";
import type { Repo } from "../../api/types";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";

const columns: Column<Repo>[] = [
  {
    id: "name",
    header: "Name",
    render: (row) => <Typography variant="body2" fontWeight={600}>{row.name}</Typography>,
  },
  { id: "remoteUrl", header: "Remote URL", render: (row) => row.remoteUrl ?? "-" },
  { id: "rootPath", header: "Root path", render: (row) => row.rootPath ?? "-" },
  { id: "lastSync", header: "Last sync", render: (row) => <RelativeTime value={row.lastSyncUtc} /> },
];

/** The synced repos: what the control plane knows about, entry point to each repo's pipelines. */
export default function ReposPage() {
  const navigate = useNavigate();
  return (
    <Stack spacing={2} data-testid="page-repos">
      <Typography variant="h5">Repos</Typography>
      <PagedTable
        queryKey={["repos", "list"]}
        fetchPage={(page, pageSize) => repoApi.list({ page, pageSize })}
        columns={columns}
        rowKey={(row) => row.id}
        onRowClick={(row) => navigate(`/repos/${row.id}`)}
        emptyMessage="No repos have been synced yet. Register a repo source to get started."
      />
    </Stack>
  );
}
