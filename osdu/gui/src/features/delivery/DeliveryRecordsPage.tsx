import { useEffect, useMemo, useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { History } from "lucide-react";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Badge } from "@/components/ui/badge";
import {
  DELIVERY_RECORD_STATUSES, deliveryApi, deliveryRecordRoute, type DeliveryRecordHit, type DeliveryRecordStatus,
} from "../../api/delivery";
import type { Column } from "@/components/DataTable";
import { FilterBar } from "@/components/FilterBar";
import { FilterCombobox, type FilterOption } from "@/components/FilterCombobox";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { PagedTable } from "@/components/PagedTable";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import { RecordStatusBadge } from "./DeliveryBadges";
import { OpenInOsduLink } from "./OpenInOsduLink";

const ALL = "all";

/** What each kind of matched value is, in the operator's terms; the badge's tooltip says it. */
const MATCH_KINDS: Record<string, string> = {
  identity: "an identity the mapping declares (a wellbore id, a well name)",
  key: "the record's key, or one of its columns",
  label: "a word of the record's label",
  osdu: "the OSDU id, or its own part",
  file: "the ingestion file the record came from",
};

/** How long the field waits after the last keystroke before the term becomes the lookup: a lookup per keystroke is noise. */
const DEBOUNCE_MS = 300;

function isRecordStatus(value: string | null): value is DeliveryRecordStatus {
  return value !== null && (DELIVERY_RECORD_STATUSES as readonly string[]).includes(value);
}

const statusColumn: Column<DeliveryRecordHit> = {
  id: "status", header: "Status", render: (row) => <RecordStatusBadge status={row.status as DeliveryRecordStatus} />,
};

const recordColumn: Column<DeliveryRecordHit> = {
  id: "record",
  header: "Record",
  fill: true,
  floor: 200,
  render: (row) => (
    // A label and a key are both as long as the estate made them: clipped to the column, each readable in full on hover.
    <div className="flex min-w-0 flex-col">
      <TruncatedText text={row.label ?? row.sourceKey} maxWidth={420} title="Record" className="font-medium" />
      {row.label !== null && <TruncatedText text={row.sourceKey} mono maxWidth={420} title="Source key" className="text-[11px] text-muted-foreground" />}
    </div>
  ),
};

/** Why a row is a hit: only a search has one, so the recency listing leaves the column out rather than showing it empty. */
const matchedColumn: Column<DeliveryRecordHit> = {
  id: "matched",
  header: "Matched",
  render: (row) => {
    const matched = row.matched ?? [];
    return matched.length === 0
      ? <span className="text-muted-foreground">-</span>
      : (
        <span className="inline-flex flex-wrap gap-1" data-testid="lookup-matched">
          {matched.map((match) => (
            <Badge key={`${match.kind}:${match.value}`} variant="outline" className="font-mono text-[10px]" title={MATCH_KINDS[match.kind] ?? match.kind}>
              {match.value}
            </Badge>
          ))}
        </span>
      );
  },
};

const restColumns: Column<DeliveryRecordHit>[] = [
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
  {
    id: "target",
    header: "OSDU id",
    render: (row) => <TruncatedText text={row.targetId} mono maxWidth={300} copy={row.targetId !== null} copyTestId="copy-hit-target" />,
  },
  { id: "delivered", header: "Delivered", render: (row) => <RelativeTime value={row.lastDeliveredUtc} /> },
  { id: "updated", header: "Updated", render: (row) => <RelativeTime value={row.updatedUtc} /> },
  { id: "key", header: "Delivery key", render: (row) => <TruncatedText text={row.deliveryKey} mono maxWidth={140} copy copyTestId="copy-hit-key" /> },
  { id: "osdu", header: "", render: (row) => (row.targetId !== null && row.status !== "deleted" ? <OpenInOsduLink record={row} /> : null) },
];

/** The columns of a search, and the columns of the recency listing, which has nothing matched to explain. */
const columnsFor = (searching: boolean): Column<DeliveryRecordHit>[] =>
  searching ? [statusColumn, recordColumn, matchedColumn, ...restColumns] : [statusColumn, recordColumn, ...restColumns];

/** How often the recency listing refreshes itself: a page left open is a view of what is arriving now. */
const RECENT_POLL_MS = 10_000;

