import { useState } from "react";
import { Link as RouterLink } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { FileCode, LayoutTemplate, PencilRuler, Plus } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { isApiError } from "@/api/client";
import { deliveryApi, type DeliveryMapping } from "../../api/delivery";
import { repoApi } from "@/api/endpoints";
import { CodeView } from "@/components/CodeView";
import { CorrelationError } from "@/components/CorrelationError";
import { DataTable, type Column } from "@/components/DataTable";
import { FilterBar } from "@/components/FilterBar";
import { GlyphRef } from "@/components/GlyphRef";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import { KindText } from "./KindText";
import { MappingPropertiesView } from "./MappingPropertiesView";
import { MappingShapeView } from "./MappingShapeView";

const ALL = "all";

function summaryText(summary: Record<string, unknown>, key: string): string {
  const value = summary[key];
  if (value === undefined || value === null) {
    return "-";
  }

  return Array.isArray(value) ? value.join(", ") : String(value);
}

/** A failed read, the one way API failures render. */
function LoadError({ error, testId }: { error: unknown; testId: string }) {
  return isApiError(error)
    ? <CorrelationError error={error} data-testid={testId} />
    : <p className="text-[13px] text-destructive" data-testid={testId}>{String(error)}</p>;
}

const mappingColumns: Column<DeliveryMapping>[] = [
  {
    id: "reference",
    header: "Mapping",
    // Whether the document is valid is a fact about the mapping, so it reads under its name.
    render: (row) => (
      <div className="flex flex-col items-start gap-0.5">
        <span className="font-mono text-[12px] font-medium">{row.reference}</span>
        <Badge variant="secondary" className={row.status === "valid" ? "bg-success/15 text-success" : "bg-destructive/15 text-destructive"}>
          {row.status}
        </Badge>
      </div>
    ),
  },
  {
    id: "template",
    header: "Template",
    fill: true,
    floor: 130,
    // The pinned version reads under the kind, so the kind keeps the column's whole width.
    render: (row) => (
      <div className="flex min-w-0 flex-col">
        <KindText kind={row.kind} />
        <span className="font-mono text-[11px] text-muted-foreground">{summaryText(row.summary, "templateVersion")}</span>
      </div>
    ),
  },
  {
    id: "source",
    header: "Source",
    // The dataset key qualifies the source system, so it reads under it.
    render: (row) => (
      <div className="flex flex-col">
        <TruncatedText text={summaryText(row.summary, "system")} maxWidth={140} title="Source system" />
        <TruncatedText text={summaryText(row.summary, "key")} mono maxWidth={140} title="Dataset key" className="text-[11px] text-muted-foreground" />
      </div>
    ),
  },
  {
    id: "entries",
    header: "Entries",
    align: "right",
    render: (row) => {
      const fixtures = summaryText(row.summary, "fixtures");
      return (
        <span className="font-mono tabular-nums">
          {summaryText(row.summary, "entries")}
          {fixtures !== "-" && <span className="text-muted-foreground @max-3xl/table:sr-only">{` + ${fixtures} fixtures`}</span>}
        </span>
      );
    },
  },
  { id: "path", header: "Path", render: (row) => <GlyphRef icon={FileCode} title="Path" body={row.relativePath} mono /> },
  { id: "seen", header: "Last seen", render: (row) => <RelativeTime value={row.lastSeenUtc} /> },
];

