import { CircleAlert, CircleCheck, CircleMinus, CirclePause, Info, type LucideIcon } from "lucide-react";
import { RichTooltip } from "@/components/RichTooltip";
import { TruncatedText } from "@/components/TruncatedText";
import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";
import type { AssertionAction, AssertionFailure, AssertionFindings } from "../../api/validation";
import { assertionActionWord, assertionLine, assertionStageWord } from "./validationModel";

/**
 * How what a failure does is drawn: a glyph in its tone, never a tinted pill (DESIGN.md 7.3). A hold stops the record, a
 * report sends it, and an omission sends it without the value.
 */
const actionMarks: Record<AssertionAction, { icon: LucideIcon; tone: string }> = {
  hold: { icon: CirclePause, tone: "text-destructive" },
  report: { icon: CircleAlert, tone: "text-warning" },
  omit: { icon: CircleMinus, tone: "text-muted-foreground" },
};

/** The glyph of what a failure does, in its tone. */
export function AssertionActionGlyph({ action, className }: { action: AssertionAction; className?: string }) {
  const mark = actionMarks[action];
  const Icon = mark.icon;
  return <Icon className={cn("size-3.5 shrink-0", mark.tone, className)} aria-hidden />;
}

/** A neutral chip led by the glyph of what a failure does, saying it in words. */
export function AssertionActionBadge({ action, className }: { action: AssertionAction; className?: string }) {
  return (
    <Badge variant="secondary" className={cn("rounded-md border-border bg-muted/60 font-normal text-foreground", className)} data-action={action}>
      <AssertionActionGlyph action={action} />
      {assertionActionWord(action)}
    </Badge>
  );
}

/**
 * One failure as a row of the section's grid: where the value was (opening it in the record, where the page can and the
 * value is the record's), the assertion and what its failure does, and why the value fails it with the value itself.
 */
function FailureRow({ failure, onOpenPath }: { failure: AssertionFailure; onOpenPath?: (path: string) => void }) {
  const where = failure.path !== "" ? failure.path : failure.at;
  const opens = onOpenPath !== undefined && failure.stage === "record" && failure.path !== "";
  return (
    <div className="col-span-full grid min-w-0 grid-cols-subgrid items-start gap-3 border-b px-3 py-1.5 text-[12px] last:border-b-0" data-testid="assertion-failure" data-action={failure.onFail}>
      {opens
        ? (
          <button type="button" className="min-w-0 cursor-pointer truncate text-left font-mono text-[11px] text-primary hover:underline" onClick={() => onOpenPath(failure.path)} title={`Open ${where} in the record`} data-testid="assertion-failure-open">
            {where}
          </button>
        )
        : <span className="min-w-0 truncate font-mono text-[11px]" title={failure.stage === "incoming" ? `${where}: ${assertionStageWord(failure.stage)}` : where}>{where}</span>}
      <span className="flex min-w-0 flex-wrap items-center gap-1">
        <Badge variant="outline" className="max-w-full truncate font-mono text-[11px] font-normal" title={`The assertion, judging ${assertionStageWord(failure.stage)}`}>{failure.assertion}</Badge>
        <AssertionActionBadge action={failure.onFail} className="text-[11px]" />
      </span>
      <span className="min-w-0 break-words">
        {failure.message}
        {failure.value !== "" && !failure.message.includes(failure.value) && (
          <span className="ml-1 text-muted-foreground">
            <TruncatedText text={failure.value} mono maxWidth={240} title="The value" />
          </span>
        )}
      </span>
    </div>
  );
}

/**
 * What a mapping's assertions found of one record (osdu/docs/reference/flow/mapping-assertions.md): how many judgements
 * were made and what the failures do, then each failure where it is. Every judgement met is one quiet line. How the
 * assertions are judged is in the tooltip of the heading, as explanations are on these pages.
 */
export function AssertionFindingsView({ findings, onOpenPath, className }: {
  findings: AssertionFindings;
  /** Opens an element of the record by its path; absent where the page shows no record to open it in. */
  onOpenPath?: (path: string) => void;
  className?: string;
}) {
  const explained = [
    `The assertions ${findings.mapping} states beside its properties ($assert), judged on this record.`,
    "A failure that holds keeps the record from being sent, its document kept, until the source or the mapping changes or a release accepts it. A reported failure is sent as it is; an omission sends the record without the value.",
    findings.shortened ? "The failures were shortened to fit the attempt; the counts are whole." : null,
  ].filter((line): line is string => line !== null);

  if (findings.failed === 0) {
    return (
      <p className={cn("flex items-center gap-1.5 text-[12px] text-muted-foreground", className)} data-testid="assertion-findings" data-failed={0}>
        <CircleCheck className="size-3.5 shrink-0 text-success" aria-hidden />
        {assertionLine(findings)}
      </p>
    );
  }

  return (
    <section className={cn("rounded-md border", className)} data-testid="assertion-findings" data-failed={findings.failed}>
      <header className="flex flex-wrap items-center gap-x-2 gap-y-1 border-b px-3 py-1.5 text-[12px] font-medium">
        Assertions
        <span className="font-mono tabular-nums text-muted-foreground">{findings.failed.toLocaleString("en-US")}</span>
        <span className="min-w-0 truncate font-normal text-muted-foreground" title={assertionLine(findings)}>{assertionLine(findings)}</span>
        {findings.failed > findings.failures.length && (
          <span className="font-normal text-muted-foreground">{`the first ${findings.failures.length.toLocaleString("en-US")} listed`}</span>
        )}
        <RichTooltip title="How the assertions are judged" body={explained.join("\n\n")}>
          <Info className="ml-auto size-3.5 text-muted-foreground" aria-label="How the assertions are judged" />
        </RichTooltip>
      </header>
      <div className="grid grid-cols-[minmax(0,1.3fr)_minmax(0,1.2fr)_minmax(0,2fr)]">
        {findings.failures.map((failure, index) => (
          <FailureRow key={`${failure.path}:${failure.assertion}:${index}`} failure={failure} onOpenPath={onOpenPath} />
        ))}
      </div>
    </section>
  );
}
