import { CircleAlert, Info, Loader2, ScanSearch } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { cn } from "@/lib/utils";
import { GlyphRef } from "@/components/GlyphRef";
import { RelativeTime } from "@/components/RelativeTime";
import { SummaryStrip, type SummaryCell } from "@/components/SummaryStrip";
import { ProblemView } from "./TemplateSheet";
import { splitPath } from "./templateFormat";
import { ScopeParameterFields } from "./ScopeParameterFields";
import { flowScope, type FailingFilter, type ValueCheckSession } from "./useValueCheck";
import { ROW_BUDGETS, compactCount, failingOf, flowKey, flowLabel, formatCount, outcomeVisual, percent, worstOf } from "./valueCheck";

/** How many of the attributes with failing rows the bar names; the tree's filter shows every one. */
const NAMED_ATTRIBUTES = 8;

const HOW_IT_WORKS = [
  "A check reads the rows of a flow that renders with this mapping, on a node, and renders each of them as a delivery would:",
  "the same columns, expressions, lookups, cache and searches. Every value written is then held to what the template says of",
  "its attribute (type, format, pattern, allowed values, lengths, ranges, the records a reference may point to).",
  "\n\nEach attribute then says how many rows are held (the record is not delivered), write a value the template does not",
  "accept, or leave it out, with every reason, the values behind it and the records. Nothing is written: not the ledger, not",
  "OSDU. Check one attribute from its properties, or every attribute here.",
].join(" ");

/**
 * The value check's controls and its answer at a glance, above the mapping's properties: the flow whose rows are read, the
 * scope's values, how many rows, and the check of every attribute; then what the latest check found, each count narrowing
 * the tree to the attributes behind it, and the attributes with the most rows that fail.
 */
