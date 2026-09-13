import { useMemo, useState, type ReactNode } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ChevronLeft, ChevronRight, CircleAlert, Eye, Loader2, Save, Search } from "lucide-react";
import { toast } from "sonner";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import {
  deliveryApi,
  type DeliveryOsduSchema, type DeliverySchemaFetchResult, type DeliverySchemaSearchRequest, type DeliverySchemaSearchResult,
} from "../../api/delivery";
import { DataTable, type Column } from "../../components/DataTable";
import { EmptyState } from "../../components/EmptyState";
import { RelativeTime } from "../../components/RelativeTime";
import { RunStatusBadge } from "../../components/StatusBadge";
import { TruncatedText } from "../../components/TruncatedText";
import { ProblemView, problemText, TaskProgress, TemplateSheet } from "./TemplateSheet";
import { isTerminalTask, taskResult, useComputeTask } from "./useComputeTask";

/** How many schemas one search asks OSDU for: the most a schema search returns at once. */
const PAGE_SIZE = 100;

/** A schema being looked at: its kind, the flow whose OSDU connection fetches it, and the fetch task once queued. */
interface Viewing {
  kind: string;
  pipelineId: string;
  taskId: string | null;
}

function blankToNull(value: string): string | null {
  const trimmed = value.trim();
  return trimmed === "" ? null : trimmed;
}

function FilterField({
  id, label, value, onChange, placeholder,
}: { id: string; label: string; value: string; onChange: (value: string) => void; placeholder: string }) {
  return (
    <div className="flex flex-col gap-1.5">
      <Label htmlFor={id}>{label}</Label>
      <Input
        id={id}
        className="h-8 font-mono"
        placeholder={placeholder}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        data-testid={id}
      />
    </div>
  );
}

interface TemplatesBrowseTabProps {
  canOperate: boolean;
  canAuthor: boolean;
}

/**
 * Browse OSDU: search the schemas OSDU publishes through a delivery flow's OSDU connection, look at one laid out as a
 * template, and save it. Both the search and the fetch run on a node with the flow's credentials, exactly like a run, so
 * the page queues a task and follows it.
 */
