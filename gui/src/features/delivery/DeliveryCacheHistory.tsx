import { useCallback, useEffect, useState } from "react";
import { Link as RouterLink } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ArrowRight, GitCommitHorizontal } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import {
  deliveryApi, type DeliveryCacheChange, type DeliveryCacheDiffItem, type DeliveryCacheHistoryEntry,
} from "../../api/delivery";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { EmptyState } from "../../components/EmptyState";
import { PagedTable } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { SearchInput } from "../../components/SearchInput";
import { TruncatedText } from "../../components/TruncatedText";
import { useOwnedPanel } from "../../layout/workbench/useOwnedPanel";
import { cachedFieldsText, cachedText } from "./cacheFormat";
import { RecordId } from "./DeliveryCacheRecords";

const ALL = "all";

/** The bottom panel content ids this surface owns. */
const PANEL = "cache-changes:";

type ChangeFilter = DeliveryCacheChange | typeof ALL;

const changeFilters: { value: ChangeFilter; label: string }[] = [
  { value: ALL, label: "All" },
  { value: "changed", label: "Changed" },
  { value: "added", label: "Added" },
  { value: "removed", label: "Removed" },
];

const changeTone: Record<DeliveryCacheChange, string> = {
  changed: "bg-info/15 text-info",
  added: "bg-success/15 text-success",
  removed: "bg-destructive/15 text-destructive",
};

const countTone: Record<DeliveryCacheChange, string> = {
  changed: "text-info",
  added: "text-success",
  removed: "text-destructive",
};

function keyOf(entry: DeliveryCacheHistoryEntry): string {
  return `${entry.version.cache}:${entry.version.version}`;
}

function totalOf(entry: DeliveryCacheHistoryEntry): number {
  return entry.changed + entry.added + entry.removed;
}

/** Whether the version changed something (in the type picked, when one is). */
function changedSomething(entry: DeliveryCacheHistoryEntry): boolean {
  return totalOf(entry) > 0;
}

/** What one version changed, as tinted counts. */
function ChangeCounts({ entry }: { entry: DeliveryCacheHistoryEntry }) {
  if (entry.before === null) {
    return (
      <span className="text-[12px] text-muted-foreground">
        first version{entry.added > 0 ? `, ${entry.added.toLocaleString()} records arrived` : ""}
      </span>
    );
  }

  const parts = (["changed", "added", "removed"] as const).filter((kind) => entry[kind] > 0);
  if (parts.length === 0) {
    return <span className="text-[12px] text-muted-foreground">no changes</span>;
  }

  return (
    <span className="inline-flex gap-3 font-mono text-[12px] tabular-nums">
      {parts.map((kind) => (
        <span key={kind} className={countTone[kind]}>
          {entry[kind].toLocaleString()} {kind}
        </span>
      ))}
    </span>
  );
}

/** What differs about one record: the values that moved for a changed record, everything it held otherwise. */
function Difference({ row }: { row: DeliveryCacheDiffItem }) {
  if (row.change !== "changed") {
    const values = row.change === "added" ? row.after : row.before;
    return (
      <TruncatedText
        text={cachedFieldsText(values)}
        mono
        maxWidth={480}
        className={row.change === "removed" ? "text-muted-foreground line-through" : undefined}
      />
    );
  }

  if (row.changedFields.length === 0) {
    return <span className="text-[12px] text-muted-foreground">the captured values differ; open the row to see both sides</span>;
  }

  return (
    <span className="flex flex-col gap-0.5 font-mono text-[12px]">
      {row.changedFields.map((name) => (
        <span key={name} className="inline-flex items-center gap-1.5">
          <span className="text-[11px] text-muted-foreground">{name}</span>
          <TruncatedText text={cachedText(row.before?.[name])} mono maxWidth={200} className="text-muted-foreground line-through" />
          <ArrowRight className="size-3.5 shrink-0 text-muted-foreground" />
          <TruncatedText text={cachedText(row.after?.[name])} mono maxWidth={200} />
        </span>
      ))}
    </span>
  );
}

