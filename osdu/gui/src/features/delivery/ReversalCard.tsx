import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { isApiError } from "@/api/client";
import { deliveryApi, deliveryRecordRoute, type DeliveryReversalItem } from "../../api/delivery";
import { CorrelationError } from "@/components/CorrelationError";
import { DataTable, type Column } from "@/components/DataTable";
import { IdChip } from "@/components/IdChip";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import { shortId } from "./idTail";
import { ReversalOutcomeCount, ReversalStatusPill } from "./ReversalParts";
import { REVERSAL_OUTCOME_ORDER, reversalActive } from "./reversalOutcomes";

/** Records of one outcome a page of the card lists. */
const PAGE = 50;

/**
 * One reversal as the ledger keeps it (docs/reversal-plan.md): its state, who asked and when, its records by outcome, each
 * outcome opening its records, and the runs that worked on it. It follows the reversal while a run works on it, so a
 * reversal of a million records shows how far it got without a reload.
 */
export function ReversalCard({ reversalId }: { reversalId: number }) {
  const navigate = useNavigate();
  const [outcome, setOutcome] = useState<string | null>(null);
  const detail = useQuery({
    queryKey: ["delivery", "reversal", reversalId],
    queryFn: () => deliveryApi.reversal(reversalId),
    refetchInterval: (q) => (reversalActive(q.state.data?.reversal) ? 5000 : false),
  });
  const records = useInfiniteQuery({
    queryKey: ["delivery", "reversal", reversalId, "records", outcome],
    queryFn: ({ pageParam }) => deliveryApi.reversalRecords(reversalId, { outcome: outcome ?? undefined, after: pageParam ?? undefined, limit: PAGE }),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.next ?? null,
    enabled: outcome !== null,
  });

  if (detail.isError) {
    return (
      <Card className="gap-2 rounded-lg p-3" data-testid="reversal-card">
        {isApiError(detail.error) ? <CorrelationError error={detail.error} /> : <p className="text-[13px] text-destructive">{String(detail.error)}</p>}
      </Card>
    );
  }

  const data = detail.data;
  if (data === undefined) {
    return (
      <Card className="gap-2 rounded-lg p-3" data-testid="reversal-card">
        <span className="inline-flex items-center gap-2 text-[13px] text-muted-foreground"><Loader2 className="size-4 animate-spin" />Reading the reversal</span>
      </Card>
    );
  }

  const reversal = data.reversal;
  const counts = { ...(reversal.outcomes ?? {}) };
  const open = (reversal.states?.pending ?? 0) + (reversal.states?.sending ?? 0);
  if (open > 0) {
    counts.pending = open;
  }

  const shown = REVERSAL_OUTCOME_ORDER.filter((name) => (counts[name] ?? 0) > 0);
  const rows = records.data?.pages.flatMap((page) => page.items);
  const columns: Column<DeliveryReversalItem>[] = [
    // The three text columns share what the versions and the time leave, so the table fits its card at any width.
    { id: "record", header: "Record", fill: true, floor: 140, render: (row) => <TruncatedText text={row.label ?? row.sourceKey ?? row.deliveryKey} maxWidth={360} /> },
    { id: "osdu", header: "OSDU id", fill: true, floor: 140, render: (row) => <TruncatedText text={row.targetId} mono maxWidth={480} /> },
    {
      id: "versions",
      header: "Versions",
      render: (row) => (
        <span className="font-mono text-[12px] tabular-nums">
          {row.restoredVersion !== null && row.newVersion !== null
            ? `${row.restoredVersion} as ${row.newVersion}`
            : row.runVersion ?? "-"}
        </span>
      ),
    },
    { id: "detail", header: "Why", fill: true, floor: 200, render: (row) => <TruncatedText text={row.detail ?? row.state} maxWidth={720} /> },
    { id: "when", header: "When", render: (row) => <RelativeTime value={row.updatedUtc} /> },
  ];

  return (
    <Card className="gap-2.5 rounded-lg p-3" data-testid="reversal-card">
      <div className="flex flex-wrap items-center gap-2">
        <h2 className="text-[13px] font-medium">{`Reversal ${reversal.reversalId}`}</h2>
        <ReversalStatusPill status={reversal.status} />
        <span className="text-[12px] text-muted-foreground">
          {`asked by ${reversal.requestedBy} `}
          <RelativeTime value={reversal.requestedUtc} />
        </span>
        <span className="ml-auto flex flex-wrap items-center gap-1.5">
          {data.runIds.map((runId) => (
            <IdChip key={runId} label="run" value={runId} display={shortId(runId)} to={`/runs/${runId}`} testId={`reversal-run-${runId}`} copyTestId={`copy-reversal-run-${runId}`} />
          ))}
        </span>
      </div>

      {reversal.error !== null && <p className="text-[13px] text-destructive" data-testid="reversal-error">{reversal.error}</p>}

      <div className="flex flex-wrap items-center gap-x-1 gap-y-1" data-testid="reversal-counts">
        <span className="mr-2 font-mono text-[13px] tabular-nums">{`${(reversal.records ?? 0).toLocaleString()} record(s)`}</span>
        {reversal.capturedUtc === null && <span className="text-[12px] text-muted-foreground">still listing what the source delivered</span>}
        {shown.map((name) => (
          <ReversalOutcomeCount
            key={name}
            outcome={name}
            count={counts[name] ?? 0}
            selected={outcome === name}
            onSelect={() => setOutcome(outcome === name ? null : name)}
            testId={`reversal-count-${name}`}
          />
        ))}
      </div>

      {outcome !== null && (
        <div className="flex flex-col gap-2" data-testid="reversal-records">
          <DataTable
            columns={columns}
            rows={rows}
            rowKey={(row) => row.deliveryKey}
            onRowClick={(row) => navigate(deliveryRecordRoute({ flowId: reversal.flowId, deliveryKey: row.deliveryKey }))}
            emptyMessage="No record of this reversal came out this way."
            data-testid="reversal-records-table"
          />
          {records.hasNextPage && (
            <Button variant="outline" size="sm" className="self-start" onClick={() => void records.fetchNextPage()} disabled={records.isFetchingNextPage} data-testid="reversal-records-more">
              {records.isFetchingNextPage && <Loader2 className="animate-spin" />}
              More records
            </Button>
          )}
        </div>
      )}
    </Card>
  );
}
