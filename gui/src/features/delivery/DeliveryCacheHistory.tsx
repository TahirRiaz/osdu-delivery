import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import {
  deliveryApi, type DeliveryCacheChange, type DeliveryCacheDiffItem, type DeliveryCacheHistoryEntry,
} from "../../api/delivery";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";
import { PagedTable, type Column } from "../../components/PagedTable";
import { SearchInput } from "../../components/SearchInput";
import { TruncatedText } from "../../components/TruncatedText";
import { cachedFieldsText, cachedText } from "./cacheFormat";

const ALL = "all";

type ChangeFilter = DeliveryCacheChange | typeof ALL;

const changeFilters: { value: ChangeFilter; label: string }[] = [
  { value: ALL, label: "All" },
  { value: "changed", label: "Changed" },
  { value: "added", label: "Added" },
  { value: "removed", label: "Removed" },
];

const changeTone: Record<DeliveryCacheChange, string> = {
  changed: "bg-info/15 text-info",
  added: "bg-primary/15 text-primary",
  removed: "bg-destructive/15 text-destructive",
};

function keyOf(entry: DeliveryCacheHistoryEntry): string {
  return `${entry.version.repoId}:${entry.version.version}`;
}

function totalOf(entry: DeliveryCacheHistoryEntry): number {
  return (entry.changed ?? 0) + (entry.added ?? 0) + (entry.removed ?? 0);
}

/** Whether the version is known to have changed something (in the type picked, when one is). */
function changedSomething(entry: DeliveryCacheHistoryEntry): boolean {
  return entry.compared && totalOf(entry) > 0;
}

/** What one version changed, as one short line, or why that is not known. */
function describe(entry: DeliveryCacheHistoryEntry): string {
  if (!entry.version.carried) {
    return "records no longer carried";
  }

  if (entry.previousVersion === null) {
    return "first version";
  }

  if (!entry.compared) {
    return "the version before it is no longer carried";
  }

  const parts = [
    entry.changed ? `${entry.changed.toLocaleString()} changed` : null,
    entry.added ? `${entry.added.toLocaleString()} added` : null,
    entry.removed ? `${entry.removed.toLocaleString()} removed` : null,
  ].filter((part): part is string => part !== null);
  return parts.length > 0 ? parts.join(" · ") : "no changes";
}

/** One version in the list: when it was captured and what it changed. */
function VersionRow({ entry, selected, showRepo, onSelect }: {
  entry: DeliveryCacheHistoryEntry;
  selected: boolean;
  showRepo: boolean;
  onSelect: () => void;
}) {
  const { version } = entry;
  return (
    <button
      type="button"
      onClick={onSelect}
      className={cn(
        "flex flex-col gap-0.5 rounded-lg border px-3 py-2 text-left transition-colors hover:border-primary/40",
        selected ? "border-primary bg-primary/5" : "border-border",
      )}
      data-testid="delivery-cache-history-version"
    >
      <span className="flex items-center gap-2">
        <span className="font-mono text-[13px]">{version.version}</span>
        {version.current && <Badge variant="secondary" className="text-[10px]">current</Badge>}
      </span>
      <span className="text-[11px] text-muted-foreground">
        {version.capturedUtc ? new Date(version.capturedUtc).toLocaleString() : "never captured"}
        {showRepo ? ` · ${version.repoName}` : ""}
      </span>
      <span className={cn("text-[12px]", changedSomething(entry) ? "text-foreground" : "text-muted-foreground")}>
        {describe(entry)}
      </span>
    </button>
  );
}

/** What differs about one record: the values that moved for a changed record, everything it held otherwise. */
function Difference({ row }: { row: DeliveryCacheDiffItem }) {
  if (row.change !== "changed") {
    const values = row.change === "added" ? row.after : row.before;
    return (
      <span className={cn("font-mono text-[12px]", row.change === "removed" && "text-muted-foreground line-through")}>
        {cachedFieldsText(values)}
      </span>
    );
  }

  if (row.changedFields.length === 0) {
    return <span className="text-[12px] text-muted-foreground">the captured values differ; open the row to see both sides</span>;
  }

  return (
    <span className="flex flex-col gap-0.5 font-mono text-[12px]">
      {row.changedFields.map((name) => (
        <span key={name}>
          <span className="text-muted-foreground">{name}: </span>
          <span className="text-muted-foreground line-through">{cachedText(row.before?.[name])}</span>
          <span className="px-1">to</span>
          <span>{cachedText(row.after?.[name])}</span>
        </span>
      ))}
    </span>
  );
}

