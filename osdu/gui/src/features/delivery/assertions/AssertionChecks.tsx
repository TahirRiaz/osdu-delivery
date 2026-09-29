import { useState } from "react";
import { ArrowLeft, ChevronRight } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { CopyButton } from "@/components/CopyButton";
import { cn } from "@/lib/utils";
import type { DeliveryAssertionOutcome } from "../../../api/delivery";
import { SeverityChip, TestOutcomeIcon } from "./AssertionBadges";
import { STANDING_TEXT, assertionStanding, counted, needsLook } from "./assertionFormat";

/** Which checks a list shows: the ones that ask for a look, or every one. */
type CheckView = "look" | "all";

/**
 * What a check that asks for a look found, in the few words a row has room for: the found text, else why it did not get
 * as far. A check that holds needs no words beside its glyph.
 */
function finding(outcome: DeliveryAssertionOutcome): string | null {
  return needsLook(outcome) ? outcome.actual ?? outcome.message ?? null : null;
}

/**
 * One check on one line: where it stands, what it asserts, and what it found, toned as its severity weighs it. A severity
 * other than error is named, since error is what a check is unless it says otherwise.
 */
function CheckRow({ outcome, expanded, onClick }: { outcome: DeliveryAssertionOutcome; expanded?: boolean; onClick: () => void }) {
  const standing = assertionStanding(outcome);
  const found = finding(outcome);
  return (
    <button
      type="button"
      onClick={onClick}
      aria-expanded={expanded}
      className={cn(
        "grid w-full grid-cols-[auto_minmax(0,1fr)_auto] items-center gap-x-2.5 px-3 py-1.5 text-left outline-none transition-colors hover:bg-accent/50 focus-visible:bg-accent/50",
        "md:grid-cols-[auto_minmax(0,1.1fr)_minmax(0,1fr)_auto]",
        expanded && "bg-muted/30",
      )}
      data-testid="check-row"
      data-outcome={standing}
    >
      <TestOutcomeIcon outcome={standing} />
      <span className="flex min-w-0 items-center gap-1.5">
        <span className="truncate text-[12.5px]" title={outcome.label}>{outcome.label}</span>
        {outcome.severity !== "error" && <SeverityChip severity={outcome.severity} />}
      </span>
      <span className={cn("hidden truncate text-right font-mono text-[11.5px] md:block", STANDING_TEXT[standing])} title={found ?? undefined}>
        {found}
      </span>
      <ChevronRight className={cn("size-4 text-muted-foreground transition-transform", expanded && "rotate-90")} />
    </button>
  );
}

