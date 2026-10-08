import type { ReactNode } from "react";
import {
  Archive, CircleCheck, CircleDashed, CircleDot, CircleOff, Hourglass, Layers, OctagonX, Trash2, Undo2, XCircle,
  type LucideIcon,
} from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";
import type { UndoneCounts } from "../../api/artifacts";

/**
 * How an artifact's state is drawn (docs/atomic-delivery-plan.md, Artifacts): a neutral chip led by a glyph in the state's
 * tone, as the cache's changes are (ChangeMark.tsx). A state is not decoration: only the glyph carries the tone, so a list of
 * artifacts never reads as a block of colour, and the tones keep their meaning: what an undo still has to take is warning
 * (due) or destructive (failed), what a delivery made and kept is success, what an undo settled is quiet.
 */
const marks: Record<string, { icon: LucideIcon; tone: string; word: string; what: string }> = {
  intent: { icon: CircleDashed, tone: "text-info", word: "intent", what: "The call that creates it was about to go; the ledger has not had its answer." },
  pending: { icon: CircleDot, tone: "text-info", word: "pending", what: "Created by a delivery that has not ended, or that newer work abandoned and the sweep has not reached yet." },
  live: { icon: CircleCheck, tone: "text-success", word: "live", what: "Its delivery completed; it stays in OSDU and the ledger names it." },
  superseded: { icon: Layers, tone: "text-muted-foreground", word: "superseded", what: "A later delivery replaced it, or newer work writes the record again; it stays in OSDU, since earlier versions name it." },
  due: { icon: Hourglass, tone: "text-warning", word: "due", what: "Its delivery did not complete; the undo has not run for it yet." },
  removed: { icon: Trash2, tone: "text-muted-foreground", word: "removed", what: "The undo removed it, reversibly where the route can." },
  restored: { icon: Undo2, tone: "text-muted-foreground", word: "restored", what: "The undo wrote back the version it replaced." },
  gone: { icon: CircleOff, tone: "text-muted-foreground", word: "gone", what: "OSDU no longer held it when the undo came." },
  kept: { icon: Archive, tone: "text-warning", word: "kept", what: "No call removes it: it is left behind, and the note says why." },
  failed: { icon: XCircle, tone: "text-destructive", word: "failed", what: "The undo was refused or could not reach OSDU; the sweep tries it again with backoff." },
};

/** The mark of a failed undo that has used every try the sweep gives it. */
const exhaustedMark = {
  icon: OctagonX,
  tone: "text-destructive",
  word: "failed, no tries left",
  what: "The undo failed as often as the sweep tries; once what stops it is fixed, an undo run with force takes it.",
};

function markOf(state: string, exhausted = false) {
  return exhausted && state === "failed" ? exhaustedMark : marks[state];
}

/** What a state means, in a sentence: what an artifact's chip says on hover. */
function artifactStateMeaning(state: string, exhausted = false): string {
  return markOf(state, exhausted)?.what ?? state;
}

/** The glyph of an artifact state, in its tone; a neutral dot for a state this page does not know. */
export function ArtifactStateGlyph({ state, exhausted = false, className }: { state: string; exhausted?: boolean; className?: string }) {
  const mark = markOf(state, exhausted);
  const Icon = mark?.icon ?? CircleDot;
  return <Icon className={cn("size-3.5 shrink-0", mark?.tone ?? "text-muted-foreground", className)} aria-hidden />;
}

/** A neutral chip led by the state's glyph, saying the state in a word. */
export function ArtifactStateBadge({
  state, exhausted = false, children, className, testId,
}: { state: string; exhausted?: boolean; children?: ReactNode; className?: string; testId?: string }) {
  return (
    <Badge
      variant="secondary"
      className={cn("rounded-md border-border bg-muted/60 font-normal text-foreground", className)}
      data-state={state}
      data-testid={testId}
      title={artifactStateMeaning(state, exhausted)}
    >
      <ArtifactStateGlyph state={state} exhausted={exhausted} />
      {children ?? markOf(state, exhausted)?.word ?? state}
    </Badge>
  );
}

/** How many artifacts stand one way: the glyph, the count as data, and the word quietly after it. A zero takes no tone. */
export function ArtifactStateCount({
  state, count, label, exhausted = false, className,
}: { state: string; count: number; label?: string; exhausted?: boolean; className?: string }) {
  return (
    <span className={cn("inline-flex items-center gap-1 whitespace-nowrap text-[12px]", className)} data-state={state}>
      {count > 0 ? <ArtifactStateGlyph state={state} exhausted={exhausted} /> : <span className="size-3.5 shrink-0" aria-hidden />}
      <span className={cn("font-mono tabular-nums", count === 0 && "text-muted-foreground")}>{count.toLocaleString()}</span>
      {" "}
      <span className="text-muted-foreground">{label ?? markOf(state, exhausted)?.word ?? state}</span>
    </span>
  );
}

/**
 * What was undone before a removal or the deletion of a ledger, in one line: how many records' unfinished deliveries were
 * taken up, and what became of what they left, the counts that are not zero. Nothing when nothing was undone.
 */
export function UndoneFirst({ undone, testId, heading = "Undone first" }: { undone: UndoneCounts | null; testId: string; heading?: string }) {
  if (undone === null || undone.records === 0) {
    return null;
  }

  const parts: { state: string; count: number; label: string }[] = [
    { state: "removed", count: undone.removed, label: "removed" },
    { state: "restored", count: undone.restored, label: "written back" },
    { state: "gone", count: undone.gone, label: "already gone" },
    { state: "kept", count: undone.kept, label: "kept" },
    { state: "superseded", count: undone.superseded, label: "left to newer work" },
    { state: "failed", count: undone.failed, label: "still to undo" },
  ];
  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-[12.5px]" data-testid={testId}>
      <span className="font-medium">{heading}</span>
      <span className="text-muted-foreground">
        {`what unfinished deliveries of ${undone.records.toLocaleString()} record${undone.records === 1 ? "" : "s"} left:`}
      </span>
      {parts.filter((part) => part.count > 0).map((part) => (
        <ArtifactStateCount key={part.state} state={part.state} count={part.count} label={part.label} />
      ))}
    </div>
  );
}

/** An undo's outcome for one artifact, as its attempt names it: `superseded` there means the record was left to newer work. */
export function UndoOutcomeBadge({ outcome, testId }: { outcome: string; testId?: string }) {
  return outcome === "superseded"
    ? <ArtifactStateBadge state={outcome} testId={testId}>left to newer work</ArtifactStateBadge>
    : <ArtifactStateBadge state={outcome} testId={testId} />;
}
