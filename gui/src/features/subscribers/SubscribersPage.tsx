import { useEffect, useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ExternalLink, MonitorPlay, X } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Separator } from "@/components/ui/separator";
import { Sheet, SheetClose, SheetContent, SheetDescription, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "../../api/client";
import { lineageApi } from "../../api/endpoints";
import type { Subscriber, SubscriberObject, SubscriberQuery } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { DetailPair } from "../../components/DetailPair";
import { EmptyState } from "../../components/EmptyState";
import { activeFilterClass, FilterBar } from "../../components/FilterBar";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { RelativeTime } from "../../components/RelativeTime";
import { encodeNodeId } from "../catalog/nodeIds";

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

/** The objects one subscriber reads: where each lives, and which of its queries reference it. */
const objectColumns: Column<SubscriberObject>[] = [
  {
    id: "name",
    header: "Object",
    render: (row) => (
      <Link
        to={`/catalog?node=${encodeURIComponent(encodeNodeId({ type: "object", objectKey: row.key }))}`}
        className="font-mono text-[12px] text-primary hover:underline"
      >
        {[row.database, row.schema, row.name].filter((part) => part !== null && part !== "").join(".")}
      </Link>
    ),
  },
  { id: "kind", header: "Kind", render: (row) => <Badge variant="secondary">{row.kind}</Badge>, width: 110 },
  { id: "level", header: "Level", render: (row) => row.level ?? "-", width: 72 },
  {
    id: "queries",
    header: "Through",
    render: (row) => (
      <span className="whitespace-normal break-words font-mono text-[12px] text-muted-foreground">
        {row.queries.join(", ")}
      </span>
    ),
  },
];

/** The drawer body: who the subscriber is, everything it reads, and the queries that prove it. */
function SubscriberDrawerContent({ subscriberKey }: { subscriberKey: string }) {
  const dossier = useQuery({
    queryKey: ["subscriber-dossier", subscriberKey],
    queryFn: () => lineageApi.subscriberDossier(subscriberKey),
  });

  if (dossier.isPending) {
    return (
      <div className="flex flex-col gap-2 p-4">
        <Skeleton className="h-8 w-60" />
        <Skeleton className="h-4 w-full" />
        <Skeleton className="h-4 w-3/5" />
        <Skeleton className="mt-2 h-60 w-full" />
      </div>
    );
  }

  if (dossier.isError) {
    return (
      <div className="p-4">
        {isApiError(dossier.error)
          ? <CorrelationError error={dossier.error} />
          : <p className="text-[13px] text-destructive">{String(dossier.error)}</p>}
      </div>
    );
  }

  const { subscriber, queries, objects } = dossier.data;
  return (
    <div className="p-4">
      <div className="mb-1 flex flex-wrap items-center gap-2">
        <h2 className="min-w-0 break-words text-base font-medium">{subscriber.name}</h2>
        <Badge variant="secondary">{subscriber.type}</Badge>
      </div>
      {subscriber.description !== null && (
        <p className="mb-3 text-[13px] text-muted-foreground">{subscriber.description}</p>
      )}

      <div className="mb-4 grid grid-cols-2 gap-3">
        <DetailPair label="Owner">{subscriber.owner === null ? "-" : <Mono>{subscriber.owner}</Mono>}</DetailPair>
        <DetailPair label="Declared in"><Mono>{subscriber.file}</Mono></DetailPair>
        <DetailPair label="Location">
          {subscriber.url === null ? "-" : (
            <a
              href={subscriber.url}
              target="_blank"
              rel="noreferrer"
              className="inline-flex items-center gap-1 text-[13px] text-primary hover:underline"
            >
              <ExternalLink className="size-3.5 shrink-0" />
              <span className="break-all">{subscriber.url}</span>
            </a>
          )}
        </DetailPair>
        <DetailPair label="First seen"><RelativeTime value={subscriber.firstSeenUtc} absolute /></DetailPair>
      </div>

      <Separator className="mb-4" />
      <h3 className="mb-1.5 text-[13px] font-medium">{`Reads (${objects.length})`}</h3>
      <p className="mb-2 text-[13px] text-muted-foreground">
        Every warehouse object this subscriber's queries touch, resolved to the same nodes the flows write.
      </p>
      <DataTable<SubscriberObject>
        columns={objectColumns}
        rows={objects}
        rowKey={(row) => row.key}
        emptyMessage="This subscriber's queries name no object lineage could resolve."
        data-testid="subscriber-objects"
      />

      <Separator className="my-4" />
      <h3 className="mb-1.5 text-[13px] font-medium">{`Queries (${queries.length})`}</h3>
      {queries.length === 0 ? (
        <EmptyState title="This subscriber declares no queries, so nothing links it to the warehouse." />
      ) : (
        <div className="flex flex-col gap-4">
          {queries.map((query: SubscriberQuery) => (
            <section key={query.ordinal}>
              <div className="mb-1.5 flex flex-wrap items-center gap-2">
                <span className="text-[13px] font-medium">{query.name}</span>
                <Badge variant="outline" className="text-[11px]">
                  {`${query.objectKeys.length} object${query.objectKeys.length === 1 ? "" : "s"}`}
                </Badge>
              </div>
              <CodeView value={query.sql} language="sql" height={200} />
            </section>
          ))}
        </div>
      )}
    </div>
  );
}

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
          placeholder="Search name, owner, description ..."
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
          {selected !== null && <SubscriberDrawerContent subscriberKey={selected} />}
        </SheetContent>
      </Sheet>
    </Page>
  );
}
