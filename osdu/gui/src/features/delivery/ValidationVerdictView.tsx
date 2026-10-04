import type { ReactNode } from "react";
import { Info } from "lucide-react";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { TruncatedText } from "@/components/TruncatedText";
import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";
import { schemaSourceName, whereOf, type ValidationFinding, type ValidationVerdict } from "../../api/validation";
import { ValidationBadge } from "./ValidationMark";
import { outcomeWord, referenceLine } from "./validationModel";

/**
 * One finding as a row of its section's grid: where (opening it in the record, where the page can), the rule, what is wrong,
 * and the value. The rows share the section's columns, so the rules line up however long each one is.
 */
function FindingRow({ finding, onOpenPath, testId }: { finding: ValidationFinding; onOpenPath?: (path: string) => void; testId: string }) {
  const where = whereOf(finding);
  return (
    <div className="col-span-full grid min-w-0 grid-cols-subgrid items-start gap-3 border-b px-3 py-1.5 text-[12px] last:border-b-0" data-testid={testId}>
      {onOpenPath !== undefined && finding.path !== ""
        ? (
          <button type="button" className="min-w-0 cursor-pointer truncate text-left font-mono text-[11px] text-primary hover:underline" onClick={() => onOpenPath(finding.path)} title={`Open ${where} in the record`} data-testid={`${testId}-open`}>
            {where}
          </button>
        )
        : <span className="min-w-0 truncate font-mono text-[11px]" title={where}>{where}</span>}
      <Badge variant="outline" className="font-mono text-[11px] font-normal" title="The rule">{finding.rule}</Badge>
      <span className="min-w-0 break-words">
        {finding.message}
        {finding.value !== "" && !finding.message.includes(finding.value) && (
          <span className="ml-1 text-muted-foreground">
            <TruncatedText text={finding.value} mono maxWidth={240} title="The value" />
          </span>
        )}
      </span>
    </div>
  );
}

const findingColumns = "grid grid-cols-[minmax(0,1.3fr)_auto_minmax(0,2fr)]";

function Section({ title, count, listed, children, testId }: { title: string; count: number; listed: number; children: ReactNode; testId: string }) {
  return (
    <section className="rounded-md border" data-testid={testId}>
      <header className="flex items-center gap-2 border-b px-3 py-1.5 text-[12px] font-medium">
        {title}
        <span className="font-mono tabular-nums text-muted-foreground">{count.toLocaleString("en-US")}</span>
        {count > listed && <span className="font-normal text-muted-foreground">{`the first ${listed.toLocaleString("en-US")} listed`}</span>}
      </header>
      {children}
    </section>
  );
}

/**
 * What a check of a record against its schema came to (osdu/docs/validation-plan.md, The verdict): the outcome and the schema
 * it was checked against, how much it checked, the records it refers to, then the problems, the parts it could not check and
 * the references not found, each where it is in the record. What the schema states that no check asserts is in the tooltip
 * of the summary, as explanations are on these pages.
 */
export function ValidationVerdictView({ verdict, onOpenPath, className }: {
  verdict: ValidationVerdict;
  /** Opens an element of the record by its path; absent where the page shows no record to open it in. */
  onOpenPath?: (path: string) => void;
  className?: string;
}) {
  const references = referenceLine(verdict);
  const schema = verdict.schema;
  const explained = [
    schema ? `Checked against ${schema.kind}, content version ${schema.version}, from ${schemaSourceName(schema.source)}.` : null,
    `${verdict.rules.toLocaleString("en-US")} rule${verdict.rules === 1 ? "" : "s"} applied, by the checks of version ${verdict.rulesVersion}.`,
    verdict.shortened ? "The listings were shortened to fit the attempt; the counts are whole." : null,
    ...verdict.notes,
  ].filter((line): line is string => line !== null);

  return (
    <div className={cn("flex flex-col gap-3", className)} data-testid="validation-verdict" data-outcome={verdict.outcome}>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-[12px]">
        <ValidationBadge outcome={verdict.outcome} data-testid="validation-outcome" />
        {schema && (
          <span className="min-w-0 truncate text-muted-foreground" title={schema.kind}>
            {"against "}
            <span className="font-mono text-[11px] text-foreground">{schema.kind}</span>
          </span>
        )}
        {verdict.outcome === "invalid" && <span className="text-muted-foreground">{`${verdict.problemCount.toLocaleString("en-US")} problem${verdict.problemCount === 1 ? "" : "s"}`}</span>}
        {verdict.unverifiedCount > 0 && <span className="text-muted-foreground">{`${verdict.unverifiedCount.toLocaleString("en-US")} not checked`}</span>}
        {verdict.accepted && <Badge variant="outline" className="text-[11px] font-normal" data-testid="validation-accepted">sent as a release accepted it</Badge>}
        <RichTooltip title={`Why the record is ${outcomeWord(verdict.outcome)}`} body={explained.join("\n\n")}>
          <Info className="size-3.5 text-muted-foreground" aria-label="How the record was checked" data-testid="validation-explained" />
        </RichTooltip>
        <span className="ml-auto text-muted-foreground"><RelativeTime value={verdict.checkedUtc} /></span>
      </div>
      {references !== null && <p className="text-[12px] text-muted-foreground" data-testid="validation-references">{references}</p>}
      {verdict.problems.length > 0 && (
        <Section title="Problems" count={verdict.problemCount} listed={verdict.problems.length} testId="validation-problems">
          <div className={findingColumns}>
            {verdict.problems.map((finding, index) => <FindingRow key={`${finding.path}:${finding.rule}:${index}`} finding={finding} onOpenPath={onOpenPath} testId="validation-problem" />)}
          </div>
        </Section>
      )}
      {verdict.unverified.length > 0 && (
        <Section title="Not checked" count={verdict.unverifiedCount} listed={verdict.unverified.length} testId="validation-unverified">
          <div className={findingColumns}>
            {verdict.unverified.map((finding, index) => <FindingRow key={`${finding.path}:${finding.rule}:${index}`} finding={finding} onOpenPath={onOpenPath} testId="validation-not-checked" />)}
          </div>
        </Section>
      )}
      {verdict.references.missingIds.length > 0 && (
        <Section title="References not found" count={verdict.references.missing} listed={verdict.references.missingIds.length} testId="validation-missing">
          {verdict.references.missingIds.map((missing) => (
            <div key={`${missing.id}:${missing.path}`} className="grid min-w-0 grid-cols-[minmax(0,1.3fr)_minmax(0,1.5fr)_minmax(0,2fr)] gap-3 border-b px-3 py-1.5 text-[12px] last:border-b-0" data-testid="validation-missing-id">
              <TruncatedText text={missing.id} mono maxWidth={420} copy />
              {onOpenPath !== undefined && missing.path !== ""
                ? <button type="button" className="min-w-0 truncate text-left font-mono text-[11px] text-primary hover:underline" onClick={() => onOpenPath(missing.path)}>{missing.path}</button>
                : <span className="min-w-0 truncate font-mono text-[11px]">{missing.path}</span>}
              <span className="min-w-0 text-muted-foreground">{missing.detail}</span>
            </div>
          ))}
        </Section>
      )}
    </div>
  );
}
