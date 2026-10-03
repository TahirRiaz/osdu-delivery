import { useMemo } from "react";
import { useInfiniteQuery } from "@tanstack/react-query";
import { ArrowDownWideNarrow, ChevronRight, Info, Loader2, RefreshCw, SearchX, X } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuLabel, DropdownMenuRadioGroup, DropdownMenuRadioItem, DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Skeleton } from "@/components/ui/skeleton";
import { CopyButton } from "@/components/CopyButton";
import { EmptyState } from "@/components/EmptyState";
import { IconAction } from "@/components/IconAction";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import {
  EXPLORER_PAGE, EXPLORER_WINDOW, explorerApi, type ExplorerAnswer, type ExplorerFilter, type ExplorerHit, type ExplorerPage, type ExplorerReading,
  type ExplorerSearchRequest, type ExplorerSort,
} from "../../../api/explorer";
import { isRecordReference } from "../osduDocument";
import { idParts } from "../osduRecordModel";
import { RecordName } from "../RecordName";
import { runComputeTask } from "../useComputeTask";
import { ExplorerGroupBy } from "./ExplorerGroupBy";
import { ExplorerGrid, type GridColumn } from "./ExplorerGrid";
import { ExplorerProblem } from "./ExplorerProblem";
import { counted, fieldLabel, kindParts, SORT_LABELS, type ExplorerScope } from "./explorerModel";

/** How a search was read, as a word after the count; nothing for the plain cases. */
const READINGS: Partial<Record<ExplorerReading, string>> = {
  id: "by id",
  idPrefix: "ids starting with it",
  lucene: "Lucene query",
  mentions: "mentioning the record",
};

/** One step of the place in view: the partition, a group, a type or a kind, the last the current one. */
function Crumb({ label, current, onClick, mono = false, testId }: { label: string; current: boolean; onClick: () => void; mono?: boolean; testId: string }) {
  return current
    ? <span className={cn("min-w-0 truncate font-medium text-foreground", mono && "font-mono text-[12px]")} aria-current="location" data-testid={testId}>{label}</span>
    : (
      <button type="button" onClick={onClick} className={cn("min-w-0 truncate text-muted-foreground hover:text-foreground hover:underline", mono && "font-mono text-[12px]")} data-testid={testId}>
        {label}
      </button>
    );
}

/**
 * Where the records in view are, as steps to broaden by: the partition (every type), the group, the type, the kind. The
 * same place leads the record view, so a record is always read as somewhere in its partition.
 */
export function ScopeCrumbs({ partition, scope, onScope, last = true }: {
  partition: string | null;
  scope: ExplorerScope;
  onScope: (scope: ExplorerScope) => void;
  /** Whether the scope is the last step shown; a record view puts the record after it. */
  last?: boolean;
}) {
  const steps: { label: string; scope: ExplorerScope; mono?: boolean }[] = [{ label: partition ?? "OSDU", scope: { level: "all" } }];
  if (scope.level === "group") {
    steps.push({ label: scope.group, scope });
  } else if (scope.level === "type" || scope.level === "kind") {
    const parts = kindParts(scope.level === "type" ? `*:*:${scope.entityType}:*` : scope.kind);
    if (parts.group !== "") {
      steps.push({ label: parts.group, scope: { level: "group", group: parts.group } });
    }

    steps.push({ label: parts.type, scope: { level: "type", entityType: parts.entityType } });
    if (scope.level === "kind") {
      steps.push({ label: `${parts.version} ${parts.authority}:${parts.source}`, scope, mono: true });
    }
  }

  return (
    <>
      {steps.map((step, index) => (
        <span key={`${step.label}:${index}`} className="inline-flex min-w-0 items-center gap-1">
          {index > 0 && <ChevronRight className="size-3.5 shrink-0 text-muted-foreground/50" aria-hidden />}
          <Crumb label={step.label} current={last && index === steps.length - 1} onClick={() => onScope(step.scope)} mono={step.mono} testId="explorer-crumb" />
        </span>
      ))}
    </>
  );
}

