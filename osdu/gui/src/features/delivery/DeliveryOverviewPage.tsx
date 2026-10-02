import { useState, type FormEvent } from "react";
import { useNavigate } from "react-router-dom";
import { useQueries, useQuery } from "@tanstack/react-query";
import { PackageCheck, PackageSearch } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { SearchInput } from "@/components/SearchInput";
import { isApiError } from "@/api/client";
import { deliveryApi, type DeliveryFlowStats } from "../../api/delivery";
import type { PipelineSummary } from "@/api/types";
import { CorrelationError } from "@/components/CorrelationError";
import { EmptyState } from "@/components/EmptyState";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { RelativeTime } from "@/components/RelativeTime";
import { fetchAllPipelines } from "@/features/pipelines/fetchAllPipelines";
import { useActivePartition } from "./activePartition";
import { SubmissionStatusBadge } from "./DeliveryBadges";

/**
 * One count of a flow's records. A tone says what the records it counts are (delivered, held), so a count of none takes
 * no tone: a zero is the absence of the thing, and is drawn as quietly as an empty cell.
 */
function Count({ label, value, tone }: { label: string; value: number; tone?: "success" | "warning" | "destructive" | "info" }) {
  const color = value === 0
    ? "text-muted-foreground"
    : tone === "success" ? "text-success" : tone === "warning" ? "text-warning" : tone === "destructive" ? "text-destructive" : tone === "info" ? "text-info" : "";
  return (
    <div className="min-w-0">
      <div className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">{label}</div>
      <div className={`font-mono text-lg tabular-nums ${color}`}>{value.toLocaleString()}</div>
    </div>
  );
}

/**
 * The partitions a flow's counts say it delivers to: those it names or serves, or the one its header's ledger is kept
 * under; null while that is not known (a flow whose partition is its header's, before it has run).
 */
function deliversTo(stats: DeliveryFlowStats | undefined): string[] | null {
  if (stats === undefined) {
    return null;
  }

  return stats.partitions ?? (stats.headerPartition ? [stats.headerPartition] : null);
}

/**
 * One delivery flow's card, in the workbench's partition. A flow that delivers there is counted there, and its partition
 * badges mark that one; a flow that delivers only to other partitions says where, and shows no counts, since none of its
 * records are the workbench partition's.
 */
