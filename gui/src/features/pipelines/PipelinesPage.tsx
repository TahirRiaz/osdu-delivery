import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import FolderOutlinedIcon from "@mui/icons-material/FolderOutlined";
import Chip from "@mui/material/Chip";
import FormControlLabel from "@mui/material/FormControlLabel";
import MenuItem from "@mui/material/MenuItem";
import Stack from "@mui/material/Stack";
import Switch from "@mui/material/Switch";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import { pipelineApi, repoApi } from "../../api/endpoints";
import type { PipelineSummary } from "../../api/types";
import { FilterBar } from "../../components/FilterBar";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { PagedTable, type Column, type TableGrouping } from "../../components/PagedTable";
import { ActiveBadge } from "../../components/StatusBadge";
import { TruncatedText } from "../../components/TruncatedText";
import { fileNameOf, folderOf } from "../repos/project";

const kinds = ["file", "ing", "exp", "sp", "inv", "hc", "scm", "batch"];

const baseColumns: Column<PipelineSummary>[] = [
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
];

const flatColumns: Column<PipelineSummary>[] = [
  ...baseColumns,
  { id: "relativePath", header: "Path", render: (row) => <TruncatedText text={row.relativePath} mono maxWidth={360} /> },
];

// Grouped, the folder lives in the tree node above the row, so the path column narrows to just the file name.
const groupedColumns: Column<PipelineSummary>[] = [
  ...baseColumns,
  { id: "fileName", header: "File", render: (row) => <TruncatedText text={fileNameOf(row.relativePath)} mono maxWidth={260} /> },
];

// The folder tree over the flat catalog: pipelines cluster under their repo, then under the folder their flow
// document lives in (nested folders keep their full path), mirroring the git estate's layout. Rows arrive from
// the API ordered repo-then-path (sort=path), which keeps both cluster levels contiguous.
function makeFolderGrouping(repoName: (repoId: string) => string): TableGrouping<PipelineSummary> {
  return {
    groupKey: (row) => row.repoId,
    renderGroupHeader: (rows) => (
      <Stack
        direction="row"
        spacing={1.5}
        alignItems="baseline"
        useFlexGap
        flexWrap="wrap"
        data-testid="repo-group-header"
      >
        <Typography variant="body2" sx={{ fontWeight: 700 }}>Repo: {repoName(rows[0].repoId)}</Typography>
        <Typography variant="body2" color="text.secondary" component="span">({rows.length})</Typography>
      </Stack>
    ),
    subKey: (row) => folderOf(row.relativePath),
    renderSubHeader: (rows) => (
      <Stack direction="row" spacing={1} alignItems="center" data-testid="folder-group-header">
        <FolderOutlinedIcon fontSize="small" sx={{ color: "text.secondary" }} />
        <Typography variant="body2" sx={{ fontWeight: 600, fontFamily: "monospace", fontSize: "0.8125rem" }}>
          {folderOf(rows[0].relativePath)}
        </Typography>
        <Typography variant="body2" color="text.secondary" component="span">({rows.length})</Typography>
      </Stack>
    ),
  };
}

/** All pipelines across repos, with server-side filtering on repo, kind, active flag, and name. Grouped by
 * folder (the default) the list mirrors the git estate: each pipeline under its repo and the folder its flow
 * document lives in. Toggled flat it is the plain catalog list with the full path as a column. */
export default function PipelinesPage() {
  const navigate = useNavigate();
  const [repoFilter, setRepoFilter] = useState("");
  const [kindFilter, setKindFilter] = useState("");
  const [activeFilter, setActiveFilter] = useState("");
  const [nameInput, setNameInput] = useState("");
  const [nameFilter, setNameFilter] = useState("");
  const [groupByFolder, setGroupByFolder] = useState(true);

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
  const repoNameById = new Map(repoOptions.map((repo) => [repo.id, repo.name]));

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
        <FormControlLabel
          control={(
            <Switch
              size="small"
              checked={groupByFolder}
              onChange={(e) => setGroupByFolder(e.target.checked)}
              data-testid="group-by-folder"
            />
          )}
          label="Group by folder"
        />
      </FilterBar>

      <PagedTable
        queryKey={["pipelines", "list", repoFilter, kindFilter, activeFilter, nameFilter, groupByFolder]}
        fetchPage={(page, pageSize) => pipelineApi.list({
          page,
          pageSize,
          repoId: repoFilter === "" ? undefined : repoFilter,
          kind: kindFilter === "" ? undefined : kindFilter,
          active: activeFilter === "" ? undefined : activeFilter === "active",
          name: nameFilter === "" ? undefined : nameFilter,
          sort: groupByFolder ? "path" : undefined,
        })}
        columns={groupByFolder ? groupedColumns : flatColumns}
        rowKey={(row) => row.id}
        onRowClick={(row) => navigate(`/pipelines/${row.id}`)}
        emptyMessage="No pipelines match the current filters."
        grouping={groupByFolder
          ? makeFolderGrouping((repoId) => repoNameById.get(repoId) ?? repoId)
          : undefined}
      />
    </Page>
  );
}
