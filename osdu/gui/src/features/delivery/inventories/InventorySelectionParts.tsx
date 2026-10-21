import type { ReactNode } from "react";
import { CircleX, Filter, Loader2, RefreshCw, TriangleAlert } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Progress } from "@/components/ui/progress";
import { EmptyState } from "@/components/EmptyState";
import { ExplainTip } from "@/components/ExplainTip";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { TruncatedText } from "@/components/TruncatedText";
import { isApiError } from "@/api/client";
import type { ExplorerFilter } from "../../../api/explorer";
import type { Inventory, InventorySelection } from "../../../api/inventories";
import { RunRef } from "../DeliveryRefs";
import { RecordName } from "../RecordName";
import { isRecordReference } from "../osduDocument";
import { ExplorerFilterChips } from "../explorer/ExplorerFilters";
import { counted } from "./inventoryFormat";
import type { InventorySelectionState } from "./inventorySelection";

/** What a refusal of the control plane says, in its own words. */
function problemText(problem: unknown): string {
  return isApiError(problem) ? problem.detail ?? problem.title : String(problem);
}

/** How far a match got, in a line: the ids it asked of, or the records it read, of all it has to. */
function progressText(selection: InventorySelection): string {
  if (selection.status === "queued" || selection.direction === undefined) {
    return "waiting for its run to start";
  }

  const noun = selection.direction === "ids" ? "id" : "record";
  return `${selection.checked.toLocaleString("en-US")} of ${counted(selection.toCheck, noun)} read`;
}

/** Everything a match has to say, as its explanation holds it: what it found, how it read OSDU, who asked and when. */
function matchExplanation(inventory: Inventory, selection: InventorySelection, sentence: string): string {
  const lines = [
    `${selection.matched.toLocaleString("en-US")} of the ${counted(selection.candidates, "id")} the inventory serves hold ${sentence}.`,
  ];
  if (selection.changed > 0) {
    lines.push(`${counted(selection.changed, "id")} of them changed since the inventory's last build: OSDU's index holds another version than the inventory listed, so a removal leaves them for the next build.`);
  }

  if (selection.outside > 0) {
    lines.push(`${counted(selection.outside, "record")} of the kind hold it that the inventory holds no row of: written since its last build, or outside its query.`);
  }

  lines.push(selection.direction === "ids"
    ? `Read by asking the conditions of the inventory's ids, a chunk at a time: ${counted(selection.requests, "search request")} through ${inventory.flowName}'s own source.`
    : `Read by what the conditions find in the inventory's kind: ${counted(selection.requests, "search request")} through ${inventory.flowName}'s own source.`);
  if (inventory.read === "storage") {
    lines.push("The inventory reads storage, which lists records the search index does not hold; no condition can match those, so they are never selected.");
  }

  if (selection.query !== undefined) {
    lines.push(`Query: ${selection.query}`);
  }

  lines.push(...selection.notes);
  lines.push(`Selection ${selection.selectionId}, asked by ${selection.actor}${selection.runId === undefined ? "" : `, run ${selection.runId}`}.`);
  return lines.join("\n\n");
}

/**
 * Where the match of the conditions stands, at the end of their row: asking and matching with how far it got and its run,
 * when it matched (what it found and how, behind the mark beside it) with a way to read OSDU again, or what keeps it from
 * holding: a build since (with the way to match again, since its ids still fill the grid), a failure, a refusal, an age past
 * a week (whose way on is the panel standing in the grid's place).
 */
