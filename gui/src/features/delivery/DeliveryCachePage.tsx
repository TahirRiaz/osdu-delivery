import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { History, Pin, PinOff, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { deliveryApi, type DeliveryCacheVersion } from "../../api/delivery";
import { repoApi } from "../../api/endpoints";
import { FilterBar } from "../../components/FilterBar";
import { FilterCombobox, type FilterOption } from "../../components/FilterCombobox";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { RelativeTime } from "../../components/RelativeTime";
import { SearchInput } from "../../components/SearchInput";
import { StatePill } from "../../components/StatusBadge";
import { SummaryStrip, type SummaryCell } from "../../components/SummaryStrip";
import { summarizeTypes, type CachedTypeSummary } from "./cacheFormat";
import { DeliveryCacheApprovals } from "./DeliveryCacheApprovals";
import { DeliveryCacheHistory } from "./DeliveryCacheHistory";
import { CacheVersionPicker, CURRENT, DeliveryCacheRecords } from "./DeliveryCacheRecords";

const ALL = "all";

type Tab = "records" | "history" | "approvals";

/**
 * The type in scope, on one line above the tabs: what it captures, who keeps it current, how many records of it the
 * version being read holds. Rendered only while a type is picked, since the whole cache needs no such line.
 */
function CacheScope({ type, onClear }: { type: CachedTypeSummary; onClear: () => void }) {
  return (
    <div
      className="flex flex-wrap items-center gap-x-4 gap-y-1 rounded-md border border-border bg-muted/30 px-3 py-1.5 text-[12px] text-muted-foreground"
      data-testid="delivery-cache-scope"
    >
      <span className="inline-flex items-baseline gap-2">
        <span className="font-mono text-[13px] font-medium text-foreground">{type.name}</span>
        <span className="font-mono">{type.entityType}</span>
      </span>
      <span className="inline-flex flex-wrap items-center gap-1.5">
        captures
        {type.fields.map((field) => (
          <span key={field.as} className="inline-flex items-baseline gap-1 rounded-sm border border-border/60 bg-muted/40 px-1.5 font-mono text-[11px]">
            <span className="text-foreground">{field.as}</span>
            <span>{field.path}</span>
          </span>
        ))}
      </span>
      <span>
        kept current by <span className="font-mono text-foreground">{type.flows.join(", ")}</span>
      </span>
      {!type.movesPin && (
        <span className="inline-flex items-center gap-1">
          <PinOff className="size-3.5" /> a refresh does not move the pin
        </span>
      )}
      <span className="ml-auto font-mono tabular-nums">
        {type.items.toLocaleString()} record{type.items === 1 ? "" : "s"}
      </span>
      <Button variant="ghost" size="icon-xs" aria-label="Show every type" onClick={onClear} data-testid="delivery-cache-clear-type">
        <X />
      </Button>
    </div>
  );
}

/**
 * The OSDU cache: the reference and master data every delivered document is built from, as the retrieval flows
 * capture it. One line of facts across the top (which version is read, how much it holds, what waits for a
 * decision), one filter row under it (search, type, repository, version), and the records, the version history and
 * the approvals across the full width below; a version or a change picked there opens in the workbench bottom
 * panel. Read-only
 * over what the repositories declare, because a cache is defined in the retrieval flow that keeps it current; the
 * decision on a change that reaches delivered records is the one thing an operator does here.
 */
export default function DeliveryCachePage() {
  const [repoFilter, setRepoFilter] = useLocalStorageState("sqlflow.filters.delivery-cache.repo", ALL);
  const [type, setType] = useState<string>("");
  // The picker holds a version label, not a snapshot id: a label identifies the same capture across repositories,
  // and CURRENT follows the pinned version rather than freezing on whichever one happens to be pinned right now.
  const [versionFilter, setVersionFilter] = useState<string>(CURRENT);
  const [search, setSearch] = useState("");
  const [tab, setTab] = useState<Tab>("records");

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
  const pendingTotal = pending.data?.total;
  const pendingRecords = (pending.data?.items ?? []).reduce((sum, tag) => sum + tag.affectedRecords, 0);

  // Narrowing the repo, or a version leaving the store, can strand the picker on a version that no longer exists,
  // which would read as an empty cache rather than as a stale selection. Fall back to the current version.
  const known: DeliveryCacheVersion[] | undefined = versions.data;
  if (versionFilter !== CURRENT && known !== undefined && !known.some((v) => v.version === versionFilter)) {
    setVersionFilter(CURRENT);
  }

  const reading = known?.find((v) => (versionFilter === CURRENT ? v.current : v.version === versionFilter));
  const historic = reading !== undefined && !reading.current ? reading : null;

  const types = useMemo(() => summarizeTypes(definitions.data ?? []), [definitions.data]);
  // The same guard for the type: a repository change can leave the scope on a type that repository does not declare.
  if (type !== "" && definitions.data !== undefined && !types.some((t) => t.name === type)) {
    setType("");
  }

  const scoped = type === "" ? null : types.find((t) => t.name === type) ?? null;
  const typeOptions: FilterOption[] = types.map((t) => ({
    value: t.name,
    label: t.name,
    hint: `${t.family.toLowerCase()} · ${t.items.toLocaleString()} record${t.items === 1 ? "" : "s"} · ${t.fields.map((f) => f.as).join(", ")}`,
  }));
  const totals = { types: types.length, records: types.reduce((sum, t) => sum + t.items, 0) };

  // What the definitions were read at: one version, or one per repository when the scope spans several.
  const rows = definitions.data ?? [];
  const readVersions = [...new Set(rows.map((d) => d.version).filter((v): v is string => v !== null))];
  const readVersion = readVersions.length === 1 ? readVersions[0] : null;
  const capturedUtc = rows.find((d) => d.capturedUtc)?.capturedUtc ?? null;

  const cells: SummaryCell[] = [
    {
      label: "Reading",
      value: readVersions.length === 0
        ? <span className="text-muted-foreground">no version</span>
        : readVersion !== null
          ? (
            <>
              <span className="truncate">{readVersion}</span>
              {historic !== null
                ? <StatePill tone="warning" label="historic" icon={History} testId="delivery-cache-reading-state" />
                : <StatePill tone="success" label="current" icon={Pin} testId="delivery-cache-reading-state" />}
            </>
          )
          : <span>{readVersions.length} versions</span>,
      caption: readVersions.length === 0
        ? "never captured"
        : readVersion === null
          ? "one per repository in scope"
          : historic !== null
            ? "not the version deliveries resolve against"
            : capturedUtc
              ? <>captured <RelativeTime value={capturedUtc} absolute={false} /></>
              : "capture time unknown",
      testId: "cache-kpi-snapshot",
    },
    { label: "Cached types", value: totals.types, caption: "declared by the retrieval flows in scope", testId: "cache-kpi-types" },
    {
      label: "Cached records",
      value: totals.records.toLocaleString(),
      caption: historic !== null ? "as the version being read held them" : "at the current version",
      testId: "cache-kpi-records",
    },
    {
      label: "Awaiting approval",
      value: pendingTotal ?? 0,
      tone: (pendingTotal ?? 0) > 0 ? "warning" : undefined,
      caption: pendingRecords > 0 ? `${pendingRecords.toLocaleString()} delivered record${pendingRecords === 1 ? "" : "s"} held back` : "no changes waiting",
      onClick: () => setTab("approvals"),
      testId: "cache-kpi-pending",
    },
  ];

  return (
    <Page data-testid="page-delivery-cache">
      <PageHeader
        title="OSDU cache"
        subtitle="The reference and master data every delivered document is built from, one version per capture. Deliveries resolve against the current one."
      />


      <SummaryStrip cells={cells} data-testid="delivery-cache-kpis" />

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
          emptyText={definitions.isPending ? "Loading the cached types." : "No cached type matches."}
          ariaLabel="Cached type"
          testId="delivery-cache-type"
          className="w-full sm:w-52"
        />
        <Select value={repoFilter} onValueChange={setRepoFilter}>
          <SelectTrigger size="sm" className="h-8 w-full sm:w-48" active={repoFilter !== ALL} aria-label="Repository" data-testid="delivery-cache-repo">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>All repositories</SelectItem>
            {(repos.data?.items ?? []).map((repo) => <SelectItem key={repo.id} value={repo.id}>{repo.name}</SelectItem>)}
          </SelectContent>
        </Select>
        <CacheVersionPicker versions={known ?? []} value={versionFilter} onChange={setVersionFilter} className="w-full sm:w-72" />
      </FilterBar>

      {scoped !== null && <CacheScope type={scoped} onClear={() => setType("")} />}

      <Tabs value={tab} onValueChange={(value) => setTab(value as Tab)}>
        <div className="border-b border-border">
          <TabsList variant="line" data-testid="delivery-cache-tabs">
            <TabsTrigger value="records" data-testid="delivery-cache-tab-records">Records</TabsTrigger>
            <TabsTrigger value="history" data-testid="delivery-cache-tab-history">History</TabsTrigger>
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
            repoId={repoId}
            type={scoped?.name ?? null}
            fields={scoped?.fields.map((field) => field.as) ?? []}
            search={search}
            version={version}
            historic={historic}
            onBackToCurrent={() => setVersionFilter(CURRENT)}
          />
        </TabsContent>

        <TabsContent value="history">
          <DeliveryCacheHistory repoId={repoId} type={scoped?.name ?? null} />
        </TabsContent>

        <TabsContent value="approvals">
          <DeliveryCacheApprovals pendingTotal={pendingTotal} />
        </TabsContent>
      </Tabs>
    </Page>
  );
}
