import { isApiError } from "@/api/client";
import { cn } from "@/lib/utils";
import { ProblemView } from "../TemplateSheet";
import { explorerErrorText } from "./explorerModel";

/** A failed read, quietly, where a short line fits (a panel, the rail). */
export function ExplorerTaskErrorText({ error, className }: { error: unknown; className?: string }) {
  return <p className={cn("text-[12px] text-destructive", className)} data-testid="explorer-read-error">{explorerErrorText(error)}</p>;
}

/** A failed read in full, where there is room: the API's problem with its correlation id, or what the node said. */
export function ExplorerProblem({ error }: { error: unknown }) {
  return isApiError(error)
    ? <ProblemView error={error} testId="explorer-problem" />
    : <p className="text-[13px] text-destructive whitespace-pre-wrap" data-testid="explorer-problem">{explorerErrorText(error)}</p>;
}
