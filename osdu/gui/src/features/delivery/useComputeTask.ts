import { useQuery } from "@tanstack/react-query";
import { computeApi, type ComputeTask } from "../../api/delivery";

const TERMINAL = new Set(["succeeded", "failed", "cancelled", "skipped"]);

/** Polls one ad-hoc compute task (a probe, a read-back, a delete) until a node has finished it. */
export function useComputeTask(taskId: string | null) {
  return useQuery<ComputeTask>({
    queryKey: ["compute-tasks", taskId],
    queryFn: () => computeApi.task(taskId!),
    enabled: taskId !== null,
    refetchInterval: (query) => {
      const task = query.state.data;
      return task && TERMINAL.has(task.status) ? false : 1500;
    },
  });
}

/** The task's JSON result parsed, or null while it is not there (or unreadable). */
export function taskResult<T>(task: ComputeTask | undefined): T | null {
  if (!task || task.resultJson === null) {
    return null;
  }

  try {
    return JSON.parse(task.resultJson) as T;
  } catch {
    return null;
  }
}

export function isTerminalTask(task: ComputeTask | undefined): boolean {
  return task !== undefined && TERMINAL.has(task.status);
}