/**
 * The records a search finds in the place picked, a page at a time as the grid is scrolled, under one line that says
 * where they are, how many there are and how they were read, with the order and the grouping beside it and the values
 * the records are narrowed to under it. A partition of millions reads as fast as one of hundreds: each page is one query
 * of the search index, and the total is the index's own count. The service pages through the first ten thousand records
 * a query matches; past them the reader narrows, and the foot of the grid says so.
 */
export function ExplorerResults({ partition, request, scope, onScope, onOpen, onFilters, onSort, onSearchEverywhere }: {
  partition: string | null;
  /** The search, without its page. */
  request: ExplorerSearchRequest & { sort: ExplorerSort; filters: ExplorerFilter[] };
  scope: ExplorerScope;
  onScope: (scope: ExplorerScope) => void;
  onOpen: (hit: ExplorerHit) => void;
  onFilters: (filters: ExplorerFilter[]) => void;
  onSort: (sort: ExplorerSort) => void;
  /** Takes the search out of the type in view, to every type. */
  onSearchEverywhere: () => void;
}) {
  const pages = useInfiniteQuery({
    queryKey: ["explorer", "search", partition, request],
    queryFn: ({ pageParam, signal }) => runComputeTask<ExplorerAnswer<ExplorerPage>>(
      () => explorerApi.search(partition, { ...request, offset: pageParam, limit: EXPLORER_PAGE }),
      signal,
    ),
    initialPageParam: 0,
    getNextPageParam: (last) => {
      const next = last.answer.offset + EXPLORER_PAGE;
      return last.answer.refusal === null && last.answer.hits.length > 0 && next < Math.min(last.answer.total, EXPLORER_WINDOW) ? next : undefined;
    },
    staleTime: 60_000,
    gcTime: 10 * 60_000,
    retry: false,
    refetchOnWindowFocus: false,
  });

  const first = pages.data?.pages[0]?.answer;
  // A record can move between two pages while they are read (the index changes under the search); it is listed once.
  const hits = useMemo(() => {
    const byId = new Map<string, ExplorerHit>();
    for (const hit of (pages.data?.pages ?? []).flatMap((page) => page.answer.hits)) {
      if (!byId.has(hit.id)) {
        byId.set(hit.id, hit);
      }
    }

    return [...byId.values()];
  }, [pages.data]);
  const notes = [...new Set((pages.data?.pages ?? []).flatMap((page) => page.answer.notes))];
  const oneType = scope.level === "type" || scope.level === "kind";
  const reading = first === undefined ? undefined : READINGS[first.reading];
  const capped = first !== undefined && first.total > EXPLORER_WINDOW && !pages.hasNextPage && hits.length > 0;

  const columns: GridColumn<ExplorerHit>[] = [
    {
      id: "name",
      header: "Name",
      flex: 2.2,
      render: (hit) => (hit.name !== null
        ? <span className="min-w-0 truncate" title={`${hit.name}\n${hit.nameField ?? ""}`}>{hit.name}</span>
        : <span className="min-w-0 truncate font-mono text-[12px] text-muted-foreground" title={hit.id}>{idParts(hit.id).unique}</span>),
    },
    ...(oneType ? [] : [{
      id: "type",
      header: "Type",
      flex: 1.1,
      render: (hit: ExplorerHit) => {
        const parts = kindParts(hit.kind ?? "");
        return (
          <span className="min-w-0 truncate" title={hit.kind ?? undefined}>
            {parts.type}
            <span className="ml-1.5 font-mono text-[11px] text-muted-foreground">{parts.version}</span>
          </span>
        );
      },
    } satisfies GridColumn<ExplorerHit>]),
    {
      id: "id",
      header: "Id",
      flex: 1.6,
      render: (hit) => (
        <span className="flex min-w-0 flex-1 items-center gap-1">
          {/* The end of an id is what tells it apart, so a narrow cell keeps the end and cuts the start. */}
          <span dir="rtl" className="min-w-0 flex-1 truncate text-left font-mono text-[12px] text-muted-foreground" title={hit.id}>
            <bdi dir="ltr">{idParts(hit.id).unique}</bdi>
          </span>
          <span className="opacity-0 transition-opacity group-hover/row:opacity-100 focus-within:opacity-100" onClick={(event) => event.stopPropagation()}>
            <CopyButton iconOnly label="Copy the id" text={hit.id} testId="explorer-copy-id" />
          </span>
        </span>
      ),
    },
    {
      id: "modified",
      header: "Changed",
      width: 136,
      align: "right",
      render: (hit) => (
        <span className="min-w-0 truncate text-[12px] text-muted-foreground" title={hit.modifyUser ?? hit.createUser ?? undefined}>
          <RelativeTime value={hit.modifyTime ?? hit.createTime} absolute={false} />
        </span>
      ),
    },
  ];

  const sortMenu = (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="sm" className="h-8 gap-1.5 px-2 text-[13px] font-normal" data-testid="explorer-sort">
          <ArrowDownWideNarrow />
          {request.sort === "relevance" && !request.text ? "Index order" : SORT_LABELS[request.sort]}
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        <DropdownMenuLabel className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Order</DropdownMenuLabel>
        <DropdownMenuSeparator />
        <DropdownMenuRadioGroup value={request.sort} onValueChange={(value) => onSort(value as ExplorerSort)}>
          {(Object.keys(SORT_LABELS) as ExplorerSort[]).map((sort) => (
            <DropdownMenuRadioItem key={sort} value={sort} className="text-[13px]" data-testid="explorer-sort-option">
              {sort === "relevance" && !request.text ? "Index order" : SORT_LABELS[sort]}
            </DropdownMenuRadioItem>
          ))}
        </DropdownMenuRadioGroup>
      </DropdownMenuContent>
    </DropdownMenu>
  );

  let body;
  if (pages.isPending) {
    body = (
      <div className="flex flex-col gap-2 p-3" data-testid="explorer-results-loading">
        {Array.from({ length: 10 }, (_, index) => <Skeleton key={index} className="h-5 rounded" style={{ width: `${70 + ((index * 13) % 30)}%` }} />)}
      </div>
    );
  } else if (pages.isError) {
    body = (
      <div className="flex flex-col items-start gap-3 p-4">
        <ExplorerProblem error={pages.error} />
        <Button variant="outline" size="sm" onClick={() => void pages.refetch()}>Try again</Button>
      </div>
    );
  } else if (first?.refusal) {
    body = (
      <Alert variant="destructive" className="m-3 w-auto" data-testid="explorer-refused">
        <AlertTitle>The search service refused this search</AlertTitle>
        <AlertDescription className="whitespace-pre-wrap">
          {first.refusal}
          {request.lucene ? "\nA Lucene query is sent as written: check its fields, quotes and parentheses." : ""}
        </AlertDescription>
      </Alert>
    );
  } else if (hits.length === 0) {
    body = (
      <EmptyState
        icon={<SearchX />}
        title="Nothing found"
        description={request.text
          ? `No record ${oneType || scope.level === "group" ? "of this type " : ""}holds "${request.text}"${request.filters.length > 0 ? " with these values" : ""}.`
          : "No record here holds these values."}
        action={(
          <div className="flex flex-wrap justify-center gap-2">
            {scope.level !== "all" && <Button variant="outline" size="sm" onClick={onSearchEverywhere} data-testid="explorer-search-everywhere">Search every type</Button>}
            {request.filters.length > 0 && <Button variant="outline" size="sm" onClick={() => onFilters([])}>Clear the values</Button>}
          </div>
        )}
        data-testid="explorer-empty"
      />
    );
  } else {
    body = (
      <ExplorerGrid
        rows={hits}
        columns={columns}
        rowKey={(hit) => hit.id}
        onOpen={onOpen}
        onNearEnd={() => { if (pages.hasNextPage && !pages.isFetchingNextPage) { void pages.fetchNextPage(); } }}
        label="Records"
        testId="explorer-grid"
        footer={(pages.isFetchingNextPage || capped) && (
          <div className="flex h-9 items-center justify-center gap-2 text-[12px] text-muted-foreground" data-testid="explorer-grid-more">
            {pages.isFetchingNextPage
              ? <><Loader2 className="size-3.5 animate-spin" />Reading the next records</>
              : `The search service lists the first ${EXPLORER_WINDOW.toLocaleString("en-US")}. Narrow by type, text or a value to reach the others.`}
          </div>
        )}
      />
    );
  }

  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="explorer-results">
      <div className="flex min-h-11 flex-wrap items-center gap-x-3 gap-y-1 border-b px-3 py-1.5">
        <nav className="flex min-w-0 flex-wrap items-center gap-1 text-[13px]" aria-label="Where the records are" data-testid="explorer-place">
          <ScopeCrumbs partition={partition} scope={scope} onScope={onScope} />
        </nav>
        <span className="flex items-center gap-1.5 text-[12px] text-muted-foreground" data-testid="explorer-count">
          {pages.isPending || (pages.isFetching && !pages.isFetchingNextPage)
            ? <><Loader2 className="size-3.5 animate-spin" />Searching</>
            : first !== undefined && first.refusal === null && (
              <>
                <span className="font-mono tabular-nums text-foreground">{counted(first.total, "record")}</span>
                {reading !== undefined && <span>{reading}</span>}
              </>
            )}
          {notes.length > 0 && (
            <RichTooltip title="What the search did" body={notes.join("\n\n")}>
              <Info className="size-3.5 text-warning" aria-label="What the search did" data-testid="explorer-notes" />
            </RichTooltip>
          )}
          {first?.query && (
            <RichTooltip title="The query sent" body={first.query} mono>
              <span className="inline-flex" onClick={(event) => event.stopPropagation()}>
                <CopyButton iconOnly label="Copy the query sent to the search service" text={first.query} testId="explorer-copy-query" />
              </span>
            </RichTooltip>
          )}
        </span>
        <div className="ml-auto flex items-center gap-1">
          {sortMenu}
          <ExplorerGroupBy partition={partition} base={request} onFilter={(filter) => onFilters([...request.filters.filter((f) => f.path !== filter.path || f.value !== filter.value), filter])} />
          <IconAction label="Read again from OSDU" icon={<RefreshCw />} variant="ghost" className="size-8" onClick={() => void pages.refetch()} data-testid="explorer-refresh" />
        </div>
      </div>
      {request.filters.length > 0 && (
        <div className="flex flex-wrap items-center gap-1.5 border-b px-3 py-1.5" data-testid="explorer-filters">
          {request.filters.map((filter) => (
            <span key={`${filter.path}=${filter.value}`} className="inline-flex max-w-full items-center gap-1 rounded-md border bg-muted/60 py-0.5 pr-1 pl-2 text-[12px]" data-testid="explorer-filter">
              <span className="shrink-0 font-mono text-[11px] text-muted-foreground">{fieldLabel(filter.path)}</span>
              <span className="min-w-0 truncate" title={filter.value}>{isRecordReference(filter.value) ? <RecordName id={filter.value} /> : filter.value}</span>
              <button
                type="button"
                className="rounded-sm p-0.5 text-muted-foreground hover:bg-accent hover:text-foreground"
                onClick={() => onFilters(request.filters.filter((f) => f !== filter))}
                aria-label={`Stop narrowing to ${fieldLabel(filter.path)} ${filter.value}`}
              >
                <X className="size-3" />
              </button>
            </span>
          ))}
          {request.filters.length > 1 && (
            <button type="button" className="px-1 text-[12px] text-muted-foreground hover:text-foreground hover:underline" onClick={() => onFilters([])}>Clear all</button>
          )}
        </div>
      )}
      {body}
      <span className="sr-only" aria-live="polite">{first === undefined ? "" : counted(first.total, "record")}</span>
    </div>
  );
}
