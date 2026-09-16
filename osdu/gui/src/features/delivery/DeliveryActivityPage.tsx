import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Badge } from "@/components/ui/badge";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { deliveryApi, type DeliveryActivity } from "../../api/delivery";
import { CodeView } from "@/components/CodeView";
import { FilterBar } from "@/components/FilterBar";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { PagedTable, type Column } from "@/components/PagedTable";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import { prettyJson } from "./prettyJson";

const ALL = "all";
const KINDS = ["deliver", "intake", "drain", "verify", "submit", "release", "redeliver", "delete"];
const OUTCOMES = ["running", "completed", "failed", "cancelled"];

function OutcomeBadge({ outcome }: { outcome: DeliveryActivity["outcome"] }) {
  const className = outcome === "completed"
    ? "bg-success/15 text-success"
    : outcome === "failed"
      ? "bg-destructive/15 text-destructive"
      : outcome === "cancelled"
        ? "bg-warning/15 text-warning"
        : "bg-info/12 text-info";
  return <Badge variant="secondary" className={className}>{outcome}</Badge>;
}

/** The audit trail across every delivery flow: who did what, when, with which inputs, and how it ended. Each entry
 * opens with its recorded parameters and, for runs, the captured log. */
export default function DeliveryActivityPage() {
  const navigate = useNavigate();
  const [actor, setActor] = useLocalStorageState("sqlflow.filters.delivery-activity.actor", "");
  const [kind, setKind] = useLocalStorageState("sqlflow.filters.delivery-activity.kind", ALL);
  const [outcome, setOutcome] = useLocalStorageState("sqlflow.filters.delivery-activity.outcome", ALL);
  const [selected, setSelected] = useState<number | null>(null);

  const detail = useQuery({
    queryKey: ["delivery", "activity", selected],
    queryFn: () => deliveryApi.activity(selected!),
    enabled: selected !== null,
  });

  const columns: Column<DeliveryActivity>[] = [
    { id: "started", header: "When", render: (row) => <RelativeTime value={row.startedUtc} absolute /> },
    { id: "flow", header: "Flow", render: (row) => <span className="font-mono text-[12px] font-medium">{row.flowName}</span> },
    { id: "kind", header: "Action", render: (row) => row.kind },
    { id: "actor", header: "By", render: (row) => <span className="font-mono text-[12px]">{row.actor}</span> },
    { id: "outcome", header: "Outcome", render: (row) => <OutcomeBadge outcome={row.outcome} /> },
    {
      id: "target",
      header: "Target",
      render: (row) => (
        <span className="inline-flex gap-2">
          {row.deliveryKey && (
            <button type="button" className="font-mono text-[12px] text-primary hover:underline" onClick={(event) => { event.stopPropagation(); navigate(`/delivery/records/${row.deliveryKey}`); }}>
              record {row.deliveryKey.slice(0, 8)}
            </button>
          )}
          {row.submissionId && (
            <button type="button" className="font-mono text-[12px] text-primary hover:underline" onClick={(event) => { event.stopPropagation(); navigate(`/delivery/submissions/${row.submissionId}`); }}>
              submission {row.submissionId.slice(0, 8)}
            </button>
          )}
          {row.runId && (
            <button type="button" className="font-mono text-[12px] text-primary hover:underline" onClick={(event) => { event.stopPropagation(); navigate(`/runs/${row.runId}`); }}>
              run {row.runId.slice(0, 8)}
            </button>
          )}
        </span>
      ),
    },
    { id: "summary", header: "Summary", render: (row) => <TruncatedText text={row.summary} maxWidth={480} /> },
  ];

  return (
    <Page data-testid="page-delivery-activity">
      <PageHeader title="Delivery audit trail" subtitle="Every run and intervention on every delivery flow, by actor, newest first." />
      <FilterBar>
        <SearchInput value={actor} onChange={setActor} placeholder="Actor (user:name, schedule, manual)" label="Filter by actor" testId="delivery-activity-actor" className="sm:w-72" />
        <Select value={kind} onValueChange={setKind}>
          <SelectTrigger size="sm" className="h-8 w-44" data-testid="delivery-activity-kind"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>All actions</SelectItem>
            {KINDS.map((value) => <SelectItem key={value} value={value}>{value}</SelectItem>)}
          </SelectContent>
        </Select>
        <Select value={outcome} onValueChange={setOutcome}>
          <SelectTrigger size="sm" className="h-8 w-40" data-testid="delivery-activity-outcome"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>All outcomes</SelectItem>
            {OUTCOMES.map((value) => <SelectItem key={value} value={value}>{value}</SelectItem>)}
          </SelectContent>
        </Select>
      </FilterBar>
      <PagedTable
        queryKey={["delivery", "activities", actor, kind, outcome]}
        fetchPage={(page, pageSize) => deliveryApi.activities({
          page,
          pageSize,
          actor: actor.trim() === "" ? undefined : actor.trim(),
          kind: kind === ALL ? undefined : kind,
          outcome: outcome === ALL ? undefined : outcome,
        })}
        columns={columns}
        rowKey={(row) => row.activityId}
        onRowClick={(row) => setSelected(row.activityId)}
        pollMs={10000}
        emptyMessage="No delivery activity recorded yet."
        data-testid="delivery-activity-table"
      />

      <Sheet open={selected !== null} onOpenChange={(open) => { if (!open) { setSelected(null); } }}>
        <SheetContent className="w-full gap-0 sm:max-w-2xl" data-testid="delivery-activity-detail">
          <SheetHeader>
            <SheetTitle>{detail.data ? `${detail.data.kind} on ${detail.data.flowName}` : "Activity"}</SheetTitle>
            <SheetDescription>
              {detail.data ? `${detail.data.actor}, started ${detail.data.startedUtc}, ${detail.data.outcome}.` : "Loading."}
            </SheetDescription>
          </SheetHeader>
          {detail.data && (
            <div className="flex flex-1 flex-col gap-3 overflow-y-auto px-4 pb-4">
              {detail.data.summary && <p className="text-[13px]">{detail.data.summary}</p>}
              <div>
                <div className="mb-1 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Parameters</div>
                <CodeView value={prettyJson(detail.data.parametersJson ?? "{}")} language="json" height={160} data-testid="delivery-activity-parameters" />
              </div>
              <div>
                <div className="mb-1 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Log</div>
                {detail.data.log
                  ? <CodeView value={detail.data.log} language="plaintext" height={360} data-testid="delivery-activity-log" />
                  : <p className="text-[13px] text-muted-foreground">No captured log for this activity{detail.data.runId ? "; the run trace has the full account." : "."}</p>}
              </div>
            </div>
          )}
        </SheetContent>
      </Sheet>
    </Page>
  );
}
