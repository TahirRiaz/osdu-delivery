import { useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { CircleAlert, FileText, Play, TriangleAlert } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { CopyButton } from "@/components/CopyButton";
import { RelativeTime } from "@/components/RelativeTime";
import { cn } from "@/lib/utils";
import {
  deliveryApi, type DeliveryAssertionFlow, type DeliveryAssertionTest,
} from "../../../api/delivery";
import { KindText } from "../KindText";
import { ProblemView } from "../TemplateSheet";
import { TestOutcomeIcon } from "./AssertionBadges";
import { CheckDetail, CheckList } from "./AssertionChecks";
import { STANDING_TEXT, checkCounts, checksVerdict, counted, duration, standingOf, testVerdict, type TestStanding } from "./assertionFormat";

/** The sheet's tabs, which its address names (`testTab`) so a link opens the one it means. */
const TABS = ["checks", "definition"] as const;
type SheetTab = (typeof TABS)[number];

/** What the test reads, as its flow document declares it: the type and schema, which records, and how. */
function Definition({ test }: { test: DeliveryAssertionTest }) {
  return (
    <dl className="grid grid-cols-[7rem_1fr] gap-x-3 gap-y-2 text-[12.5px]" data-testid="test-definition">
      <dt className="text-muted-foreground">Type</dt>
      <dd className="min-w-0"><KindText kind={test.kind} /></dd>
      <dt className="text-muted-foreground">Schema</dt>
      <dd>
        {test.template === null
          ? <span className="text-muted-foreground">none needed: it reads no field</span>
          : <span className="font-mono text-[12px]">{test.template}</span>}
      </dd>
      <dt className="text-muted-foreground">Selects</dt>
      <dd className="min-w-0">
        {test.query !== null
          ? (
            <span className="flex items-start gap-1">
              <code className="whitespace-pre-wrap break-all font-mono text-[12px]">{test.query}</code>
              <CopyButton label="Copy the query" text={test.query} testId="copy-test-query" iconOnly />
            </span>
          )
          : test.ids > 0 ? counted(test.ids, "record") + " by id" : "every record of the type"}
      </dd>
      <dt className="text-muted-foreground">Reads</dt>
      <dd>
        {test.read === "index" ? "the search index" : "storage"}, at most {test.maxRecords.toLocaleString("en-US")} records
        {test.bulk ? ", and each record's bulk data in the wellbore DDMS" : ""}
      </dd>
      <dt className="text-muted-foreground">Checks</dt>
      <dd>{counted(test.assertions.length, "check")}, {test.severity} unless a check says otherwise</dd>
      {test.tags.length > 0 && (
        <>
          <dt className="text-muted-foreground">Tags</dt>
          <dd className="font-mono text-[12px]">{test.tags.map((tag) => `#${tag}`).join(" ")}</dd>
        </>
      )}
      {test.partitions.length > 0 && (
        <>
          <dt className="text-muted-foreground">Partitions</dt>
          <dd className="font-mono text-[12px]">{test.partitions.join(", ")}</dd>
        </>
      )}
    </dl>
  );
}

/** A test with no result yet: its checks as declared, each with what it will expect. */
function DeclaredChecks({ test }: { test: DeliveryAssertionTest }) {
  return (
    <div className="flex flex-col gap-2" data-testid="declared-checks">
      <p className="text-[12.5px] text-muted-foreground">Not run yet. These are the checks its first run will make.</p>
      <div className="divide-y overflow-hidden rounded-lg border">
        {test.assertions.map((assertion) => (
          <div key={assertion.index} className="grid grid-cols-[auto_minmax(0,1fr)_minmax(0,1fr)] items-center gap-x-2.5 px-3 py-1.5">
            <TestOutcomeIcon outcome="notRun" />
            <span className="truncate text-[12.5px]" title={assertion.label}>{assertion.label}</span>
            <span className="truncate text-right font-mono text-[11.5px] text-muted-foreground" title={assertion.expected}>{assertion.expected}</span>
          </div>
        ))}
      </div>
    </div>
  );
}

/**
 * One test, opened from a board, as its last run found the data: the Checks tab answers what does not hold and why, one
 * check at a time, and Definition what the test reads. The test runs again from here for how the data stands now.
 */
export function AssertionTestSheet({ flow, test, onClose, onRun }: {
  flow: DeliveryAssertionFlow;
  test: DeliveryAssertionTest | null;
  onClose: () => void;
  onRun: (test: DeliveryAssertionTest) => void;
}) {
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  // The test's last result is what the sheet shows: how the data stood when it last ran. Earlier runs are the flow's
  // History and Reports tabs' to show.
  // The run the board names as the test's last is read whole, as the report page reads it, and the test's result taken
  // from it: a later run that left the test out does not hide it.
  const lastRunId = test?.latest?.assertionRunId ?? null;
  const last = useQuery({
    queryKey: ["delivery", "assertions", "run", lastRunId],
    queryFn: () => deliveryApi.assertionRun(lastRunId!),
    enabled: lastRunId !== null,
  });
  const latest = test === null || lastRunId === null ? undefined : last.data?.results.find((r) => r.test === test.name);
  const requestedTab = params.get("testTab");
  const tab: SheetTab = (TABS as readonly string[]).includes(requestedTab ?? "") ? requestedTab as SheetTab : "checks";
  const checkParam = params.get("check");
  const openCheck = checkParam === null ? null : Number(checkParam);
  const checkOutcome = latest?.assertions?.find((a) => a.index === openCheck) ?? null;

  const show = (next: { tab?: SheetTab; check?: number | null }) => setParams((was) => {
    const merged = new URLSearchParams(was);
    if (next.tab !== undefined) {
      if (next.tab === "checks") {
        merged.delete("testTab");
      } else {
        merged.set("testTab", next.tab);
      }
    }

    if (next.check !== undefined) {
      if (next.check === null) {
        merged.delete("check");
      } else {
        merged.set("check", String(next.check));
      }
    }

    return merged;
  }, { replace: true });

  const verdict = test === null
    ? null
    : latest !== undefined && latest.outcome !== "skipped" ? { ...checksVerdict(checkCounts(latest.assertions ?? [])), standing: latest.outcome as TestStanding } : testVerdict(test);

  return (
    <Sheet open={test !== null} onOpenChange={(next) => { if (!next) { onClose(); } }}>
      <SheetContent
        className="w-full gap-0 sm:max-w-3xl"
        // Focus lands on the sheet itself rather than its first button, so it opens at the top with no tooltip showing.
        onOpenAutoFocus={(event) => { event.preventDefault(); (event.currentTarget as HTMLElement).focus(); }}
        data-testid="assertion-test-sheet"
      >
        {test !== null && verdict !== null && (
          <>
            <SheetHeader className="gap-1.5 border-b pb-3">
              <div className="flex flex-wrap items-center gap-2 pr-8">
                <TestOutcomeIcon outcome={standingOf(test)} />
                <SheetTitle className="font-mono text-[15px]">{test.name}</SheetTitle>
                <div className="ml-auto flex items-center gap-1.5">
                  {test.latest !== null && (
                    <Button
                      size="sm"
                      variant="outline"
                      onClick={() => navigate(`/delivery/assertions/runs/${test.latest!.assertionRunId}`)}
                      data-testid="assertion-test-report"
                    >
                      <FileText />
                      Latest report
                    </Button>
                  )}
                  <Button size="sm" onClick={() => onRun(test)} disabled={!test.runsHere} data-testid="assertion-test-run">
                    <Play />
                    Run
                  </Button>
                </div>
              </div>
              <p className={cn("text-[13px]", STANDING_TEXT[verdict.standing])} data-testid="assertion-test-verdict">
                {verdict.text}
                {latest !== undefined && (
                  <span className="text-muted-foreground">
                    {` · ${latest.matched?.toLocaleString("en-US") ?? "-"} matched`}
                    {latest.sampled ? `, ${latest.evaluated?.toLocaleString("en-US") ?? "-"} read as a sample` : ""}
                    {` · ${duration(latest.durationMs)} · `}
                    <RelativeTime value={latest.completedUtc ?? null} absolute={false} />
                  </span>
                )}
              </p>
              <SheetDescription className={cn(test.description === null && "sr-only")}>
                {test.description ?? `A test of ${flow.name}.`}
              </SheetDescription>
            </SheetHeader>

            <Tabs
              value={tab}
              onValueChange={(value) => show({ tab: value as SheetTab, check: null })}
              className="flex min-h-0 flex-1 flex-col gap-0"
            >
              <TabsList variant="line" className="mx-4 mt-2" data-testid="assertion-test-tabs">
                <TabsTrigger value="checks" data-testid="assertion-test-tab-checks">Checks</TabsTrigger>
                <TabsTrigger value="definition" data-testid="assertion-test-tab-definition">Definition</TabsTrigger>
              </TabsList>
              <div className="flex-1 overflow-y-auto px-4 py-3">
                <TabsContent value="checks" className="flex flex-col gap-3 [&>*]:shrink-0">
                  {!test.runsHere && (
                    <p className="text-[12.5px] text-muted-foreground">
                      It tests {test.partitions.join(", ")}; pick {test.partitions.length > 1 ? "one of them" : "it"} in the title bar to run it.
                    </p>
                  )}
                  {test.problems.length > 0 && (
                    <Alert variant="destructive" data-testid="assertion-test-problems">
                      <CircleAlert />
                      <AlertTitle>It does not fit the schema of its type, so it is not evaluated</AlertTitle>
                      <AlertDescription>
                        <ul className="list-disc pl-4">{test.problems.map((problem) => <li key={problem}>{problem}</li>)}</ul>
                      </AlertDescription>
                    </Alert>
                  )}
                  {test.changed && (
                    <Alert data-testid="assertion-test-changed">
                      <TriangleAlert />
                      <AlertDescription>The test changed since this result: run it again for a result of what it checks now.</AlertDescription>
                    </Alert>
                  )}
                  {last.isError && <ProblemView error={last.error} testId="assertion-test-last-error" />}
                  {test.latest !== null && last.isPending && <Skeleton className="h-40 w-full rounded-lg" />}
                  {test.latest === null && <DeclaredChecks test={test} />}
                  {latest !== undefined && latest.error && (
                    <Alert variant="destructive" data-testid="assertion-test-error">
                      <CircleAlert />
                      <AlertTitle>The test could not be evaluated</AlertTitle>
                      <AlertDescription>{latest.error}</AlertDescription>
                    </Alert>
                  )}
                  {latest !== undefined && (latest.notes ?? []).length > 0 && (
                    <ul className="list-disc pl-5 text-[12.5px] text-muted-foreground" data-testid="assertion-test-notes">
                      {(latest.notes ?? []).map((note) => <li key={note}>{note}</li>)}
                    </ul>
                  )}
                  {latest !== undefined && (checkOutcome !== null
                    ? <CheckDetail outcome={checkOutcome} onBack={() => show({ check: null })} />
                    : <CheckList key={latest.completedUtc ?? ""} outcomes={latest.assertions ?? []} onOpen={(index) => show({ check: index })} />)}
                </TabsContent>

                <TabsContent value="definition">
                  <Definition test={test} />
                </TabsContent>
              </div>
            </Tabs>
          </>
        )}
      </SheetContent>
    </Sheet>
  );
}
