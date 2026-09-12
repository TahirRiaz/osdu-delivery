import { useCallback, useEffect, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { ArrowRight, Check, ShieldCheck, X } from "lucide-react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { cn } from "@/lib/utils";
import { deliveryApi, type DeliveryUpdateTag } from "../../api/delivery";
import { CopyButton } from "../../components/CopyButton";
import { DetailPair } from "../../components/DetailPair";
import { EmptyState } from "../../components/EmptyState";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { TruncatedText } from "../../components/TruncatedText";
import { useOwnedPanel } from "../../layout/workbench/useOwnedPanel";
import { RecordId } from "./DeliveryCacheRecords";

/** The bottom panel content ids this surface owns. */
const PANEL = "cache-tag:";

const statuses: { value: string; label: string }[] = [
  { value: "pending", label: "Awaiting approval" },
  { value: "approved", label: "Approved" },
  { value: "rolling", label: "Rolling out" },
  { value: "applied", label: "Rolled out" },
  { value: "rejected", label: "Rejected" },
];

const statusLabel = Object.fromEntries(statuses.map((s) => [s.value, s.label])) as Record<string, string>;

const changeTone: Record<string, string> = {
  changed: "bg-info/15 text-info",
  removed: "bg-destructive/15 text-destructive",
  unmatched: "bg-warning/15 text-warning",
};

/**
 * The one decision this page makes, as the toolbar and the detail panel both make it: approving hands the change to
 * the batched rollout, rejecting leaves OSDU with what it holds. Every cache query refreshes afterwards, since the
 * change leaves the pending list and the summary's count moves with it.
 */
function useDecideTags(onDecided?: () => void) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ ids, approve }: { ids: number[]; approve: boolean }) => deliveryApi.decideTags(ids, approve),
    onSuccess: (result) => {
      toast.success(result.approved
        ? `${result.decided} change${result.decided === 1 ? "" : "s"} approved; the rollout carries the records in batches.`
        : `${result.decided} change${result.decided === 1 ? "" : "s"} rejected; OSDU keeps what it holds.`);
      void queryClient.invalidateQueries({ queryKey: ["delivery", "cache"] });
      onDecided?.();
    },
    onError: (error) => toast.error(`The decision could not be recorded: ${error instanceof Error ? error.message : String(error)}`),
  });
}

/** The value before and after, as one line: what OSDU holds now struck through, what the cache holds now beside it. */
function ValueChange({ row, maxWidth }: { row: DeliveryUpdateTag; maxWidth: number }) {
  return (
    <span className="inline-flex items-center gap-1.5 font-mono text-[12px]">
      <TruncatedText text={row.oldValue} mono maxWidth={maxWidth} className="text-muted-foreground line-through" />
      <ArrowRight className="size-3.5 shrink-0 text-muted-foreground" />
      {row.newValue === null
        ? <span className="text-destructive">gone</span>
        : <TruncatedText text={row.newValue} mono maxWidth={maxWidth} />}
    </span>
  );
}

/** How far the rollout has carried the change, or why it has not started. */
function Rollout({ row }: { row: DeliveryUpdateTag }) {
  if (row.status === "pending") {
    return <span className="text-[12px] text-muted-foreground">waiting for a decision</span>;
  }

  if (row.status === "rejected") {
    return <span className="text-[12px] text-muted-foreground">not sent</span>;
  }

  const done = row.affectedRecords === 0 ? 1 : row.processed / row.affectedRecords;
  return (
    <span className="inline-flex items-center gap-2">
      <span className="h-1.5 w-24 overflow-hidden rounded-full bg-muted">
        <span
          className={cn("block h-full", row.status === "applied" ? "bg-success" : "bg-primary")}
          style={{ width: `${Math.round(done * 100)}%` }}
        />
      </span>
      <span className="font-mono text-[11px] tabular-nums text-muted-foreground">
        {row.processed.toLocaleString()} / {row.affectedRecords.toLocaleString()}
      </span>
    </span>
  );
}

const columns: Column<DeliveryUpdateTag>[] = [
  {
    id: "change",
    header: "Change",
    render: (row) => <Badge variant="secondary" className={changeTone[row.change] ?? ""}>{row.change}</Badge>,
  },
  {
    id: "what",
    header: "Cached value",
    render: (row) => (
      <span className="flex flex-col gap-0.5">
        <span className="font-mono text-[12px]">
          {row.typeName}<span className="text-muted-foreground">.</span>{row.path}
        </span>
        <RecordId id={row.itemId} maxWidth={300} />
      </span>
    ),
  },
  { id: "values", header: "Was / is now", render: (row) => <ValueChange row={row} maxWidth={180} /> },
  {
    id: "records",
    header: "Records",
    align: "right",
    render: (row) => <span className="font-mono tabular-nums">{row.affectedRecords.toLocaleString()}</span>,
  },
  { id: "progress", header: "Rollout", render: (row) => <Rollout row={row} /> },
  { id: "detected", header: "Detected", render: (row) => <RelativeTime value={row.detectedUtc} /> },
];

