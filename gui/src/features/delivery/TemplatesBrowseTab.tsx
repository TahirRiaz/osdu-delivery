import { useDeferredValue, useMemo, useState, type ReactNode } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Archive, ExternalLink, Eye, FlaskConical, Loader2, Save } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { deliveryApi, type DeliveryOsduSchema } from "../../api/delivery";
import { DataTable, type Column } from "../../components/DataTable";
import { FilterBar } from "../../components/FilterBar";
import { LinkRef } from "../../components/LinkRef";
import { RelativeTime } from "../../components/RelativeTime";
import { SearchInput } from "../../components/SearchInput";
import { StatePill } from "../../components/StatusBadge";
import { TruncatedText } from "../../components/TruncatedText";
import { ProblemView, problemText, TaskProgress, TemplateSheet } from "./TemplateSheet";

/** The most rows the table renders at once; the search narrows the rest. */
const MAX_ROWS = 200;

/** A schema in the table, and how many versions of its entity type the release publishes. */
interface BrowseRow {
  schema: DeliveryOsduSchema;
  versions: number;
}

/** A schema being looked at: the release it is read from, and its kind. */
interface Viewing {
  release: string;
  kind: string;
}

/** A published schema, the usual case, reads plainly; one in development or obsolete stands out. */
function StatusCell({ schema }: { schema: DeliveryOsduSchema }) {
  if (schema.status === null || schema.status === "PUBLISHED") {
    return <span className="text-muted-foreground">{schema.status === null ? "-" : "Published"}</span>;
  }

  const obsolete = schema.status === "OBSOLETE";
  return (
    <StatePill
      tone={obsolete ? "muted" : "warning"}
      label={obsolete ? "Obsolete" : schema.status === "DEVELOPMENT" ? "In development" : schema.status}
      icon={obsolete ? Archive : FlaskConical}
      testId={`templates-browse-status-${schema.kind}`}
    />
  );
}

/** A link out to the data definitions repository, opening in a new tab. */
function RepositoryLink({ href, children, testId }: { href: string; children: ReactNode; testId: string }) {
  return (
    <Button asChild variant="link" size="sm" className="h-auto px-0">
      <a href={href} target="_blank" rel="noreferrer" data-testid={testId}>
        {children}
        <ExternalLink className="size-3.5" />
      </a>
    </Button>
  );
}

/**
 * Browse OSDU: the OSDU data definitions, the Open Group's public repository of OSDU schemas and the canonical source of
 * every OSDU kind. Pick a release (the newest by default), find a kind with one search, look at it laid out as a template
 * and save it. The control plane reads the repository; the schemas are public, so no flow, credential or node is involved.
 */
