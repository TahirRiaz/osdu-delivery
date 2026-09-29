import { Fragment, useState } from "react";
import { ChevronRight } from "lucide-react";
import { CopyButton } from "@/components/CopyButton";
import { cn } from "@/lib/utils";
import type { DeliveryAssertionOutcome } from "../../../api/delivery";
import { SeverityChip, TestOutcomeIcon } from "./AssertionBadges";
import { STANDING_TEXT, assertionStanding } from "./assertionFormat";

/** A count the way the outcome reads it: the number, or a muted dash when the assertion did not get that far. */
function Count({ value }: { value: number | null | undefined }) {
  return value === null || value === undefined
    ? <span className="text-muted-foreground">-</span>
    : <span className="tabular-nums">{value.toLocaleString("en-US")}</span>;
}

/** What an assertion found beyond its line: why it did not hold, and the records that failed it. */
function OutcomeDetail({ outcome }: { outcome: DeliveryAssertionOutcome }) {
  const examples = outcome.examples ?? [];
  return (
    <div className="flex flex-col gap-2 bg-muted/30 px-3 py-2.5" data-testid="assertion-detail">
      {outcome.message && <p className="text-[12.5px]" data-testid="assertion-message">{outcome.message}</p>}
      {outcome.description && <p className="text-[12px] text-muted-foreground">{outcome.description}</p>}
      {examples.length > 0 && (
        <div className="overflow-x-auto rounded-md border bg-card">
          <table className="w-full text-[12px]" data-testid="assertion-examples">
            <thead>
              <tr className="border-b text-left text-[11px] uppercase tracking-wider text-muted-foreground">
                <th className="px-2 py-1.5 font-medium">Record</th>
                <th className="px-2 py-1.5 font-medium">Held</th>
                <th className="px-2 py-1.5 font-medium">Why it fails</th>
              </tr>
            </thead>
            <tbody>
              {examples.map((example, index) => (
                <tr key={`${example.id ?? ""}-${index}`} className="border-b align-top last:border-b-0">
                  <td className="max-w-[22rem] px-2 py-1.5">
                    {example.id
                      ? (
                        <span className="flex items-center gap-1">
                          <span className="truncate font-mono text-[11.5px]" title={example.id}>{example.id}</span>
                          <CopyButton label="Copy the id" text={example.id} testId="copy-example-id" iconOnly />
                        </span>
                      )
                      : <span className="text-muted-foreground">-</span>}
                  </td>
                  <td className="max-w-[16rem] px-2 py-1.5">
                    <span className="line-clamp-3 break-all font-mono text-[11.5px]" title={example.value ?? undefined}>{example.value ?? "(nothing)"}</span>
                  </td>
                  <td className="px-2 py-1.5">{example.reason}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {outcome.examplesTrimmed && (
        <p className="text-[11px] text-muted-foreground">
          Fewer examples are kept than the run met, so the result stays within what the ledger keeps for one test.
        </p>
      )}
    </div>
  );
}

/**
 * Every assertion of one test's result, as a list a reader scans for what did not hold: its outcome, what it expected,
 * what it found, how many it checked and how many failed it. A line with more to say (why, the records that failed it)
 * opens in place.
 */
export function AssertionOutcomesTable({ outcomes, testId = "assertion-outcomes" }: { outcomes: readonly DeliveryAssertionOutcome[]; testId?: string }) {
  const [open, setOpen] = useState<ReadonlySet<number>>(
    () => new Set(outcomes.filter((o) => o.outcome === "failed" || o.outcome === "errored").slice(0, 3).map((o) => o.index)),
  );
  if (outcomes.length === 0) {
    return <p className="text-[12.5px] text-muted-foreground">The test recorded no assertion outcome.</p>;
  }

  const toggle = (index: number) => setOpen((was) => {
    const next = new Set(was);
    if (!next.delete(index)) {
      next.add(index);
    }

    return next;
  });

  return (
    <div className="overflow-x-auto rounded-lg border" data-testid={testId}>
      <table className="w-full min-w-[44rem] text-[12.5px]">
        <thead>
          <tr className="border-b bg-muted/40 text-left text-[11px] uppercase tracking-wider text-muted-foreground">
            <th className="w-8 px-2 py-2" aria-label="Outcome" />
            <th className="px-2 py-2 font-medium">Assertion</th>
            <th className="px-2 py-2 font-medium">Expected</th>
            <th className="px-2 py-2 font-medium">Found</th>
            <th className="px-2 py-2 text-right font-medium">Checked</th>
            <th className="px-2 py-2 text-right font-medium">Failing</th>
            <th className="w-8 px-2 py-2" aria-label="More" />
          </tr>
        </thead>
        <tbody>
          {outcomes.map((outcome) => {
            const more = Boolean(outcome.message) || (outcome.examples ?? []).length > 0 || Boolean(outcome.description);
            const expanded = more && open.has(outcome.index);
            const standing = assertionStanding(outcome);
            const tone = STANDING_TEXT[standing];
            return (
              <Fragment key={outcome.index}>
                <tr
                  className={cn("border-b align-top last:border-b-0", more && "cursor-pointer hover:bg-muted/40", expanded && "bg-muted/20")}
                  onClick={more ? () => toggle(outcome.index) : undefined}
                  data-testid="assertion-outcome"
                  data-outcome={outcome.outcome}
                >
                  <td className="px-2 py-2"><TestOutcomeIcon outcome={standing} /></td>
                  <td className="px-2 py-2">
                    <div className="flex flex-wrap items-center gap-1.5">
                      <span className="font-medium">{outcome.label}</span>
                      <SeverityChip severity={outcome.severity} />
                      <span className="font-mono text-[10.5px] text-muted-foreground">{outcome.type}</span>
                    </div>
                  </td>
                  <td className="max-w-[18rem] px-2 py-2 font-mono text-[11.5px]"><span className="line-clamp-2 break-words">{outcome.expected}</span></td>
                  <td className="max-w-[18rem] px-2 py-2 font-mono text-[11.5px]">
                    <span className={cn("line-clamp-2 break-words", outcome.outcome === "failed" && tone)}>{outcome.actual ?? "-"}</span>
                  </td>
                  <td className="px-2 py-2 text-right font-mono"><Count value={outcome.checked} /></td>
                  <td className={cn("px-2 py-2 text-right font-mono", (outcome.failing ?? 0) > 0 && (tone ?? "text-destructive"))}><Count value={outcome.failing} /></td>
                  <td className="px-2 py-2">
                    {more && (
                      <button
                        type="button"
                        aria-expanded={expanded}
                        aria-label={expanded ? "Hide what it found" : "Show what it found"}
                        className="rounded p-0.5 text-muted-foreground hover:text-foreground"
                        onClick={(event) => { event.stopPropagation(); toggle(outcome.index); }}
                      >
                        <ChevronRight className={cn("size-4 transition-transform", expanded && "rotate-90")} />
                      </button>
                    )}
                  </td>
                </tr>
                {expanded && (
                  <tr className="border-b last:border-b-0">
                    <td colSpan={7} className="p-0"><OutcomeDetail outcome={outcome} /></td>
                  </tr>
                )}
              </Fragment>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