/**
 * One change in full, in the bottom panel: both values unclipped, what it reaches, how the rollout stands, who
 * decided it, and the decision itself while it is still open.
 */
function TagDetail({ tag, onDecided }: { tag: DeliveryUpdateTag; onDecided: () => void }) {
  const decide = useDecideTags(onDecided);
  const pending = tag.status === "pending";

  return (
    <div className="flex flex-col gap-4 p-3" data-testid="delivery-cache-tag-detail">
      <div className="flex flex-wrap items-center gap-2">
        <Badge variant="secondary" className={changeTone[tag.change] ?? ""}>{tag.change}</Badge>
        <span className="font-mono text-[13px] font-medium">
          {tag.typeName}<span className="text-muted-foreground">.</span>{tag.path}
        </span>
        <span className="inline-flex items-center gap-0.5">
          <RecordId id={tag.itemId} maxWidth={420} />
          <CopyButton iconOnly label="Copy the cached record id" text={tag.itemId} testId="delivery-cache-tag-copy-id" />
        </span>
        <div className="grow" />
        {pending
          ? (
            <>
              <Button
                size="sm"
                disabled={decide.isPending}
                onClick={() => decide.mutate({ ids: [tag.tagId], approve: true })}
                data-testid="delivery-cache-tag-approve"
              >
                <Check /> Approve
              </Button>
              <Button
                size="sm"
                variant="outline"
                disabled={decide.isPending}
                onClick={() => decide.mutate({ ids: [tag.tagId], approve: false })}
                data-testid="delivery-cache-tag-reject"
              >
                <X /> Reject
              </Button>
            </>
          )
          : <span className="text-[12px] text-muted-foreground">{statusLabel[tag.status] ?? tag.status}</span>}
      </div>

      <div className="grid gap-4 [grid-template-columns:repeat(auto-fill,minmax(200px,1fr))]">
        <DetailPair label="Was">
          <span className="break-all font-mono text-[12px] text-muted-foreground line-through">{tag.oldValue ?? "-"}</span>
        </DetailPair>
        <DetailPair label="Is now">
          {tag.newValue === null
            ? <span className="font-mono text-[12px] text-destructive">gone</span>
            : <span className="break-all font-mono text-[12px]">{tag.newValue}</span>}
        </DetailPair>
        <DetailPair label="Versions">
          <span className="inline-flex items-center gap-1.5 font-mono text-[12px]">
            <span className="text-muted-foreground">{tag.fromVersion ?? "-"}</span>
            <ArrowRight className="size-3.5 shrink-0 text-muted-foreground" />
            <span>{tag.toVersion}</span>
          </span>
        </DetailPair>
        <DetailPair label="Delivered records reached">
          <span className="font-mono tabular-nums">{tag.affectedRecords.toLocaleString()}</span>
          <span className="text-[12px] text-muted-foreground">
            {pending ? " held back until decided" : ` · ${tag.remaining.toLocaleString()} remaining`}
          </span>
        </DetailPair>
        <DetailPair label="Rollout"><Rollout row={tag} /></DetailPair>
        <DetailPair label="Mode">{tag.mode === "auto" ? "automatic: approved as detected" : "needs approval"}</DetailPair>
        <DetailPair label="Detected"><RelativeTime value={tag.detectedUtc} /></DetailPair>
        <DetailPair label="Decided">
          {tag.decidedUtc === null
            ? <span className="text-muted-foreground">not yet</span>
            : (
              <span className="inline-flex flex-wrap items-center gap-1.5">
                <RelativeTime value={tag.decidedUtc} />
                {tag.decidedBy !== null && <span className="text-[12px] text-muted-foreground">by {tag.decidedBy}</span>}
              </span>
            )}
        </DetailPair>
        {tag.startedUtc !== null && (
          <DetailPair label="Rollout started"><RelativeTime value={tag.startedUtc} /></DetailPair>
        )}
        {tag.completedUtc !== null && (
          <DetailPair label="Rollout completed"><RelativeTime value={tag.completedUtc} /></DetailPair>
        )}
      </div>

      {tag.summary !== "" && <p className="text-[12px] text-muted-foreground">{tag.summary}</p>}
    </div>
  );
}

/**
 * The cache changes that reach records already delivered, by state, and the one decision this page makes: whether
 * such a change goes out. A row raises the change in the workbench bottom panel, with both values in full and the
 * decision buttons; selecting rows raises a toolbar in the table that decides them in bulk and says how many
 * delivered records the decision reaches before it is taken.
 */