const columns: Column<DeliveryCacheDiffItem>[] = [
  {
    id: "change",
    header: "Change",
    render: (row) => <Badge variant="secondary" className={changeTone[row.change]}>{row.change}</Badge>,
  },
  { id: "type", header: "Type", render: (row) => <Badge variant="outline">{row.typeName}</Badge> },
  { id: "recordId", header: "OSDU id", render: (row) => <TruncatedText text={row.recordId} mono maxWidth={320} /> },
  { id: "difference", header: "What differs", render: (row) => <Difference row={row} /> },
];

/** One side of a record in the detail sheet: its captured values, or that the version does not hold it. */
function Side({ title, values, testId }: { title: string; values: Record<string, unknown> | null; testId: string }) {
  return (
    <div className="flex min-w-0 flex-col gap-2" data-testid={testId}>
      <h3 className="font-mono text-[12px] text-muted-foreground">{title}</h3>
      {values === null
        ? <p className="text-[13px] text-muted-foreground">This version does not hold the record.</p>
        : <CodeView value={JSON.stringify(values, null, 2)} language="json" height={420} />}
    </div>
  );
}

/** The changes one version made to the cache (to the type picked, when one is), compared with the version before it. */
function VersionChanges({ entry, type }: { entry: DeliveryCacheHistoryEntry; type: string | null }) {
  const [change, setChange] = useState<ChangeFilter>(ALL);
  const [search, setSearch] = useState("");
  const [row, setRow] = useState<DeliveryCacheDiffItem | null>(null);
  const { version, previousVersion } = entry;

  const header = (
    <div className="flex flex-col gap-0.5">
      <h3 className="text-[14px] font-medium">
        {type ? <>Changes to <span className="font-mono">{type}</span> in </> : "Changes in "}
        <span className="font-mono">{version.version}</span>
      </h3>
      {previousVersion && (
        <p className="text-[12px] text-muted-foreground">
          compared with <span className="font-mono">{previousVersion}</span>, the version captured before it
        </p>
      )}
    </div>
  );

  if (!entry.compared || previousVersion === null) {
    const reason = !version.carried
      ? "The catalog no longer carries the records of this version, so its changes cannot be listed. The snapshot files still hold it."
      : previousVersion === null
        ? "This is the first version, so there is nothing before it to compare with."
        : "The catalog no longer carries the records of the version before it, so the changes cannot be listed. The snapshot files still hold both.";
    return (
      <div className="flex flex-col gap-3" data-testid="delivery-cache-history-detail">
        {header}
        <Card className="gap-2 p-3 text-[13px] text-muted-foreground" data-testid="delivery-cache-history-uncomparable">{reason}</Card>
      </div>
    );
  }

  const count = (filter: ChangeFilter) => (filter === ALL ? totalOf(entry) : entry[filter] ?? 0).toLocaleString();
  const scope = { repoId: version.repoId, from: previousVersion, to: version.version, type: type ?? undefined };

  return (
    <div className="flex flex-col gap-2" data-testid="delivery-cache-history-detail">
      {header}
      <p className="text-[12px] text-muted-foreground">
        A change needs a decision only when a record already delivered to OSDU was built from it; those are listed under
        Approvals.
      </p>

      <div className="flex flex-wrap items-center gap-2">
        <div className="flex items-center gap-1" data-testid="delivery-cache-history-summary">
          {changeFilters.map((filter) => (
            <Button
              key={filter.value}
              variant={change === filter.value ? "secondary" : "ghost"}
              size="sm"
              className="h-7"
              onClick={() => setChange(filter.value)}
              data-testid={`delivery-cache-history-change-${filter.value}`}
            >
              {filter.label}
              <span className="ml-1 font-mono tabular-nums text-muted-foreground">{count(filter.value)}</span>
            </Button>
          ))}
        </div>
        <SearchInput
          value={search}
          onChange={setSearch}
          placeholder="Search ids and cached values"
          label="Search the changes"
          className="ml-auto w-full max-w-xs"
          testId="delivery-cache-history-search"
        />
      </div>

      <PagedTable
        queryKey={["delivery", "cache", "diff", "items", scope, search, change]}
        fetchPage={(page, pageSize) => deliveryApi
          .cacheDiff({ ...scope, search: search || undefined, change: change === ALL ? undefined : change, page, pageSize })
          .then((result) => result.items)}
        columns={columns}
        rowKey={(item) => `${item.repoId}:${item.typeName}:${item.recordId}`}
        onRowClick={setRow}
        emptyMessage={search || change !== ALL
          ? "No changes match these filters."
          : type
            ? `This version holds the same ${type} records with the same values as the one before it.`
            : "This version holds the same cached records with the same values as the one before it."}
        data-testid="delivery-cache-history-table"
      />

      <Sheet open={row !== null} onOpenChange={(open) => { if (!open) { setRow(null); } }}>
        <SheetContent className="w-full gap-0 sm:max-w-4xl" data-testid="delivery-cache-history-record">
          <SheetHeader>
            <SheetTitle>{row ? `${row.typeName} ${row.change}` : "Cached record"}</SheetTitle>
            <SheetDescription>{row ? `${row.entityType} · ${row.recordId}` : "Loading."}</SheetDescription>
          </SheetHeader>
          {row && (
            <div className="grid flex-1 gap-3 overflow-y-auto px-4 pb-4 md:grid-cols-2">
              <Side title={`Before, at ${previousVersion}`} values={row.before} testId="delivery-cache-history-before" />
              <Side title={`After, at ${version.version}`} values={row.after} testId="delivery-cache-history-after" />
            </div>
          )}
        </SheetContent>
      </Sheet>
    </div>
  );
}

