import { useQuery, type UseQueryOptions } from "@tanstack/react-query";
import { datasourceApi } from "@/api/endpoints";
import type { ComputeTask, ComputeTaskAccepted } from "@/api/types";

const TERMINAL = new Set(["succeeded", "failed", "cancelled", "skipped"]);

/** How long one poll waits on the control plane for the task to finish before it answers with the task as it stands. */
const WAIT_MS = 10_000;

/**
 * The query that polls one compute task a node runs for the delivery pages (a target probe, a read-back, a removal)
 * until it has finished, through SQLFlow's compute task endpoint. Each request long-polls, so the result arrives in
 * one round trip once a node has produced it. Shared by {@link useComputeTask} and by a page that polls several at once.
 */
export function computeTaskQuery(taskId: string | null): UseQueryOptions<ComputeTask> {
  return {
    queryKey: ["compute-tasks", taskId],
    queryFn: ({ signal }) => {
      if (taskId === null) {
        throw new Error("There is no compute task to poll.");
      }

      return datasourceApi.task(taskId, WAIT_MS, signal);
    },
    enabled: taskId !== null,
    refetchInterval: (query) => {
      const task = query.state.data;
      return task !== undefined && TERMINAL.has(task.status) ? false : 250;
    },
  };
}

/** Polls one compute task to its end; see {@link computeTaskQuery}. */
export function useComputeTask(taskId: string | null) {
  return useQuery(computeTaskQuery(taskId));
}

/** The task's result as indented JSON, once a node produced one; null before that, and for a task that reported none. */
export function taskResultJson(task: ComputeTask | undefined): string | null {
  return task === undefined || task.result === null || task.result === undefined
    ? null
    : JSON.stringify(task.result, null, 2);
}

export function isTerminalTask(task: ComputeTask | undefined): boolean {
  return task !== undefined && TERMINAL.has(task.status);
}

/** A node task that ended without an answer (failed, cancelled or skipped), with what it said. */
export class ComputeTaskError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "ComputeTaskError";
  }
}

/**
 * Queues a node task and waits for its answer, a long poll at a time, until the task ends or the caller stops waiting
 * (`signal`): the answer of a task that succeeded, and a {@link ComputeTaskError} with the task's words for one that did not.
 * What a query that is one node read runs, so the read is kept by what it asked like any other query.
 */
export async function runComputeTask<T>(queue: () => Promise<ComputeTaskAccepted>, signal: AbortSignal): Promise<T> {
  const accepted = await queue();
  for (;;) {
    const task = await datasourceApi.task(accepted.taskId, WAIT_MS, signal);
    if (TERMINAL.has(task.status)) {
      if (task.status === "succeeded") {
        return task.result as T;
      }

      throw new ComputeTaskError(task.error ?? `The read ended ${task.status} without an answer.`);
    }
  }
}