export function DeliveryCacheApprovals({ pendingTotal }: { pendingTotal: number | undefined }) {
  const [status, setStatus] = useLocalStorageState("sqlflow.filters.delivery-cache.tag-status", "pending");
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [openTag, setOpenTag] = useState<DeliveryUpdateTag | null>(null);
  // The affected counts of every row seen so far, so a selection that spans pages still adds up.
  const [affected, setAffected] = useState<ReadonlyMap<string, number>>(new Map());
  const { ownedId, show, close } = useOwnedPanel(PANEL);

  const raise = useCallback((tag: DeliveryUpdateTag) => show(
    String(tag.tagId),
    `Change · ${tag.typeName}.${tag.path}`,
    <TagDetail tag={tag} onDecided={close} />,
  ), [show, close]);

  const onPageLoaded = useCallback((rows: DeliveryUpdateTag[]) => {
    setAffected((known) => {
      const next = new Map(known);
      for (const row of rows) {
        next.set(String(row.tagId), row.affectedRecords);
      }

      return next;
    });

    // A page that arrives with the open change in it carries its current state (a rollout that moved on).
    setOpenTag((current) => {
      const fresh = current === null ? undefined : rows.find((row) => row.tagId === current.tagId);
      return fresh ?? current;
    });
  }, []);

  const open = ownedId !== null;
  useEffect(() => {
    if (open && openTag !== null) {
      raise(openTag);
    }
  }, [open, openTag, raise]);

  const decide = useDecideTags(() => setSelected(new Set()));
  const decideSelected = (approve: boolean) => decide.mutate({ ids: [...selected].map(Number), approve });
  const reach = [...selected].reduce((sum, id) => sum + (affected.get(id) ?? 0), 0);
  const pending = status === "pending";
  const highlighted = open && openTag !== null ? String(openTag.tagId) : null;

  const toolbar = pending && selected.size > 0
    ? (
      <div className="flex flex-wrap items-center gap-2 border-b border-border bg-muted/40 px-3 py-1.5 text-[12px]" data-testid="delivery-cache-selection">
        <span className="font-medium">{selected.size} selected</span>
        <span className="text-muted-foreground">
          {reach.toLocaleString()} delivered record{reach === 1 ? "" : "s"} would be redelivered
        </span>
        <div className="grow" />
        <Button size="xs" disabled={decide.isPending} onClick={() => decideSelected(true)} data-testid="delivery-cache-approve">
          <Check /> Approve {selected.size}
        </Button>
        <Button size="xs" variant="outline" disabled={decide.isPending} onClick={() => decideSelected(false)} data-testid="delivery-cache-reject">
          <X /> Reject
        </Button>
        <Button size="xs" variant="ghost" disabled={decide.isPending} onClick={() => setSelected(new Set())}>
          Clear
        </Button>
      </div>
    )
    : undefined;

  return (
    <div className="flex flex-col gap-2">
      <ToggleGroup
        type="single"
        variant="outline"
        size="sm"
        value={status}
        onValueChange={(value) => {
          if (value !== "") {
            setStatus(value);
            setSelected(new Set());
          }
        }}
        aria-label="Change state"
        data-testid="delivery-cache-tag-status"
      >
        {statuses.map((option) => (
          <ToggleGroupItem
            key={option.value}
            value={option.value}
            className="h-8 gap-1.5 text-[13px]"
            data-testid={`delivery-cache-tag-status-${option.value}`}
          >
            {option.label}
            {option.value === "pending" && pendingTotal !== undefined && pendingTotal > 0 && (
              <span className="rounded-full bg-warning/15 px-1.5 font-mono text-[11px] tabular-nums text-warning">{pendingTotal}</span>
            )}
          </ToggleGroupItem>
        ))}
      </ToggleGroup>

      {pending && pendingTotal === 0
        ? (
          <Card className="gap-0 rounded-lg p-0">
            <EmptyState
              icon={<ShieldCheck />}
              title="Nothing needs approval"
              description="A change waits here only when a record already delivered to OSDU was built from the value that moved. Every change a version made, delivered or not, is under History."
              data-testid="delivery-cache-approvals-empty"
            />
          </Card>
        )
        : (
          <PagedTable
            queryKey={["delivery", "cache", "tags", status]}
            fetchPage={(page, pageSize) => deliveryApi.updateTags({ page, pageSize, status })}
            columns={columns}
            rowKey={(row) => String(row.tagId)}
            onRowClick={(row) => {
              setOpenTag(row);
              raise(row);
            }}
            rowSx={(row) => (String(row.tagId) === highlighted ? { backgroundColor: "var(--accent)" } : undefined)}
            selection={pending ? { selected, onChange: setSelected } : undefined}
            toolbar={toolbar}
            onPageLoaded={onPageLoaded}
            emptyMessage={pending ? "Nothing needs approval." : "No changes in this state."}
            data-testid="delivery-cache-tags-table"
          />
        )}
    </div>
  );
}
