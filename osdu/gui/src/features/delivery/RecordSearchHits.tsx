import { useNavigate } from "react-router-dom";
import { Badge } from "@/components/ui/badge";
import type { SearchCategory, SearchHit } from "@/api/types";
import { DataTable, type Column } from "@/components/DataTable";
import { Mono } from "@/components/Mono";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import type { DeliveryRecordHit } from "../../api/delivery";

function isRecordHit(value: unknown): value is DeliveryRecordHit {
  if (value === null || typeof value !== "object") {
    return false;
  }

  const hit = value as Partial<Record<keyof DeliveryRecordHit, unknown>>;
  return typeof hit.deliveryKey === "string" && typeof hit.sourceKey === "string" && typeof hit.status === "string";
}

/** The delivery record a contributed search hit carries in its `data`; null for a hit that carries none. */
function recordOf(item: unknown): DeliveryRecordHit | null {
  if (item === null || typeof item !== "object") {
    return null;
  }

  const data = (item as Partial<SearchHit>).data;
  return isRecordHit(data) ? data : null;
}

const recordColumns: Column<DeliveryRecordHit>[] = [
  {
    id: "record",
    header: "Record",
    render: (row) => (
      <div className="flex min-w-0 flex-col">
        <span className="truncate font-medium">{row.label ?? row.sourceKey}</span>
        {row.label !== null && (
          <span className="truncate font-mono text-[11px] text-muted-foreground">{row.sourceKey}</span>
        )}
      </div>
    ),
  },
  { id: "flow", header: "Flow", render: (row) => <Mono>{row.flowName ?? "-"}</Mono> },
  { id: "status", header: "Status", render: (row) => <Badge variant="secondary">{row.status}</Badge> },
  { id: "target", header: "OSDU id", render: (row) => <TruncatedText text={row.targetId} mono maxWidth={300} /> },
  { id: "delivered", header: "Delivered", render: (row) => <RelativeTime value={row.lastDeliveredUtc} /> },
  { id: "key", header: "Delivery key", render: (row) => <Mono>{row.deliveryKey}</Mono> },
];

/**
 * The delivery records a search names: a delivery key lands on one record; an OSDU id, source key or label prefix lists
 * the records that start with it, across every flow.
 */
export function RecordSearchHits({ category }: { category: SearchCategory<unknown> }) {
  const navigate = useNavigate();
  const records = category.items.map(recordOf).filter((record): record is DeliveryRecordHit => record !== null);
  if (records.length === 0) {
    return null;
  }

  const capped = category.totalCapped === true;
  const total = Math.max(category.total, records.length);
  return (
    <section className="flex flex-col gap-2" data-testid="search-records">
      <h2 className="text-[13px] font-medium">
        {`${total.toLocaleString()}${capped ? "+" : ""} delivery record${total === 1 && !capped ? "" : "s"}`}
        {total > records.length && <span className="font-normal text-muted-foreground">{` (first ${records.length})`}</span>}
      </h2>
      <DataTable
        columns={recordColumns}
        rows={records}
        rowKey={(row) => row.deliveryKey}
        onRowClick={(row) => navigate(`/delivery/records/${row.deliveryKey}`)}
        emptyMessage="No records match."
        data-testid="search-records-table"
      />
    </section>
  );
}
