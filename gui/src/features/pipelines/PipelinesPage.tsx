import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import Chip from "@mui/material/Chip";
import MenuItem from "@mui/material/MenuItem";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import { pipelineApi, repoApi } from "../../api/endpoints";
import type { PipelineSummary } from "../../api/types";
import { FilterBar } from "../../components/FilterBar";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { PagedTable, type Column } from "../../components/PagedTable";
import { ActiveBadge } from "../../components/StatusBadge";

const kinds = ["file", "ing", "exp", "sp", "inv", "hc", "scm", "batch"];

const columns: Column<PipelineSummary>[] = [
  {
    id: "name",
    header: "Name",
    render: (row) => <Typography variant="body2" fontWeight={600}>{row.name}</Typography>,
  },
  { id: "kind", header: "Kind", render: (row) => <Chip size="small" label={row.kind} variant="outlined" /> },
  { id: "batch", header: "Batch", render: (row) => row.batch ?? "-" },
  { id: "wave", header: "Wave", render: (row) => (row.wave === -1 ? "-" : String(row.wave)) },
  { id: "active", header: "Active", render: (row) => <ActiveBadge active={row.active} /> },
  { id: "sourceServer", header: "Source", render: (row) => row.sourceServer ?? "-" },
  { id: "targetServer", header: "Target", render: (row) => row.targetServer ?? "-" },
  { id: "relativePath", header: "Path", render: (row) => row.relativePath },
];

/** All pipelines across repos, with server-side filtering on repo, kind, active flag, and name. */
export default function PipelinesPage() {
  const navigate = useNavigate();
  const [repoFilter, setRepoFilter] = useState("");
  const [kindFilter, setKindFilter] = useState("");
  const [activeFilter, setActiveFilter] = useState("");
  const [nameInput, setNameInput] = useState("");
  const [nameFilter, setNameFilter] = useState("");

  // The name filter debounces keystrokes so each pause, not each character, costs an API call.
  useEffect(() => {
    const timer = window.setTimeout(() => setNameFilter(nameInput.trim()), 400);
    return () => window.clearTimeout(timer);
  }, [nameInput]);

  const repos = useQuery({
    queryKey: ["repos", "all-for-pipeline-filter"],
    queryFn: () => repoApi.list({ page: 1, pageSize: 200 }),
  });
  const repoOptions = repos.data?.items ?? [];

  return (
    <Page data-testid="page-pipelines">
      <PageHeader title="Pipelines" />

      <FilterBar>
        <TextField
          select
          size="small"
          label="Repo"
          value={repoFilter}
          onChange={(e) => setRepoFilter(e.target.value)}
          inputProps={{ "data-testid": "filter-repo" }}
          sx={{ minWidth: 200 }}
        >
          <MenuItem value="">all</MenuItem>
          {repoOptions.map((repo) => (
            <MenuItem key={repo.id} value={repo.id}>{repo.name}</MenuItem>
          ))}
        </TextField>
        <TextField
          select
          size="small"
          label="Kind"
          value={kindFilter}
          onChange={(e) => setKindFilter(e.target.value)}
          inputProps={{ "data-testid": "filter-kind" }}
          sx={{ minWidth: 140 }}
        >
          <MenuItem value="">all</MenuItem>
          {kinds.map((kind) => (
            <MenuItem key={kind} value={kind}>{kind}</MenuItem>
          ))}
        </TextField>
        <TextField
          select
          size="small"
          label="Active"
          value={activeFilter}
          onChange={(e) => setActiveFilter(e.target.value)}
          inputProps={{ "data-testid": "filter-active" }}
          sx={{ minWidth: 140 }}
        >
          <MenuItem value="">all</MenuItem>
          <MenuItem value="active">active</MenuItem>
          <MenuItem value="inactive">inactive</MenuItem>
        </TextField>
        <TextField
          size="small"
          label="Name"
          placeholder="Filter by name"
          value={nameInput}
          onChange={(e) => setNameInput(e.target.value)}
          inputProps={{ "data-testid": "filter-name" }}
          sx={{ minWidth: 220 }}
        />
      </FilterBar>

      <PagedTable
        queryKey={["pipelines", "list", repoFilter, kindFilter, activeFilter, nameFilter]}
        fetchPage={(page, pageSize) => pipelineApi.list({
          page,
          pageSize,
          repoId: repoFilter === "" ? undefined : repoFilter,
          kind: kindFilter === "" ? undefined : kindFilter,
          active: activeFilter === "" ? undefined : activeFilter === "active",
          name: nameFilter === "" ? undefined : nameFilter,
        })}
        columns={columns}
        rowKey={(row) => row.id}
        onRowClick={(row) => navigate(`/pipelines/${row.id}`)}
        emptyMessage="No pipelines match the current filters."
      />
    </Page>
  );
}
