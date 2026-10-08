import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Eraser } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { pipelineApi } from "@/api/endpoints";
import type { PagedResult } from "@/api/types";
import { PagedTable, type Column } from "@/components/PagedTable";
import { TruncatedText } from "@/components/TruncatedText";
import { TriggerRunDialog } from "@/features/runs/TriggerRunDialog";
import { artifactApi, type DeliveryArtifactCounts, type DeliveryOpenUndoRecord } from "../../api/artifacts";
import { deliveryRecordRoute, type DeliveryFlowScope, type DeliveryRecordStatus } from "../../api/delivery";
import { ArtifactStateCount, ArtifactStateGlyph } from "./ArtifactMarks";
import { RecordStatusBadge } from "./DeliveryBadges";
import { CompactTime } from "./RecordCells";
import { NoteMark } from "./RecordArtifacts";

/** How many tries the sweep gives a failed undo before it leaves it for an undo run with force (ArtifactLimits.MaxUndoAttempts). */
const MAX_UNDO_TRIES = 10;

const NO_RECORDS: PagedResult<DeliveryOpenUndoRecord> = { items: [], page: 1, pageSize: 50, total: 0 };

/** The query of a flow's open undos as counted for the source, which the card and the undo button share. */
function useOpenUndoCounts(pipelineId: string, scope: DeliveryFlowScope, enabled: boolean) {
  return useQuery({
    queryKey: ["delivery", "undos", pipelineId, scope.interfaceName, scope.partition, "counts"],
    queryFn: () => artifactApi.openUndos(pipelineId, scope, 1, 1),
    enabled,
    refetchInterval: 15000,
  });
}

/** The counts of one record's open artifacts, the ones that are not zero, each with its glyph. */
function RecordCounts({ row }: { row: DeliveryOpenUndoRecord }) {
  const parts = [
    { state: "failed", exhausted: true, count: row.failedExhausted, label: "no tries left" },
    { state: "failed", exhausted: false, count: row.failed - row.failedExhausted, label: "failed" },
    { state: "due", exhausted: false, count: row.due, label: "due" },
    { state: "pending", exhausted: false, count: row.intent + row.pending, label: "under way" },
  ].filter((part) => part.count > 0);
  return (
    <span className="flex flex-wrap gap-x-2.5 gap-y-0.5">
      {parts.map((part) => (
        <ArtifactStateCount key={part.label} state={part.state} exhausted={part.exhausted} count={part.count} label={part.label} />
      ))}
    </span>
  );
}

const columns: Column<DeliveryOpenUndoRecord>[] = [
  {
    id: "record",
    header: "Record",
    fill: true,
    floor: 200,
    render: (row) => (
      <span className="inline-flex min-w-0 max-w-full flex-col leading-tight">
        <TruncatedText text={row.label ?? row.sourceKey ?? row.deliveryKey} maxWidth={420} className="text-[13px]" />
        {row.targetId !== null && <TruncatedText text={row.targetId} mono maxWidth={420} className="text-[11.5px] text-muted-foreground" title="OSDU id" />}
      </span>
    ),
  },
  {
    id: "status",
    header: "Status",
    render: (row) => (row.status === null
      ? <span className="text-[12px] text-muted-foreground">not in the ledger</span>
      : <RecordStatusBadge status={row.status as DeliveryRecordStatus} testId="open-undo-record-status" />),
  },
  { id: "open", header: "Left to undo", render: (row) => <RecordCounts row={row} /> },
  { id: "oldest", header: "Since", render: (row) => <CompactTime value={row.oldestUtc} caption="The oldest written" className="text-[12px]" /> },
  {
    id: "next",
    header: "Next try",
    render: (row) => (row.failedExhausted > 0 && row.nextUndoUtc === null
      ? <span className="text-[12px] text-muted-foreground">none left</span>
      : <CompactTime value={row.nextUndoUtc} caption="The sweep tries it again" className="text-[12px]" absent="next run" />),
  },
];

/** What the card's line says the open artifacts are, the counts that are not zero. */
function CountsLine({ counts }: { counts: DeliveryArtifactCounts }) {
  const retried = counts.failed - counts.failedExhausted;
  return (
    <span className="flex flex-wrap items-center gap-x-3 gap-y-1">
      {counts.due > 0 && <ArtifactStateCount state="due" count={counts.due} label="due" />}
      {retried > 0 && <ArtifactStateCount state="failed" count={retried} label="failed, tried again" />}
      {counts.failedExhausted > 0 && <ArtifactStateCount state="failed" exhausted count={counts.failedExhausted} label="failed, no tries left" />}
      {counts.intent + counts.pending > 0 && <ArtifactStateCount state="pending" count={counts.intent + counts.pending} label="of deliveries under way" />}
    </span>
  );
}

/**
 * A flow's open undos on its overview (docs/atomic-delivery-plan.md, When the undo runs): what unfinished deliveries left in
 * OSDU that an undo still has to take, counted for the source, those whose undo has used every try called out, then the
 * records holding them, a page at a time in a grid that scrolls in place, the ones with no tries left first. Each row opens
 * the record on its artifacts. Shown only while something is due or failed: a delivery under way has its own.
 */
