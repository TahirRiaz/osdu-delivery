import { useMemo, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { DatabaseZap, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { isApiError } from "../../api/client";
import { deliveryApi, type DeliveryCacheVersion } from "../../api/delivery";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { FilterBar } from "../../components/FilterBar";
import { FilterCombobox, type FilterOption } from "../../components/FilterCombobox";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { SearchInput } from "../../components/SearchInput";
import { TriggerRunDialog } from "../runs/TriggerRunDialog";
import { summarizeTypes } from "./cacheFormat";
import { DeliveryCacheApprovals } from "./DeliveryCacheApprovals";
import { DeliveryCacheDefinitionCard } from "./DeliveryCacheDefinitionCard";
import { DeliveryCacheHistory } from "./DeliveryCacheHistory";
import { CacheVersionPicker, CURRENT, DeliveryCacheRecords } from "./DeliveryCacheRecords";

type Tab = "records" | "versions" | "approvals";

/**
 * The OSDU cache: the reference and master data every delivered document is built from. The page opens on what the cache
 * is (the cache flow file that defines it, what refreshes it, the current version and the run that captured it, and every
 * type it declares) and then shows what it holds: the records of one version, the versions with what each changed, and the
 * changes to delivered records that wait for a decision. What is cached is changed in the cache flow's file, never here;
 * a refresh is a run of that flow, triggered from here or by its schedule.
 */
export default function DeliveryCachePage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const [remembered, setRemembered] = useLocalStorageState("sqlflow.filters.delivery-cache.cache", "");
  const [type, setType] = useState<string>("");
  // The picker holds a version label; CURRENT follows whichever version is current rather than freezing on one.
  const [versionFilter, setVersionFilter] = useState<string>(CURRENT);
  const [search, setSearch] = useState("");
  const [tab, setTab] = useState<Tab>("records");
  const [refreshOpen, setRefreshOpen] = useState(false);

  const caches = useQuery({ queryKey: ["delivery", "cache", "caches"], queryFn: () => deliveryApi.caches() });
  const all = caches.data ?? [];
  const requested = searchParams.get("cache") ?? remembered;
  const cache = all.find((candidate) => candidate.name === requested) ?? all[0] ?? null;
  const cacheName = cache?.name ?? null;

  const chooseCache = (name: string) => {
    setRemembered(name);
    setType("");
    setVersionFilter(CURRENT);
    setSearchParams((current) => {
      const next = new URLSearchParams(current);
      next.set("cache", name);
      return next;
    }, { replace: true });
  };

  const versions = useQuery({
    queryKey: ["delivery", "cache", "versions", cacheName],
    queryFn: () => deliveryApi.cacheVersions(cacheName!),
    enabled: cacheName !== null,
  });
  const pending = useQuery({
    queryKey: ["delivery", "cache", "tags", "pending-count"],
    queryFn: () => deliveryApi.updateTags({ page: 1, pageSize: 100, status: "pending" }),
  });
  const pendingTotal = pending.data?.total;

  // A version that is not among the cache's versions (another cache was picked) would read as an empty cache rather than
  // as a stale selection, so the picker falls back to the current version.
  const known: DeliveryCacheVersion[] | undefined = versions.data;
  if (versionFilter !== CURRENT && known !== undefined && !known.some((v) => v.version === versionFilter)) {
    setVersionFilter(CURRENT);
  }

  const reading = known?.find((v) => (versionFilter === CURRENT ? v.current : v.version === versionFilter));
  const historic = reading !== undefined && !reading.current ? reading : null;
  const version = versionFilter === CURRENT ? undefined : versionFilter;

  const types = useMemo(() => summarizeTypes(cache), [cache]);
  if (type !== "" && cache !== null && !types.some((t) => t.name === type)) {
    setType("");
  }

  const scoped = type === "" ? null : types.find((t) => t.name === type) ?? null;
  const typeOptions: FilterOption[] = types.map((t) => ({
    value: t.name,
    label: t.name,
    hint: `${t.family.toLowerCase()} · ${t.items.toLocaleString()} record${t.items === 1 ? "" : "s"} · ${t.fields.map((f) => f.as).join(", ")}`,
  }));

  return (
    <Page data-testid="page-delivery-cache">
      <PageHeader
        title="OSDU cache"
        subtitle="The reference and master data mappings resolve against. A cache flow in a repository defines what a cache holds, its runs capture versions of it into the catalog, and delivery flows read it by name."
        actions={all.length > 0 && cache !== null ? (
          <Select value={cache.name} onValueChange={chooseCache}>
            <SelectTrigger size="sm" className="h-8 w-64" aria-label="Cache" data-testid="delivery-cache-picker">
              <DatabaseZap />
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {all.map((candidate) => (
                <SelectItem key={`${candidate.repoId}:${candidate.name}`} value={candidate.name}>
                  <span className="font-mono text-[12px]">{candidate.name}</span>
                  <span className="text-[11px] text-muted-foreground">{candidate.repoName}</span>
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        ) : undefined}
      />

      {caches.isError && (isApiError(caches.error)
        ? <CorrelationError error={caches.error} />
        : <p className="text-[13px] text-destructive">{String(caches.error)}</p>)}

      {caches.isPending
        ? <Skeleton className="h-64 w-full rounded-lg" />
        : cache === null
          ? (
            <Card className="gap-0 rounded-lg p-0">
              <EmptyState
                icon={<DatabaseZap />}
                title="No cache is defined yet"
                description="A cache is defined by a cache flow: a YAML file in a repository with flowType: cache, listing the OSDU types to cache and the paths of each record to keep. Sync the repository and the cache appears here; run the flow with the refresh operation to capture its first version."
                data-testid="delivery-cache-none"
              />
            </Card>
          )
          : (
            <>
              <DeliveryCacheDefinitionCard
                cache={cache}
                scopedType={scoped?.name ?? null}
                onPickType={(name) => {
                  setType(name === type ? "" : name);
                  setTab("records");
                }}
                onRefresh={() => setRefreshOpen(true)}
              />

              <FilterBar>
                <SearchInput
                  value={search}
                  onChange={(value) => {
                    setSearch(value);
                    // The search is over the records, so typing one brings them forward from whichever tab is open.
                    if (value !== "") {
                      setTab("records");
                    }
                  }}
                  placeholder="Search values, ids and aliases"
                  label="Search cached records"
                  className="sm:w-72"
                  testId="delivery-cache-search"
                />
                <FilterCombobox
                  options={typeOptions}
                  value={type}
                  onChange={setType}
                  placeholder="All types"
                  searchPlaceholder="Type name, family or captured name"
                  emptyText="No cached type matches."
                  ariaLabel="Cached type"
                  testId="delivery-cache-type"
                  className="w-full sm:w-52"
                />
                <CacheVersionPicker versions={known ?? []} value={versionFilter} onChange={setVersionFilter} className="w-full sm:w-72" />
                {scoped !== null && (
                  <Button variant="ghost" size="sm" onClick={() => setType("")} data-testid="delivery-cache-clear-type">
                    <X />
                    Every type
                  </Button>
                )}
              </FilterBar>

              <Tabs value={tab} onValueChange={(value) => setTab(value as Tab)}>
                <div className="border-b border-border">
                  <TabsList variant="line" data-testid="delivery-cache-tabs">
                    <TabsTrigger value="records" data-testid="delivery-cache-tab-records">
                      {scoped === null ? "Records" : `${scoped.name} records`}
                    </TabsTrigger>
                    <TabsTrigger value="versions" data-testid="delivery-cache-tab-versions">
                      Versions
                      <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{cache.versions}</span>
                    </TabsTrigger>
                    <TabsTrigger value="approvals" data-testid="delivery-cache-tab-approvals">
                      Approvals
                      {(pendingTotal ?? 0) > 0 && (
                        <span className="rounded-full bg-warning/15 px-1.5 font-mono text-[11px] tabular-nums text-warning">{pendingTotal}</span>
                      )}
                    </TabsTrigger>
                  </TabsList>
                </div>

                <TabsContent value="records">
                  <DeliveryCacheRecords
                    cache={cache.name}
                    type={scoped?.name ?? null}
                    fields={scoped?.fields.map((field) => field.as) ?? []}
                    search={search}
                    version={version}
                    historic={historic}
                    onBackToCurrent={() => setVersionFilter(CURRENT)}
                  />
                </TabsContent>

                <TabsContent value="versions">
                  <DeliveryCacheHistory cache={cache.name} type={scoped?.name ?? null} />
                </TabsContent>

                <TabsContent value="approvals">
                  <DeliveryCacheApprovals pendingTotal={pendingTotal} />
                </TabsContent>
              </Tabs>
            </>
          )}

      {refreshOpen && cache !== null && cache.pipelineId !== null && (
        <TriggerRunDialog
          open
          onClose={() => setRefreshOpen(false)}
          repoId={cache.repoId}
          flowName={cache.name}
          flowId={cache.pipelineId}
          flowKind="cache"
        />
      )}
    </Page>
  );
}