export function TemplatesBrowseTab({ canOperate, canAuthor }: TemplatesBrowseTabProps) {
  const queryClient = useQueryClient();
  const [repoId, setRepoId] = useLocalStorageState("sqlflow.templates.browse.repo", "");
  const [pipelineId, setPipelineId] = useLocalStorageState("sqlflow.templates.browse.flow", "");
  const [authority, setAuthority] = useState("");
  const [source, setSource] = useState("");
  const [entityType, setEntityType] = useState("");
  const [status, setStatus] = useState("PUBLISHED");
  const [latestOnly, setLatestOnly] = useState(true);
  const [searchTaskId, setSearchTaskId] = useState<string | null>(null);
  const [searchRequest, setSearchRequest] = useState<DeliverySchemaSearchRequest | null>(null);
  const [viewing, setViewing] = useState<Viewing | null>(null);

  const repos = useQuery({ queryKey: ["delivery", "mapping-builder", "repos"], queryFn: deliveryApi.builderRepos });
  const repoList = repos.data ?? [];
  const withFlows = repoList.filter((candidate) => candidate.flows.length > 0);
  const repo = withFlows.find((candidate) => candidate.repoId === repoId) ?? null;
  const flow = repo?.flows.find((candidate) => candidate.pipelineId === pipelineId) ?? null;

  // A remembered repository or flow that is gone would aim the search at nothing: fall back to none, and take a
  // repository's only flow when it has just one.
  if (repos.data !== undefined && repoId !== "" && repo === null) {
    setRepoId("");
  }

  const onlyFlow = repo !== null && repo.flows.length === 1 ? repo.flows[0].pipelineId : "";
  if (repo !== null && flow === null && pipelineId !== onlyFlow) {
    setPipelineId(onlyFlow);
  }

  const search = useComputeTask(searchTaskId);
  const searchResult = useMemo(
    () => (search.data?.status === "succeeded" ? taskResult<DeliverySchemaSearchResult>(search.data) : null),
    [search.data],
  );
  const searching = searchTaskId !== null && !search.isError && !isTerminalTask(search.data);

  const fetchTask = useComputeTask(viewing?.taskId ?? null);
  const fetched = useMemo(
    () => (fetchTask.data?.status === "succeeded" ? taskResult<DeliverySchemaFetchResult>(fetchTask.data) : null),
    [fetchTask.data],
  );
  const preview = useQuery({
    queryKey: ["delivery", "templates", "preview", "osdu", viewing?.taskId ?? null, repoId],
    queryFn: () => deliveryApi.previewTemplate(fetched!.kind, fetched!.schema, repoId === "" ? null : repoId),
    enabled: fetched !== null,
  });

  const startSearch = useMutation({
    mutationFn: (request: DeliverySchemaSearchRequest) => deliveryApi.searchSchemas(request),
    onSuccess: (accepted, request) => {
      setSearchTaskId(accepted.taskId);
      setSearchRequest(request);
    },
    onError: (error) => toast.error(problemText(error)),
  });

  const startFetch = useMutation({
    mutationFn: (target: { pipelineId: string; kind: string }) => deliveryApi.fetchSchema(target.pipelineId, target.kind),
    // The sheet may have been closed, or another schema opened, while the fetch was being queued.
    onSuccess: (accepted, target) => setViewing((current) => (
      current !== null && current.kind === target.kind && current.pipelineId === target.pipelineId && current.taskId === null
        ? { ...current, taskId: accepted.taskId }
        : current)),
    onError: (error) => toast.error(problemText(error)),
  });

  const save = useMutation({
    mutationFn: (schema: DeliverySchemaFetchResult) =>
      deliveryApi.saveTemplate(schema.kind, schema.schema, `OSDU ${schema.endpoint} through flow '${schema.flow}'`),
    onSuccess: (saved) => {
      toast.success(saved.outcome === "created"
        ? `Saved template ${saved.template.kind} version ${saved.template.version}.`
        : `Template ${saved.template.kind} version ${saved.template.version} was already saved, so nothing changed.`);
      void queryClient.invalidateQueries({ queryKey: ["delivery", "templates"] });
    },
    onError: (error) => toast.error(problemText(error)),
  });

  const searchFromForm = () => {
    if (flow === null) {
      return;
    }

    startSearch.mutate({
      pipelineId: flow.pipelineId,
      authority: blankToNull(authority),
      source: blankToNull(source),
      entityType: blankToNull(entityType),
      status: blankToNull(status),
      latestVersion: latestOnly,
      limit: PAGE_SIZE,
      offset: 0,
    });
  };

  // Paging repeats the search that produced the page on show, not whatever the form holds now.
  const page = (offset: number) => {
    if (searchRequest !== null) {
      startSearch.mutate({ ...searchRequest, offset });
    }
  };

  const view = (kind: string) => {
    if (searchRequest === null || !canOperate) {
      return;
    }

    save.reset();
    setViewing({ kind, pipelineId: searchRequest.pipelineId, taskId: null });
    startFetch.mutate({ pipelineId: searchRequest.pipelineId, kind });
  };

  const columns: Column<DeliveryOsduSchema>[] = [
    { id: "kind", header: "Kind", render: (row) => <TruncatedText text={row.kind} mono maxWidth={460} /> },
    { id: "status", header: "Status", render: (row) => <span className="font-mono text-[12px]">{row.status ?? "-"}</span> },
    { id: "scope", header: "Scope", render: (row) => <span className="font-mono text-[12px]">{row.scope ?? "-"}</span> },
    { id: "created", header: "Created", render: (row) => <RelativeTime value={row.createdUtc} /> },
    { id: "createdBy", header: "Created by", render: (row) => <TruncatedText text={row.createdBy} maxWidth={220} /> },
    {
      id: "view",
      header: "",
      align: "right",
      render: (row) => (
        <Button
          variant="outline"
          size="xs"
          onClick={(event) => { event.stopPropagation(); view(row.kind); }}
          data-testid={`templates-browse-view-${row.kind}`}
        >
          <Eye />
          View
        </Button>
      ),
    },
  ];

  // What the sheet shows while the schema is on its way, and what went wrong when it does not arrive.
  const fetchEnded = fetchTask.data !== undefined && isTerminalTask(fetchTask.data);
  let progress: ReactNode = null;
  let problem: ReactNode = null;
  if (viewing !== null) {
    if (startFetch.isError) {
      problem = <ProblemView error={startFetch.error} testId="templates-browse-fetch-error" />;
    } else if (fetchTask.isError) {
      problem = <ProblemView error={fetchTask.error} testId="templates-browse-fetch-error" />;
    } else if (fetchEnded && fetchTask.data?.status !== "succeeded") {
      problem = (
        <Alert variant="destructive" data-testid="templates-browse-fetch-error">
          <CircleAlert />
          <AlertDescription>
            <p>{fetchTask.data?.error ?? `The fetch ended ${fetchTask.data?.status ?? "without a status"} and returned no schema.`}</p>
          </AlertDescription>
        </Alert>
      );
    } else if (fetchEnded && fetched === null) {
      problem = (
        <Alert variant="destructive" data-testid="templates-browse-fetch-error">
          <CircleAlert />
          <AlertDescription><p>The node answered, but its answer could not be read as a schema.</p></AlertDescription>
        </Alert>
      );
    } else if (preview.isError) {
      problem = <ProblemView error={preview.error} testId="templates-browse-preview-error" />;
    } else if (!fetchEnded) {
      progress = (
        <TaskProgress
          label={`Fetching ${viewing.kind} and every schema it refers to from OSDU`}
          task={fetchTask.data}
          testId="templates-browse-fetch-progress"
        />
      );
    } else if (preview.data === undefined) {
      progress = <TaskProgress label="Laying the schema out as a template" testId="templates-browse-preview-progress" />;
    } else if (save.isError) {
      problem = <ProblemView error={save.error} testId="templates-browse-save-error" />;
    }
  }

  if (!canOperate) {
    return (
      <Alert data-testid="templates-browse-forbidden">
        <CircleAlert />
        <AlertDescription>
          <p>Browsing OSDU runs on a node with a delivery flow&apos;s credentials, which needs the operate scope.</p>
        </AlertDescription>
      </Alert>
    );
  }

  return (
    <div className="flex flex-col gap-4" data-testid="templates-browse">
      {repos.isError && <ProblemView error={repos.error} />}
      {repos.data === undefined && !repos.isError && <Skeleton className="h-40 w-full rounded-lg" />}
      {repos.data !== undefined && withFlows.length === 0 && (
        <EmptyState
          icon={<Search />}
          title="No repository has a delivery flow"
          description="OSDU is browsed through a delivery flow's OSDU connection. Sync a repository that declares one, then come back here."
          data-testid="templates-browse-no-flows"
        />
      )}
      {withFlows.length > 0 && (
        <Card className="gap-3 rounded-lg p-4" data-testid="templates-browse-form">
          <form
            className="flex flex-col gap-3"
            onSubmit={(event) => { event.preventDefault(); searchFromForm(); }}
          >
            <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="templates-browse-repo">Repository</Label>
                <Select value={repo?.repoId ?? ""} onValueChange={(next) => { setRepoId(next); setPipelineId(""); }}>
                  <SelectTrigger id="templates-browse-repo" size="sm" className="h-8 w-full" data-testid="templates-browse-repo">
                    <SelectValue placeholder="Pick a repository" />
                  </SelectTrigger>
                  <SelectContent>
                    {withFlows.map((candidate) => (
                      <SelectItem key={candidate.repoId} value={candidate.repoId}>{candidate.name}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="templates-browse-flow">OSDU connection</Label>
                <Select value={flow?.pipelineId ?? ""} onValueChange={setPipelineId} disabled={repo === null}>
                  <SelectTrigger id="templates-browse-flow" size="sm" className="h-8 w-full" data-testid="templates-browse-flow">
                    <SelectValue placeholder={repo === null ? "Pick a repository first" : "Pick a delivery flow"} />
                  </SelectTrigger>
                  <SelectContent>
                    {(repo?.flows ?? []).map((candidate) => (
                      <SelectItem key={candidate.pipelineId} value={candidate.pipelineId}>
                        {candidate.name}
                        <span className="font-mono text-[11px] text-muted-foreground">{candidate.endpoint}</span>
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
            </div>
            {flow !== null && (
              <p className="text-xs text-muted-foreground" data-testid="templates-browse-endpoint">
                A node searches <span className="font-mono">{flow.endpoint}</span> with the credentials of flow {flow.name}.
              </p>
            )}
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
              <FilterField id="templates-browse-authority" label="Authority" value={authority} onChange={setAuthority} placeholder="osdu" />
              <FilterField id="templates-browse-source" label="Source" value={source} onChange={setSource} placeholder="wks" />
              <FilterField id="templates-browse-entity-type" label="Entity type" value={entityType} onChange={setEntityType} placeholder="master-data--Wellbore" />
              <FilterField id="templates-browse-status" label="Status" value={status} onChange={setStatus} placeholder="PUBLISHED" />
            </div>
            <div className="flex flex-wrap items-center gap-3">
              <Label className="flex items-center gap-2 text-[13px] font-normal">
                <Switch checked={latestOnly} onCheckedChange={setLatestOnly} data-testid="templates-browse-latest" />
                Latest versions only
              </Label>
              <Button
                type="submit"
                size="sm"
                className="ml-auto"
                disabled={flow === null || startSearch.isPending || searching}
                data-testid="templates-browse-search"
              >
                {startSearch.isPending || searching ? <Loader2 className="animate-spin" /> : <Search />}
                Search OSDU
              </Button>
            </div>
          </form>
        </Card>
      )}

      {searchTaskId !== null && (
        <Card className="gap-2 rounded-lg p-3" data-testid="templates-browse-task">
          <div className="flex flex-wrap items-center gap-2 text-[13px] font-medium">
            Schema search
            {search.data !== undefined
              ? <RunStatusBadge status={search.data.status} testId="templates-browse-status-badge" />
              : !search.isError && <Loader2 className="size-4 animate-spin text-muted-foreground" />}
            {search.data?.claimedByNode && <span className="font-mono text-[11px] text-muted-foreground">{search.data.claimedByNode}</span>}
            {searchResult !== null && (
              <span className="text-xs font-normal text-muted-foreground" data-testid="templates-browse-summary">
                <span className="font-mono tabular-nums text-foreground">{searchResult.totalCount.toLocaleString()}</span>
                {` schema${searchResult.totalCount === 1 ? "" : "s"} found through flow ${searchResult.flow} at `}
                <span className="font-mono">{searchResult.endpoint}</span>
              </span>
            )}
          </div>
          {search.isError && <ProblemView error={search.error} testId="templates-browse-error" />}
          {search.data !== undefined && isTerminalTask(search.data) && search.data.status !== "succeeded" && (
            <p className="text-[13px] text-destructive" data-testid="templates-browse-error">
              {search.data.error ?? `The search ended ${search.data.status} and found nothing.`}
            </p>
          )}
          {search.data?.status === "succeeded" && searchResult === null && (
            <p className="text-[13px] text-destructive" data-testid="templates-browse-error">
              The node answered, but its answer could not be read as a list of schemas.
            </p>
          )}
        </Card>
      )}

      {searchResult !== null && (
        <DataTable
          columns={columns}
          rows={searchResult.schemas}
          rowKey={(row) => row.kind}
          onRowClick={(row) => view(row.kind)}
          emptyMessage="OSDU publishes no schema that matches. Loosen the filters, or turn off Latest versions only."
          minWidth={900}
          footer={searchResult.totalCount > searchResult.schemas.length ? (
            <div
              className="flex flex-wrap items-center justify-between gap-2 border-t border-border px-3 py-1.5 text-xs text-muted-foreground"
              data-testid="templates-browse-paging"
            >
              <span className="tabular-nums">
                {`${(searchResult.offset + 1).toLocaleString()} to ${(searchResult.offset + searchResult.schemas.length).toLocaleString()} of ${searchResult.totalCount.toLocaleString()}`}
              </span>
              <div className="flex items-center gap-1">
                <Button
                  variant="ghost"
                  size="xs"
                  disabled={searchResult.offset === 0 || startSearch.isPending || searching}
                  onClick={() => page(Math.max(0, searchResult.offset - PAGE_SIZE))}
                  data-testid="templates-browse-previous"
                >
                  <ChevronLeft />
                  Previous
                </Button>
                <Button
                  variant="ghost"
                  size="xs"
                  disabled={searchResult.offset + searchResult.schemas.length >= searchResult.totalCount || startSearch.isPending || searching}
                  onClick={() => page(searchResult.offset + searchResult.schemas.length)}
                  data-testid="templates-browse-next"
                >
                  Next
                  <ChevronRight />
                </Button>
              </div>
            </div>
          ) : undefined}
          data-testid="templates-browse-results"
        />
      )}

      <TemplateSheet
        open={viewing !== null}
        onClose={() => setViewing(null)}
        title={viewing?.kind ?? "Schema"}
        description={fetched !== null
          ? `Fetched from OSDU at ${fetched.endpoint} through flow ${fetched.flow}. Nothing is saved until you save it.`
          : "Fetched from OSDU on a node, with the flow's credentials. Nothing is saved until you save it."}
        progress={progress}
        problem={problem}
        detail={fetched !== null ? preview.data : undefined}
        previewSchema={fetched?.schema}
        actions={canAuthor && fetched !== null && preview.data !== undefined ? (
          <Button size="sm" onClick={() => save.mutate(fetched)} disabled={save.isPending} data-testid="templates-browse-save">
            {save.isPending ? <Loader2 className="animate-spin" /> : <Save />}
            Save template
          </Button>
        ) : undefined}
        busy={save.isPending}
        testId="templates-browse-sheet"
      />
    </div>
  );
}
