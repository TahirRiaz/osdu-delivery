import { useHref } from "react-router-dom";
import { AppWindow, BookOpenCheck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/EmptyState";
import { IconAction } from "@/components/IconAction";
import { deliveryApi, type DeliveryFlowScope, type DeliveryRecord, type DeliveryRecordRef } from "../../api/delivery";
import { OsduRecordPanel } from "./OsduRecordView";
import { ProblemView } from "./TemplateSheet";
import { isTerminalTask } from "./useComputeTask";
import type { RecordOsduRead } from "./useRecordOsduRead";

/**
 * A record as OSDU holds it, read through its flow's route on a node, in the OSDU inspector: the read's controls, and
 * before any read what a read would show or why there is nothing to read. The record page's OSDU tab shows it under the
 * page's header, with a way to open it in a window of its own; that window shows it alone, filling the window.
 */
export function RecordOsduView({ record, deliveryRef, pipelineId, flowScope, canOperate, disabled, osdu, fill = false, popout = false }: {
  record: DeliveryRecord;
  deliveryRef: DeliveryRecordRef;
  pipelineId: string | null;
  /** The record's ledger: the interface and partition a record opened from this one is read through. */
  flowScope: DeliveryFlowScope;
  /** Whether the viewer holds the operate scope a node task takes. */
  canOperate: boolean;
  /** Whether another request of the page is in flight, which holds a read back until it lands. */
  disabled: boolean;
  osdu: RecordOsduRead;
  /** Whether the inspector fills its flex column parent: in a window of its own. */
  fill?: boolean;
  /** Whether to offer the view in a window of its own; not where it already is one. */
  popout?: boolean;
}) {
  const windowHref = useHref(`/delivery/records/${deliveryRef.flowId}/${deliveryRef.deliveryKey}/osdu`);
  const canActOnTarget = record.targetId !== null && record.status !== "deleted";
  const reading = osdu.queueing || (osdu.taskId !== null && !isTerminalTask(osdu.task.data));
  // The read's controls: on their own above the empty state, and on the inspector's location bar once there is a read.
  const actions = (
    <>
      <Button
        variant="outline"
        size="sm"
        className="h-7"
        onClick={osdu.read}
        disabled={disabled || reading || !canActOnTarget || !canOperate}
        title={canOperate ? "Reads the record as OSDU holds it now, through its flow's route and credentials, on a node. Nothing is written." : "A read runs on a node, which takes the operate scope."}
        data-testid="record-osdu-read"
      >
        <BookOpenCheck />
        {osdu.taskId === null ? "Read" : "Read again"}
      </Button>
      {popout && (
        <IconAction
          label="Open the OSDU explorer in a window of its own"
          icon={<AppWindow />}
          variant="ghost"
          className="size-7"
          // One window per record: opening it again brings that window back rather than stacking another.
          onClick={() => window.open(windowHref, `osdu-record-${deliveryRef.deliveryKey}`, "popup=yes,width=1280,height=900")}
          data-testid="record-osdu-popout"
        />
      )}
    </>
  );

  return (
    <div className={fill ? "flex min-h-0 flex-1 flex-col gap-2" : "flex flex-col gap-2"} data-testid="record-osdu">
      {osdu.taskId === null && <div className="flex flex-wrap items-center gap-1">{actions}</div>}
      {osdu.taskId === null && (
        <EmptyState
          icon={<BookOpenCheck />}
          title={canActOnTarget ? "Not read yet" : record.status === "deleted" ? "Removed from OSDU" : "No OSDU id yet"}
          description={canActOnTarget
            ? "Read shows the record as OSDU holds it now, through its flow's route and credentials, on a node. Nothing is written."
            : record.status === "deleted"
              ? "The record was removed, so there is nothing of this flow's to read."
              : "The record has not been planned for delivery, so there is nothing to read."}
          data-testid="record-osdu-empty"
        />
      )}
      {osdu.taskId !== null && (osdu.task.isError
        ? <ProblemView error={osdu.task.error} testId="record-osdu-error" />
        : (
          <OsduRecordPanel
            key={osdu.taskId}
            pipelineId={pipelineId}
            flowScope={flowScope}
            task={osdu.task.data}
            targetId={record.targetId ?? record.deliveryKey}
            readRootVersion={canActOnTarget && canOperate ? (version) => deliveryApi.read(deliveryRef, version) : undefined}
            ledgerVersion={record.targetVersion}
            actions={actions}
            fill={fill}
          />
        ))}
    </div>
  );
}
