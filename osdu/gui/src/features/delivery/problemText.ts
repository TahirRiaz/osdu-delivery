import { isApiError } from "@/api/client";

/** The API's problem detail for a failed call, or the error's own text: what a failure toast says. */
export function problemText(error: unknown): string {
  return isApiError(error) ? error.detail ?? error.title : String(error);
}