/**
 * Where an operator starts from what they hold, not from a flow: a well name, an OSDU id, a delivery key or the file a
 * record came from finds the record across every flow, and its row opens the record's page with its whole history.
 * With nothing typed it opens on the records the ledger last took in or sent, so the page answers "what has come in?"
 * before it is asked anything. The term and the state travel in the URL, so a lookup is a link that can be sent on.
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

  const searching = term !== "";
  const columns = useMemo(() => columnsFor(searching), [searching]);

  // The flow is a ledger identity holding records: one per interface of a source. An interface's choice leads with the
  // interface, since a source's interfaces share the flow's name and the picker is too narrow to show both whole.
  const flow = searchParams.get("flow") ?? "";
  const flows = useQuery({ queryKey: ["delivery", "record-flows"], queryFn: deliveryApi.recordFlows });
  const flowOptions = useMemo<FilterOption[]>(() => {
    const named = (flows.data ?? []).map((f) => ({
      value: f.flowId,
      label: f.interface ? `${f.interface} · ${f.flowName}` : f.flowName,
    }));
    // Two identities named alike (a ledger an interface stopped adopting, say) are told apart by the identity itself.
    const shared = new Set(named.map((o) => o.label).filter((label, i, all) => all.indexOf(label) !== i));
    const options: FilterOption[] = named.map((o) => (shared.has(o.label) ? { ...o, hint: `ledger ${o.value}` } : o));
    // A link can name a flow the list leaves out, because it holds no records or no synced pipeline names it any more;
    // the filter still applies, and says so.
    return flow !== "" && flows.isSuccess && !options.some((o) => o.value === flow)
      ? [{ value: flow, label: "Flow not listed", hint: `${flow}: no records, or no longer synced` }, ...options]
      : options;
  }, [flows.data, flows.isSuccess, flow]);

  const setParam = (name: string, next: string | null) => setSearchParams((current) => {
    const params = new URLSearchParams(current);
    if (next === null) {
      params.delete(name);
    } else {
      params.set(name, next);
    }

    return params;
  }, { replace: true });
  const selectStatus = (next: string) => setParam("status", next === ALL ? null : next);
  const selectFlow = (next: string) => setParam("flow", next === "" ? null : next);

  return (
    <Page data-testid="page-delivery-records">
      <PageHeader
        title="Records"
        subtitle="The records of every flow, newest first, and a way back to any one of them: a wellbore id or well name the mapping declares, a source key or label, an OSDU id, a delivery key, or the ingestion file it came from. Every flow is searched unless one is chosen, and each hit says which value matched."
      />
      <FilterBar>
        <SearchInput
          value={typed}
          onChange={setTyped}
          placeholder="Wellbore id, well name, source key, OSDU id, delivery key or file"
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
        <FilterCombobox
          options={flowOptions}
          value={flow}
          onChange={selectFlow}
          placeholder="All flows"
          searchPlaceholder="Search flows"
          emptyText="No flow matches."
          ariaLabel="Filter by flow"
          testId="delivery-lookup-flow"
          className="w-64"
        />
      </FilterBar>
      {!searching && (
        <p className="flex items-center gap-1.5 text-[12px] text-muted-foreground" data-testid="delivery-lookup-caption">
          <History className="size-3.5 shrink-0 text-primary" aria-hidden="true" />
          <span>
            The latest records the delivery system took in or sent, newest first. Type above to find one by what you
            hold; open a row for its journey through pre-ingestion, ingestion and OSDU.
          </span>
        </p>
      )}
      <PagedTable
        queryKey={["delivery", "lookup", term, status, flow]}
        fetchPage={(page, pageSize) => deliveryApi.lookupRecords({
          search: searching ? term : undefined,
          status: status === ALL ? undefined : (status as DeliveryRecordStatus),
          flowId: flow === "" ? undefined : flow,
          page,
          pageSize,
        })}
        columns={columns}
        rowKey={(row) => `${row.flowId}/${row.deliveryKey}`}
        onRowClick={(row) => navigate(deliveryRecordRoute(row))}
        pollMs={searching ? undefined : RECENT_POLL_MS}
        emptyMessage={searching
          ? "No record starts with that. A term matches the start of a value the record is known by; try a shorter one, or open the flow's Records tab and match anywhere."
          : "No records yet. A delivery flow takes its records into the ledger on its first run, and they appear here as they arrive."}
        data-testid="delivery-lookup-table"
      />
    </Page>
  );
}
