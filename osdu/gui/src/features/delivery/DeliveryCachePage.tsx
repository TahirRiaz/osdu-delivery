import { useState } from "react";
import { Link as RouterLink, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, DatabaseZap, Play, ScrollText, ShieldAlert } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { isApiError } from "@/api/client";
import { deliveryApi, type DeliveryCache, type DeliveryCacheFlow } from "../../api/delivery";
import { CorrelationError } from "@/components/CorrelationError";
import { EmptyState } from "@/components/EmptyState";
import { FilterCombobox, type FilterOption } from "@/components/FilterCombobox";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { RelativeTime } from "@/components/RelativeTime";
import { SummaryStrip, type SummaryCell } from "@/components/SummaryStrip";
import { TriggerRunDialog } from "@/features/runs/TriggerRunDialog";
import { scheduleCadence, summarizeTypes } from "./cacheFormat";
import { DeliveryCacheApprovals } from "./DeliveryCacheApprovals";
import { DeliveryCacheDefinition } from "./DeliveryCacheDefinition";
import { DeliveryCacheHistory } from "./DeliveryCacheHistory";
import { DeliveryCacheRecords } from "./DeliveryCacheRecords";
import { DeliveryCacheSystemProperties } from "./DeliveryCacheSystemProperties";

type Tab = "records" | "versions" | "changes" | "definition" | "system";

const TABS: readonly string[] = ["records", "versions", "changes", "definition", "system"];

function isTab(value: string | null): value is Tab {
  return value !== null && TABS.includes(value);
}

/**
 * The OSDU cache: the reference and master data every delivered document is built from, one cache per OSDU partition. The
 * header names the partition and the cache flow files that fill it, with Cache files and Refresh for them; a summary row says
 * which version deliveries read, how much it holds, how it is refreshed and whether anything waits for a decision. Below
 * are the working tabs: the records, the versions, the changes a refresh found, the definition, and the partition's system
 * properties (its settings, as the platform reports them), with a searchable type picker in the tab bar for the tabs a
 * type narrows. The partition, the tab and the type live in the URL, so a link lands on
 * the same view; a link naming a cache flow (?flow=) opens the partition that flow fills.
 */