export function OpenUndosCard({ pipelineId, scope, many, onSelectInterface, ready }: {
  pipelineId: string;
  /** False until the flow's ledger in view is known (its partition), when nothing is asked. */
  ready: boolean;
  /** The interface in view (its records are listed) and the partition. */
  scope: DeliveryFlowScope;
  many: boolean;
  onSelectInterface: (name: string) => void;
}) {
  const navigate = useNavigate();
  const counts = useOpenUndoCounts(pipelineId, scope, ready);
  const data = counts.data;
  if (data === undefined || data.counts.toUndo === 0) {
    return null;
  }

  const explanation = [
    "A delivery that did not complete is undone: what it created in OSDU is removed (reversibly where the route can) and a version it wrote over gets the earlier version back.",
    "The worker undoes it when the try ends; the sweep at the end of every deliver and drain run undoes the rest, and tries a refused undo again with backoff,",
    `up to ${MAX_UNDO_TRIES} times. One with no tries left waits for an operator: once what OSDU refused is fixed (each artifact's note says what it answered), run Undo unfinished with force.`,
    "Newer work for a record waits while what an earlier delivery left is not undone.",
  ].join(" ");
  const listed = data.interfaces.find((entry) => entry.interface === scope.interfaceName) ?? (many ? undefined : data.interfaces[0]);
  return (
    <Card className="gap-2 overflow-hidden rounded-lg p-0" data-testid="delivery-open-undos">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 px-3 pt-3 text-[13px]">
        <span className="inline-flex items-center gap-1.5 font-medium">
          <ArtifactStateGlyph state="failed" exhausted={data.counts.failedExhausted > 0} />
          Unfinished deliveries to undo
        </span>
        <span className="font-mono text-[12px] tabular-nums" data-testid="delivery-open-undos-total">{data.counts.toUndo.toLocaleString()}</span>
        <CountsLine counts={data.counts} />
        <NoteMark note={explanation} title="What this means" testId="delivery-open-undos-why" />
      </div>
      {data.counts.failedExhausted > 0 && (
        <p className="px-3 text-[12.5px]" data-testid="delivery-open-undos-exhausted">
          {`${data.counts.failedExhausted.toLocaleString()} ${data.counts.failedExhausted === 1 ? "has" : "have"} used all ${MAX_UNDO_TRIES} tries and ${data.counts.failedExhausted === 1 ? "is" : "are"} no longer tried by the sweep: Undo unfinished with force tries ${data.counts.failedExhausted === 1 ? "it" : "them"} again.`}
        </p>
      )}
      {many && (
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 px-3 text-[12px]" data-testid="delivery-open-undos-interfaces">
          {data.interfaces.map((entry) => (
            <button
              key={entry.flowId}
              type="button"
              onClick={() => entry.interface !== null && onSelectInterface(entry.interface)}
              className={entry.interface === scope.interfaceName ? "font-medium text-foreground" : "text-muted-foreground hover:text-foreground"}
            >
              {`${entry.interface ?? "(single)"} `}
              <span className="font-mono tabular-nums">{entry.counts.toUndo.toLocaleString()}</span>
            </button>
          ))}
        </div>
      )}
      {listed !== undefined && (
        <div
          className={[
            "min-w-0 [&_[data-slot=table-container]]:max-h-72 [&_[data-slot=table-container]]:overflow-y-auto",
            "[&_th]:sticky [&_th]:top-0 [&_th]:z-10 [&_th]:bg-card [&_th]:shadow-[inset_0_-1px_0_var(--border)]",
            "[&_td]:py-1",
          ].join(" ")}
        >
          <PagedTable
            queryKey={["delivery", "undos", pipelineId, scope.interfaceName, scope.partition, "records"]}
            fetchPage={(page, pageSize) => artifactApi.openUndos(pipelineId, scope, page, pageSize).then((answer) => answer.records ?? NO_RECORDS)}
            columns={columns}
            rowKey={(row) => row.deliveryKey}
            onRowClick={(row) => navigate(`${deliveryRecordRoute({ flowId: row.flowId, deliveryKey: row.deliveryKey })}?tab=artifacts`)}
            pollMs={15000}
            emptyMessage="This interface has nothing left to undo."
            data-testid="delivery-open-undos-table"
          />
        </div>
      )}
    </Card>
  );
}

/**
 * The flow's undo, wherever its other operations are offered: the platform's one run dialog, opened on the `undo` operation
 * of the flow, with force picked when an undo has used every try (the operator can change both before it runs).
 */
export function UndoUnfinishedButton({ pipelineId, flowName, scope, ready }: {
  pipelineId: string;
  flowName: string;
  scope: DeliveryFlowScope;
  /** False until the flow's ledger in view is known (its partition), when nothing is asked. */
  ready: boolean;
}) {
  const [open, setOpen] = useState(false);
  const counts = useOpenUndoCounts(pipelineId, scope, ready);
  const pipeline = useQuery({
    queryKey: ["pipelines", "detail", pipelineId],
    queryFn: () => pipelineApi.getById(pipelineId),
    staleTime: 60000,
  });
  const exhausted = counts.data?.counts.failedExhausted ?? 0;
  return (
    <>
      <Button
        variant="outline"
        size="sm"
        onClick={() => setOpen(true)}
        disabled={pipeline.data === undefined}
        title="Undo what deliveries that did not complete left in OSDU, now rather than at the end of the next deliver run: what they created is removed (reversibly), a version they wrote over gets the earlier one back, and each undo is written to the record's history. With force it also retries the undos that used every try."
        data-testid="delivery-undo"
      >
        <Eraser />
        Undo unfinished
      </Button>
      {open && pipeline.data !== undefined && (
        <TriggerRunDialog
          open
          onClose={() => setOpen(false)}
          repoId={pipeline.data.repoId}
          flowName={flowName}
          flowId={pipelineId}
          flowKind="delivery"
          initialOperation="undo"
          initialValues={null}
          initialPayload={exhausted > 0 ? { force: true } : null}
        />
      )}
    </>
  );
}