export function ValueCheckBar({ session }: { session: ValueCheckSession }) {
  const last = session.summary;
  const failingAttributes = session.checked
    .filter((result) => failingOf(result.variable.rows) > 0)
    .sort((a, b) => failingOf(b.variable.rows) - failingOf(a.variable.rows));

  const narrow = (filter: FailingFilter) => () => session.setFilter(session.filter === filter ? null : filter);
  const cells: SummaryCell[] = last === null
    ? []
    : [
      {
        label: "Rows checked",
        value: formatCount(last.rows.checked),
        caption: last.rows.complete
          ? "the whole scope"
          : `the first of about ${formatCount(last.rows.scopeRecords ?? last.rows.read)}`,
        testId: "value-check-summary-rows",
      },
      {
        label: "Held",
        value: formatCount(last.rows.withHeld),
        caption: "not delivered",
        tone: last.rows.withHeld > 0 ? "destructive" : undefined,
        onClick: last.rows.withHeld > 0 ? narrow("held") : undefined,
        testId: "value-check-summary-held",
      },
      {
        label: "Invalid value",
        value: formatCount(last.rows.withInvalid),
        caption: "sent, breaking the template",
        tone: last.rows.withInvalid > 0 ? "warning" : undefined,
        onClick: last.rows.withInvalid > 0 ? narrow("invalid") : undefined,
        testId: "value-check-summary-invalid",
      },
      {
        label: "Attribute left out",
        value: formatCount(last.rows.withEmpty),
        caption: "sent without it",
        tone: last.rows.withEmpty > 0 ? "info" : undefined,
        onClick: last.rows.withEmpty > 0 ? narrow("empty") : undefined,
        testId: "value-check-summary-empty",
      },
      {
        label: "Clean",
        value: formatCount(last.rows.clean),
        caption: `${percent(last.rows.clean, last.rows.checked)} as expected`,
        tone: last.rows.clean === last.rows.checked && last.rows.checked > 0 ? "success" : undefined,
        testId: "value-check-summary-clean",
      },
    ];

  return (
    <Card className="gap-3 rounded-lg p-3" data-testid="value-check-bar">
      <div className="flex flex-wrap items-center gap-2">
        <ScanSearch className="size-4 text-primary" />
        <h3 className="text-sm font-semibold">Check values</h3>
        <GlyphRef icon={Info} title="How a check works" body={HOW_IT_WORKS} />
        <span className="text-[12px] text-muted-foreground">Find the rows that will not give an attribute the value its template expects.</span>
        <Button
          type="button"
          size="sm"
          className="ml-auto"
          disabled={!session.ready || session.checkingAll}
          onClick={() => session.check(null)}
          title={session.blocked ?? undefined}
          data-testid="value-check-all"
        >
          {session.checkingAll ? <Loader2 className="animate-spin" /> : <ScanSearch />}
          {session.checkingAll ? "Checking" : "Check all attributes"}
        </Button>
      </div>

      {session.flowsError !== null && <ProblemView error={session.flowsError} testId="value-check-flows-error" />}

      {session.flows.length > 0 && (
        <div className="flex flex-wrap items-start gap-2" data-testid="value-check-controls">
          <div className="flex min-w-0 flex-col gap-1">
            <Label className="text-[12px] text-muted-foreground" htmlFor="value-check-flow">Rows of</Label>
            <Select value={session.flow === null ? undefined : flowKey(session.flow)} onValueChange={session.chooseFlow}>
              <SelectTrigger id="value-check-flow" size="sm" className="h-8 w-72 max-w-full font-mono text-[12px]" data-testid="value-check-flow">
                <SelectValue placeholder="Pick a flow" />
              </SelectTrigger>
              <SelectContent>
                {session.flows.map((flow) => (
                  <SelectItem key={flowKey(flow)} value={flowKey(flow)} className="font-mono text-[12px]">
                    {flowLabel(flow)}
                    <span className="ml-2 font-sans text-[11px] text-muted-foreground">{flow.recordObject}</span>
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          <ScopeParameterFields
            pipelineId={session.flow?.pipelineId ?? null}
            scope={session.flow === null ? {} : flowScope(session.flow)}
            parameters={session.parameters}
            values={session.values}
            onChange={session.setValue}
            canRead={session.canOperate}
            prefix="value-check"
            inputClassName="w-48"
          />
          <div className="flex flex-col gap-1">
            <Label className="text-[12px] text-muted-foreground" htmlFor="value-check-rows">Read</Label>
            <Select value={String(session.maxRows)} onValueChange={(value) => session.setMaxRows(Number(value))}>
              <SelectTrigger id="value-check-rows" size="sm" className="h-8 w-40 text-[12px]" data-testid="value-check-rows">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {ROW_BUDGETS.map((budget) => (
                  <SelectItem key={budget.value} value={String(budget.value)} className="text-[12px]">{budget.label}</SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
        </div>
      )}

      {session.blocked !== null && !session.flowsLoading && (
        <p className="flex items-start gap-1.5 text-[12px] text-muted-foreground" data-testid="value-check-blocked">
          <Info className="mt-0.5 size-3.5 shrink-0" />
          {session.blocked}
        </p>
      )}

      {session.running > 0 && (
        <p className="flex items-center gap-2 text-[12px] text-muted-foreground" data-testid="value-check-progress">
          <Loader2 className="size-4 animate-spin" />
          {`${session.running === 1 ? "A check is" : `${session.running} checks are`} rendering the rows of ${session.flow === null ? "the flow" : flowLabel(session.flow)} on a node${session.maxRows === 0 ? ", the whole scope" : `, up to ${formatCount(session.maxRows)} rows`}.`}
        </p>
      )}

      {session.failure !== null && (
        <Alert variant="destructive" data-testid="value-check-failed">
          <CircleAlert />
          <AlertTitle>The value check could not be made</AlertTitle>
          <AlertDescription className="whitespace-pre-wrap">{session.failure}</AlertDescription>
        </Alert>
      )}

      {last !== null && (
        <div className="flex flex-col gap-2" data-testid="value-check-summary">
          <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-[12px] text-muted-foreground">
            <span>
              {last.targets === null
                ? "Every attribute"
                : <span className="font-mono">{last.targets.map((target) => splitPath(target).leaf).join(", ")}</span>}
              {" over the rows of "}
              <span className="font-mono">{flowLabel(last.flow)}</span>
            </span>
            <span>&middot;</span>
            <RelativeTime value={last.checkedUtc} />
            {last.rows.keyless > 0 && (
              <span className="text-destructive">{`${formatCount(last.rows.keyless)} rows have an empty key part and cannot be tracked`}</span>
            )}
            {last.rows.passedOver > 0 && (
              <GlyphRef
                icon={Info}
                title="Rows passed over"
                label={`${formatCount(last.rows.passedOver)} passed over`}
                body={last.rows.passedOverWhy.map((why) => `${formatCount(why.count)}: ${why.reason}`).join("\n")}
              />
            )}
            {last.issues.length > 0 && (
              <GlyphRef icon={CircleAlert} title="The mapping's preflight warnings" label={`${last.issues.length} warnings`} body={last.issues.join("\n")} />
            )}
          </div>
          <SummaryStrip cells={cells} minCellWidth={140} data-testid="value-check-summary-strip" />
          {failingAttributes.length > 0 && (
            <div className="flex flex-wrap items-center gap-1.5" data-testid="value-check-failing-attributes">
              <span className="text-[12px] text-muted-foreground">
                {`${failingAttributes.length} ${failingAttributes.length === 1 ? "attribute has" : "attributes have"} rows that fail:`}
              </span>
              {failingAttributes.slice(0, NAMED_ATTRIBUTES).map((result) => {
                const worst = outcomeVisual(worstOf(result.variable.rows) ?? "valid");
                const Icon = worst.icon;
                return (
                  <button
                    key={result.variable.target}
                    type="button"
                    onClick={() => session.requestFocus(result.variable.target)}
                    className="inline-flex items-center gap-1 rounded-md border px-1.5 py-0.5 text-[12px] outline-none transition-colors hover:bg-accent/60 focus-visible:ring-2 focus-visible:ring-ring/50"
                    title={result.variable.target}
                    data-testid={`value-check-failing-${result.variable.target}`}
                  >
                    <Icon className={cn("size-3.5", worst.textClass)} />
                    <span className="font-mono">{splitPath(result.variable.target).leaf}</span>
                    <span className="font-mono text-muted-foreground tabular-nums">{compactCount(failingOf(result.variable.rows))}</span>
                  </button>
                );
              })}
              {failingAttributes.length > NAMED_ATTRIBUTES && (
                <span className="text-[12px] text-muted-foreground">{`and ${failingAttributes.length - NAMED_ATTRIBUTES} more`}</span>
              )}
              <Label className="ml-auto flex items-center gap-2 text-[12px] font-normal">
                <Switch
                  checked={session.filter !== null}
                  onCheckedChange={(on) => session.setFilter(on ? "any" : null)}
                  data-testid="value-check-show-failing"
                />
                {session.filter === null || session.filter === "any"
                  ? "Show only failing in the tree"
                  : `Show only ${outcomeVisual(session.filter).label.toLowerCase()} in the tree`}
              </Label>
            </div>
          )}
        </div>
      )}
    </Card>
  );
}