export default function DeliveryCachePage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const [remembered, setRemembered] = useLocalStorageState("sqlflow.filters.delivery-cache.scope", "");
  const [refreshing, setRefreshing] = useState<DeliveryCacheFlow | null>(null);

  const caches = useQuery({ queryKey: ["delivery", "cache", "caches"], queryFn: () => deliveryApi.caches() });
  const all = caches.data ?? [];
  const byFlow = searchParams.get("flow");
  const requested = searchParams.get("scope") ?? remembered;
  const cache = (byFlow === null ? undefined : all.find((candidate) => candidate.flows.some((flow) => flow.name === byFlow)))
    ?? all.find((candidate) => candidate.scope === requested)
    ?? all[0]
    ?? null;

  const tabParam = searchParams.get("tab");
  const tab: Tab = isTab(tabParam) ? tabParam : "records";

  const update = (changes: Record<string, string | null>) => setSearchParams((current) => {
    const next = new URLSearchParams(current);
    for (const [key, value] of Object.entries(changes)) {
      if (value === null) {
        next.delete(key);
      } else {
        next.set(key, value);
      }
    }

    return next;
  }, { replace: true });

  const chooseCache = (scope: string) => {
    setRemembered(scope);
    update({ scope, flow: null, type: null });
  };

  return (
    <Page data-testid="page-delivery-cache">
      <PageHeader
        title="OSDU cache"
        subtitle={cache === null
          ? "The reference and master data mappings resolve against, one cache per OSDU partition."
          : <CacheSubtitle cache={cache} />}
        actions={cache === null ? undefined : (
          <>
            {all.length > 1 && (
              <Select value={cache.scope} onValueChange={chooseCache}>
                <SelectTrigger size="sm" className="h-8 min-w-52" aria-label="Partition" data-testid="delivery-cache-picker">
                  <DatabaseZap />
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {all.map((candidate) => (
                    <SelectItem key={candidate.scope} value={candidate.scope}>
                      <span className="font-mono text-[12px]">{candidate.scope}</span>
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )}
            <CacheFlowActions flows={cache.flows} onRefresh={setRefreshing} />
          </>
        )}
      />

      {caches.isError && (isApiError(caches.error)
        ? <CorrelationError error={caches.error} />
        : <p className="text-[13px] text-destructive">{String(caches.error)}</p>)}

      {caches.isPending
        ? (
          <>
            <Skeleton className="h-16 w-full rounded-lg" />
            <Skeleton className="h-96 w-full rounded-lg" />
          </>
        )
        : cache === null
          ? (
            <Card className="gap-0 rounded-lg p-0">
              <EmptyState
                icon={<DatabaseZap />}
                title="No cache is defined yet"
                description="A cache is filled by cache flows: YAML files in a repository with flowType: cache, listing the OSDU types to cache and the paths of each record to keep. Each fills the cache of the partition in its data-partition-id. Sync the repository and the partition's cache appears here; refresh a flow to capture the first version."
                data-testid="delivery-cache-none"
              />
            </Card>
          )
          : (
            <CacheWorkbench
              key={cache.scope}
              cache={cache}
              tab={tab}
              type={searchParams.get("type")}
              onTab={(next) => update({ tab: next === "records" ? null : next })}
              onType={(name) => update({ type: name })}
              onRefresh={setRefreshing}
            />
          )}

      {refreshing !== null && refreshing.pipelineId !== null && (
        <TriggerRunDialog
          open
          onClose={() => setRefreshing(null)}
          repoId={refreshing.repoId}
          flowName={refreshing.name}
          flowId={refreshing.pipelineId}
          flowKind="cache"
        />
      )}
    </Page>
  );
}

/** The partition, the files that fill its cache, and where to change what is cached. */
function CacheSubtitle({ cache }: { cache: DeliveryCache }) {
  const files = cache.flows.map((flow) => flow.relativePath).join(", ");
  return (
    <span className="flex flex-wrap items-baseline gap-x-1.5 gap-y-0.5">
      <span>Partition</span>
      <span className="font-mono text-foreground" data-testid="delivery-cache-name">{cache.scope}</span>
      <span>is filled by</span>
      {cache.flows.length === 1
        ? <span className="font-mono text-foreground" data-testid="delivery-cache-defined-in">{files}</span>
        : (
          <span className="text-foreground" title={files} data-testid="delivery-cache-defined-in">
            {cache.flows.length} cache flows
          </span>
        )}
      <span>and read by every delivery flow that delivers to it. Edit a cache flow file to change what it caches.</span>
    </span>
  );
}

/**
 * Where the cache flow files are, as a filter on Pipelines: every cache flow, narrowed to the repository when one repository
 * holds every flow filling the partition. A partition can be filled by several files, so the header points at the list of
 * them rather than at one file; each row of the Definition tab still opens its own file.
 */
function cacheFilesLink(flows: DeliveryCacheFlow[]): string {
  const repos = new Set(flows.map((flow) => flow.repoId));
  const params = new URLSearchParams({ kind: "cache" });
  if (repos.size === 1) {
    params.set("repo", [...repos][0]);
  }

  return `/pipelines?${params.toString()}`;
}

/**
 * The header's actions for the cache flows filling the partition: Cache files opens them as a filter on Pipelines, and
 * Refresh runs one flow's capture (a button when one flow fills the partition, a menu naming the flows when several do,
 * because a refresh captures what one flow declares, not the partition's whole cache).
 */
function CacheFlowActions({ flows, onRefresh }: { flows: DeliveryCacheFlow[]; onRefresh: (flow: DeliveryCacheFlow) => void }) {
  const runnable = flows.filter((flow) => flow.pipelineId !== null);
  const files = (
    <Button asChild size="sm" variant="outline" data-testid="delivery-cache-files">
      <RouterLink to={cacheFilesLink(flows)}>
        <ScrollText />
        Cache files
        <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{flows.length}</span>
      </RouterLink>
    </Button>
  );

  if (runnable.length === 0) {
    return files;
  }

  if (runnable.length === 1) {
    const flow = runnable[0];
    return (
      <>
        {files}
        <Button size="sm" onClick={() => onRefresh(flow)} data-testid="delivery-cache-refresh">
          <Play />
          Refresh now
        </Button>
      </>
    );
  }

  return (
    <>
      {files}
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button size="sm" data-testid="delivery-cache-refresh">
            <Play />
            Refresh
            <ChevronDown />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end">
          <DropdownMenuLabel>Refresh what one cache flow captures</DropdownMenuLabel>
          {runnable.map((flow) => (
            <DropdownMenuItem
              key={`${flow.repoId}:${flow.name}`}
              onSelect={() => onRefresh(flow)}
              data-testid={`delivery-cache-refresh-${flow.name}`}
            >
              <span className="font-mono text-[12px]">{flow.name}</span>
              <span className="text-[11px] text-muted-foreground">{flow.types.join(", ")}</span>
            </DropdownMenuItem>
          ))}
        </DropdownMenuContent>
      </DropdownMenu>
    </>
  );
}

/** One partition cache's summary and its working tabs. Keyed by the partition, so another partition starts from a clean view. */
function CacheWorkbench({ cache, tab, type, onTab, onType, onRefresh }: {
  cache: DeliveryCache;
  tab: Tab;
  /** The type in scope from the URL; a name the cache does not hold scopes nothing. */
  type: string | null;
  onTab: (tab: Tab) => void;
  onType: (type: string | null) => void;
  onRefresh: (flow: DeliveryCacheFlow) => void;
}) {
  const versions = useQuery({
    queryKey: ["delivery", "cache", "versions", cache.scope],
    queryFn: () => deliveryApi.cacheVersions(cache.scope),
  });
  const pending = useQuery({
    queryKey: ["delivery", "cache", "tags", "pending-count", cache.scope],
    queryFn: () => deliveryApi.updateTags({ page: 1, pageSize: 1, status: "pending", scope: cache.scope }),
  });
  const pendingTotal = pending.data?.total ?? 0;

  const types = summarizeTypes(cache);
  const scoped = type === null ? null : types.find((candidate) => candidate.name === type) ?? null;
  const approvalTypes = cache.types.filter((candidate) => candidate.onChange === "approve").map((candidate) => candidate.name);
  const records = types.reduce((sum, candidate) => sum + candidate.items, 0);
  const current = cache.current;
  const schedules = cache.flows.flatMap((flow) => flow.schedules);
  const typeOptions: FilterOption[] = types.map((candidate) => ({
    value: candidate.name,
    label: candidate.name,
    hint: `${candidate.family.toLowerCase()} · ${candidate.items.toLocaleString()} record${candidate.items === 1 ? "" : "s"}`
      + (candidate.onChange === "approve" ? " · changes need approval" : ""),
  }));

  const changesCell: SummaryCell = approvalTypes.length === 0 && pendingTotal === 0
    ? {
      label: "Changes",
      value: "automatic",
      caption: "a changed value goes out on the next run",
      onClick: () => onTab("changes"),
      testId: "delivery-cache-approval",
    }
    : {
      label: "Waiting for approval",
      value: pendingTotal.toLocaleString(),
      tone: pendingTotal > 0 ? "warning" : undefined,
      caption: `${approvalTypes.length} of ${types.length} type${types.length === 1 ? "" : "s"} ask for approval`,
      onClick: () => onTab("changes"),
      testId: "delivery-cache-approval",
    };

  return (
    <>
      <SummaryStrip
        data-testid="delivery-cache-summary"
        cells={[
          {
            label: "Current version",
            value: current === null ? "none yet" : current.version,
            caption: current === null
              ? "refresh a cache flow to capture one"
              : <>written by {current.flow} <RelativeTime value={current.capturedUtc} absolute={false} /> for {current.capturedBy}</>,
            onClick: () => onTab("versions"),
            testId: "delivery-cache-current",
          },
          {
            label: "Records",
            value: records.toLocaleString(),
            caption: `in ${types.length} type${types.length === 1 ? "" : "s"}`,
            onClick: () => {
              onType(null);
              onTab("records");
            },
            testId: "delivery-cache-records",
          },
          {
            label: "Refreshed",
            value: schedules.length === 0 ? "on demand" : "on schedule",
            caption: cache.flows.length > 1
              ? `by ${cache.flows.length} cache flows`
              : schedules.length === 0 ? "no schedule; use Refresh now" : schedules.map(scheduleCadence).join("; "),
            onClick: () => onTab("definition"),
            testId: "delivery-cache-schedules",
          },
          changesCell,
        ]}
      />

      {pendingTotal > 0 && (
        <Alert className="border-warning/40 bg-warning/8" data-testid="delivery-cache-pending-banner">
          <ShieldAlert className="text-warning" />
          <AlertTitle>
            {pendingTotal.toLocaleString()} change{pendingTotal === 1 ? " waits" : "s wait"} for your approval
          </AlertTitle>
          <AlertDescription className="flex flex-wrap items-center gap-x-3 gap-y-1">
            <span>The delivered records built from these values are held back until each change is approved or rejected.</span>
            <Button size="xs" variant="outline" onClick={() => onTab("changes")} data-testid="delivery-cache-review-changes">
              Review changes
            </Button>
          </AlertDescription>
        </Alert>
      )}

      <Tabs value={tab} onValueChange={(value) => onTab(value as Tab)} className="min-w-0 gap-3">
        <div className="flex flex-wrap items-center justify-between gap-2 border-b border-border">
          <TabsList variant="line" data-testid="delivery-cache-tabs">
            <TabsTrigger value="records" data-testid="delivery-cache-tab-records">
              {scoped === null ? "Records" : `${scoped.name} records`}
            </TabsTrigger>
            <TabsTrigger value="versions" data-testid="delivery-cache-tab-versions">
              Versions
              <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{cache.versions}</span>
            </TabsTrigger>
            <TabsTrigger value="changes" data-testid="delivery-cache-tab-changes">
              Changes
              {pendingTotal > 0 && (
                <span className="rounded-full bg-warning/15 px-1.5 font-mono text-[11px] tabular-nums text-warning">{pendingTotal}</span>
              )}
            </TabsTrigger>
            <TabsTrigger value="definition" data-testid="delivery-cache-tab-definition">Definition</TabsTrigger>
            <TabsTrigger value="system" data-testid="delivery-cache-tab-system">System properties</TabsTrigger>
          </TabsList>
          {/* The type narrows the records and the versions; the changes and the definition always cover the whole cache. */}
          {(tab === "records" || tab === "versions") && (
            <FilterCombobox
              options={typeOptions}
              value={scoped?.name ?? ""}
              onChange={(value) => onType(value === "" ? null : value)}
              placeholder="All types"
              searchPlaceholder="Type, family or captured name"
              emptyText="No cached type matches."
              ariaLabel="Cached type"
              testId="delivery-cache-type"
              className="mb-1 w-60"
            />
          )}
        </div>

        <TabsContent value="records">
          <DeliveryCacheRecords
            scope={cache.scope}
            type={scoped?.name ?? null}
            fields={scoped?.fields.map((field) => field.as) ?? []}
            versions={versions.data ?? []}
          />
        </TabsContent>

        <TabsContent value="versions">
          <DeliveryCacheHistory scope={cache.scope} type={scoped?.name ?? null} />
        </TabsContent>

        <TabsContent value="changes">
          <DeliveryCacheApprovals scope={cache.scope} approvalTypes={approvalTypes} pendingTotal={pending.data?.total} />
        </TabsContent>

        <TabsContent value="definition">
          <DeliveryCacheDefinition cache={cache} onRefresh={onRefresh} />
        </TabsContent>

        <TabsContent value="system">
          <DeliveryCacheSystemProperties version={current} />
        </TabsContent>
      </Tabs>
    </>
  );
}