/** The mapping documents the synced repositories hold: what every delivery flow renders with. The caches they read are on the OSDU cache page. */
export default function DeliveryDocumentsPage() {
  const [repoFilter, setRepoFilter] = useLocalStorageState("sqlflow.filters.delivery-documents.repo", ALL);
  const [selected, setSelected] = useState<string | null>(null);

  const repos = useQuery({ queryKey: ["repos", "all-for-delivery-documents"], queryFn: () => repoApi.list({ page: 1, pageSize: 200 }) });
  const repoId = repoFilter === ALL ? undefined : repoFilter;
  const mappings = useQuery({ queryKey: ["delivery", "mappings", repoId], queryFn: () => deliveryApi.mappings(repoId) });
  const mapping = useQuery({
    queryKey: ["delivery", "mapping", selected],
    queryFn: () => deliveryApi.mapping(selected!),
    enabled: selected !== null,
  });

  const detail = mapping.data;
  const templateVersion = detail !== undefined ? summaryText(detail.mapping.summary, "templateVersion") : "-";

  return (
    <Page data-testid="page-delivery-documents">
      <PageHeader
        title="Mappings"
        subtitle="The mapping documents the repositories hold, as the last sync found them. The caches mappings read are on the OSDU cache page."
        actions={(
          <Button asChild size="sm" data-testid="delivery-documents-new-mapping">
            <RouterLink to="/delivery/mappings/build">
              <Plus />
              New mapping
            </RouterLink>
          </Button>
        )}
      />
      <FilterBar>
        <Select value={repoFilter} onValueChange={setRepoFilter}>
          <SelectTrigger size="sm" className="h-8 w-56" data-testid="delivery-documents-repo"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>All repos</SelectItem>
            {(repos.data?.items ?? []).map((repo) => <SelectItem key={repo.id} value={repo.id}>{repo.name}</SelectItem>)}
          </SelectContent>
        </Select>
      </FilterBar>
      <div className="flex flex-col gap-3">
        {mappings.isError && <LoadError error={mappings.error} testId="delivery-mappings-error" />}
        <DataTable
          columns={mappingColumns}
          rows={mappings.data}
          rowKey={(row) => row.id}
          onRowClick={(row) => setSelected(row.id)}
          emptyMessage="No mapping documents synced yet. A mapping is a YAML file with documentType: mapping under a repository's mappings directory, and the Mapping builder writes one."
          data-testid="delivery-mappings-table"
        />
      </div>

      <Sheet open={selected !== null} onOpenChange={(open) => { if (!open) { setSelected(null); } }}>
        {/*
          * As wide as the window leaves once the workbench's menu is kept clear: the 48px activity bar, and the side
          * bar at the widest it resizes to (420px). It never reaches the menu whatever width the side bar is dragged
          * to, and never falls below 48rem, so a window too narrow to hold both still opens a sheet worth reading.
          */}
        <SheetContent
          className="w-full gap-0 sm:max-w-[max(48rem,calc(100vw_-_468px))]"
          data-testid="delivery-mapping-detail"
        >
          <SheetHeader>
            <SheetTitle>{detail?.mapping.reference ?? "Mapping"}</SheetTitle>
            <SheetDescription>
              {detail !== undefined ? `${detail.mapping.kind} from ${detail.mapping.relativePath}` : "Loading."}
            </SheetDescription>
          </SheetHeader>
          <div className="flex flex-1 flex-col gap-3 overflow-y-auto px-4 pb-4">
            {mapping.isError && <LoadError error={mapping.error} testId="delivery-mapping-error" />}
            {detail !== undefined && (
              <>
                <div className="flex flex-wrap items-center gap-2">
                  <Button asChild size="sm" variant="outline" data-testid="delivery-mapping-open-builder">
                    <RouterLink to={`/delivery/mappings/build?mappingId=${encodeURIComponent(detail.mapping.id)}`}>
                      <PencilRuler />
                      Open in builder
                    </RouterLink>
                  </Button>
                  {templateVersion !== "-" && (
                    <Button asChild size="sm" variant="ghost" data-testid="delivery-mapping-template">
                      <RouterLink
                        to={`/delivery/templates?${new URLSearchParams({ kind: detail.mapping.kind, version: templateVersion }).toString()}`}
                      >
                        <LayoutTemplate />
                        <span className="font-mono text-[12px]">{templateVersion}</span>
                      </RouterLink>
                    </Button>
                  )}
                </div>
                {detail.mapping.message && <p className="text-[13px] text-destructive">{detail.mapping.message}</p>}
                <Tabs defaultValue="properties" className="min-h-0 flex-1">
                  <TabsList data-testid="delivery-mapping-tabs">
                    <TabsTrigger value="properties" data-testid="delivery-mapping-tab-properties">Properties</TabsTrigger>
                    <TabsTrigger value="yaml" data-testid="delivery-mapping-tab-yaml">YAML</TabsTrigger>
                    <TabsTrigger value="shape" data-testid="delivery-mapping-tab-shape">Record shape</TabsTrigger>
                  </TabsList>
                  <TabsContent value="properties" className="flex min-h-0 flex-col">
                    <MappingPropertiesView
                      yaml={detail.yaml}
                      path={detail.mapping.relativePath}
                      contentHash={detail.mapping.contentHash}
                    />
                  </TabsContent>
                  <TabsContent value="yaml" className="flex min-h-0 flex-col">
                    <CodeView value={detail.yaml} language="yaml" lsp={false} fill data-testid="delivery-mapping-yaml" />
                  </TabsContent>
                  <TabsContent value="shape" className="flex min-h-0 flex-col">
                    <MappingShapeView yaml={detail.yaml} path={detail.mapping.relativePath} contentHash={detail.mapping.contentHash} />
                  </TabsContent>
                </Tabs>
              </>
            )}
          </div>
        </SheetContent>
      </Sheet>
    </Page>
  );
}
