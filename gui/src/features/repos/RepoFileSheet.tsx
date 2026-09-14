import { Link as RouterLink } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ExternalLink, FileCode2 } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { isApiError } from "../../api/client";
import { repoApi, scheduleApi } from "../../api/endpoints";
import type { PipelineSummary } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";
import { Mono } from "../../components/Mono";
import { PagedTable } from "../../components/PagedTable";
import { formatBytes } from "../../lib/time";
import { scheduleColumns } from "../schedules/scheduleColumns";
import { isScheduleLibraryPath } from "./project";

/**
 * One YAML document of a repository, opened from the repo view's file list: its text as the repository holds it
 * (secret-redacted by the control plane) and, for a document that schedules anything, the schedules the catalog
 * holds for it. A flow file lists every schedule that runs the flow, its own inline block and any it joined by name;
 * a schedule library lists the schedules it declares. A mapping schedules nothing, so it shows the YAML alone.
 */
export function RepoFileSheet({
  repoId, path, pipeline, onClose,
}: {
  repoId: string;
  path: string;
  /** The pipeline the catalog imported from this file, when the file is a registered flow. */
  pipeline: PipelineSummary | undefined;
  onClose: () => void;
}) {
  const file = useQuery({
    queryKey: ["repos", "file", repoId, path],
    queryFn: () => repoApi.file(repoId, path),
    // A refused preview (binary content, a file that left the branch) is an answer, not a transient fault.
    retry: false,
  });

  const data = file.data;
  const library = isScheduleLibraryPath(path);
  const where = data?.readFrom === "disk" ? "in the repo's root path" : "on the synced branch";

  const yaml = (
    <>
      {file.isLoading && <Skeleton className="h-[520px] w-full" />}
      {file.isError && (
        isApiError(file.error)
          ? <CorrelationError error={file.error} />
          : <p className="text-[13px] text-destructive">{String(file.error)}</p>
      )}
      {data?.truncated === true && (
        <p className="text-xs text-warning" data-testid="repo-file-truncated">
          {`This file is ${formatBytes(data.sizeBytes)}; the preview shows its first ${data.yaml.length.toLocaleString()} characters.`}
        </p>
      )}
      {data !== undefined && <CodeView value={data.yaml} language="yaml" fill data-testid="repo-file-yaml" />}
    </>
  );

  return (
    <Sheet open onOpenChange={(next) => !next && onClose()}>
      <SheetContent className="w-full gap-0 sm:max-w-3xl" data-testid="repo-file-sheet">
        <SheetHeader className="border-b">
          <SheetTitle className="flex min-w-0 items-center gap-2">
            <FileCode2 className="size-4 shrink-0 text-muted-foreground" />
            <span className="truncate font-mono text-[13px]">{path.slice(path.lastIndexOf("/") + 1)}</span>
            {pipeline !== undefined && <Badge variant="outline">{pipeline.kind}</Badge>}
            {library && <Badge variant="outline">schedule library</Badge>}
          </SheetTitle>
          <SheetDescription>
            {pipeline !== undefined
              ? `The document behind the ${pipeline.name} pipeline, as it stands ${where}.`
              : library
                ? `The shared schedules flows join by name, as they stand ${where}.`
                : `As it stands ${where}.`}
          </SheetDescription>
        </SheetHeader>

        <div className="flex min-h-0 flex-1 flex-col gap-3 overflow-y-auto p-4">
          <div className="flex items-center justify-between gap-2">
            <span className="flex min-w-0 items-center gap-2">
              <Mono>{path}</Mono>
              {data !== undefined && (
                <span className="shrink-0 text-xs text-muted-foreground">{formatBytes(data.sizeBytes)}</span>
              )}
            </span>
            {pipeline !== undefined && (
              <Button variant="ghost" size="xs" asChild data-testid="repo-file-flow-link">
                <RouterLink to={`/pipelines/${pipeline.id}`} onClick={onClose}>
                  Open flow
                  <ExternalLink />
                </RouterLink>
              </Button>
            )}
          </div>

          {pipeline !== undefined || library ? (
            <Tabs defaultValue="yaml" className="flex min-h-0 flex-1 flex-col">
              <TabsList>
                <TabsTrigger value="yaml" data-testid="repo-file-tab-yaml">YAML</TabsTrigger>
                <TabsTrigger value="schedules" data-testid="repo-file-tab-schedules">Schedules</TabsTrigger>
              </TabsList>
              <TabsContent value="yaml" className="flex min-h-0 flex-1 flex-col gap-3">
                {yaml}
              </TabsContent>
              <TabsContent value="schedules">
                <PagedTable
                  queryKey={["schedules", "by-file", repoId, path, pipeline?.id ?? null]}
                  fetchPage={(page, pageSize) => (pipeline !== undefined
                    ? scheduleApi.list({ pipelineId: pipeline.id, page, pageSize })
                    : scheduleApi.list({ repoId, definitionPath: path, page, pageSize }))}
                  columns={scheduleColumns}
                  rowKey={(row) => row.id}
                  emptyMessage={pipeline !== undefined
                    ? "No schedule runs this flow."
                    : "The catalog holds no schedule from this file. Its schedules appear once a sync has read it."}
                />
              </TabsContent>
            </Tabs>
          ) : yaml}
        </div>
      </SheetContent>
    </Sheet>
  );
}