/**
 * The cache's version history: every snapshot version, newest first, with what it changed compared with the version of
 * the same repository captured before it, and the changes themselves for the version picked. Picking a type in the
 * sidebar narrows the history to the versions that changed that type; the others fold under a toggle. It covers the
 * whole cache, where Approvals covers only the changes that reach records already delivered.
 */
export function DeliveryCacheHistory({ repoId, type }: { repoId: string | undefined; type: string | null }) {
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [showOthers, setShowOthers] = useState(false);
  const history = useQuery({
    queryKey: ["delivery", "cache", "history", repoId, type],
    queryFn: () => deliveryApi.cacheHistory(repoId, type ?? undefined),
  });

  if (history.isPending) {
    return <Skeleton className="h-24 w-full rounded-lg" />;
  }

  if (history.isError) {
    return isApiError(history.error)
      ? <CorrelationError error={history.error} />
      : <p className="text-[13px] text-destructive">{String(history.error)}</p>;
  }

  const entries = history.data;
  if (entries.length === 0) {
    return (
      <Card className="gap-2 p-3 text-[13px] text-muted-foreground" data-testid="delivery-cache-history-empty">
        No snapshot version has been captured yet. Running the retrieval flow that declares the cache captures the first one.
      </Card>
    );
  }

  const changedType = type ? entries.filter(changedSomething) : entries;
  const others = entries.length - changedType.length;
  const shown = type && !showOthers ? changedType : entries;
  const showRepo = new Set(entries.map((e) => e.version.repoId)).size > 1;

  // A selection can go stale when the filters change; the newest version shown is the default.
  const selected = shown.find((e) => keyOf(e) === selectedKey) ?? shown[0];

  return (
    <div className="grid gap-4 xl:grid-cols-[260px_minmax(0,1fr)]" data-testid="delivery-cache-history">
      <div className="flex flex-col gap-2">
        <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
          {type ? `Versions that changed ${type}` : "Versions"}
        </h3>
        {type && changedType.length === 0 && !showOthers && (
          <p className="text-[12px] text-muted-foreground" data-testid="delivery-cache-history-none">
            No version changed {type}.
          </p>
        )}
        {shown.map((entry) => (
          <VersionRow
            key={keyOf(entry)}
            entry={entry}
            selected={selected !== undefined && keyOf(entry) === keyOf(selected)}
            showRepo={showRepo}
            onSelect={() => setSelectedKey(keyOf(entry))}
          />
        ))}
        {type && others > 0 && (
          <Button
            variant="ghost"
            size="sm"
            className="h-7 justify-start text-muted-foreground"
            onClick={() => setShowOthers(!showOthers)}
            data-testid="delivery-cache-history-toggle-others"
          >
            {showOthers ? "Show only the versions that changed it" : `Show the other ${others} version${others === 1 ? "" : "s"}`}
          </Button>
        )}
      </div>
      {selected
        ? <VersionChanges key={`${keyOf(selected)}:${type ?? ""}`} entry={selected} type={type} />
        : (
          <Card className="gap-2 p-3 text-[13px] text-muted-foreground" data-testid="delivery-cache-history-detail">
            {type} holds the same records with the same values in every version the catalog carries.
          </Card>
        )}
    </div>
  );
}