const changeColumn: Column<DeliveryCacheDiffItem> = {
  id: "change",
  header: "Change",
  render: (row) => <Badge variant="secondary" className={changeTone[row.change]}>{row.change}</Badge>,
};

const typeColumn: Column<DeliveryCacheDiffItem> = {
  id: "type", header: "Type", render: (row) => <span className="font-mono text-[12px]">{row.typeName}</span>,
};

const recordColumns: Column<DeliveryCacheDiffItem>[] = [
  { id: "recordId", header: "OSDU id", render: (row) => <RecordId id={row.recordId} maxWidth={300} /> },
  { id: "difference", header: "What differs", render: (row) => <Difference row={row} /> },
];

/** One side of a record in the detail sheet: its captured values, or that the version does not hold it. */
function Side({ title, version, values, testId }: {
  title: string;
  version: string;
  values: Record<string, unknown> | null;
  testId: string;
}) {
  return (
    <div className="flex min-w-0 flex-col gap-2" data-testid={testId}>
      <h3 className="flex items-baseline gap-2 text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
        {title}
        <span className="font-mono text-[11px] normal-case tracking-normal text-foreground">{version}</span>
      </h3>
      {values === null
        ? (
          <Card className="gap-0 rounded-lg p-0">
            <EmptyState title="This version does not hold the record" />
          </Card>
        )
        : <CodeView value={JSON.stringify(values, null, 2)} language="json" height={420} />}
    </div>
  );
}

/**
 * The changes one version made to the cache (to the type in scope, when one is), compared with the version before
 * it: the bottom panel's content once a version is picked.
 */
