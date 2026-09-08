import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Badge } from "@/components/ui/badge";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { deliveryApi, type DeliveryMapping, type DeliverySnapshot } from "../../api/delivery";
import { repoApi } from "../../api/endpoints";
import { CodeView } from "../../components/CodeView";
import { DataTable, type Column } from "../../components/DataTable";
import { FilterBar } from "../../components/FilterBar";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { RelativeTime } from "../../components/RelativeTime";
import { TruncatedText } from "../../components/TruncatedText";

const ALL = "all";

function summaryText(summary: Record<string, unknown>, key: string): string {
  const value = summary[key];
  if (value === undefined || value === null) {
    return "-";
  }

  return Array.isArray(value) ? value.join(", ") : String(value);
}

const mappingColumns: Column<DeliveryMapping>[] = [
  { id: "reference", header: "Mapping", render: (row) => <span className="font-mono text-[12px] font-medium">{row.reference}</span> },
  { id: "kind", header: "OSDU kind", render: (row) => <TruncatedText text={row.kind} mono maxWidth={320} /> },
  {
    id: "status",
    header: "Status",
    render: (row) => (
      <Badge variant="secondary" className={row.status === "valid" ? "bg-success/15 text-success" : "bg-destructive/15 text-destructive"}>
        {row.status}
      </Badge>
    ),
  },
  { id: "system", header: "Source system", render: (row) => summaryText(row.summary, "system") },
  { id: "key", header: "Natural key", render: (row) => <span className="font-mono text-[12px]">{summaryText(row.summary, "naturalKey")}</span> },
  { id: "properties", header: "Properties", align: "right", render: (row) => <span className="font-mono tabular-nums">{summaryText(row.summary, "properties")}</span> },
  { id: "fixtures", header: "Fixtures", align: "right", render: (row) => <span className="font-mono tabular-nums">{summaryText(row.summary, "fixtures")}</span> },
  { id: "path", header: "Path", render: (row) => <TruncatedText text={row.relativePath} mono maxWidth={260} /> },
  { id: "seen", header: "Last seen", render: (row) => <RelativeTime value={row.lastSeenUtc} /> },
];

const snapshotColumns: Column<DeliverySnapshot>[] = [
  { id: "kind", header: "Kind", render: (row) => <Badge variant="outline">{row.kind}</Badge> },
  { id: "name", header: "Name", render: (row) => <TruncatedText text={row.name} mono maxWidth={360} /> },
  { id: "version", header: "Version", render: (row) => <span className="font-mono text-[12px]">{row.version}</span> },
  {
    id: "current",
    header: "",
    render: (row) => (row.current ? <Badge variant="secondary" className="bg-success/15 text-success">current</Badge> : null),
  },
  { id: "captured", header: "Captured", render: (row) => <RelativeTime value={row.capturedUtc} absolute /> },
  {
    id: "summary",
    header: "Contents",
    render: (row) => (row.kind === "references"
      ? `${summaryText(row.summary, "items")} item(s) in ${Array.isArray(row.summary.types) ? row.summary.types.length : 0} type(s)`
      : `${summaryText(row.summary, "dataProperties")} data properties`),
  },
  { id: "path", header: "Path", render: (row) => <TruncatedText text={row.relativePath} mono maxWidth={300} /> },
];

/** The mappings and snapshots the synced repositories hold: what every delivery flow renders with. */
export default function DeliveryDocumentsPage() {
  const [repoFilter, setRepoFilter] = useLocalStorageState("sqlflow.filters.delivery-documents.repo", ALL);
  const [selected, setSelected] = useState<string | null>(null);

  const repos = useQuery({ queryKey: ["repos", "all-for-delivery-documents"], queryFn: () => repoApi.list({ page: 1, pageSize: 200 }) });
  const repoId = repoFilter === ALL ? undefined : repoFilter;
  const mappings = useQuery({ queryKey: ["delivery", "mappings", repoId], queryFn: () => deliveryApi.mappings(repoId) });
  const snapshots = useQuery({ queryKey: ["delivery", "snapshots", repoId], queryFn: () => deliveryApi.snapshots(repoId) });
  const mapping = useQuery({
    queryKey: ["delivery", "mapping", selected],
    queryFn: () => deliveryApi.mapping(selected!),
    enabled: selected !== null,
  });

  return (
    <Page data-testid="page-delivery-documents">
      <PageHeader
        title="Mappings and snapshots"
        subtitle="The pinned mapping documents and the schema and reference snapshots the repositories hold, as the last sync found them."
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
      <Tabs defaultValue="mappings">
        <TabsList data-testid="delivery-documents-tabs">
          <TabsTrigger value="mappings" data-testid="delivery-documents-tab-mappings">Mappings</TabsTrigger>
          <TabsTrigger value="snapshots" data-testid="delivery-documents-tab-snapshots">Snapshots</TabsTrigger>
        </TabsList>
        <TabsContent value="mappings">
          <DataTable
            columns={mappingColumns}
            rows={mappings.data}
            rowKey={(row) => row.id}
            onRowClick={(row) => setSelected(row.id)}
            emptyMessage="No mapping documents synced yet. A mapping is a YAML file with documentType: mapping under a repository's mappings directory."
            data-testid="delivery-mappings-table"
          />
        </TabsContent>
        <TabsContent value="snapshots">
          <DataTable
            columns={snapshotColumns}
            rows={snapshots.data}
            rowKey={(row) => row.id}
            emptyMessage="No snapshots synced yet. Capture them with sqlflow snapshot and commit the snapshots directory."
            data-testid="delivery-snapshots-table"
          />
        </TabsContent>
      </Tabs>

      <Sheet open={selected !== null} onOpenChange={(open) => { if (!open) { setSelected(null); } }}>
        <SheetContent className="w-full gap-0 sm:max-w-3xl" data-testid="delivery-mapping-detail">
          <SheetHeader>
            <SheetTitle>{mapping.data?.mapping.reference ?? "Mapping"}</SheetTitle>
            <SheetDescription>
              {mapping.data ? `${mapping.data.mapping.kind} from ${mapping.data.mapping.relativePath}` : "Loading."}
            </SheetDescription>
          </SheetHeader>
          {mapping.data && (
            <div className="flex flex-1 flex-col gap-3 overflow-y-auto px-4 pb-4">
              {mapping.data.mapping.message && <p className="text-[13px] text-destructive">{mapping.data.mapping.message}</p>}
              <CodeView value={mapping.data.yaml} language="yaml" height={560} data-testid="delivery-mapping-yaml" />
            </div>
          )}
        </SheetContent>
      </Sheet>
    </Page>
  );
}
