import { useState } from "react";
import { Link as RouterLink, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { DatabaseZap, Play, ScrollText, ShieldAlert } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { isApiError } from "../../api/client";
import { deliveryApi, type DeliveryCache } from "../../api/delivery";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { RelativeTime } from "../../components/RelativeTime";
import { SummaryStrip, type SummaryCell } from "../../components/SummaryStrip";
import { TriggerRunDialog } from "../runs/TriggerRunDialog";
import { scheduleCadence, summarizeTypes } from "./cacheFormat";
import { DeliveryCacheApprovals } from "./DeliveryCacheApprovals";
import { DeliveryCacheDefinition } from "./DeliveryCacheDefinition";
import { DeliveryCacheHistory } from "./DeliveryCacheHistory";
import { DeliveryCacheRecords } from "./DeliveryCacheRecords";
import { DeliveryCacheTypeList } from "./DeliveryCacheTypeList";

type Tab = "records" | "versions" | "changes" | "definition";

const TABS: readonly string[] = ["records", "versions", "changes", "definition"];

function isTab(value: string | null): value is Tab {
  return value !== null && TABS.includes(value);
}

/**
 * The OSDU cache: the reference and master data every delivered document is built from. The header names the cache and
 * the cache flow file that defines it, with View YAML and Refresh now beside it; a summary row says which version
 * deliveries read, how much it holds, how it is refreshed and whether anything waits for a decision. Below, the declared
 * types sit in a list on the left and scope the working tabs on the right: the records, the versions, the changes a
 * refresh found, and the definition. The tab, the cache and the type live in the URL, so a link lands on the same view.
 */
export default function DeliveryCachePage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const [remembered, setRemembered] = useLocalStorageState("sqlflow.filters.delivery-cache.cache", "");
  const [refreshOpen, setRefreshOpen] = useState(false);

  const caches = useQuery({ queryKey: ["delivery", "cache", "caches"], queryFn: () => deliveryApi.caches() });
  const all = caches.data ?? [];
  const requested = searchParams.get("cache") ?? remembered;
  const cache = all.find((candidate) => candidate.name === requested) ?? all[0] ?? null;

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

  const chooseCache = (name: string) => {
    setRemembered(name);
    update({ cache: name, type: null });
  };

  return (
    <Page data-testid="page-delivery-cache">
      <PageHeader
        title="OSDU cache"
        subtitle={cache === null
          ? "The reference and master data mappings resolve against."
          : (
            <span className="flex flex-wrap items-baseline gap-x-1.5 gap-y-0.5">
              <span className="font-mono text-foreground" data-testid="delivery-cache-name">{cache.name}</span>
              <span>is defined in</span>
              <span className="font-mono text-foreground" data-testid="delivery-cache-defined-in">{cache.relativePath}</span>
              <span>in {cache.repoName}. Edit that file to change what is cached.</span>
            </span>
          )}
        actions={cache === null ? undefined : (
          <>
            {all.length > 1 && (
              <Select value={cache.name} onValueChange={chooseCache}>
                <SelectTrigger size="sm" className="h-8 min-w-52" aria-label="Cache" data-testid="delivery-cache-picker">
                  <DatabaseZap />
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {all.map((candidate) => (
                    <SelectItem key={`${candidate.repoId}:${candidate.name}`} value={candidate.name}>
                      <span className="font-mono text-[12px]">{candidate.name}</span>
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )}
            {cache.pipelineId !== null && (
              <>
                <Button asChild size="sm" variant="outline" data-testid="delivery-cache-view-yaml">
                  <RouterLink to={`/pipelines/${cache.pipelineId}?tab=yaml`}>
                    <ScrollText />
                    View YAML
                  </RouterLink>
                </Button>
                <Button size="sm" onClick={() => setRefreshOpen(true)} data-testid="delivery-cache-refresh">
                  <Play />
                  Refresh now
                </Button>
              </>
            )}
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
                description="A cache is defined by a cache flow: a YAML file in a repository with flowType: cache, listing the OSDU types to cache and the paths of each record to keep. Sync the repository and the cache appears here; refresh it to capture its first version."
                data-testid="delivery-cache-none"
              />
            </Card>
          )
          : (
            <CacheWorkbench
              key={cache.name}
              cache={cache}
              tab={tab}
              type={searchParams.get("type")}
              onTab={(next) => update({ tab: next === "records" ? null : next })}
              onType={(name) => update({ type: name })}
            />
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

/** One cache's summary, its type list and its working tabs. Keyed by the cache, so another cache starts from a clean view. */
function CacheWorkbench({ cache, tab, type, onTab, onType }: {
  cache: DeliveryCache;
  tab: Tab;
  /** The type in scope from the URL; a name the cache does not declare scopes nothing. */
  type: string | null;
  onTab: (tab: Tab) => void;
  onType: (type: string | null) => void;
}) {
  const versions = useQuery({
    queryKey: ["delivery", "cache", "versions", cache.name],
    queryFn: () => deliveryApi.cacheVersions(cache.name),
  });
  const pending = useQuery({
    queryKey: ["delivery", "cache", "tags", "pending-count", cache.name],
    queryFn: () => deliveryApi.updateTags({ page: 1, pageSize: 1, status: "pending", cache: cache.name }),
  });
  const pendingTotal = pending.data?.total ?? 0;

  const types = summarizeTypes(cache);
  const scoped = type === null ? null : types.find((candidate) => candidate.name === type) ?? null;
  const approvalTypes = cache.types.filter((candidate) => candidate.onChange === "approve").map((candidate) => candidate.name);
  const records = types.reduce((sum, candidate) => sum + candidate.items, 0);
  const current = cache.current;

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
              ? "refresh the cache to capture one"
              : <>captured <RelativeTime value={current.capturedUtc} absolute={false} /> by {current.capturedBy}</>,
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
            value: cache.schedules.length === 0 ? "on demand" : "on schedule",
            caption: cache.schedules.length === 0 ? "no schedule; use Refresh now" : cache.schedules.map(scheduleCadence).join("; "),
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

      <div className="grid items-start gap-4 lg:grid-cols-[240px_minmax(0,1fr)]">
        <DeliveryCacheTypeList
          types={types}
          selected={scoped?.name ?? null}
          onSelect={(name) => {
            onType(name);
            // The changes and the definition cover the whole cache, so picking a type goes to what the pick scopes.
            if (tab === "changes" || tab === "definition") {
              onTab("records");
            }
          }}
        />

        <Tabs value={tab} onValueChange={(value) => onTab(value as Tab)} className="min-w-0 gap-3">
          <div className="border-b border-border">
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
            </TabsList>
          </div>

          <TabsContent value="records">
            <DeliveryCacheRecords
              cache={cache.name}
              type={scoped?.name ?? null}
              fields={scoped?.fields.map((field) => field.as) ?? []}
              versions={versions.data ?? []}
            />
          </TabsContent>

          <TabsContent value="versions">
            <DeliveryCacheHistory cache={cache.name} type={scoped?.name ?? null} />
          </TabsContent>

          <TabsContent value="changes">
            <DeliveryCacheApprovals cache={cache.name} approvalTypes={approvalTypes} pendingTotal={pending.data?.total} />
          </TabsContent>

          <TabsContent value="definition">
            <DeliveryCacheDefinition cache={cache} />
          </TabsContent>
        </Tabs>
      </div>
    </>
  );
}
