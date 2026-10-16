import { useMemo, useState, type ReactNode } from "react";
import { ChevronLeft, ChevronRight, KeyRound, RotateCcw, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import { ResizableHandle, ResizablePanel, ResizablePanelGroup } from "@/components/ui/resizable";
import { ConfirmDialog } from "@/components/ConfirmDialog";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import type { DeliveryDimensionKey } from "../../../../api/delivery";
import type { DimensionCompose, DimensionExample, DimensionKeyCandidate } from "../../../../api/explorer";
import { failureText } from "../../answers";
import { BlueprintDiagram } from "../../dimensions/BlueprintDiagram";
import { examplesOf, layoutOf } from "../../dimensions/blueprintModel";
import { kindParts } from "../explorerModel";
import { BuilderColumns } from "./BuilderColumns";
import { BuilderExpression } from "./BuilderExpression";
import { BuilderFilterDialog } from "./BuilderFilterDialog";
import { applyPlan, typeName, type PickPlan } from "./dimensionDraft";
import type { DimensionBuild } from "./useDimensionBuild";

/** The suggestions shown before the rest are asked for. */
const SHOWN_SUGGESTIONS = 4;

/** The example row a dimension's pages draw a key from, made of what the builder's example read. */
function keyOf(example: DimensionExample): DeliveryDimensionKey {
  return {
    keyId: 0,
    key: example.key,
    label: example.label,
    labelFrom: example.labelFrom,
    valueId: null,
    value: example.value,
    leftOut: null,
    note: example.note,
    count: example.records ?? 0,
    filterable: example.filter !== null,
    filter: example.filter,
    firstSeenBuildId: 0,
    firstSeenUtc: "",
    valueSinceBuildId: 0,
    removedBuildId: null,
    removedUtc: null,
    attributes: example.attributes,
  };
}

/** A suggestion as a chip names it: its last property, and the type of record it names. */
function suggestionLabel(candidate: DimensionKeyCandidate): string {
  const property = candidate.path.split(".").pop() ?? candidate.path;
  const named = candidate.names.find((name) => name.includes("--"));
  return named === undefined ? property : `${property} · ${typeName(named)}`;
}

/**
 * The dimension being built in the explorer (osdu/docs/reference/concepts/explorer.md, Building a dimension), docked
 * beside the records it is built from: its name and kind, the keys the kind's template suggests, the example key every
 * part is filled with (the record in view's own first), the table with its columns and settings, and the YAML with what
 * is wrong with it. Every pick is made in the records themselves; the panel says what to pick next.
 */
export function DimensionBuildPanel({ build, startKind }: {
  build: DimensionBuild;
  /** The kind a build started over reads: the explorer's place, or the record in view's kind; empty for none. */
  startKind: string;
}) {
  const draft = build.draft;
  const [pointed, setPointed] = useState<string | null>(null);
  const [showRow, setShowRow] = useState(false);
  const [closing, setClosing] = useState(false);
  if (draft === null) {
    return null;
  }

  const compose = build.compose;
  const suggestions = build.candidates.data;
  const keyed = draft.path !== null && draft.path !== "";
  const parts = draft.kind === "" ? null : kindParts(draft.kind);
  const exampleLabel = build.exampleKey === null
    ? null
    : compose.data?.example?.key === build.exampleKey ? compose.data.example.value ?? build.exampleKey : build.exampleKey;
  const notes = [
    ...(suggestions?.missing ? [suggestions.missing] : []),
    ...(build.keys.data?.answer.notes ?? []),
    ...(build.keys.data?.answer.refusal ? [`The search service refused the keys: ${build.keys.data.answer.refusal}`] : []),
    ...(compose.data?.exampleProblem ? [`The example row could not be made: ${compose.data.exampleProblem}`] : []),
    ...(compose.data?.example?.notes ?? []),
    ...(build.keys.isError ? [`The keys could not be read: ${failureText(build.keys.error)}`] : []),
    ...(build.candidates.isError ? [`The template's keys could not be read: ${failureText(build.candidates.error)}`] : []),
  ];
  const makeKey = (candidate: DimensionKeyCandidate) => {
    const plan: PickPlan = { action: "key", kind: draft.kind, key: candidate.path, keyExample: null, path: candidate.path, steps: [], questions: [] };
    build.update(applyPlan(draft, plan, [], [], candidate.names));
  };

  let next: ReactNode;
  if (draft.kind === "") {
    next = "Pick a type or a kind on the left, or open a record: a dimension reads the records of one kind, and its key is a value they hold.";
  } else if (!keyed) {
    next = (
      <>
        Pick the key: open a record of the kind, and on a value whose distinct values the dimension holds choose <span className="font-medium text-foreground">Make it the key</span>.
        The values marked <KeyRound className="inline size-3.5 text-primary" aria-label="with a key" /> name other records, as a key most often does.
      </>
    );
  } else if (draft.label.length === 0 && draft.attributes.length === 0) {
    next = (
      <>
        Open the record the key names from its link in a record of the kind, and choose <span className="font-medium text-foreground">Read it as the value</span> on its name.
        Follow its links further to read attributes from the records it names. Without a value, each key is its own value.
      </>
    );
  } else {
    next = null;
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col" data-testid="builder-panel">
      <div className="flex flex-col gap-2 border-b px-3 py-2">
        <div className="flex min-w-0 items-center gap-2">
          <h2 className="text-[14px] font-semibold">Build a dimension</h2>
          {parts !== null && (
            <RichTooltip title="Reads the records of" body={draft.kind} mono>
              <span className="inline-flex min-w-0 items-center gap-1.5 rounded-full border px-2 py-0.5 text-[12px]" data-testid="builder-kind">
                <span className="truncate">{typeName(parts.entityType)}</span>
                <span className="font-mono text-muted-foreground">{parts.version}</span>
              </span>
            </RichTooltip>
          )}
          <span className="ml-auto flex items-center">
            <RichTooltip body={startKind === "" ? "Start over: a new dimension." : `Start over: a new dimension of ${startKind}.`}>
              <Button variant="ghost" size="icon-xs" onClick={() => build.start(startKind)} aria-label="Start over" data-testid="builder-restart">
                <RotateCcw />
              </Button>
            </RichTooltip>
            <RichTooltip body="Close the builder. Copy the YAML first: the draft is not kept.">
              <Button variant="ghost" size="icon-xs" onClick={() => (keyed ? setClosing(true) : build.stop())} aria-label="Close the builder" data-testid="builder-close">
                <X />
              </Button>
            </RichTooltip>
          </span>
        </div>
        <Input
          value={draft.name}
          onChange={(event) => build.update({ ...draft, name: event.target.value })}
          placeholder="Name the dimension"
          className="h-8 font-mono text-[13px]"
          aria-label="The dimension's name"
          data-testid="builder-name"
        />
        {suggestions !== undefined && suggestions.keys.length > 0 && (
          <div className="flex min-w-0 flex-wrap items-center gap-1" data-testid="builder-suggestions">
            <RichTooltip body={`The values of ${suggestions.template?.kind ?? draft.kind} its template says name other records, the likeliest key first.`}>
              <span className="mr-1 inline-flex items-center gap-1 text-[11.5px] text-muted-foreground">
                <KeyRound className="size-3.5 text-primary" aria-hidden />
                Keys
              </span>
            </RichTooltip>
            {suggestions.keys.slice(0, SHOWN_SUGGESTIONS).map((candidate) => (
              <SuggestionChip key={candidate.path} candidate={candidate} picked={draft.path === candidate.path} onPick={() => makeKey(candidate)} />
            ))}
            {suggestions.keys.length > SHOWN_SUGGESTIONS && (
              <DropdownMenu>
                <DropdownMenuTrigger asChild>
                  <button type="button" className="rounded border border-dashed px-1.5 py-0.5 text-[11.5px] text-muted-foreground hover:text-foreground" data-testid="builder-suggestions-more">
                    {`+${suggestions.keys.length - SHOWN_SUGGESTIONS} more`}
                  </button>
                </DropdownMenuTrigger>
                <DropdownMenuContent align="start" className="max-h-80 w-96 overflow-auto">
                  {suggestions.keys.slice(SHOWN_SUGGESTIONS).map((candidate) => (
                    <DropdownMenuItem key={candidate.path} onSelect={() => makeKey(candidate)} className="flex flex-col items-start gap-0" data-testid="builder-suggestion-more">
                      <span className="font-mono text-[12px]">{candidate.path}</span>
                      <span className="text-[11.5px] text-muted-foreground">
                        {`names ${candidate.names.map((name) => (name.includes("--") ? typeName(name) : name)).join(" or ")}${candidate.repeated ? ", several to a record" : ""}`}
                      </span>
                    </DropdownMenuItem>
                  ))}
                </DropdownMenuContent>
              </DropdownMenu>
            )}
          </div>
        )}
        {keyed && (
          <ExampleControl
            label={exampleLabel}
            title={build.exampleKey}
            own={build.exampleKey !== null && build.exampleKey === build.recordKey}
            at={build.exampleAt}
            count={build.examples.length}
            onStep={build.stepExample}
          />
        )}
        {next !== null && <p className="text-[12px] leading-5 text-muted-foreground" data-testid="builder-next">{next}</p>}
      </div>

      {draft.kind !== "" && (
        <ResizablePanelGroup orientation="vertical" className="min-h-0 flex-1">
          <ResizablePanel defaultSize="52" minSize="20" className="flex min-h-0 flex-col">
            <BuilderColumns
              draft={draft}
              compose={compose.data}
              issues={compose.data?.issues ?? []}
              recordKind={build.recordKind}
              onChange={build.update}
              onPoint={setPointed}
            />
          </ResizablePanel>
          <ResizableHandle />
          <ResizablePanel defaultSize="48" minSize="20" className="flex min-h-0 flex-col">
            <BuilderExpression
              compose={compose.data}
              composing={compose.isFetching}
              failure={compose.isError ? failureText(compose.error) : null}
              notes={notes}
              pointed={pointed}
              onPoint={setPointed}
              onShowRow={() => setShowRow(true)}
            />
          </ResizablePanel>
        </ResizablePanelGroup>
      )}

      <Dialog open={showRow} onOpenChange={setShowRow}>
        <DialogContent className="max-h-[90vh] overflow-auto sm:max-w-[min(1400px,95vw)]" data-testid="builder-row-dialog">
          <DialogHeader>
            <DialogTitle>How a row is built</DialogTitle>
            <DialogDescription>
              The records searched, each record found by id, and the table, as a build reads them
              {exampleLabel === null ? "" : `, filled with what the example key ${exampleLabel} reads`}.
            </DialogDescription>
          </DialogHeader>
          <RowPreview compose={compose.data} />
        </DialogContent>
      </Dialog>
      <BuilderFilterDialog pick={build.question} onRead={build.answer} onCancel={build.cancel} />
      <ConfirmDialog
        open={closing}
        title="Close the builder?"
        message="The draft is not kept anywhere: copy its YAML first if you want it."
        confirmLabel="Close the builder"
        onConfirm={() => {
          setClosing(false);
          build.stop();
        }}
        onClose={() => setClosing(false)}
      />
    </div>
  );
}

function SuggestionChip({ candidate, picked, onPick }: { candidate: DimensionKeyCandidate; picked: boolean; onPick: () => void }) {
  return (
    <RichTooltip
      title={candidate.path}
      body={`Names ${candidate.names.map((name) => (name.includes("--") ? typeName(name) : name)).join(" or ")} records${candidate.repeated ? ", several to a record" : ""}.${candidate.description ? ` ${candidate.description}` : ""}`}
    >
      <button
        type="button"
        onClick={onPick}
        aria-pressed={picked}
        className={cn(
          "max-w-full truncate rounded border px-1.5 py-0.5 font-mono text-[11.5px]",
          picked ? "border-primary/60 bg-primary/10 text-foreground" : "border-border text-muted-foreground hover:text-foreground",
        )}
        data-testid="builder-suggestion"
        data-path={candidate.path}
      >
        {suggestionLabel(candidate)}
      </button>
    </RichTooltip>
  );
}

/** The example every part is filled with: the record in view's own key, or one of the keys held by the most records. */
function ExampleControl({ label, title, own, at, count, onStep }: {
  label: string | null;
  title: string | null;
  own: boolean;
  at: number;
  count: number;
  onStep: (step: number) => void;
}) {
  return (
    <div className="flex min-w-0 items-center gap-1 text-[12px]" data-testid="builder-example">
      <RichTooltip body="Every part is filled with what one key reads: the key of the record in view, then the keys held by the most records.">
        <span className="shrink-0 text-muted-foreground">Example key</span>
      </RichTooltip>
      <Button variant="ghost" size="icon-xs" aria-label="The previous example" onClick={() => onStep(-1)} disabled={count < 2} data-testid="builder-example-previous">
        <ChevronLeft />
      </Button>
      <RichTooltip title="Key" body={title ?? ""} mono>
        <span className="min-w-0 truncate font-mono" data-testid="builder-example-label">{label ?? "none"}</span>
      </RichTooltip>
      <Button variant="ghost" size="icon-xs" aria-label="The next example" onClick={() => onStep(1)} disabled={count < 2} data-testid="builder-example-next">
        <ChevronRight />
      </Button>
      <span className="shrink-0 font-mono tabular-nums text-muted-foreground" data-testid="builder-example-count">{count === 0 ? "0/0" : `${at + 1}/${count}`}</span>
      {own && <span className="shrink-0 text-[11.5px] text-muted-foreground">this record's</span>}
    </div>
  );
}

/** How a build makes each column of the drafted dimension, drawn as a flow's own dimension is, filled with the example key's row. */
function RowPreview({ compose }: { compose: DimensionCompose | undefined }) {
  const layout = useMemo(() => (compose?.blueprint == null ? null : layoutOf(compose.blueprint)), [compose]);
  const examples = useMemo(
    () => (layout === null || compose?.example == null ? null : examplesOf(layout, keyOf(compose.example))),
    [layout, compose],
  );
  if (layout === null || compose?.dimension == null) {
    return <p className="p-4 text-[12.5px] text-muted-foreground">The row is drawn once the YAML reads.</p>;
  }

  return (
    <div className="min-w-0 overflow-x-auto rounded-lg border border-border" data-testid="builder-row-preview">
      <BlueprintDiagram
        layout={layout}
        dimension={compose.dimension}
        table={compose.table ?? ""}
        coverage={new Map()}
        light={null}
        picked={null}
        examples={examples}
        onPoint={() => undefined}
        onPick={() => undefined}
      />
    </div>
  );
}