function VersionChanges({ entry, type }: { entry: DeliveryCacheHistoryEntry; type: string | null }) {
  const [change, setChange] = useState<ChangeFilter>(ALL);
  const [search, setSearch] = useState("");
  const [row, setRow] = useState<DeliveryCacheDiffItem | null>(null);
  const { version, before } = entry;

  const header = (
    <div className="flex flex-wrap items-baseline gap-x-3 gap-y-0.5">
      <h3 className="text-[13px] font-medium">
        {type ? <>Changes to <span className="font-mono">{type}</span> in </> : "Changes in "}
        <span className="font-mono">{version.version}</span>
      </h3>
      {before && (
        <span className="text-[12px] text-muted-foreground">
          compared with <span className="font-mono">{before}</span>, the version captured before it
        </span>
      )}
    </div>
  );

  if (before === null) {
    return (
      <div className="flex flex-col gap-3 p-3" data-testid="delivery-cache-history-detail">
        {header}
        <Card className="gap-0 rounded-lg p-0">
          <EmptyState
            icon={<GitCommitHorizontal />}
            title="Nothing to compare"
            description="This is the first version of the cache, so there is nothing before it to compare with. Its records are under Records."
            data-testid="delivery-cache-history-uncomparable"
          />
        </Card>
      </div>
    );
  }

  const count = (filter: ChangeFilter) => (filter === ALL ? totalOf(entry) : entry[filter]).toLocaleString();
  const scope = { cache: version.cache, from: before, to: version.version, type: type ?? undefined };

  return (
    <div className="flex flex-col gap-2 p-3" data-testid="delivery-cache-history-detail">
      <div className="flex flex-col flex-wrap gap-2 sm:flex-row sm:items-center">
        {header}
        <div className="grow" />
        <SearchInput
          value={search}
          onChange={setSearch}
          placeholder="Search ids and cached values"
          label="Search the changes"
          className="sm:w-64"
          testId="delivery-cache-history-search"
        />
        <ToggleGroup
          type="single"
          variant="outline"
          size="sm"
          value={change}
          onValueChange={(value) => {
            if (value !== "") {
              setChange(value as ChangeFilter);
            }
          }}
          aria-label="Kind of change"
          data-testid="delivery-cache-history-summary"
        >
          {changeFilters.map((filter) => (
            <ToggleGroupItem
              key={filter.value}
              value={filter.value}
              className="h-8 gap-1.5 text-[13px]"
              data-testid={`delivery-cache-history-change-${filter.value}`}
            >
              {filter.label}
              <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{count(filter.value)}</span>
            </ToggleGroupItem>
          ))}
        </ToggleGroup>
      </div>

      <PagedTable
        queryKey={["delivery", "cache", "diff", "items", scope, search, change]}
        fetchPage={(page, pageSize) => deliveryApi
          .cacheDiff({ ...scope, search: search || undefined, change: change === ALL ? undefined : change, page, pageSize })
          .then((result) => result.items)}
        // The type column says nothing a scoped listing does not already say in its title.
        columns={type === null ? [changeColumn, typeColumn, ...recordColumns] : [changeColumn, ...recordColumns]}
        rowKey={(item) => `${item.typeName}:${item.recordId}`}
        onRowClick={setRow}
        emptyMessage={search || change !== ALL
          ? "No changes match these filters."
          : type
            ? `This version holds the same ${type} records with the same values as the one before it.`
            : "This version holds the same cached records with the same values as the one before it."}
        data-testid="delivery-cache-history-table"
      />

      <Sheet open={row !== null} onOpenChange={(open) => { if (!open) { setRow(null); } }}>
        <SheetContent
          className="w-full gap-0 sm:max-w-4xl"
          onOpenAutoFocus={(event) => { event.preventDefault(); (event.currentTarget as HTMLElement).focus(); }}
          data-testid="delivery-cache-history-record"
        >
          {row !== null && (
            <>
              <SheetHeader className="border-b border-border">
                <SheetTitle className="flex flex-wrap items-center gap-2">
                  <span className="font-mono">{row.typeName}</span>
                  <Badge variant="secondary" className={changeTone[row.change]}>{row.change}</Badge>
                </SheetTitle>
                <SheetDescription className="font-mono text-[12px] text-foreground">{row.recordId}</SheetDescription>
                <p className="text-[12px] text-muted-foreground">{row.entityType}</p>
              </SheetHeader>
              <div className="grid flex-1 gap-4 overflow-y-auto p-4 md:grid-cols-2">
                <Side title="Before" version={before} values={row.before} testId="delivery-cache-history-before" />
                <Side title="After" version={version.version} values={row.after} testId="delivery-cache-history-after" />
              </div>
            </>
          )}
        </SheetContent>
      </Sheet>
    </div>
  );
}

/**
 * One cache's version history: every version, newest first, with what captured it (the run and who asked, or the import
 * from files) and what it changed compared with the version captured before it. Picking a version raises its changes in
 * the workbench bottom panel, where they can be filtered, searched and opened record by record while the list stays in
 * view. With a type in scope the counts are that type's alone, and the versions that left it untouched can be folded
 * away. It covers the whole cache, where Approvals covers only the changes that reach records already delivered.
 */
