import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { pipelineApi, repoApi } from "../../api/endpoints";
import type { PipelineSummary } from "../../api/types";
import { FilterBar } from "../../components/FilterBar";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { PagedTable, type Column } from "../../components/PagedTable";
import { ActiveBadge } from "../../components/StatusBadge";
import { ConnectionRef } from "../../components/ConnectionRef";
import { TruncatedText } from "../../components/TruncatedText";

const kinds = ["file", "ing", "exp", "sp", "inv", "hc", "scm", "batch"];

/** The radix Select cannot carry an empty-string item value, so "all" stands in for the unfiltered choice. */
const ALL = "all";

const columns: Column<PipelineSummary>[] = [
  {
    id: "name",
    header: "Name",
    render: (row) => <span className="font-mono text-[12px] font-medium">{row.name}</span>,
  },
  {
    id: "kind",
    header: "Kind",
    render: (row) => (
      <span className="inline-flex items-center gap-1">
        <Badge variant="outline">{row.kind}</Badge>
        {row.executionMode === "manual" && (
          <Badge variant="secondary" className="bg-warning/15 text-warning">manual</Badge>
        )}
      </span>
    ),
  },
  { id: "batch", header: "Batch", render: (row) => row.batch ?? "-" },
  {
    id: "wave",
    header: "Wave",
    align: "right",
    render: (row) => (
      <span className="font-mono tabular-nums">{row.wave === -1 ? "-" : String(row.wave)}</span>
    ),
  },
  { id: "active", header: "Active", render: (row) => <ActiveBadge active={row.active} /> },
  { id: "sourceServer", header: "Source", render: (row) => <ConnectionRef value={row.sourceServer} /> },
  { id: "targetServer", header: "Target", render: (row) => <ConnectionRef value={row.targetServer} /> },
  { id: "relativePath", header: "Path", render: (row) => <TruncatedText text={row.relativePath} mono maxWidth={360} /> },
];

/** All pipelines across repos, with server-side filtering on repo, project (root folder), kind, active flag,
 * and name. */
export default function PipelinesPage() {
  const navigate = useNavigate();
  const [repoFilter, setRepoFilter] = useState("");
  const [projectFilter, setProjectFilter] = useState("");
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

  // Project options are the repo-root folders, scoped to the selected repo so the dropdown offers only projects
  // that exist in it; changing the repo refetches them (and the repo's onChange clears any stale selection).
  const projects = useQuery({
    queryKey: ["pipelines", "projects", repoFilter],
    queryFn: () => pipelineApi.projects(repoFilter === "" ? undefined : repoFilter),
  });
  const projectOptions = projects.data ?? [];

  return (
    <Page data-testid="page-pipelines">
      <PageHeader title="Pipelines" />

      <FilterBar>
        <Select
          value={repoFilter === "" ? ALL : repoFilter}
          onValueChange={(value) => {
            setRepoFilter(value === ALL ? "" : value);
            setProjectFilter(""); // the project options are repo-scoped; a selection from another repo is stale
          }}
        >
          <SelectTrigger size="sm" className="h-8 w-52" aria-label="Repo" data-testid="filter-repo">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>all repos</SelectItem>
            {repoOptions.map((repo) => (
              <SelectItem key={repo.id} value={repo.id}>{repo.name}</SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select
          value={projectFilter === "" ? ALL : projectFilter}
          onValueChange={(value) => setProjectFilter(value === ALL ? "" : value)}
        >
          <SelectTrigger size="sm" className="h-8 w-44" aria-label="Project" data-testid="filter-project">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>all projects</SelectItem>
            {projectOptions.map((project) => (
              <SelectItem key={project} value={project}>{project}</SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select
          value={kindFilter === "" ? ALL : kindFilter}
          onValueChange={(value) => setKindFilter(value === ALL ? "" : value)}
        >
          <SelectTrigger size="sm" className="h-8 w-32" aria-label="Kind" data-testid="filter-kind">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>all kinds</SelectItem>
            {kinds.map((kind) => (
              <SelectItem key={kind} value={kind}>{kind}</SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select
          value={activeFilter === "" ? ALL : activeFilter}
          onValueChange={(value) => setActiveFilter(value === ALL ? "" : value)}
        >
          <SelectTrigger size="sm" className="h-8 w-32" aria-label="Active" data-testid="filter-active">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>all</SelectItem>
            <SelectItem value="active">active</SelectItem>
            <SelectItem value="inactive">inactive</SelectItem>
          </SelectContent>
        </Select>
        <Input
          value={nameInput}
          onChange={(e) => setNameInput(e.target.value)}
          placeholder="Filter by name"
          aria-label="Name"
          data-testid="filter-name"
          className="h-8 w-56"
        />
      </FilterBar>

      <PagedTable
        queryKey={["pipelines", "list", repoFilter, projectFilter, kindFilter, activeFilter, nameFilter]}
        fetchPage={(page, pageSize) => pipelineApi.list({
          page,
          pageSize,
          repoId: repoFilter === "" ? undefined : repoFilter,
          project: projectFilter === "" ? undefined : projectFilter,
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
