import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { deliveryApi, type DeliveryCacheDefinition, type DeliveryCachedItem } from "../../api/delivery";
import { repoApi } from "../../api/endpoints";
import { CodeView } from "../../components/CodeView";
import { DataTable, type Column } from "../../components/DataTable";
import { FilterBar } from "../../components/FilterBar";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { PagedTable } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { TruncatedText } from "../../components/TruncatedText";

const ALL = "all";

/** A cached value on one line: a scalar as itself, a set as its values, an object as its JSON. */
function cachedText(value: unknown): string {
  if (value === null || value === undefined) {
    return "-";
  }

  if (Array.isArray(value)) {
    return value.map((v) => cachedText(v)).join(", ");
  }

  return typeof value === "object" ? JSON.stringify(value) : String(value);
}

const definitionColumns: Column<DeliveryCacheDefinition>[] = [
  { id: "name", header: "Cached type", render: (row) => <span className="font-mono text-[12px] font-medium">{row.name}</span> },
  { id: "entityType", header: "OSDU entity", render: (row) => <TruncatedText text={row.entityType} mono maxWidth={260} /> },
  {
    id: "fields",
    header: "Cached paths",
    render: (row) => (
      <div className="flex flex-wrap gap-1">
        {row.fields.map((field) => (
          <Badge key={field.as} variant="outline" className="font-mono text-[11px]" title={field.path}>
            {field.as}
          </Badge>
        ))}
      </div>
    ),
  },
  { id: "items", header: "Records", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.items.toLocaleString()}</span> },
  { id: "flow", header: "Maintained by", render: (row) => <TruncatedText text={row.flowName} mono maxWidth={200} /> },
  { id: "kind", header: "Search kind", render: (row) => <TruncatedText text={row.kind} mono maxWidth={280} /> },
  {
    id: "captured",
    header: "Snapshot",
    render: (row) => (row.version
      ? <span className="flex flex-col"><span className="font-mono text-[12px]">{row.version}</span><RelativeTime value={row.capturedUtc} /></span>
      : <span className="text-muted-foreground">not captured yet</span>),
  },
];

/**
 * The OSDU cache: the reference and master data the mappings resolve against. Read-only on purpose. A cache is
 * declared in the retrieval flow that syncs it (its cache.types section), so this page shows what the last
 * repository sync read there, what the current snapshot actually holds, and lets an operator search the cached
 * values by any of them.
 */
export default function DeliveryCachePage() {
  const [repoFilter, setRepoFilter] = useLocalStorageState("sqlflow.filters.delivery-cache.repo", ALL);
  const [typeFilter, setTypeFilter] = useLocalStorageState("sqlflow.filters.delivery-cache.type", ALL);
  const [search, setSearch] = useState("");
  const [selected, setSelected] = useState<DeliveryCachedItem | null>(null);

  const repos = useQuery({ queryKey: ["repos", "all-for-delivery-cache"], queryFn: () => repoApi.list({ page: 1, pageSize: 200 }) });
  const repoId = repoFilter === ALL ? undefined : repoFilter;
  const definitions = useQuery({ queryKey: ["delivery", "cache", repoId], queryFn: () => deliveryApi.cache(repoId) });
  const type = typeFilter === ALL ? undefined : typeFilter;
  const types = [...new Set((definitions.data ?? []).map((d) => d.name))].sort((a, b) => a.localeCompare(b));

  return (
    <Page data-testid="page-delivery-cache">
      <PageHeader
        title="OSDU cache"
        subtitle="The reference and master data the mappings resolve against. Each cached type is declared in the retrieval flow that keeps it current; this is what the last sync read and what the current snapshot holds."
      />
      <FilterBar>
        <Select value={repoFilter} onValueChange={setRepoFilter}>
          <SelectTrigger size="sm" className="h-8 w-56" data-testid="delivery-cache-repo"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>All repos</SelectItem>
            {(repos.data?.items ?? []).map((repo) => <SelectItem key={repo.id} value={repo.id}>{repo.name}</SelectItem>)}
          </SelectContent>
        </Select>
        <Select value={typeFilter} onValueChange={setTypeFilter}>
          <SelectTrigger size="sm" className="h-8 w-56" data-testid="delivery-cache-type"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>All cached types</SelectItem>
            {types.map((name) => <SelectItem key={name} value={name}>{name}</SelectItem>)}
          </SelectContent>
        </Select>
        <Input
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder="Search cached values, ids and aliases"
          className="h-8 w-72"
          data-testid="delivery-cache-search"
        />
      </FilterBar>

      <section className="flex flex-col gap-2">
        <h2 className="text-[13px] font-medium text-muted-foreground">What is cached</h2>
        <DataTable
          columns={definitionColumns}
          rows={definitions.data}
          rowKey={(row) => row.id}
          onRowClick={(row) => setTypeFilter(row.name)}
          emptyMessage="No cache declared yet. A retrieval flow declares one with a cache.types section naming the OSDU types and the paths to cache."
          data-testid="delivery-cache-definitions-table"
        />
      </section>

      <section className="flex flex-col gap-2">
        <h2 className="text-[13px] font-medium text-muted-foreground">Cached records</h2>
        <PagedTable
          queryKey={["delivery", "cache", "items", repoId, type, search]}
          fetchPage={(page, pageSize) => deliveryApi.cachedItems({ page, pageSize, repoId, type, search: search || undefined })}
          columns={[
            { id: "type", header: "Type", render: (row) => <Badge variant="outline">{row.typeName}</Badge> },
            { id: "recordId", header: "OSDU id", render: (row) => <TruncatedText text={row.recordId} mono maxWidth={420} /> },
            {
              id: "values",
              header: "Cached values",
              render: (row) => (
                <span className="font-mono text-[12px]">
                  {Object.entries(row.fields).map(([name, value]) => `${name}: ${cachedText(value)}`).join("  ·  ") || "-"}
                </span>
              ),
            },
          ]}
          rowKey={(row) => row.itemId}
          onRowClick={(row) => setSelected(row)}
          emptyMessage="No cached records match. A cache is filled by running the retrieval flow that declares it, then syncing the repository."
          data-testid="delivery-cache-items-table"
        />
      </section>

      <Sheet open={selected !== null} onOpenChange={(open) => { if (!open) { setSelected(null); } }}>
        <SheetContent className="w-full gap-0 sm:max-w-2xl" data-testid="delivery-cache-item-detail">
          <SheetHeader>
            <SheetTitle>{selected?.typeName ?? "Cached record"}</SheetTitle>
            <SheetDescription>{selected ? `${selected.entityType} · ${selected.recordId}` : "Loading."}</SheetDescription>
          </SheetHeader>
          {selected && (
            <div className="flex flex-1 flex-col gap-3 overflow-y-auto px-4 pb-4">
              <CodeView value={JSON.stringify(selected.fields, null, 2)} language="json" height={480} data-testid="delivery-cache-item-json" />
            </div>
          )}
        </SheetContent>
      </Sheet>
    </Page>
  );
}
