import { Badge } from "@/components/ui/badge";
import { Card } from "@/components/ui/card";
import { CodeView } from "@/components/CodeView";
import type { ComputeTask } from "@/api/types";
import { isTerminalTask, taskResultJson } from "./useComputeTask";

/**
 * A compute task a node runs for the record page (a removal, a read of its source rows) as it stands: what it is, its
 * status and the node that claimed it, its error when it failed, and its result as JSON once it has one.
 */
export function TaskResultCard({ label, task, testId }: { label: string; task: ComputeTask | undefined; testId: string }) {
  const json = isTerminalTask(task) ? taskResultJson(task) : null;
  return (
    <Card className="gap-2 rounded-lg p-3" data-testid={testId}>
      <div className="flex flex-wrap items-center gap-2 text-[13px] font-medium">
        {label}
        <Badge variant="outline">{task?.status ?? "queued"}</Badge>
        {task?.claimedByNode && <span className="font-mono text-[11px] text-muted-foreground">{task.claimedByNode}</span>}
      </div>
      {task?.error && <p className="text-[13px] text-destructive">{task.error}</p>}
      {json !== null && <CodeView value={json} language="json" height={360} data-testid={`${testId}-json`} />}
    </Card>
  );
}
