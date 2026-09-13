import type { ReactNode } from "react";
import { Loader2 } from "lucide-react";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "../../api/client";
import type { ComputeTask, DeliveryTemplateDetail } from "../../api/delivery";
import { CorrelationError } from "../../components/CorrelationError";
import { RunStatusBadge } from "../../components/StatusBadge";
import { TemplateView } from "./TemplateView";

/** The API's problem detail for a failed call, or the error's own text: what a failure toast says. */
export function problemText(error: unknown): string {
  return isApiError(error) ? error.detail ?? error.title : String(error);
}

/** A failed call, rendered the one way API failures render: the problem with its correlation id. */
export function ProblemView({ error, testId }: { error: unknown; testId?: string }) {
  return isApiError(error)
    ? <CorrelationError error={error} data-testid={testId} />
    : <p className="text-[13px] text-destructive" data-testid={testId}>{String(error)}</p>;
}

/** Work under way: what is happening and, when a node does it, the task's status and the node that took it. */
export function TaskProgress({ label, task, testId }: { label: string; task?: ComputeTask; testId: string }) {
  return (
    <div className="flex flex-wrap items-center gap-2 text-[13px] text-muted-foreground" data-testid={testId}>
      <Loader2 className="size-4 animate-spin" />
      <span>{label}</span>
      {task !== undefined && <RunStatusBadge status={task.status} testId={`${testId}-status`} />}
      {task !== undefined && task.claimedByNode !== null && <span className="font-mono text-[11px]">{task.claimedByNode}</span>}
    </div>
  );
}

interface TemplateSheetProps {
  open: boolean;
  onClose: () => void;
  /** The kind the sheet is about, known before the template is laid out. */
  title: string;
  /** Where the template comes from. */
  description: string;
  /** Progress while the schema is fetched or laid out. */
  progress: ReactNode;
  /** A failure to show above the template, or instead of it. */
  problem: ReactNode;
  /** The template once it is laid out. */
  detail: DeliveryTemplateDetail | undefined;
  /** The schema being looked at before it is saved. */
  previewSchema?: Record<string, unknown>;
  actions?: ReactNode;
  /** Holds the sheet open while a save or a delete is in flight. */
  busy?: boolean;
  testId: string;
}

/** The side sheet every template opens in on the Templates page: a saved version, a schema fetched from OSDU, or an imported file. */
export function TemplateSheet({
  open, onClose, title, description, progress, problem, detail, previewSchema, actions, busy = false, testId,
}: TemplateSheetProps) {
  const waiting = detail === undefined && progress === null && problem === null;
  return (
    <Sheet open={open} onOpenChange={(next) => { if (!next && !busy) { onClose(); } }}>
      <SheetContent className="w-full gap-0 sm:max-w-5xl" data-testid={testId}>
        <SheetHeader>
          <SheetTitle className="break-all pr-6 font-mono text-[14px]">{title}</SheetTitle>
          <SheetDescription>{description}</SheetDescription>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-3 overflow-y-auto px-4 pb-4">
          {problem}
          {progress}
          {waiting && <Skeleton className="h-64 w-full rounded-lg" />}
          {detail !== undefined && <TemplateView detail={detail} previewSchema={previewSchema} actions={actions} />}
        </div>
      </SheetContent>
    </Sheet>
  );
}