function SelectionStatus({ inventory, state, sentence }: { inventory: Inventory; state: InventorySelectionState; sentence: string }) {
  const { selection } = state;
  const again = (label: string, testId: string, quiet = false) => (
    <Button
      variant={quiet ? "ghost" : "link"}
      size="sm"
      className={quiet ? "size-6 p-0 text-muted-foreground hover:text-foreground" : "h-6 px-1 text-[12px]"}
      onClick={state.matchAgain}
      disabled={state.matchingAgain}
      title="Read OSDU again for these conditions: a new match of the inventory as its last build left it"
      aria-label={quiet ? label : undefined}
      data-testid={testId}
    >
      {quiet ? <RefreshCw className="size-3.5" /> : label}
    </Button>
  );

  switch (state.phase) {
    case "asking":
      return (
        <span className="inline-flex items-center gap-1.5 whitespace-nowrap text-[12px] text-muted-foreground" data-testid="inventory-selection-status" data-phase="asking">
          <Loader2 className="size-3.5 animate-spin" aria-hidden />
          Asking OSDU
        </span>
      );
    case "matching":
      return (
        <span className="inline-flex items-center gap-1.5 whitespace-nowrap text-[12px] text-muted-foreground" data-testid="inventory-selection-status" data-phase="matching">
          <Loader2 className="size-3.5 animate-spin text-info" aria-hidden />
          <span className="text-foreground">Matching in OSDU</span>
          <span>{`· ${progressText(selection!)}`}</span>
          {selection?.runId !== undefined && <RunRef runId={selection.runId} />}
        </span>
      );
    case "completed":
      return (
        <span className="inline-flex items-center gap-1.5 whitespace-nowrap text-[12px] text-muted-foreground" data-testid="inventory-selection-status" data-phase="completed">
          <ExplainTip title="The match" text={matchExplanation(inventory, selection!, sentence)} testId="inventory-selection-about">
            <button type="button" className="inline-flex items-center gap-1 rounded-sm outline-none hover:text-foreground focus-visible:text-foreground" data-testid="inventory-selection-matched">
              <Filter className="size-3.5" aria-hidden />
              {"Matched in OSDU "}
              <RelativeTime value={selection!.completedUtc ?? selection!.createdUtc} absolute={false} />
            </button>
          </ExplainTip>
          {selection!.changed > 0 && (
            <RichTooltip body="OSDU's index holds another version of these ids than the inventory listed: they changed since its last build, and a removal leaves them for the next build to look at.">
              <span className="cursor-help" data-testid="inventory-selection-changed">{`· ${counted(selection!.changed, "id")} changed since the build`}</span>
            </RichTooltip>
          )}
          {again("Match again", "inventory-selection-again", true)}
        </span>
      );
    case "stale":
      return (
        <span className="inline-flex items-center gap-1.5 whitespace-nowrap text-[12px]" data-testid="inventory-selection-status" data-phase="stale">
          <TriangleAlert className="size-3.5 text-warning" aria-hidden />
          <span>The inventory was built again since this match</span>
          {again("Match again", "inventory-selection-again")}
        </span>
      );
    // A match that holds no id says so here; the panel in the grid's place offers the way on, once.
    case "expired":
      return (
        <span className="inline-flex items-center gap-1.5 whitespace-nowrap text-[12px] text-muted-foreground" data-testid="inventory-selection-status" data-phase="expired">
          This match is older than a week
        </span>
      );
    case "failed":
      return (
        <span className="inline-flex min-w-0 items-center gap-1.5 text-[12px]" data-testid="inventory-selection-status" data-phase="failed">
          <CircleX className="size-3.5 shrink-0 text-destructive" aria-hidden />
          <ExplainTip title="Why the match failed" text={selection?.error ?? "The match ended without saying why."} testId="inventory-selection-error">
            <span className="cursor-help whitespace-nowrap">The match failed</span>
          </ExplainTip>
        </span>
      );
    case "refused":
      return (
        <span className="inline-flex min-w-0 items-center gap-1.5 text-[12px]" data-testid="inventory-selection-status" data-phase="refused">
          <CircleX className="size-3.5 shrink-0 text-destructive" aria-hidden />
          <ExplainTip title="Why these conditions cannot be matched" text={problemText(state.problem)} testId="inventory-selection-refusal">
            <span className="min-w-0 max-w-[28rem] cursor-help truncate">{problemText(state.problem)}</span>
          </ExplainTip>
        </span>
      );
    default:
      return null;
  }
}

/**
 * The conditions the inventory's ids are selected by, one chip each as the explorer draws them (a click changes one, its cross
 * drops it), with where their match stands at the end of the row. Nothing when there are none.
 */
