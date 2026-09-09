import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Check, Database, History, X } from "lucide-react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { cn } from "@/lib/utils";
import {
  deliveryApi, type DeliveryCacheDefinition, type DeliveryCachedItem, type DeliveryCacheVersion, type DeliveryUpdateTag,
} from "../../api/delivery";
import { repoApi } from "../../api/endpoints";
import { CodeView } from "../../components/CodeView";
import { FilterBar } from "../../components/FilterBar";
import { KpiCard } from "../../components/KpiCard";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { PagedTable } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { SearchInput } from "../../components/SearchInput";
import { TruncatedText } from "../../components/TruncatedText";

const ALL = "all";

/** The picker's value for "whichever version is current", which is what the page opens on. */
const CURRENT = "current";

/** A version as the picker labels it: the version, when it was captured, and whether it is the pinned one. */
function versionLabel(version: DeliveryCacheVersion): string {
  const captured = version.capturedUtc ? new Date(version.capturedUtc).toLocaleString() : "never captured";
  return `${version.version} · ${captured}${version.current ? " · current" : ""}`;
}

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

/** One cached type: what it holds, where it comes from, and what a change to it does. */
function CachedTypeCard({ definition, selected, onSelect }: {
  definition: DeliveryCacheDefinition;
  selected: boolean;
  onSelect: () => void;
}) {
  const auto = definition.makeCurrent;
  return (
    <Card
      onClick={onSelect}
      className={cn(
        "cursor-pointer gap-2 rounded-lg p-3 transition-colors hover:border-primary/40",
        selected && "border-primary bg-primary/5",
      )}
      data-testid="delivery-cache-type-card"
    >
      <div className="flex items-baseline justify-between gap-2">
        <span className="font-mono text-[13px] font-medium">{definition.name}</span>
        <span className="font-mono text-[13px] tabular-nums text-muted-foreground">{definition.items.toLocaleString()}</span>
      </div>
      <TruncatedText text={definition.entityType} mono maxWidth={240} />
      <div className="flex flex-wrap gap-1">
        {definition.fields.map((field) => (
          <Badge key={field.as} variant="outline" className="font-mono text-[10px]" title={field.path}>
            {field.as}
          </Badge>
        ))}
      </div>
      <div className="flex items-center gap-2 text-[11px] text-muted-foreground">
        <span>{definition.flowName}</span>
        {!auto && <Badge variant="secondary" className="text-[10px]">not current</Badge>}
      </div>
    </Card>
  );
}

/**
 * The OSDU cache: the reference and master data every delivered document is built from. Read-only over what the
 * repositories declare, because a cache is defined in the retrieval flow that keeps it current, with one thing an
 * operator does decide here: whether a changed cached value goes out to OSDU, for the types whose changes wait for
 * approval.
 */
