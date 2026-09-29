import { useMemo, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { format } from "date-fns";
import { ChevronRight, CircleAlert, Play, RotateCcw } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { pipelineApi } from "@/api/endpoints";
import { IdChip } from "@/components/IdChip";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { useTabTitle } from "@/layout/workbench/TabsContext";
import { cn } from "@/lib/utils";
import { deliveryApi, type DeliveryAssertionRunDetail, type DeliveryTestResult, type TestOutcome } from "../../../api/delivery";
import { KindText } from "../KindText";
import { ProblemView } from "../TemplateSheet";
import { AssertionRunStatusBadge, OutcomeBar, PassRateRing, TestOutcomeBadge, TestOutcomeIcon } from "./AssertionBadges";
import { AssertionOutcomesTable } from "./AssertionOutcomesTable";
import { AssertionRunDialog, ReportDownloads, type AssertionLaunch } from "./AssertionRunDialog";
import { OUTCOME_ORDER, STANDING_TEXT, counted, duration, kindEntity, selectionText, worstStanding } from "./assertionFormat";

const OUTCOMES: readonly TestOutcome[] = ["failed", "errored", "warned", "passed", "skipped"];

function tally(results: readonly DeliveryTestResult[]) {
  const count = (outcome: TestOutcome) => results.filter((r) => r.outcome === outcome).length;
  return { passed: count("passed"), failed: count("failed"), warned: count("warned"), errored: count("errored"), skipped: count("skipped") };
}

/** One test's result in the report: its line, and when opened, everything it found. */
function ResultCard({ result, initiallyOpen }: { result: DeliveryTestResult; initiallyOpen: boolean }) {
  const [open, setOpen] = useState(initiallyOpen);
  const assertions = result.assertions ?? [];
  const holding = assertions.filter((a) => a.outcome === "passed").length;
  const worst = worstStanding(assertions);
  return (
    <Card className="gap-0 overflow-hidden rounded-lg p-0" data-testid={`report-result-${result.test}`} data-outcome={result.outcome}>
      <button
        type="button"
        onClick={() => setOpen((was) => !was)}
        aria-expanded={open}
        className="grid w-full grid-cols-[auto_auto_minmax(0,1fr)_auto] items-center gap-3 px-3 py-2.5 text-left hover:bg-muted/40 md:grid-cols-[auto_auto_minmax(0,1fr)_9rem_7rem_5rem]"
      >
        <ChevronRight className={cn("size-4 text-muted-foreground transition-transform", open && "rotate-90")} />
        <TestOutcomeIcon outcome={result.outcome} />
        <span className="min-w-0">
          <span className="flex flex-wrap items-center gap-1.5">
            <span className="font-mono text-[12.5px] font-medium">{result.test}</span>
            {(result.tags ?? []).map((tag) => <span key={tag} className="font-mono text-[10.5px] text-muted-foreground">#{tag}</span>)}
          </span>
          <span className="block truncate text-[12px] text-muted-foreground">{result.description ?? kindEntity(result.kind)}</span>
        </span>
        <span className="hidden text-right font-mono text-[11.5px] tabular-nums text-muted-foreground md:block">
          {result.matched !== null && result.matched !== undefined ? `${result.matched.toLocaleString("en-US")} matched` : ""}
          {result.sampled ? ", sample" : ""}
        </span>
        <span className={cn("hidden text-right font-mono text-[12px] tabular-nums md:block", result.outcome !== "skipped" && worst !== null && STANDING_TEXT[worst])}>
          {assertions.length === 0 ? "-" : `${holding}/${assertions.length} hold`}
        </span>
        <span className="text-right font-mono text-[11.5px] tabular-nums text-muted-foreground">{duration(result.durationMs)}</span>
      </button>
      {open && (
        <div className="flex flex-col gap-3 border-t px-3 py-3">
          <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-[12px] text-muted-foreground">
            <span className="min-w-0 max-w-[28rem]"><KindText kind={result.kind} /></span>
            {result.query && <code className="break-all font-mono text-[11.5px]">{result.query}</code>}
            {(result.ids ?? 0) > 0 && <span>{result.ids} record(s) by id</span>}
            {result.template && <span>template version <span className="font-mono text-[11.5px]">{result.template}</span></span>}
          </div>
          {result.error && (
            <Alert variant="destructive">
              <CircleAlert />
              <AlertTitle>The test could not be evaluated</AlertTitle>
              <AlertDescription>{result.error}</AlertDescription>
            </Alert>
          )}
          {(result.problems ?? []).length > 0 && (
            <Alert variant="destructive">
              <CircleAlert />
              <AlertTitle>It does not fit the template of its kind</AlertTitle>
              <AlertDescription>
                <ul className="list-disc pl-4">{(result.problems ?? []).map((p) => <li key={p}>{p}</li>)}</ul>
              </AlertDescription>
            </Alert>
          )}
          {(result.notes ?? []).length > 0 && (
            <ul className="list-disc pl-5 text-[12.5px] text-muted-foreground">{(result.notes ?? []).map((note) => <li key={note}>{note}</li>)}</ul>
          )}
          <AssertionOutcomesTable outcomes={assertions} />
        </div>
      )}
    </Card>
  );
}

/** The tests of the report by the kind they read, each kind's outcomes as a bar: where the estate is weak at a glance. */
function ByKind({ results }: { results: readonly DeliveryTestResult[] }) {
  const kinds = useMemo(() => {
    const groups = new Map<string, DeliveryTestResult[]>();
    for (const result of results) {
      groups.set(result.kind, [...(groups.get(result.kind) ?? []), result]);
    }

    return [...groups.entries()].sort((a, b) => a[0].localeCompare(b[0]));
  }, [results]);
  if (kinds.length < 2) {
    return null;
  }

  return (
    <Card className="gap-2 rounded-lg p-3" data-testid="report-by-kind">
      <h2 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">By kind</h2>
      <div className="grid gap-x-4 gap-y-2 md:grid-cols-[minmax(0,22rem)_minmax(8rem,1fr)_auto]">
        {kinds.map(([kind, group]) => {
          const counts = tally(group);
          return (
            <div key={kind} className="contents">
              <span className="min-w-0"><KindText kind={kind} /></span>
              <span className="flex items-center"><OutcomeBar counts={counts} /></span>
              <span className="font-mono text-[11.5px] tabular-nums text-muted-foreground">{counts.passed}/{group.length} passed</span>
            </div>
          );
        })}
      </div>
    </Card>
  );
}

/** The report's facts line: which flow, where, when, how long, by whom, and what it was asked to run. */
function Subtitle({ detail }: { detail: DeliveryAssertionRunDetail }) {
  const run = detail.run;
  return (
    <span className="flex flex-wrap items-center gap-x-2 gap-y-1">
      {detail.pipelineId !== null
        ? <Link to={`/pipelines/${detail.pipelineId}?tab=tests`} className="font-mono text-foreground hover:underline">{run.flowName}</Link>
        : <span className="font-mono">{run.flowName}</span>}
      {run.partition !== null && <span>in <span className="font-mono">{run.partition}</span></span>}
      <span>{format(new Date(run.startedUtc), "yyyy-MM-dd HH:mm:ss")}</span>
      {run.completedUtc !== null && <span>took {duration(new Date(run.completedUtc).getTime() - new Date(run.startedUtc).getTime())}</span>}
      <span>by {run.actor}</span>
      <span>ran {selectionText(run.selection)}</span>
    </span>
  );
}

/**
 * One run of an assertion flow's tests, as the report of how OSDU stood when it ran: how many passed, how each kind fared,
 * and every test with what each assertion found and the records that failed it. It downloads as HTML, Markdown, JSON and
 * JUnit XML, and the tests that did not pass run again from here.
 */
export default function AssertionReportPage() {
  const { assertionRunId: raw } = useParams();
  const assertionRunId = Number(raw);
  const valid = Number.isSafeInteger(assertionRunId) && assertionRunId > 0;
  const [shown, setShown] = useState<TestOutcome | null>(null);
  const [launch, setLaunch] = useState<AssertionLaunch | null>(null);
  const detail = useQuery({
    queryKey: ["delivery", "assertions", "run", assertionRunId],
    queryFn: () => deliveryApi.assertionRun(assertionRunId),
    enabled: valid,
    refetchInterval: (query) => (query.state.data?.run.status === "running" ? 5000 : false),
  });
  const pipeline = useQuery({
    queryKey: ["pipelines", "detail", detail.data?.pipelineId ?? null],
    queryFn: () => pipelineApi.getById(detail.data!.pipelineId!),
    enabled: detail.data?.pipelineId !== null && detail.data?.pipelineId !== undefined,
    staleTime: 60000,
  });
  useTabTitle(valid ? `Report #${assertionRunId}` : undefined);

  const results = useMemo(() => {
    const all = detail.data?.results ?? [];
    return [...all].sort((a, b) => OUTCOME_ORDER.indexOf(a.outcome) - OUTCOME_ORDER.indexOf(b.outcome) || a.test.localeCompare(b.test));
  }, [detail.data]);

  if (!valid) {
    return <Page data-testid="page-assertion-report"><p className="text-[13px] text-destructive">'{raw}' is not a report number.</p></Page>;
  }

  if (detail.isError) {
    return <Page data-testid="page-assertion-report"><ProblemView error={detail.error} testId="assertion-report-error" /></Page>;
  }

  if (detail.data === undefined) {
    return <Page data-testid="page-assertion-report"><Skeleton className="h-72 w-full rounded-lg" /></Page>;
  }

  const { run } = detail.data;
  const counts = tally(results);
  const evaluated = counts.passed + counts.failed + counts.warned + counts.errored;
  const notPassing = results.filter((r) => r.outcome === "failed" || r.outcome === "errored" || r.outcome === "warned").map((r) => r.test);
  const shownResults = shown === null ? results : results.filter((r) => r.outcome === shown);
  const launchable = pipeline.data !== undefined;
  const again = (tests: readonly string[], tags: readonly string[]) => {
    if (pipeline.data !== undefined) {
      setLaunch({ pipelineId: pipeline.data.id, repoId: pipeline.data.repoId, flowName: pipeline.data.name, tests, tags });
    }
  };
  const selection = (() => {
    try {
      const parsed = run.selection === null ? {} : JSON.parse(run.selection) as { tests?: string[]; tags?: string[] };
      return { tests: parsed.tests ?? [], tags: parsed.tags ?? [] };
    } catch {
      return { tests: [], tags: [] };
    }
  })();

  return (
    <Page data-testid="page-assertion-report">
      <PageHeader
        title={(
          <span className="flex flex-wrap items-center gap-2">
            Report #{run.assertionRunId}
            <AssertionRunStatusBadge status={run.status} />
          </span>
        )}
        subtitle={<Subtitle detail={detail.data} />}
        actions={(
          <>
            {run.runId !== null && <IdChip label="run" value={run.runId} to={`/runs/${run.runId}`} testId="assertion-report-run" />}
            {notPassing.length > 0 && (
              <Button size="sm" onClick={() => again(notPassing, [])} disabled={!launchable} data-testid="assertion-report-rerun-failing">
                <RotateCcw />
                Run the {counted(notPassing.length, "test")} not passing again
              </Button>
            )}
            <Button size="sm" variant="outline" onClick={() => again(selection.tests, selection.tags)} disabled={!launchable} data-testid="assertion-report-rerun">
              <Play />
              Run again
            </Button>
            <ReportDownloads assertionRunId={run.assertionRunId} flowName={run.flowName} partition={run.partition} testId="assertion-report-downloads" />
          </>
        )}
      />

      {run.error && (
        <Alert variant={run.status === "failed" ? "default" : "destructive"} data-testid="assertion-report-run-error">
          <CircleAlert />
          <AlertTitle>{run.status === "failed" ? "The run failed on its tests" : "The run stopped"}</AlertTitle>
          <AlertDescription>{run.error}</AlertDescription>
        </Alert>
      )}

      <Card className="flex flex-col gap-3 rounded-lg p-3 lg:flex-row lg:items-center" data-testid="assertion-report-score">
        <div className="shrink-0 px-1">
          <PassRateRing rate={evaluated === 0 ? null : Math.round((1000 * counts.passed) / evaluated) / 10} evaluated={evaluated} />
        </div>
        <div className="flex flex-1 flex-wrap gap-2">
          {OUTCOMES.map((outcome) => {
            const count = counts[outcome as keyof typeof counts];
            return (
              <button
                key={outcome}
                type="button"
                aria-pressed={shown === outcome}
                onClick={() => setShown((was) => (was === outcome ? null : outcome))}
                className={cn(
                  "flex min-w-[6.5rem] flex-1 flex-col items-start gap-0.5 rounded-lg border bg-card px-3 py-2 text-left transition-colors hover:bg-accent/50",
                  shown === outcome && "border-primary ring-1 ring-inset ring-primary",
                )}
                data-testid={`assertion-report-tile-${outcome}`}
              >
                <TestOutcomeBadge outcome={outcome} testId={`assertion-report-tile-${outcome}-badge`} />
                <span className="font-mono text-xl font-semibold leading-7 tabular-nums">{count}</span>
              </button>
            );
          })}
        </div>
      </Card>

      <ByKind results={results} />

      <div className="flex flex-col gap-2" data-testid="assertion-report-results">
        {shownResults.length === 0
          ? <p className="text-[13px] text-muted-foreground">{results.length === 0 ? "The run recorded no result yet." : "No test came out this way."}</p>
          : shownResults.map((result) => (
            <ResultCard
              key={result.test}
              result={result}
              initiallyOpen={result.outcome === "failed" || result.outcome === "errored" || (result.outcome === "warned" && notPassing.length <= 5)}
            />
          ))}
      </div>

      <AssertionRunDialog launch={launch} onClose={() => setLaunch(null)} />
    </Page>
  );
}