export function InventoryConditionsBar({ inventory, conditions, onConditions, state, sentence }: {
  inventory: Inventory;
  conditions: ExplorerFilter[];
  onConditions: (conditions: ExplorerFilter[]) => void;
  state: InventorySelectionState;
  /** The conditions as a sentence, a source column by its name. */
  sentence: string;
}) {
  if (conditions.length === 0) {
    return null;
  }

  return (
    <ExplorerFilterChips
      partition={inventory.partition}
      request={{ kind: inventory.kind, filters: conditions }}
      onFilters={onConditions}
      trailing={<SelectionStatus inventory={inventory} state={state} sentence={sentence} />}
    />
  );
}

/**
 * What stands in the grid's place while the conditions have no ids to show: the match being asked for or reading OSDU, with
 * how far it got; or what keeps it from holding any (a failure, a refusal, an age past a week), with the way on.
 */
export function SelectionPanel({ state, sentence, onClear }: { state: InventorySelectionState; sentence: string; onClear: () => void }) {
  const { selection } = state;
  const clear = (
    <Button variant="ghost" size="sm" onClick={onClear} data-testid="inventory-selection-clear">
      Clear the conditions
    </Button>
  );
  switch (state.phase) {
    case "asking":
      return (
        <EmptyState
          icon={<Loader2 className="animate-spin" />}
          title="Asking OSDU"
          description={`Opening the match of ${sentence}.`}
          data-testid="inventory-selection-panel"
        />
      );
    case "matching": {
      const share = selection === undefined || selection.toCheck === 0 ? 0 : Math.min(100, (selection.checked / selection.toCheck) * 100);
      return (
        <EmptyState
          icon={<Filter />}
          title="Matching in OSDU"
          description={(
            <span className="flex flex-col items-center gap-2">
              <span>{`Asking OSDU's search which ids of the inventory hold ${sentence}, through the inventory flow's own source.`}</span>
              <Progress value={share} className="h-1.5 w-64" aria-label="How far the match got" data-testid="inventory-selection-progress" />
              <span className="inline-flex items-center gap-1.5 text-[12px]">
                {selection === undefined ? "waiting for its run to start" : progressText(selection)}
                {selection?.runId !== undefined && <RunRef runId={selection.runId} />}
              </span>
            </span>
          )}
          data-testid="inventory-selection-panel"
        />
      );
    }
    case "failed":
      return (
        <EmptyState
          icon={<CircleX />}
          title="The match failed"
          description={selection?.error ?? "The match ended without saying why."}
          action={(
            <span className="flex items-center gap-2">
              <Button size="sm" onClick={state.matchAgain} disabled={state.matchingAgain} data-testid="inventory-selection-retry">Try again</Button>
              {clear}
            </span>
          )}
          data-testid="inventory-selection-panel"
        />
      );
    case "refused":
      return (
        <EmptyState
          icon={<CircleX />}
          title="These conditions cannot be matched"
          description={problemText(state.problem)}
          action={clear}
          data-testid="inventory-selection-panel"
        />
      );
    case "expired":
      return (
        <EmptyState
          icon={<Filter />}
          title="This match is older than a week"
          description="Its ids were retired with it. Match the conditions again to see which ids hold them now."
          action={<Button size="sm" onClick={state.matchAgain} disabled={state.matchingAgain} data-testid="inventory-selection-retry">Match again</Button>}
          data-testid="inventory-selection-panel"
        />
      );
    default:
      return null;
  }
}

/**
 * What an id held at one property its selection's conditions ask: its values, one after another, an OSDU id named as a reader
 * knows it; every value on hover. A quiet dash where it held none.
 */
export function SelectionValueCell({ values, testId }: { values: string[] | undefined; testId: string }): ReactNode {
  if (values === undefined || values.length === 0) {
    return <span className="text-[12px] text-muted-foreground">-</span>;
  }

  if (values.length === 1 && isRecordReference(values[0])) {
    return <RecordName id={values[0]} className="text-[12px]" testId={testId} />;
  }

  return (
    <span className="inline-flex min-w-0 max-w-full" data-testid={testId}>
      <TruncatedText text={values.join(", ")} maxWidth={260} className="text-[12.5px]" />
    </span>
  );
}