/** The records that failed a check: the id (copyable), what it held there, and why that fails it. */
function Examples({ outcome }: { outcome: DeliveryAssertionOutcome }) {
  const examples = outcome.examples ?? [];
  if (examples.length === 0) {
    return null;
  }

  return (
    <div className="flex flex-col gap-1">
      <div className="overflow-x-auto rounded-md border bg-card">
        <table className="w-full text-[12px]" data-testid="assertion-examples">
          <thead>
            <tr className="border-b text-left text-[11px] text-muted-foreground">
              <th className="px-2 py-1.5 font-medium">Record</th>
              <th className="px-2 py-1.5 font-medium">Value</th>
              <th className="px-2 py-1.5 font-medium">Why it fails</th>
            </tr>
          </thead>
          <tbody>
            {examples.map((example, index) => (
              <tr key={`${example.id ?? ""}-${index}`} className="border-b align-top last:border-b-0">
                <td className="max-w-[20rem] px-2 py-1.5">
                  {example.id
                    ? (
                      <span className="flex items-center gap-1">
                        <span className="truncate font-mono text-[11.5px]" title={example.id}>{example.id}</span>
                        <CopyButton label="Copy the id" text={example.id} testId="copy-example-id" iconOnly />
                      </span>
                    )
                    : <span className="text-muted-foreground">-</span>}
                </td>
                <td className="max-w-[14rem] px-2 py-1.5">
                  <span className="line-clamp-3 break-all font-mono text-[11.5px]" title={example.value ?? undefined}>{example.value ?? "(nothing)"}</span>
                </td>
                <td className="px-2 py-1.5">{example.reason}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {outcome.examplesTrimmed && (
        <p className="text-[11px] text-muted-foreground">
          Fewer examples are kept than the run met, so the result stays within what the ledger keeps for one test.
        </p>
      )}
    </div>
  );
}

/**
 * Everything one check found now: what it found (and what it expects, when its name does not already say), why, and the
 * records that failed it.
 */
export function CheckDetail({ outcome, onBack }: { outcome: DeliveryAssertionOutcome; onBack?: () => void }) {
  const standing = assertionStanding(outcome);
  const named = !outcome.label.includes(outcome.expected);
  return (
    <div className="flex flex-col gap-3" data-testid="check-detail">
      {onBack !== undefined && (
        <Button variant="ghost" size="xs" className="w-fit" onClick={onBack} data-testid="check-detail-back">
          <ArrowLeft />
          All checks
        </Button>
      )}
      {onBack !== undefined && (
        <div className="flex flex-wrap items-center gap-2">
          <TestOutcomeIcon outcome={standing} />
          <span className="text-[14px] font-medium">{outcome.label}</span>
          <SeverityChip severity={outcome.severity} />
          <span className="font-mono text-[11px] text-muted-foreground">{outcome.type}</span>
        </div>
      )}
      {outcome.description && <p className="text-[12.5px] text-muted-foreground">{outcome.description}</p>}
      <dl className={cn("grid grid-cols-1 gap-2", named && "sm:grid-cols-2")}>
        {named && (
          <div className="rounded-md border bg-card px-3 py-2">
            <dt className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Expects</dt>
            <dd className="mt-0.5 break-words font-mono text-[12px]">{outcome.expected}</dd>
          </div>
        )}
        <div className="rounded-md border bg-card px-3 py-2">
          <dt className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Found</dt>
          <dd className={cn("mt-0.5 break-words font-mono text-[12px]", needsLook(outcome) && STANDING_TEXT[standing])}>{outcome.actual ?? "-"}</dd>
        </div>
      </dl>
      {outcome.message && <p className="text-[12.5px]" data-testid="assertion-message">{outcome.message}</p>}
      <Examples outcome={outcome} />
    </div>
  );
}

/**
 * A test's checks as one list, opening on the ones that ask for a look (every one, when all hold), the rest a click away.
 * In a sheet a check opens on its own (`onOpen`); in a report it opens in place, under its line.
 */
export function CheckList({ outcomes, onOpen, testId = "check-list" }: {
  outcomes: readonly DeliveryAssertionOutcome[];
  /** Opens one check on its own; without it a check opens in place. */
  onOpen?: (index: number) => void;
  testId?: string;
}) {
  const looking = outcomes.filter(needsLook);
  const [view, setView] = useState<CheckView>(() => (looking.length > 0 ? "look" : "all"));
  const [open, setOpen] = useState<ReadonlySet<number>>(new Set());
  if (outcomes.length === 0) {
    return <p className="text-[12.5px] text-muted-foreground">The test recorded no check.</p>;
  }

  // The choice is only offered when it changes something: some checks ask for a look and some do not.
  const choosing = looking.length > 0 && looking.length < outcomes.length;
  const shown = view === "look" ? looking : outcomes;
  const hidden = outcomes.length - shown.length;
  const toggle = (index: number) => setOpen((was) => {
    const next = new Set(was);
    if (!next.delete(index)) {
      next.add(index);
    }

    return next;
  });

  return (
    <div className="flex flex-col gap-2" data-testid={testId}>
      {choosing && (
        <ToggleGroup
          type="single"
          variant="outline"
          size="sm"
          value={view}
          onValueChange={(value) => { if (value === "look" || value === "all") { setView(value); } }}
          aria-label="Which checks to show"
          data-testid={`${testId}-view`}
        >
          <ToggleGroupItem value="look" className="h-7 gap-1.5 px-2.5 text-[12.5px]">
            Needs a look
            <span className="rounded-full bg-muted px-1.5 font-mono text-[11px] tabular-nums">{looking.length}</span>
          </ToggleGroupItem>
          <ToggleGroupItem value="all" className="h-7 gap-1.5 px-2.5 text-[12.5px]">
            All
            <span className="rounded-full bg-muted px-1.5 font-mono text-[11px] tabular-nums">{outcomes.length}</span>
          </ToggleGroupItem>
        </ToggleGroup>
      )}
      <div className="divide-y overflow-hidden rounded-lg border">
        {shown.map((outcome) => (
          <div key={outcome.index}>
            <CheckRow
              outcome={outcome}
              expanded={onOpen === undefined ? open.has(outcome.index) : undefined}
              onClick={() => (onOpen === undefined ? toggle(outcome.index) : onOpen(outcome.index))}
            />
            {onOpen === undefined && open.has(outcome.index) && (
              <div className="border-t bg-muted/20 px-3 py-3"><CheckDetail outcome={outcome} /></div>
            )}
          </div>
        ))}
      </div>
      {view === "look" && hidden > 0 && (
        <button
          type="button"
          onClick={() => setView("all")}
          className="w-fit text-[12px] text-muted-foreground underline-offset-2 hover:text-foreground hover:underline"
          data-testid={`${testId}-show-all`}
        >
          {`Show ${counted(hidden, "passed check")}`}
        </button>
      )}
    </div>
  );
}
