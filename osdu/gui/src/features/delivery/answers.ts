import { isApiError } from "@/api/client";

// What the control plane answers to a read a person asks for (a record read from OSDU, its rows in the ingestion tables, a
// preview, a probe): the answer itself, in the response, or a problem saying why there is none. Nothing is queued, so
// there is nothing to poll.

/** A read that failed, in one line: the problem the control plane answered with, or what failed on the way to it. */
export function failureText(error: unknown): string {
  if (isApiError(error)) {
    return error.detail ?? error.title;
  }

  return error instanceof Error ? error.message : String(error);
}

/** A read as a page keeps it once it has answered: its result, or why there is none. */
export interface Settled<T> {
  result: T | null;
  failure: string | null;
}

/** Waits for a read and keeps what it answered, a failure included, so several reads can be shown side by side. */
export async function settle<T>(answer: Promise<T>): Promise<Settled<T>> {
  try {
    return { result: await answer, failure: null };
  } catch (error) {
    return { result: null, failure: failureText(error) };
  }
}
