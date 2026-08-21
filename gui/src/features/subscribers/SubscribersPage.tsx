import { useEffect, useMemo, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { MonitorPlay, X } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Sheet, SheetClose, SheetContent, SheetDescription, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "../../api/client";
import { lineageApi } from "../../api/endpoints";
import type { Subscriber } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { EmptyState } from "../../components/EmptyState";
import { activeFilterClass, FilterBar } from "../../components/FilterBar";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { isFollowable, SubscriberDetails } from "./SubscriberDetails";

/** Local debounce for the free-text filter: the list re-queries 400ms after the user stops typing. */
function useDebounced(value: string, delayMs: number): string {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const handle = window.setTimeout(() => setDebounced(value), delayMs);
    return () => window.clearTimeout(handle);
  }, [value, delayMs]);
  return debounced;
}

const subscriberColumns: Column<Subscriber>[] = [
  {
    id: "name",
    header: "Subscriber",
    render: (row) => (
      <span className="flex min-w-0 items-center gap-1.5">
        <MonitorPlay className="size-4 shrink-0 text-muted-foreground" />
        <span className="truncate text-[13px] font-medium">{row.name}</span>
      </span>
    ),
  },
  { id: "type", header: "Type", render: (row) => <Badge variant="secondary">{row.type}</Badge>, width: 130 },
  { id: "owner", header: "Owner", render: (row) => (row.owner === null ? "-" : <Mono>{row.owner}</Mono>) },
  {
    id: "description",
    header: "Description",
    render: (row) => (
      <span className="text-[13px] text-muted-foreground">{row.description ?? "-"}</span>
    ),
  },
  {
    id: "notes",
    header: "Notes",
    // One line in the list, in full on the row's title: a note is often several sentences, and letting it wrap
    // would push every other column off the useful part of the table.
    render: (row) => (
      <span className="block truncate text-[13px] text-muted-foreground" title={row.notes ?? undefined}>
        {row.notes ?? "-"}
      </span>
    ),
  },
  {
    id: "url",
    header: "Location",
    // Where the report lives, so the list answers "open it" as well as "what does it read".
    render: (row) => (
      row.url === null ? <span className="text-[13px] text-muted-foreground">-</span>
        : isFollowable(row.url) ? (
          <a
            href={row.url}
            target="_blank"
            rel="noreferrer"
            onClick={(event) => event.stopPropagation()}
            className="block truncate text-[13px] text-primary hover:underline"
            title={row.url}
          >
            {row.url}
          </a>
        ) : <span className="block truncate font-mono text-[12px]" title={row.url}>{row.url}</span>
    ),
  },
  {
    id: "objects",
    header: "Reads",
    align: "right",
    render: (row) => (
      <span className="tabular-nums">{`${row.objectCount} object${row.objectCount === 1 ? "" : "s"}`}</span>
    ),
    width: 110,
  },
  {
    id: "queries",
    header: "Queries",
    align: "right",
    render: (row) => <span className="tabular-nums">{row.queryCount}</span>,
    width: 90,
  },
];

/**
 * The consumption side of the estate: who reads the warehouse. Every row is a declared data subscriber (a
 * report, workbook, notebook, or application) whose queries were parsed into lineage, so the counts here are
 * what the graph actually proved, not a hand-kept inventory. Opening one shows everything it reads and the
 * queries behind each link; the reverse question, "who consumes this table", is the Consumers tab on any
 * object in the catalog.
 */
export default function SubscribersPage() {
  const [params, setParams] = useSearchParams();
  const selected = params.get("key");
  const [search, setSearch] = useState("");
  const [type, setType] = useState("all");
  const debouncedSearch = useDebounced(search, 400);

  const subscribers = useQuery({
    queryKey: ["subscribers", type, debouncedSearch],
    queryFn: () => lineageApi.subscribers({
      type: type === "all" ? undefined : type,
      search: debouncedSearch === "" ? undefined : debouncedSearch,
    }),
  });

  // The type filter offers exactly the types the estate declares: the field is free text by design (it was a
  // free nvarchar in the legacy model), so a fixed list would hide whatever this estate actually uses.
  const types = useMemo(
    () => [...new Set((subscribers.data ?? []).map((row) => row.type))].sort((a, b) => a.localeCompare(b)),
    [subscribers.data],
  );

  const select = (key: string | null) => {
    const next = new URLSearchParams(params);
    if (key === null) {
      next.delete("key");
    } else {
      next.set("key", key);
    }

    setParams(next, { replace: true });
  };

  return (
    <Page>
      <PageHeader
        title="Subscribers"
        subtitle="Who consumes the warehouse. Declared in a subscribers.yaml; every query is parsed into lineage."
      />

      <FilterBar>
        <Input
          value={search}
          onChange={(event) => setSearch(event.target.value)}
          placeholder="Search name, owner, description, notes, location ..."
          className={search === "" ? undefined : activeFilterClass}
          data-testid="subscriber-search"
        />
        <Select value={type} onValueChange={setType}>
          <SelectTrigger className={type === "all" ? "w-44" : `w-44 ${activeFilterClass}`} data-testid="subscriber-type">
            <SelectValue placeholder="Type" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All types</SelectItem>
            {types.map((value) => <SelectItem key={value} value={value}>{value}</SelectItem>)}
          </SelectContent>
        </Select>
        {(search !== "" || type !== "all") && (
          <Button
            variant="ghost"
            size="sm"
            onClick={() => { setSearch(""); setType("all"); }}
            data-testid="subscriber-clear-filters"
          >
            <X className="size-4" />
            Clear
          </Button>
        )}
      </FilterBar>

      {subscribers.isPending ? (
        <Skeleton className="h-64 w-full" />
      ) : subscribers.isError ? (
        isApiError(subscribers.error)
          ? <CorrelationError error={subscribers.error} />
          : <p className="text-[13px] text-destructive">{String(subscribers.error)}</p>
      ) : subscribers.data.length === 0 ? (
        <EmptyState
          title="No subscribers declared."
          description="Add a subscribers.yaml to a repo to record who reads the warehouse; its queries become lineage on the next sync."
        />
      ) : (
        <DataTable<Subscriber>
          columns={subscriberColumns}
          rows={subscribers.data}
          rowKey={(row) => row.key}
          onRowClick={(row) => select(row.key)}
          emptyMessage="No subscriber matches the filters."
          data-testid="subscribers-table"
        />
      )}

      <Sheet open={selected !== null} onOpenChange={(open) => { if (!open) select(null); }}>
        <SheetContent className="w-full overflow-y-auto sm:max-w-3xl" data-testid="subscriber-drawer">
          <SheetTitle className="sr-only">Subscriber</SheetTitle>
          <SheetDescription className="sr-only">What this subscriber reads, and the queries behind it.</SheetDescription>
          <SheetClose />
          <div className="p-4">{selected !== null && <SubscriberDetails subscriberKey={selected} />}</div>
        </SheetContent>
      </Sheet>
    </Page>
  );
}
