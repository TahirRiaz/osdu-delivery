import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ExternalLink } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Separator } from "@/components/ui/separator";
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "../../api/client";
import { lineageApi } from "../../api/endpoints";
import type { SubscriberObject, SubscriberQuery } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { DetailPair } from "../../components/DetailPair";
import { EmptyState } from "../../components/EmptyState";
import { Mono } from "../../components/Mono";
import { RelativeTime } from "../../components/RelativeTime";
import { encodeNodeId } from "../catalog/nodeIds";

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

/**
 * Everything about one data subscriber: who it is, every warehouse object its queries read, and the queries
 * themselves. The single implementation behind both surfaces that show a subscriber, the Subscribers page
 * drawer and the catalog tree's details panel, so the two can never drift apart.
 */
export function SubscriberDetails({ subscriberKey }: { subscriberKey: string }) {
  const dossier = useQuery({
    queryKey: ["subscriber-dossier", subscriberKey],
    queryFn: () => lineageApi.subscriberDossier(subscriberKey),
  });

  if (dossier.isPending) {
    return (
      <div className="flex flex-col gap-2">
        <Skeleton className="h-8 w-60" />
        <Skeleton className="h-4 w-full" />
        <Skeleton className="h-4 w-3/5" />
        <Skeleton className="mt-2 h-60 w-full" />
      </div>
    );
  }

  if (dossier.isError) {
    return isApiError(dossier.error)
      ? <CorrelationError error={dossier.error} />
      : <p className="text-[13px] text-destructive">{String(dossier.error)}</p>;
  }

  const { subscriber, queries, objects } = dossier.data;
  return (
    <div data-testid="subscriber-details">
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
