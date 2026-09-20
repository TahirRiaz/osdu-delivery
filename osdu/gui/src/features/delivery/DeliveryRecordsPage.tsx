import { useEffect, useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { PackageSearch } from "lucide-react";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Badge } from "@/components/ui/badge";
import {
  DELIVERY_RECORD_STATUSES, deliveryApi, deliveryRecordRoute, type DeliveryRecordHit, type DeliveryRecordStatus,
} from "../../api/delivery";
import type { Column } from "@/components/DataTable";
import { EmptyState } from "@/components/EmptyState";
import { FilterBar } from "@/components/FilterBar";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { PagedTable } from "@/components/PagedTable";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import { RecordStatusBadge } from "./DeliveryBadges";

const ALL = "all";

/** How long the field waits after the last keystroke before the term becomes the lookup: a lookup per keystroke is noise. */
const DEBOUNCE_MS = 300;

function isRecordStatus(value: string | null): value is DeliveryRecordStatus {
  return value !== null && (DELIVERY_RECORD_STATUSES as readonly string[]).includes(value);
}

const hitColumns: Column<DeliveryRecordHit>[] = [
  { id: "status", header: "Status", render: (row) => <RecordStatusBadge status={row.status as DeliveryRecordStatus} /> },
  {
    id: "record",
    header: "Record",
    fill: true,
    floor: 200,
    render: (row) => (
      <div className="flex min-w-0 flex-col">
        <span className="truncate font-medium">{row.label ?? row.sourceKey}</span>
        {row.label !== null && <span className="truncate font-mono text-[11px] text-muted-foreground">{row.sourceKey}</span>}
      </div>
    ),
  },
  {
    id: "flow",
    header: "Flow",
    render: (row) => (
      row.flowName === null
        ? <span className="text-[12px] text-muted-foreground" title={row.flowId}>no longer synced</span>
        : (
          <div className="flex min-w-0 flex-col">
            <span className="truncate font-mono text-[12px]">{row.flowName}</span>
            {row.interface && <Badge variant="outline" className="w-fit font-mono text-[10px]">{row.interface}</Badge>}
          </div>
        )
    ),
  },
  { id: "target", header: "OSDU id", render: (row) => <TruncatedText text={row.targetId} mono maxWidth={300} /> },
  { id: "delivered", header: "Delivered", render: (row) => <RelativeTime value={row.lastDeliveredUtc} /> },
  { id: "updated", header: "Updated", render: (row) => <RelativeTime value={row.updatedUtc} /> },
  { id: "key", header: "Delivery key", render: (row) => <TruncatedText text={row.deliveryKey} mono maxWidth={140} /> },
];

/**
 * Where an operator starts from what they hold, not from a flow: a well name, an OSDU id, a delivery key or the file a
 * record came from finds the record across every flow, and its row opens the record's page with its whole history.
 * The term and the state travel in the URL, so a lookup is a link that can be sent on.
 */
export default function DeliveryRecordsPage() {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const term = searchParams.get("q")?.trim() ?? "";
  const status = isRecordStatus(searchParams.get("status")) ? searchParams.get("status")! : ALL;
  const [typed, setTyped] = useState(term);

  // The field is what the operator types; the URL is what is looked up, a moment after they stop.
  useEffect(() => {
    if (typed.trim() === term) {
      return;
    }

    const handle = window.setTimeout(() => {
      setSearchParams((current) => {
        const next = new URLSearchParams(current);
        if (typed.trim() === "") {
          next.delete("q");
        } else {
          next.set("q", typed.trim());
        }

        return next;
      }, { replace: true });
    }, DEBOUNCE_MS);
    return () => window.clearTimeout(handle);
  }, [typed, term, setSearchParams]);

  // A link that arrives with a term (from the Delivery page's field, or sent on) fills the field it would have been typed in.
  const [shownTerm, setShownTerm] = useState(term);
  if (term !== shownTerm) {
    setShownTerm(term);
    if (typed.trim() !== term) {
      setTyped(term);
    }
  }

  const selectStatus = (next: string) => setSearchParams((current) => {
    const params = new URLSearchParams(current);
    if (next === ALL) {
      params.delete("status");
    } else {
      params.set("status", next);
    }

    return params;
  }, { replace: true });

  return (
    <Page data-testid="page-delivery-records">
      <PageHeader
        title="Records"
        subtitle="Find a delivered record by what you hold: a source key or label, an OSDU id, a delivery key, or the ingestion file it came from. Every flow is searched."
      />
      <FilterBar>
        <SearchInput
          value={typed}
          onChange={setTyped}
          placeholder="Source key, label, OSDU id, delivery key or file name"
          label="Look up records"
          testId="delivery-lookup-search"
          className="sm:w-[28rem]"
        />
        <Select value={status} onValueChange={selectStatus}>
          <SelectTrigger size="sm" className="h-8 w-40" data-testid="delivery-lookup-status">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>All statuses</SelectItem>
            {DELIVERY_RECORD_STATUSES.map((value) => <SelectItem key={value} value={value}>{value}</SelectItem>)}
          </SelectContent>
        </Select>
      </FilterBar>
      {term === ""
        ? (
          <EmptyState
            icon={<PackageSearch />}
            title="Type what you hold"
            description="The start of a source key, a label or an OSDU id lists every record that begins with it; a delivery key lands on that record; an ingestion file name lists the records built from it. A flow's own Records tab lists everything it holds."
            data-testid="delivery-lookup-empty"
          />
        )
        : (
          <PagedTable
            queryKey={["delivery", "lookup", term, status]}
            fetchPage={(page, pageSize) => deliveryApi.lookupRecords({
              search: term, status: status === ALL ? undefined : (status as DeliveryRecordStatus), page, pageSize,
            })}
            columns={hitColumns}
            rowKey={(row) => `${row.flowId}/${row.deliveryKey}`}
            onRowClick={(row) => navigate(deliveryRecordRoute(row))}
            emptyMessage="No record starts with that. A term matches the start of a source key, a label, an OSDU id or a file name; try a shorter one, or open the flow's Records tab and match anywhere."
            data-testid="delivery-lookup-table"
          />
        )}
    </Page>
  );
}