export function TemplatesBrowseTab({ canAuthor }: { canAuthor: boolean }) {
  const queryClient = useQueryClient();
  const [pickedRelease, setPickedRelease] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [allVersions, setAllVersions] = useState(false);
  const [viewing, setViewing] = useState<Viewing | null>(null);
  const term = useDeferredValue(search);

  const releases = useQuery({
    queryKey: ["delivery", "osdu-definitions", "releases"],
    queryFn: deliveryApi.osduReleases,
    staleTime: 10 * 60_000,
  });
  const release = pickedRelease ?? releases.data?.[0]?.name ?? null;

  // A release is a tag at a fixed commit, so what it publishes never changes while the page is open.
  const index = useQuery({
    queryKey: ["delivery", "osdu-definitions", "schemas", release],
    queryFn: () => deliveryApi.osduSchemas(release!),
    enabled: release !== null,
    staleTime: Infinity,
  });

  const file = useQuery({
    queryKey: ["delivery", "osdu-definitions", "schema", viewing?.release ?? null, viewing?.kind ?? null],
    queryFn: () => deliveryApi.osduSchema(viewing!.release, viewing!.kind),
    enabled: viewing !== null,
    staleTime: Infinity,
  });

  // Under the templates key, so a save refreshes it and the sheet shows the version as saved.
  const preview = useQuery({
    queryKey: ["delivery", "templates", "preview", "osdu", file.data?.release.commit ?? null, file.data?.kind ?? null],
    queryFn: () => deliveryApi.previewTemplate(file.data!.kind, file.data!.schema),
    enabled: viewing !== null && file.data !== undefined,
  });

  const save = useMutation({
    mutationFn: () => deliveryApi.saveTemplate(file.data!.kind, file.data!.schema, file.data!.origin),
    onSuccess: (saved) => {
      toast.success(saved.outcome === "created"
        ? `Saved template ${saved.template.kind} version ${saved.template.version}.`
        : `Template ${saved.template.kind} version ${saved.template.version} was already saved, so nothing changed.`);
      void queryClient.invalidateQueries({ queryKey: ["delivery", "templates"] });
    },
    onError: (error) => toast.error(problemText(error)),
  });

  const counts = useMemo(() => {
    const byType = new Map<string, number>();
    for (const schema of index.data?.schemas ?? []) {
      byType.set(schema.entityType, (byType.get(schema.entityType) ?? 0) + 1);
    }

    return byType;
  }, [index.data]);

  // The index lists each entity type newest version first, so the first of a type is its newest.
  const rows = useMemo((): BrowseRow[] | undefined => {
    if (index.data === undefined) {
      return undefined;
    }

    const terms = term.trim().toLowerCase().split(/\s+/).filter((part) => part !== "");
    const shown = new Set<string>();
    const result: BrowseRow[] = [];
    for (const schema of index.data.schemas) {
      if (!allVersions) {
        if (shown.has(schema.entityType)) {
          continue;
        }

        shown.add(schema.entityType);
      }

      const kind = schema.kind.toLowerCase();
      if (terms.every((part) => kind.includes(part))) {
        result.push({ schema, versions: counts.get(schema.entityType) ?? 1 });
      }
    }

    return result;
  }, [index.data, term, allVersions, counts]);

  const view = (kind: string) => {
    if (index.data === undefined) {
      return;
    }

    save.reset();
    setViewing({ release: index.data.release.name, kind });
  };

  const columns: Column<BrowseRow>[] = [
    { id: "kind", header: "Kind", render: (row) => <TruncatedText text={row.schema.kind} mono maxWidth={460} /> },
    { id: "status", header: "Status", render: (row) => <StatusCell schema={row.schema} /> },
    ...(allVersions ? [] : [{
      id: "versions",
      header: "Versions",
      align: "right" as const,
      render: (row: BrowseRow) => (
        <span className="font-mono tabular-nums text-muted-foreground">{row.versions} version{row.versions === 1 ? "" : "s"}</span>
      ),
    }]),
    {
      id: "actions",
      header: "",
      align: "right",
      render: (row) => (
        <span className="inline-flex items-center gap-1">
          <LinkRef
            url={row.schema.webUrl}
            title="In the OSDU data definitions"
            testId={`templates-browse-link-${row.schema.kind}`}
            copyTestId={`templates-browse-copy-${row.schema.kind}`}
          />
          <Button
            variant="outline"
            size="xs"
            onClick={(event) => { event.stopPropagation(); view(row.schema.kind); }}
            data-testid={`templates-browse-view-${row.schema.kind}`}
          >
            <Eye />
            View
          </Button>
        </span>
      ),
    },
  ];

  // What the sheet shows while the schema is on its way, and what went wrong when it does not arrive.
  let progress: ReactNode = null;
  let problem: ReactNode = null;
  if (viewing !== null) {
    if (file.isError) {
      problem = <ProblemView error={file.error} testId="templates-browse-fetch-error" />;
    } else if (file.data === undefined) {
      progress = (
        <TaskProgress
          label={`Reading ${viewing.kind} and every schema it refers to from release ${viewing.release}`}
          testId="templates-browse-fetch-progress"
        />
      );
    } else if (preview.isError) {
      problem = <ProblemView error={preview.error} testId="templates-browse-preview-error" />;
    } else if (preview.data === undefined) {
      progress = <TaskProgress label="Laying the schema out as a template" testId="templates-browse-preview-progress" />;
    } else if (save.isError) {
      problem = <ProblemView error={save.error} testId="templates-browse-save-error" />;
    }
  }

  const shownRows = rows?.slice(0, MAX_ROWS);
  const typeCount = counts.size;
  const schemaCount = index.data?.schemas.length ?? 0;
  const listed = index.data?.release;

  return (
    <div className="flex flex-col gap-3" data-testid="templates-browse">
      <Card className="gap-2 rounded-lg p-3" data-testid="templates-browse-form">
        <FilterBar>
          <div className="flex items-center gap-2">
            <Label htmlFor="templates-browse-release" className="text-[13px] font-normal text-muted-foreground">Release</Label>
            <Select value={release ?? ""} onValueChange={setPickedRelease} disabled={releases.data === undefined}>
              <SelectTrigger id="templates-browse-release" size="sm" className="h-8 w-40" data-testid="templates-browse-release">
                <SelectValue placeholder={releases.isError ? "Unavailable" : "Loading releases"} />
              </SelectTrigger>
              <SelectContent>
                {(releases.data ?? []).map((candidate, i) => (
                  <SelectItem key={candidate.name} value={candidate.name}>
                    <span className="font-mono">{candidate.name}</span>
                    {i === 0 && <span className="text-[11px] text-muted-foreground">newest</span>}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          <SearchInput
            value={search}
            onChange={setSearch}
            placeholder="Wellbore, WellLog, reference-data..."
            label="Search the record kinds of the release"
            className="sm:w-80"
            testId="templates-browse-search"
          />
          <Label className="flex items-center gap-2 text-[13px] font-normal">
            <Switch checked={allVersions} onCheckedChange={setAllVersions} data-testid="templates-browse-all-versions" />
            Every version
          </Label>
        </FilterBar>
        <p className="flex flex-wrap items-center gap-x-1.5 text-xs text-muted-foreground" data-testid="templates-browse-summary">
          {listed === undefined
            ? "The OSDU data definitions: the Open Group's public repository of OSDU schemas."
            : (
              <>
                <span>
                  <span className="font-mono tabular-nums text-foreground">{typeCount.toLocaleString()}</span>
                  {` record type${typeCount === 1 ? "" : "s"} and `}
                  <span className="font-mono tabular-nums text-foreground">{schemaCount.toLocaleString()}</span>
                  {` schema${schemaCount === 1 ? "" : "s"} in the OSDU data definitions `}
                  <span className="font-mono text-foreground">{listed.name}</span>
                  {listed.publishedUtc !== null && <>, released <RelativeTime value={listed.publishedUtc} /></>}
                  .
                </span>
                <RepositoryLink href={listed.webUrl} testId="templates-browse-repository-link">Open in the repository</RepositoryLink>
              </>
            )}
        </p>
      </Card>

      {releases.isError && <ProblemView error={releases.error} testId="templates-browse-error" />}
      {index.isError && <ProblemView error={index.error} testId="templates-browse-error" />}
      {!releases.isError && !index.isError && rows === undefined && <Skeleton className="h-64 w-full rounded-lg" />}

      {shownRows !== undefined && (
        <DataTable
          columns={columns}
          rows={shownRows}
          rowKey={(row) => row.schema.kind}
          onRowClick={(row) => view(row.schema.kind)}
          emptyMessage={`No record kind in ${listed?.name ?? "the release"} matches the search.`}
          footer={rows !== undefined && rows.length > MAX_ROWS ? (
            <div className="border-t border-border px-3 py-1.5 text-xs text-muted-foreground" data-testid="templates-browse-truncated">
              {`Showing ${MAX_ROWS.toLocaleString()} of ${rows.length.toLocaleString()}. Narrow the search to see the rest.`}
            </div>
          ) : undefined}
          minWidth={900}
          data-testid="templates-browse-results"
        />
      )}

      <TemplateSheet
        open={viewing !== null}
        onClose={() => setViewing(null)}
        title={viewing?.kind ?? "Schema"}
        description={file.data !== undefined
          ? `From the OSDU data definitions ${file.data.release.name} (commit ${file.data.release.commit.slice(0, 12)}), Generated/${file.data.path}. Nothing is saved until you save it.`
          : "Read from the OSDU data definitions. Nothing is saved until you save it."}
        progress={progress}
        problem={problem}
        detail={file.data !== undefined ? preview.data : undefined}
        previewSchema={file.data?.schema}
        actions={file.data !== undefined && preview.data !== undefined ? (
          <span className="inline-flex items-center gap-3">
            <RepositoryLink href={file.data.webUrl} testId="templates-browse-source-link">View the source</RepositoryLink>
            {canAuthor && (
              <Button size="sm" onClick={() => save.mutate()} disabled={save.isPending} data-testid="templates-browse-save">
                {save.isPending ? <Loader2 className="animate-spin" /> : <Save />}
                Save template
              </Button>
            )}
          </span>
        ) : undefined}
        busy={save.isPending}
        testId="templates-browse-sheet"
      />
    </div>
  );
}
