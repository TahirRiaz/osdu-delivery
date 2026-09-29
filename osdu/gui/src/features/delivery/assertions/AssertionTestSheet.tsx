import { useMemo, useState, type ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { format } from "date-fns";
import { CircleAlert, FileText, Play, TriangleAlert } from "lucide-react";
import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { CopyButton } from "@/components/CopyButton";
import { EmptyState } from "@/components/EmptyState";
import { SummaryStrip } from "@/components/SummaryStrip";
import { useThemeMode } from "@/theme/ThemeModeContext";
import {
  deliveryApi, type DeliveryAssertionFlow, type DeliveryAssertionTest, type DeliveryTestResult,
} from "../../../api/delivery";
import { useActivePartition } from "../activePartition";
import { KindText } from "../KindText";
import { ProblemView } from "../TemplateSheet";
import { OutcomeSquare, SeverityChip, TestOutcomeBadge } from "./AssertionBadges";
import { AssertionOutcomesTable } from "./AssertionOutcomesTable";
import { assertionStanding, duration, standingOf, worstStanding, type TestStanding } from "./assertionFormat";

/** How many past runs the sheet reads a test's trend over. */
const TREND_RUNS = 30;

/** The tone of the holding tile, by the heaviest assertion that does not hold. */
const HOLDING_TONES: Partial<Record<TestStanding, "destructive" | "warning" | "info">> = {
  failed: "destructive",
  errored: "destructive",
  warned: "warning",
  noted: "info",
};

/** Chart ink from the app's own custom properties (DESIGN.md 3.4), re-read when the theme flips. */
function useInk() {
  const { mode } = useThemeMode();
  return useMemo(() => {
    const token = (name: string) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    return {
      series: token("--chart-1"),
      axis: token("--muted-foreground"),
      grid: token("--border"),
      passed: token("--success"),
      failed: token("--destructive"),
      warned: token("--warning"),
      noted: token("--info"),
      muted: token("--muted-foreground"),
    };
    // The mode is what changes the tokens' values; the hook reads them again when it flips.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mode]);
}

function when(utc: string | undefined): string {
  return utc === undefined ? "-" : format(new Date(utc), "MM-dd HH:mm");
}

interface TrendPoint {
  at: string;
  value: number | null;
  outcome: TestStanding;
}

/**
 * A number a test measures, run over run: the records it matched, or what one of its assertions measured (a count, an
 * aggregate, the share that held), each point in the tone of how that run came out, so a drift is seen before it fails.
 */
function TestTrend({ results }: { results: readonly DeliveryTestResult[] }) {
  const ink = useInk();
  const oldestFirst = useMemo(() => [...results].reverse(), [results]);
  const measures = useMemo(() => {
    const labels = new Map<string, string>();
    for (const result of oldestFirst) {
      for (const outcome of result.assertions ?? []) {
        if (typeof outcome.value === "number" && !labels.has(outcome.label)) {
          labels.set(outcome.label, outcome.label);
        }
      }
    }

    return [{ key: "matched", label: "Records matched" }, ...[...labels.keys()].map((label) => ({ key: `a:${label}`, label }))];
  }, [oldestFirst]);
  const [measure, setMeasure] = useState("matched");
  const chosen = measures.some((m) => m.key === measure) ? measure : "matched";
  const points = useMemo<TrendPoint[]>(() => oldestFirst.map((result) => {
    if (chosen === "matched") {
      return { at: when(result.completedUtc), value: result.matched ?? null, outcome: result.outcome };
    }

    const found = (result.assertions ?? []).find((a) => `a:${a.label}` === chosen);
    return { at: when(result.completedUtc), value: typeof found?.value === "number" ? found.value : null, outcome: found === undefined ? "skipped" : assertionStanding(found) };
  }), [chosen, oldestFirst]);
  const toneOf = (outcome: TestStanding) => (
    outcome === "passed" ? ink.passed : outcome === "failed" || outcome === "errored" ? ink.failed : outcome === "warned" ? ink.warned : outcome === "noted" ? ink.noted : ink.muted
  );

  if (points.filter((p) => p.value !== null).length < 2) {
    return (
      <p className="text-[12.5px] text-muted-foreground" data-testid="test-trend-empty">
        The trend draws once the test has measured something in two runs.
      </p>
    );
  }

  return (
    <div className="flex flex-col gap-2" data-testid="test-trend">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="text-[12px] text-muted-foreground">Each point is a run, in the tone of how it came out.</span>
        <Select value={chosen} onValueChange={setMeasure}>
          <SelectTrigger size="sm" className="h-8 w-64" data-testid="test-trend-measure"><SelectValue /></SelectTrigger>
          <SelectContent>
            {measures.map((m) => <SelectItem key={m.key} value={m.key}>{m.label}</SelectItem>)}
          </SelectContent>
        </Select>
      </div>
      <ResponsiveContainer width="100%" height={180}>
        <LineChart data={points} margin={{ top: 6, right: 12, bottom: 0, left: 0 }}>
          <CartesianGrid stroke={ink.grid} strokeDasharray="3 3" vertical={false} />
          <XAxis dataKey="at" stroke={ink.axis} tick={{ fontSize: 11 }} minTickGap={24} />
          <YAxis stroke={ink.axis} tick={{ fontSize: 11 }} width={56} allowDecimals domain={["auto", "auto"]} />
          <Tooltip
            formatter={(value) => [typeof value === "number" ? value.toLocaleString("en-US") : String(value), measures.find((m) => m.key === chosen)?.label ?? ""]}
            contentStyle={{ fontSize: 12 }}
          />
          <Line
            dataKey="value"
            stroke={ink.series}
            strokeWidth={2}
            connectNulls
            isAnimationActive={false}
            dot={(props: { cx?: number; cy?: number; index?: number }) => {
              const point = points[props.index ?? 0];
              if (props.cx === undefined || props.cy === undefined || point === undefined || point.value === null) {
                return <g key={`dot-${props.index}`} />;
              }

              return <circle key={`dot-${props.index}`} cx={props.cx} cy={props.cy} r={4} fill={toneOf(point.outcome)} stroke="none" />;
            }}
          />
        </LineChart>
      </ResponsiveContainer>
    </div>
  );
}

/**
 * Each assertion of a test across its last runs, oldest on the left: which one broke, when, and whether it has been
 * flipping. An assertion the test did not have in a run (it was added or renamed since) leaves that run's square empty.
 */
function AssertionTimeline({ test, results }: { test: DeliveryAssertionTest; results: readonly DeliveryTestResult[] }) {
  const oldestFirst = [...results].reverse();
  if (oldestFirst.length === 0) {
    return null;
  }

  return (
    <div className="overflow-x-auto rounded-lg border p-3" data-testid="assertion-timeline">
      <div className="flex flex-col gap-1.5">
        {test.assertions.map((assertion) => (
          <div key={assertion.index} className="flex items-center gap-3">
            <span className="w-56 shrink-0 truncate text-[12px]" title={assertion.label}>{assertion.label}</span>
            <div className="flex items-center gap-[3px]">
              {oldestFirst.map((result, column) => {
                const found = (result.assertions ?? []).find((a) => a.label === assertion.label);
                return found === undefined
                  ? <span key={column} className="size-3 rounded-[2px] border border-dashed border-border" title="not in this run" />
                  : (
                    <OutcomeSquare
                      key={column}
                      outcome={assertionStanding(found)}
                      title={`${when(result.completedUtc)}${found.actual ? `, found ${found.actual}` : ""}`}
                      size="size-3"
                      testId="timeline-cell"
                    />
                  );
              })}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

/** What the test reads and asserts, as its flow document declares it. */
function Definition({ test }: { test: DeliveryAssertionTest }) {
  return (
    <div className="flex flex-col gap-2" data-testid="test-definition">
      <dl className="grid grid-cols-[8rem_1fr] gap-x-3 gap-y-1.5 text-[12.5px]">
        <dt className="text-muted-foreground">Kind</dt>
        <dd className="min-w-0"><KindText kind={test.kind} /></dd>
        <dt className="text-muted-foreground">Selects</dt>
        <dd className="min-w-0">
          {test.query !== null
            ? (
              <span className="flex items-start gap-1">
                <code className="whitespace-pre-wrap break-all font-mono text-[11.5px]">{test.query}</code>
                <CopyButton label="Copy the query" text={test.query} testId="copy-test-query" iconOnly />
              </span>
            )
            : test.ids > 0 ? `${test.ids.toLocaleString("en-US")} record(s) by id` : "every record of the kind"}
        </dd>
        <dt className="text-muted-foreground">Reads</dt>
        <dd>
          {test.read === "index" ? "the search index" : "storage"}, up to {test.maxRecords.toLocaleString("en-US")} records
          {test.bulk ? ", and each record's bulk data in its DDMS" : ""}
        </dd>
        <dt className="text-muted-foreground">Template</dt>
        <dd className="text-[12.5px]">
          {test.template === null
            ? "none needed: it reads no field of an exact kind"
            : <>the saved version <span className="font-mono text-[11.5px]">{test.template}</span> of its kind</>}
        </dd>
        {test.partitions.length > 0 && (
          <>
            <dt className="text-muted-foreground">Partitions</dt>
            <dd className="font-mono text-[11.5px]">{test.partitions.join(", ")}</dd>
          </>
        )}
      </dl>
      <div className="overflow-x-auto rounded-lg border">
        <table className="w-full text-[12.5px]">
          <thead>
            <tr className="border-b bg-muted/40 text-left text-[11px] uppercase tracking-wider text-muted-foreground">
              <th className="px-2 py-1.5 font-medium">Assertion</th>
              <th className="px-2 py-1.5 font-medium">Type</th>
              <th className="px-2 py-1.5 font-medium">Severity</th>
              <th className="px-2 py-1.5 font-medium">Expects</th>
            </tr>
          </thead>
          <tbody>
            {test.assertions.map((assertion) => (
              <tr key={assertion.index} className="border-b last:border-b-0">
                <td className="px-2 py-1.5" title={assertion.description ?? undefined}>{assertion.label}</td>
                <td className="px-2 py-1.5 font-mono text-[11.5px] text-muted-foreground">{assertion.type}</td>
                <td className="px-2 py-1.5"><SeverityChip severity={assertion.severity} /></td>
                <td className="px-2 py-1.5 font-mono text-[11.5px]">{assertion.expected}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function Section({ title, children, testId }: { title: string; children: ReactNode; testId?: string }) {
  return (
    <section className="flex flex-col gap-2" data-testid={testId}>
      <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">{title}</h3>
      {children}
    </section>
  );
}

/**
 * One test of an assertion flow, opened from a board: where it stands, what every assertion found in its latest run and
 * the records that failed it, how it has come out over its last runs, and what it is declared to read and assert. The test
 * runs from here on its own.
 */
export function AssertionTestSheet({ flow, test, onClose, onRun }: {
  flow: DeliveryAssertionFlow;
  test: DeliveryAssertionTest | null;
  onClose: () => void;
  onRun: (test: DeliveryAssertionTest) => void;
}) {
  const navigate = useNavigate();
  const [active] = useActivePartition();
  const history = useQuery({
    queryKey: ["delivery", "assertions", "history", flow.pipelineId, test?.name ?? null, active],
    queryFn: () => deliveryApi.assertionTestHistory(flow.pipelineId, test!.name, TREND_RUNS),
    enabled: test !== null,
  });
  const results = useMemo(() => history.data?.results ?? [], [history.data]);
  const latest = results.find((r) => r.outcome !== "skipped") ?? results[0];

  return (
    <Sheet open={test !== null} onOpenChange={(next) => { if (!next) { onClose(); } }}>
      <SheetContent
        className="w-full gap-0 sm:max-w-4xl"
        // Focus lands on the sheet itself rather than its first button, so it opens at the top with no tooltip showing.
        onOpenAutoFocus={(event) => { event.preventDefault(); (event.currentTarget as HTMLElement).focus(); }}
        data-testid="assertion-test-sheet"
      >
        {test !== null && (
          <>
            <SheetHeader className="gap-2 border-b">
              <div className="flex flex-wrap items-center gap-2 pr-8">
                <TestOutcomeBadge outcome={standingOf(test)} />
                <SheetTitle className="font-mono text-[15px]">{test.name}</SheetTitle>
                <SeverityChip severity={test.severity} />
                {test.tags.map((tag) => <Badge key={tag} variant="outline" className="font-mono text-[11px]">#{tag}</Badge>)}
              </div>
              <SheetDescription>{test.description ?? `A test of ${flow.name}.`}</SheetDescription>
              <div className="flex flex-wrap items-center gap-2">
                <Button size="sm" onClick={() => onRun(test)} disabled={!test.runsHere} data-testid="assertion-test-run">
                  <Play />
                  Run this test
                </Button>
                {test.latest !== null && (
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => navigate(`/delivery/assertions/runs/${test.latest!.assertionRunId}`)}
                    data-testid="assertion-test-report"
                  >
                    <FileText />
                    Its latest report
                  </Button>
                )}
                {!test.runsHere && (
                  <span className="text-[12px] text-muted-foreground">
                    It tests {test.partitions.join(", ")}; pick {test.partitions.length > 1 ? "one of them" : "it"} in the title bar to run it.
                  </span>
                )}
              </div>
            </SheetHeader>
            <div className="flex flex-1 flex-col gap-5 overflow-y-auto px-4 py-4 [&>*]:shrink-0">
              {test.problems.length > 0 && (
                <Alert variant="destructive" data-testid="assertion-test-problems">
                  <CircleAlert />
                  <AlertTitle>It does not fit the template of its kind, so it is not evaluated</AlertTitle>
                  <AlertDescription>
                    <ul className="list-disc pl-4">
                      {test.problems.map((problem) => <li key={problem}>{problem}</li>)}
                    </ul>
                  </AlertDescription>
                </Alert>
              )}
              {test.changed && (
                <Alert data-testid="assertion-test-changed">
                  <TriangleAlert />
                  <AlertDescription>
                    The test has changed since its latest result: run it again for a result of what it asserts now.
                  </AlertDescription>
                </Alert>
              )}

              {history.isError && <ProblemView error={history.error} testId="assertion-test-history-error" />}
              {history.isPending && <Skeleton className="h-48 w-full rounded-lg" />}
              {!history.isPending && latest === undefined && (
                <EmptyState title="Not run yet" description="Run the test for its first result: what every assertion found, and the records that failed it." />
              )}

              {latest !== undefined && (
                <>
                  <SummaryStrip
                    minCellWidth={140}
                    cells={[
                      { label: "Matched", value: latest.matched?.toLocaleString("en-US") ?? "-", caption: latest.query ? "records the query matched" : "records named by id", testId: "test-matched" },
                      {
                        label: "Evaluated",
                        value: latest.evaluated?.toLocaleString("en-US") ?? "-",
                        caption: latest.sampled ? "a sample: more matched than it reads" : "records its assertions held to",
                        tone: latest.sampled ? "info" : undefined,
                        testId: "test-evaluated",
                      },
                      {
                        label: "Holding",
                        value: `${(latest.assertions ?? []).filter((a) => a.outcome === "passed").length} of ${(latest.assertions ?? []).length}`,
                        caption: "assertions",
                        tone: HOLDING_TONES[worstStanding(latest.assertions ?? []) ?? "passed"],
                        testId: "test-holding",
                      },
                      { label: "Took", value: duration(latest.durationMs), caption: latest.completedUtc ? `at ${when(latest.completedUtc)}` : undefined, testId: "test-took" },
                    ]}
                  />
                  {latest.error && (
                    <Alert variant="destructive" data-testid="assertion-test-error">
                      <CircleAlert />
                      <AlertTitle>The test could not be evaluated</AlertTitle>
                      <AlertDescription>{latest.error}</AlertDescription>
                    </Alert>
                  )}
                  {(latest.notes ?? []).length > 0 && (
                    <ul className="list-disc pl-5 text-[12.5px] text-muted-foreground" data-testid="assertion-test-notes">
                      {(latest.notes ?? []).map((note) => <li key={note}>{note}</li>)}
                    </ul>
                  )}
                  <Section title="What each assertion found" testId="assertion-test-latest">
                    <AssertionOutcomesTable key={`${latest.completedUtc ?? ""}`} outcomes={latest.assertions ?? []} />
                  </Section>
                  <Section title={`Over the last ${results.length} run${results.length === 1 ? "" : "s"}`} testId="assertion-test-history">
                    <AssertionTimeline test={test} results={results} />
                    <TestTrend results={results} />
                  </Section>
                </>
              )}

              <Section title="As declared" testId="assertion-test-declared">
                <Definition test={test} />
              </Section>
            </div>
          </>
        )}
      </SheetContent>
    </Sheet>
  );
}