export default function DeliveryCachePage() {
  const queryClient = useQueryClient();
  const [repoFilter, setRepoFilter] = useLocalStorageState("sqlflow.filters.delivery-cache.repo", ALL);
  const [type, setType] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  // The picker holds a version label, not a snapshot id: a label identifies the same capture across repositories,
  // and CURRENT follows the pinned version rather than freezing on whichever one happens to be pinned right now.
  const [versionFilter, setVersionFilter] = useState<string>(CURRENT);
  const [tagStatus, setTagStatus] = useLocalStorageState("sqlflow.filters.delivery-cache.tag-status", "pending");
  const [selectedTags, setSelectedTags] = useState<ReadonlySet<string>>(new Set());
  const [item, setItem] = useState<DeliveryCachedItem | null>(null);

  const repos = useQuery({ queryKey: ["repos", "all-for-delivery-cache"], queryFn: () => repoApi.list({ page: 1, pageSize: 200 }) });
  const repoId = repoFilter === ALL ? undefined : repoFilter;
  const versions = useQuery({
    queryKey: ["delivery", "cache", "versions", repoId],
    queryFn: () => deliveryApi.cacheVersions(repoId),
  });
  const version = versionFilter === CURRENT ? undefined : versionFilter;
  const definitions = useQuery({
    queryKey: ["delivery", "cache", repoId, version],
    queryFn: () => deliveryApi.cache(repoId, undefined, version),
  });
  const pending = useQuery({
    queryKey: ["delivery", "cache", "tags", "pending-count"],
    queryFn: () => deliveryApi.updateTags({ page: 1, pageSize: 100, status: "pending" }),
  });
  const pendingRecords = (pending.data?.items ?? []).reduce((sum, tag) => sum + tag.affectedRecords, 0);

  // Narrowing the repo, or a version leaving the store, can strand the picker on a version that no longer exists,
  // which would read as an empty cache rather than as a stale selection. Fall back to the current version.
  const known = versions.data;
  if (versionFilter !== CURRENT && known !== undefined && !known.some((v) => v.version === versionFilter)) {
    setVersionFilter(CURRENT);
  }

  const selected = known?.find((v) => (versionFilter === CURRENT ? v.current : v.version === versionFilter));
  const historic = selected !== undefined && !selected.current;

  const rows = definitions.data ?? [];
  const snapshot = rows.find((d) => d.version)?.version ?? null;
  const capturedUtc = rows.find((d) => d.capturedUtc)?.capturedUtc ?? null;
  const totals = useMemo(() => ({
    types: rows.length,
    records: rows.reduce((sum, d) => sum + d.items, 0),
  }), [rows]);

  const decide = useMutation({
    mutationFn: ({ ids, approve }: { ids: number[]; approve: boolean }) => deliveryApi.decideTags(ids, approve),
    onSuccess: (result) => {
      toast.success(result.approved
        ? `${result.decided} change(s) approved; the rollout carries the records in batches.`
        : `${result.decided} change(s) rejected; OSDU keeps what it holds.`);
      setSelectedTags(new Set());
      void queryClient.invalidateQueries({ queryKey: ["delivery", "cache"] });
    },
    onError: () => toast.error("The decision could not be recorded."),
  });

  const decideSelected = (approve: boolean) =>
    decide.mutate({ ids: [...selectedTags].map(Number), approve });

  return (
    <Page data-testid="page-delivery-cache">
      <PageHeader
        title="OSDU cache"
        subtitle="The reference and master data every delivered document is built from. Each type is declared in the retrieval flow that keeps it current; when a cached value changes, the records built from it are tagged here."
      />

      <div className="grid grid-cols-2 gap-3 md:grid-cols-4" data-testid="delivery-cache-kpis">
        <KpiCard label="Cached types" value={totals.types} testId="cache-kpi-types" />
        <KpiCard label="Cached records" value={totals.records.toLocaleString()} testId="cache-kpi-records" />
        <KpiCard
          label="Snapshot"
          value={snapshot ?? "none"}
          color={historic ? "warning" : undefined}
          caption={historic
            ? "an earlier version, not the one deliveries resolve against"
            : capturedUtc ? `captured ${new Date(capturedUtc).toLocaleString()}` : "never captured"}
          testId="cache-kpi-snapshot"
        />
        <KpiCard
          label="Awaiting approval"
          value={pending.data?.total ?? 0}
          color={(pending.data?.total ?? 0) > 0 ? "warning" : undefined}
          caption={pendingRecords > 0 ? `${pendingRecords.toLocaleString()} record(s) held back` : "no changes waiting"}
          testId="cache-kpi-pending"
        />
      </div>

      <FilterBar>
        <Select value={repoFilter} onValueChange={setRepoFilter}>
          <SelectTrigger size="sm" className="h-8 w-56" data-testid="delivery-cache-repo"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>All repos</SelectItem>
            {(repos.data?.items ?? []).map((repo) => <SelectItem key={repo.id} value={repo.id}>{repo.name}</SelectItem>)}
          </SelectContent>
        </Select>
        <Select value={versionFilter} onValueChange={setVersionFilter} disabled={(versions.data?.length ?? 0) === 0}>
          <SelectTrigger size="sm" className="h-8 w-[26rem]" data-testid="delivery-cache-version">
            <History className="size-3.5 shrink-0 text-muted-foreground" />
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={CURRENT}>Current version</SelectItem>
            {(versions.data ?? []).map((v) => (
              <SelectItem key={`${v.repoId}:${v.version}`} value={v.version} disabled={!v.carried}>
                {versionLabel(v)}{v.carried ? "" : " · not carried"}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        {type && (
          <Button variant="ghost" size="sm" className="h-8" onClick={() => setType(null)} data-testid="delivery-cache-clear-type">
            Showing {type} <X className="ml-1 size-3" />
          </Button>
        )}
      </FilterBar>

      {historic && (
        <Card className="flex-row items-center gap-2 rounded-lg border-warning/40 bg-warning/5 p-3 text-[13px]" data-testid="delivery-cache-historic">
          <History className="size-4 shrink-0 text-warning" />
          <span>
            Reading the cache as it stood at <span className="font-mono">{selected!.version}</span>. Deliveries resolve
            against the current version; nothing here is what a render would read today.
          </span>
          <Button variant="outline" size="sm" className="ml-auto h-7" onClick={() => setVersionFilter(CURRENT)} data-testid="delivery-cache-back-to-current">
            Back to current
          </Button>
        </Card>
      )}

      <div className="grid gap-4 lg:grid-cols-[280px_minmax(0,1fr)]">
        <aside className="flex flex-col gap-2" data-testid="delivery-cache-types">
          <h2 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">What is cached</h2>
          {definitions.isPending && <Skeleton className="h-24 w-full rounded-lg" />}
          {!definitions.isPending && rows.length === 0 && (
            <Card className="gap-2 p-3 text-[13px] text-muted-foreground">
              <Database className="size-4" />
              No cache declared yet. A retrieval flow declares one with a cache.types section naming the OSDU types and the paths to cache.
            </Card>
          )}
          {rows.map((definition) => (
            <CachedTypeCard
              key={definition.id}
              definition={definition}
              selected={type === definition.name}
              onSelect={() => setType(type === definition.name ? null : definition.name)}
            />
          ))}
        </aside>

        <Tabs defaultValue="records">
          <TabsList data-testid="delivery-cache-tabs">
            <TabsTrigger value="records" data-testid="delivery-cache-tab-records">Cached records</TabsTrigger>
            <TabsTrigger value="updates" data-testid="delivery-cache-tab-updates">
              Updates{(pending.data?.total ?? 0) > 0 ? ` (${pending.data?.total})` : ""}
            </TabsTrigger>
          </TabsList>

          <TabsContent value="records" className="flex flex-col gap-2">
            <SearchInput
              value={search}
              onChange={setSearch}
              placeholder="Search cached values, ids and aliases"
              label="Search cached records"
              className="w-full max-w-md"
              testId="delivery-cache-search"
            />
            <PagedTable
              queryKey={["delivery", "cache", "items", repoId, type, search, version]}
              fetchPage={(page, pageSize) => deliveryApi.cachedItems({
                page, pageSize, repoId, type: type ?? undefined, search: search || undefined, version,
              })}
              columns={[
                { id: "type", header: "Type", render: (row) => <Badge variant="outline">{row.typeName}</Badge> },
                { id: "recordId", header: "OSDU id", render: (row) => <TruncatedText text={row.recordId} mono maxWidth={380} /> },
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
              onRowClick={(row) => setItem(row)}
              emptyMessage="No cached records match. A cache is filled by running the retrieval flow that declares it, then syncing the repository."
              data-testid="delivery-cache-items-table"
            />
          </TabsContent>

          <TabsContent value="updates" className="flex flex-col gap-2">
            <div className="flex items-center gap-2">
              <Select value={tagStatus} onValueChange={(v) => { setTagStatus(v); setSelectedTags(new Set()); }}>
                <SelectTrigger size="sm" className="h-8 w-44" data-testid="delivery-cache-tag-status"><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="pending">Awaiting approval</SelectItem>
                  <SelectItem value="approved">Approved</SelectItem>
                  <SelectItem value="rolling">Rolling out</SelectItem>
                  <SelectItem value="applied">Rolled out</SelectItem>
                  <SelectItem value="rejected">Rejected</SelectItem>
                </SelectContent>
              </Select>
              {tagStatus === "pending" && selectedTags.size > 0 && (
                <div className="flex items-center gap-2">
                  <Button size="sm" className="h-8" disabled={decide.isPending} onClick={() => decideSelected(true)} data-testid="delivery-cache-approve">
                    <Check className="mr-1 size-3" /> Approve {selectedTags.size}
                  </Button>
                  <Button size="sm" variant="outline" className="h-8" disabled={decide.isPending} onClick={() => decideSelected(false)} data-testid="delivery-cache-reject">
                    <X className="mr-1 size-3" /> Reject
                  </Button>
                </div>
              )}
            </div>
            <PagedTable
              queryKey={["delivery", "cache", "tags", tagStatus]}
              fetchPage={(page, pageSize) => deliveryApi.updateTags({ page, pageSize, status: tagStatus })}
              columns={tagColumns}
              rowKey={(row) => String(row.tagId)}
              selection={tagStatus === "pending"
                ? { selected: selectedTags, onChange: setSelectedTags }
                : undefined}
              emptyMessage={tagStatus === "pending"
                ? "Nothing is waiting. When a cached value changes, the delivered records built from it appear here for a decision."
                : "No updates in this state."}
              data-testid="delivery-cache-tags-table"
            />
          </TabsContent>
        </Tabs>
      </div>

      <Sheet open={item !== null} onOpenChange={(open) => { if (!open) { setItem(null); } }}>
        <SheetContent className="w-full gap-0 sm:max-w-2xl" data-testid="delivery-cache-item-detail">
          <SheetHeader>
            <SheetTitle>{item?.typeName ?? "Cached record"}</SheetTitle>
            <SheetDescription>
              {item ? `${item.entityType} · ${item.recordId}` : "Loading."}
            </SheetDescription>
            {item && (
              <p className="px-4 text-[12px] text-muted-foreground" data-testid="delivery-cache-item-version">
                As cached at version <span className="font-mono">{item.version}</span>.
              </p>
            )}
          </SheetHeader>
          {item && (
            <div className="flex flex-1 flex-col gap-3 overflow-y-auto px-4 pb-4">
              <CodeView value={JSON.stringify(item.fields, null, 2)} language="json" height={480} data-testid="delivery-cache-item-json" />
            </div>
          )}
        </SheetContent>
      </Sheet>
    </Page>
  );
}

const changeTone: Record<string, string> = {
  changed: "bg-info/15 text-info",
  removed: "bg-destructive/15 text-destructive",
  unmatched: "bg-warning/15 text-warning",
};

const tagColumns = [
  {
    id: "change",
    header: "Change",
    render: (row: DeliveryUpdateTag) => (
      <Badge variant="secondary" className={changeTone[row.change] ?? ""}>{row.change}</Badge>
    ),
  },
  {
    id: "what",
    header: "Cached value",
    render: (row: DeliveryUpdateTag) => (
      <span className="flex flex-col">
        <span className="font-mono text-[12px]">{row.typeName}.{row.path}</span>
        <TruncatedText text={row.itemId} mono maxWidth={300} />
      </span>
    ),
  },
  {
    id: "values",
    header: "Was / is now",
    render: (row: DeliveryUpdateTag) => (
      <span className="font-mono text-[12px]">
        <span className="text-muted-foreground line-through">{row.oldValue ?? "-"}</span>
        <span className="px-1">to</span>
        <span>{row.newValue ?? "gone"}</span>
      </span>
    ),
  },
  {
    id: "records",
    header: "Records",
    align: "right" as const,
    render: (row: DeliveryUpdateTag) => (
      <span className="font-mono tabular-nums">{row.affectedRecords.toLocaleString()}</span>
    ),
  },
  {
    id: "progress",
    header: "Rollout",
    render: (row: DeliveryUpdateTag) => {
      if (row.status === "pending") {
        return <span className="text-[12px] text-muted-foreground">waiting for a decision</span>;
      }

      if (row.status === "rejected") {
        return <span className="text-[12px] text-muted-foreground">not sent</span>;
      }

      const done = row.affectedRecords === 0 ? 1 : row.processed / row.affectedRecords;
      return (
        <span className="flex items-center gap-2">
          <span className="h-1.5 w-24 overflow-hidden rounded-full bg-muted">
            <span className="block h-full bg-primary" style={{ width: `${Math.round(done * 100)}%` }} />
          </span>
          <span className="font-mono text-[11px] tabular-nums text-muted-foreground">
            {row.processed.toLocaleString()} / {row.affectedRecords.toLocaleString()}
          </span>
        </span>
      );
    },
  },
  { id: "mode", header: "Mode", render: (row: DeliveryUpdateTag) => <Badge variant="outline">{row.mode}</Badge> },
  { id: "detected", header: "Detected", render: (row: DeliveryUpdateTag) => <RelativeTime value={row.detectedUtc} /> },
];