function FlowCard({ pipeline, stats, active, elsewhere, onOpen }: {
  pipeline: PipelineSummary;
  stats: DeliveryFlowStats | undefined;
  active: string | null;
  elsewhere: boolean;
  onOpen: () => void;
}) {
  const partitions = deliversTo(stats) ?? [];
  const counted = stats?.partition ?? stats?.headerPartition ?? null;
  const total = stats?.total ?? 0;
  const delivered = stats?.delivered ?? 0;
  const ratio = total === 0 ? 0 : Math.round((delivered / total) * 100);
  return (
    <Card
      className={`cursor-pointer gap-3 rounded-lg p-4 hover:bg-muted/40 ${elsewhere ? "opacity-60" : ""}`}
      onClick={onOpen}
      data-testid={`delivery-flow-${pipeline.id}`}
      data-elsewhere={elsewhere ? "true" : undefined}
    >
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-mono text-[13px] font-medium">{pipeline.name}</span>
        <Badge variant="outline">{pipeline.batch ?? "default"}</Badge>
        {!pipeline.active && <Badge variant="outline" className="border-warning/50 text-warning">inactive</Badge>}
        {stats && stats.drifted > 0 && <Badge variant="secondary" className="bg-warning/15 text-warning">{stats.drifted} drifted</Badge>}
        {partitions.length > 0 && (
          <span
            className="ml-auto flex flex-wrap items-center gap-1"
            title={elsewhere
              ? `The partitions this flow delivers to; ${active} is not one of them.`
              : counted !== null
                ? `The partitions this flow delivers to; the counts are ${counted}'s.`
                : "The partitions this flow delivers to; the counts add every partition's ledger up."}
            data-testid={`delivery-flow-${pipeline.id}-partitions`}
          >
            {partitions.map((name) => (
              <Badge
                key={name}
                variant={name === counted ? "default" : "secondary"}
                className="font-mono text-[11px]"
              >
                {name}
              </Badge>
            ))}
          </span>
        )}
      </div>
      {stats === undefined ? (
        <Skeleton className="h-14 w-full" />
      ) : elsewhere ? (
        <p className="text-[12px] text-muted-foreground" data-testid={`delivery-flow-${pipeline.id}-elsewhere`}>
          Delivers to {partitions.join(", ")}, not {active}. Pick one of those in the title bar for its counts.
        </p>
      ) : (
        <>
          <div className="h-2 w-full overflow-hidden rounded bg-muted">
            <div className="h-full bg-success" style={{ width: `${ratio}%` }} />
          </div>
          <div className="grid grid-cols-3 gap-3 sm:grid-cols-6">
            <Count label="Records" value={stats.total} />
            <Count label="Delivered" value={stats.delivered} tone="success" />
            <Count label="Pending" value={stats.pending + stats.delivering} tone="info" />
            <Count label="Held" value={stats.held} tone="warning" />
            <Count label="Failed" value={stats.failed} tone="destructive" />
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

/** The way from the estate's front door to one record: a term typed here opens the Records page looked up for it. */
function FindRecordForm() {
  const navigate = useNavigate();
  const [term, setTerm] = useState("");
  const submit = (event: FormEvent) => {
    event.preventDefault();
    const trimmed = term.trim();
    navigate(trimmed === "" ? "/delivery/records" : `/delivery/records?q=${encodeURIComponent(trimmed)}`);
  };

  return (
    <form className="flex flex-wrap items-center gap-2" onSubmit={submit} data-testid="delivery-find-record">
      <SearchInput
        value={term}
        onChange={setTerm}
        placeholder="Find a record: source key, OSDU id, key or file"
        label="Find a record"
        testId="delivery-find-record-term"
        className="sm:w-80"
      />
      <Button type="submit" variant="outline" size="sm" data-testid="delivery-find-record-go">
        <PackageSearch />
        Look up
      </Button>
    </form>
  );
}

/** The page's one line of totals: the flows that deliver to the workbench's partition, and what they hold there. */
function subtitle(flows: number, active: string | null, totals: { total: number; delivered: number; pending: number; held: number; failed: number; drifted: number }) {
  const where = active === null ? "" : ` in ${active}`;
  return `${flows} delivery flow${flows === 1 ? "" : "s"}${where}: ${totals.delivered.toLocaleString()} of ${totals.total.toLocaleString()} records delivered, `
    + `${totals.pending.toLocaleString()} pending, ${totals.held.toLocaleString()} held, ${totals.failed.toLocaleString()} failed, ${totals.drifted.toLocaleString()} drifted.`;
}

/**
 * Every delivery flow with what it has delivered in the workbench's partition: the "uploaded versus not" view. A flow that
 * delivers only to other partitions is listed, dimmed, with where it delivers, and left out of the totals.
 */
export default function DeliveryOverviewPage() {
  const navigate = useNavigate();
  const flows = useQuery({
    queryKey: ["pipelines", "grouped", "", "delivery", ""],
    queryFn: () => fetchAllPipelines({ kind: "delivery" }),
  });
  const pipelines = flows.data?.items ?? [];
  const [active] = useActivePartition();
  const whole = useQueries({
    queries: pipelines.map((pipeline) => ({
      queryKey: ["delivery", "stats", pipeline.id],
      queryFn: () => deliveryApi.stats(pipeline.id),
      refetchInterval: 15000,
    })),
  });
  // A flow that names or serves the workbench's partition is counted in it, unless its whole counts already are that
  // partition's (a flow of one partition, or one whose partition is its header's); the whole flow's counts say which
  // partitions it delivers to. A flow that delivers only elsewhere is not the workbench partition's, and is not added up.
  const inActive = useQueries({
    queries: pipelines.map((pipeline, index) => {
      const counted = whole[index]?.data;
      return {
        queryKey: ["delivery", "stats", pipeline.id, null, active],
        queryFn: () => deliveryApi.stats(pipeline.id, { partition: active }),
        enabled: active !== null && (counted?.partitions ?? []).includes(active) && counted?.partition !== active,
        refetchInterval: 15000,
      };
    }),
  });
  const stats = pipelines.map((_, index) => (inActive[index]?.data !== undefined ? inActive[index] : whole[index]));
  const elsewhere = pipelines.map((_, index) => {
    const delivers = deliversTo(whole[index]?.data);
    return active !== null && delivers !== null && !delivers.includes(active);
  });

  if (flows.isError) {
    return (
      <Page data-testid="page-delivery">
        {isApiError(flows.error) ? <CorrelationError error={flows.error} /> : <p className="text-[13px] text-destructive">{String(flows.error)}</p>}
      </Page>
    );
  }

  const totals = stats.reduce(
    (acc, q, index) => {
      const s = q.data;
      if (!s || elsewhere[index]) {
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
        subtitle={flows.data ? subtitle(pipelines.length - elsewhere.filter(Boolean).length, active, totals) : undefined}
        actions={<FindRecordForm />}
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
            <FlowCard
              key={pipeline.id}
              pipeline={pipeline}
              stats={stats[index]?.data}
              active={active}
              elsewhere={elsewhere[index] ?? false}
              onOpen={() => navigate(`/pipelines/${pipeline.id}?tab=delivery${stats[index]?.data?.partition ? `&partition=${encodeURIComponent(stats[index]!.data!.partition!)}` : ""}`)}
            />
          ))}
        </div>
      )}
    </Page>
  );
}