export function DeliveryCacheHistory({ cache, type }: { cache: string; type: string | null }) {
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [onlyChanged, setOnlyChanged] = useState(true);
  const { ownedId, show } = useOwnedPanel(PANEL);
  const history = useQuery({
    queryKey: ["delivery", "cache", "history", cache, type],
    queryFn: () => deliveryApi.cacheHistory(cache, type ?? undefined),
  });

  const entries = history.data ?? [];
  const selected = entries.find((entry) => keyOf(entry) === selectedKey);

  const raise = useCallback((entry: DeliveryCacheHistoryEntry) => show(
    `${keyOf(entry)}:${type ?? ""}`,
    `Changes · ${entry.version.version}`,
    <VersionChanges entry={entry} type={type} />,
  ), [show, type]);

  // While the panel shows this surface's content, keep it on the version picked with the data as it is now: a
  // scope change or a refetch re-raises it rather than leaving a stale copy open.
  const open = ownedId !== null;
  useEffect(() => {
    if (open && selected !== undefined) {
      raise(selected);
    }
  }, [open, selected, raise]);

  if (history.isPending) {
    return <Skeleton className="h-40 w-full rounded-lg" />;
  }

  if (history.isError) {
    return isApiError(history.error)
      ? <CorrelationError error={history.error} />
      : <p className="text-[13px] text-destructive">{String(history.error)}</p>;
  }

  if (entries.length === 0) {
    return (
      <Card className="gap-0 rounded-lg p-0">
        <EmptyState
          icon={<GitCommitHorizontal />}
          title="No version captured yet"
          description="Refreshing the cache captures the first one: run its cache flow with the refresh operation."
          data-testid="delivery-cache-history-empty"
        />
      </Card>
    );
  }

  const changedType = type ? entries.filter(changedSomething) : entries;
  const others = entries.length - changedType.length;
  const rows = type && onlyChanged ? changedType : entries;
  const highlighted = open ? selectedKey : null;

  const columns: Column<DeliveryCacheHistoryEntry>[] = [
    {
      id: "version",
      header: "Version",
      render: (entry) => (
        <span className="inline-flex items-center gap-2">
          <span
            aria-hidden
            className={cn("size-2 shrink-0 rounded-full", entry.version.current ? "bg-primary" : "bg-muted-foreground/40")}
          />
          <span className="font-mono text-[12.5px]">{entry.version.version}</span>
          {entry.version.current && (
            <span className="rounded-sm bg-primary/12 px-1.5 text-[11px] font-medium text-primary">current</span>
          )}
        </span>
      ),
    },
    { id: "captured", header: "Captured", render: (entry) => <RelativeTime value={entry.version.capturedUtc} /> },
    {
      id: "capturedBy",
      header: "Captured by",
      fill: true,
      floor: 160,
      render: (entry) => (
        <span className="flex flex-col">
          {entry.version.runId !== null
            ? (
              <RouterLink
                to={`/runs/${entry.version.runId}`}
                className="font-mono text-[12px] text-primary hover:underline"
                onClick={(event) => event.stopPropagation()}
                data-testid="delivery-cache-history-run"
              >
                run {entry.version.runId.slice(0, 8)}
              </RouterLink>
            )
            : <TruncatedText text={entry.version.origin} maxWidth={1200} className="text-[12px]" />}
          <span className="text-[11px] text-muted-foreground">{entry.version.capturedBy}</span>
        </span>
      ),
    },
    {
      id: "records",
      header: "Records",
      align: "right",
      render: (entry) => <span className="font-mono tabular-nums">{entry.version.items.toLocaleString()}</span>,
    },
    { id: "changes", header: type ? `Changes to ${type}` : "Changes", render: (entry) => <ChangeCounts entry={entry} /> },
  ];

  return (
    <div className="flex flex-col gap-2" data-testid="delivery-cache-history">
      <div className="flex flex-wrap items-center gap-2 text-[12px] text-muted-foreground">
        <span>
          {rows.length.toLocaleString()} version{rows.length === 1 ? "" : "s"}; pick one to see what it changed.
        </span>
        {type && changedType.length === 0 && (
          <span data-testid="delivery-cache-history-none">No version changed {type}.</span>
        )}
        {type && others > 0 && (
          <Button
            variant="ghost"
            size="xs"
            className="text-muted-foreground"
            onClick={() => setOnlyChanged(!onlyChanged)}
            data-testid="delivery-cache-history-toggle-others"
          >
            {onlyChanged ? `Show the other ${others} version${others === 1 ? "" : "s"}` : "Show only the versions that changed it"}
          </Button>
        )}
      </div>
      <DataTable
        columns={columns}
        rows={rows}
        rowKey={keyOf}
        onRowClick={(entry) => {
          setSelectedKey(keyOf(entry));
          raise(entry);
        }}
        rowSx={(entry) => (keyOf(entry) === highlighted ? { backgroundColor: "var(--accent)" } : undefined)}
        emptyMessage={type ? `No version changed ${type}.` : "No versions."}
        data-testid="delivery-cache-history-versions"
      />
    </div>
  );
}
