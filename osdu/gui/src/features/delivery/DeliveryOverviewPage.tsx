import { useNavigate } from "react-router-dom";
import { useQueries, useQuery } from "@tanstack/react-query";
import { PackageCheck } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "@/api/client";
import { deliveryApi, type DeliveryFlowStats } from "../../api/delivery";
import type { PipelineSummary } from "@/api/types";
import { CorrelationError } from "@/components/CorrelationError";
import { EmptyState } from "@/components/EmptyState";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { RelativeTime } from "@/components/RelativeTime";
import { fetchAllPipelines } from "@/features/pipelines/fetchAllPipelines";
import { SubmissionStatusBadge } from "./DeliveryBadges";

function Count({ label, value, tone }: { label: string; value: number; tone?: "success" | "warning" | "destructive" | "info" }) {
  const color = tone === "success" ? "text-success" : tone === "warning" ? "text-warning" : tone === "destructive" ? "text-destructive" : tone === "info" ? "text-info" : "";
  return (
    <div className="min-w-0">
      <div className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">{label}</div>
      <div className={`font-mono text-lg tabular-nums ${color}`}>{value.toLocaleString()}</div>
    </div>
  );
}

function FlowCard({ pipeline, stats, onOpen }: { pipeline: PipelineSummary; stats: DeliveryFlowStats | undefined; onOpen: () => void }) {
  const total = stats?.total ?? 0;
  const delivered = stats?.delivered ?? 0;
  const ratio = total === 0 ? 0 : Math.round((delivered / total) * 100);
  return (
    <Card className="cursor-pointer gap-3 rounded-lg p-4 hover:bg-muted/40" onClick={onOpen} data-testid={`delivery-flow-${pipeline.id}`}>
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-mono text-[13px] font-medium">{pipeline.name}</span>
        <Badge variant="outline">{pipeline.batch ?? "default"}</Badge>
        {!pipeline.active && <Badge variant="outline" className="border-warning/50 text-warning">inactive</Badge>}
        {stats && stats.drifted > 0 && <Badge variant="secondary" className="bg-warning/15 text-warning">{stats.drifted} drifted</Badge>}
      </div>
      {stats === undefined ? (
        <Skeleton className="h-14 w-full" />
      ) : (
        <>
          <div className="h-2 w-full overflow-hidden rounded bg-muted">
            <div className="h-full bg-success" style={{ width: `${ratio}%` }} />
          </div>
          <div className="grid grid-cols-3 gap-3 sm:grid-cols-6">
            <Count label="Records" value={stats.total} />
            <Count label="Delivered" value={stats.delivered} tone="success" />
            <Count label="Pending" value={stats.pending + stats.delivering} tone="info" />
            <Count label="Held" value={stats.held} tone={stats.held > 0 ? "warning" : undefined} />
            <Count label="Failed" value={stats.failed} tone={stats.failed > 0 ? "destructive" : undefined} />
            <Count label="Last 24h" value={stats.deliveredLast24h} />
          </div>
          <div className="flex flex-wrap items-center gap-2 text-[12px] text-muted-foreground">
            <span>{ratio}% delivered</span>
            <span>last delivery <RelativeTime value={stats.lastDeliveredUtc} /></span>
            <span>last verify <RelativeTime value={stats.lastVerifiedUtc} /></span>
            {stats.lastSubmission && (
              <span className="inline-flex items-center gap-1">
                last submission <SubmissionStatusBadge status={stats.lastSubmission.status} testId={`delivery-flow-${pipeline.id}-submission`} />
                <RelativeTime value={stats.lastSubmission.receivedUtc} />
              </span>
            )}
          </div>
        </>
      )}
    </Card>
  );
}

/** Every delivery flow with what it has delivered: the "uploaded versus not" view across the estate. */
export default function DeliveryOverviewPage() {
  const navigate = useNavigate();
  const flows = useQuery({
    queryKey: ["pipelines", "grouped", "", "delivery", ""],
    queryFn: () => fetchAllPipelines({ kind: "delivery" }),
  });
  const pipelines = flows.data?.items ?? [];
  const stats = useQueries({
    queries: pipelines.map((pipeline) => ({
      queryKey: ["delivery", "stats", pipeline.id],
      queryFn: () => deliveryApi.stats(pipeline.id),
      refetchInterval: 15000,
    })),
  });

  if (flows.isError) {
    return (
      <Page data-testid="page-delivery">
        {isApiError(flows.error) ? <CorrelationError error={flows.error} /> : <p className="text-[13px] text-destructive">{String(flows.error)}</p>}
      </Page>
    );
  }

  const totals = stats.reduce(
    (acc, q) => {
      const s = q.data;
      if (!s) {
        return acc;
      }

      return { total: acc.total + s.total, delivered: acc.delivered + s.delivered, pending: acc.pending + s.pending + s.delivering, held: acc.held + s.held, failed: acc.failed + s.failed, drifted: acc.drifted + s.drifted };
    },
    { total: 0, delivered: 0, pending: 0, held: 0, failed: 0, drifted: 0 },
  );

  return (
    <Page data-testid="page-delivery">
      <PageHeader
        title="Delivery"
        subtitle={flows.data ? `${pipelines.length} delivery flow${pipelines.length === 1 ? "" : "s"}: ${totals.delivered.toLocaleString()} of ${totals.total.toLocaleString()} records delivered, ${totals.pending.toLocaleString()} pending, ${totals.held.toLocaleString()} held, ${totals.failed.toLocaleString()} failed, ${totals.drifted.toLocaleString()} drifted.` : undefined}
      />
      {flows.data === undefined ? (
        <Skeleton className="h-40 w-full rounded-lg" />
      ) : pipelines.length === 0 ? (
        <EmptyState
          icon={<PackageCheck />}
          title="No delivery flows synced yet"
          description="Register a repository holding flowType: delivery documents; each flow appears here with what it has delivered."
          data-testid="delivery-empty"
        />
      ) : (
        <div className="grid gap-3 lg:grid-cols-2" data-testid="delivery-flows">
          {pipelines.map((pipeline, index) => (
            <FlowCard key={pipeline.id} pipeline={pipeline} stats={stats[index]?.data} onOpen={() => navigate(`/pipelines/${pipeline.id}?tab=delivery`)} />
          ))}
        </div>
      )}
    </Page>
  );
}
