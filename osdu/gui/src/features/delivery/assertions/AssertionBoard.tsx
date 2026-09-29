import { useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { CircleAlert, FileWarning, ListChecks, PencilLine, Play, Tags, X } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { EmptyState } from "@/components/EmptyState";
import { FilterBar } from "@/components/FilterBar";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { SearchInput } from "@/components/SearchInput";
import { cn } from "@/lib/utils";
import type { DeliveryAssertionBoard, DeliveryAssertionFlow, DeliveryAssertionTest } from "../../../api/delivery";
import { KindText } from "../KindText";
import { StatusStrip, TestOutcomeIcon, type StripCell } from "./AssertionBadges";
import { AssertionRunDialog, ReportDownloads, type AssertionLaunch } from "./AssertionRunDialog";
import { AssertionTestSheet } from "./AssertionTestSheet";
import { OUTCOME_ORDER, STANDING_TEXT, runStatusVisual, standingOf, testVerdict, type TestStanding } from "./assertionFormat";

/** What the status strip filters the tests by: where they stand, or what keeps them from being trusted. */
type BoardFilter = TestStanding | "problems" | "changed";

/** The search parameters the test sheet owns, cleared with it. */
const SHEET_PARAMS = ["test", "flow", "testTab", "check"];

function matches(test: DeliveryAssertionTest, term: string, tags: ReadonlySet<string>, filter: BoardFilter | null): boolean {
  if (tags.size > 0 && !test.tags.some((tag) => tags.has(tag))) {
    return false;
  }

  if (filter === "problems" ? test.problems.length === 0 : filter === "changed" ? !test.changed : filter !== null && standingOf(test) !== filter) {
    return false;
  }

  return term === ""
    || [test.name, test.description ?? "", test.kind, test.query ?? "", ...test.tags, ...test.assertions.map((a) => a.label)]
      .some((text) => text.toLowerCase().includes(term));
}

/**
 * The order tests are listed in: by the type they read when a flow reads several, so each type heads its tests once, then
 * what needs a look first, then as the document declares them.
 */
function byStanding(tests: readonly DeliveryAssertionTest[]): DeliveryAssertionTest[] {
  const kindOrder = [...new Set(tests.map((t) => t.kind))];
  return tests
    .map((test, order) => ({ test, order }))
    .sort((a, b) => kindOrder.indexOf(a.test.kind) - kindOrder.indexOf(b.test.kind)
      || OUTCOME_ORDER.indexOf(standingOf(a.test)) - OUTCOME_ORDER.indexOf(standingOf(b.test))
      || a.order - b.order)
    .map((entry) => entry.test);
}

/** The tags the tests carry, as a picker: every test carrying one of the tags picked is shown. */
function TagFilter({ tags, picked, onChange }: {
  tags: readonly (readonly [string, number])[];
  picked: ReadonlySet<string>;
  onChange: (next: ReadonlySet<string>) => void;
}) {
  if (tags.length === 0) {
    return null;
  }

  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button variant="outline" size="sm" className={cn(picked.size > 0 && "border-primary/70 bg-primary/10 dark:bg-primary/20")} data-testid="assertion-board-tags">
          <Tags />
          {picked.size === 0 ? "Tags" : [...picked].map((tag) => `#${tag}`).join(", ")}
        </Button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-56 p-1">
        {tags.map(([tag, count]) => (
          <label key={tag} className="flex cursor-pointer items-center gap-2 rounded-sm px-2 py-1.5 text-[12.5px] hover:bg-accent/50">
            <Checkbox
              checked={picked.has(tag)}
              onCheckedChange={(on) => {
                const next = new Set(picked);
                if (on === true) {
                  next.add(tag);
                } else {
                  next.delete(tag);
                }

                onChange(next);
              }}
              data-testid={`tag-${tag}`}
            />
            <span className="flex-1 font-mono">#{tag}</span>
            <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{count}</span>
          </label>
        ))}
      </PopoverContent>
    </Popover>
  );
}

/**
 * One test on one line: where it stands, the verdict of its last result, and a way to run it again for how the data
 * stands now. The flow's heading says when its last run was; a test whose last result is older than that (a run that
 * left it out) says which run it is from and when, since an older result may no longer describe the data.
 */
function TestRow({ test, lastRunId, picked, onPick, onOpen, onRun }: {
  test: DeliveryAssertionTest;
  lastRunId: number | null;
  picked: boolean;
  onPick: (next: boolean) => void;
  onOpen: () => void;
  onRun: () => void;
}) {
  const standing = standingOf(test);
  const verdict = testVerdict(test);
  const stale = test.latest !== null && lastRunId !== null && test.latest.assertionRunId !== lastRunId ? test.latest : null;
  return (
    <div
      role="button"
      tabIndex={0}
      onClick={onOpen}
      onKeyDown={(event) => { if (event.key === "Enter") { onOpen(); } }}
      className={cn(
        "group grid cursor-pointer grid-cols-[auto_auto_minmax(0,1fr)_auto] items-center gap-x-2.5 px-3 py-1 outline-none transition-colors hover:bg-accent/50 focus-visible:bg-accent/50",
        "md:grid-cols-[auto_auto_minmax(0,16rem)_minmax(0,1fr)_10rem_auto]",
        standing === "elsewhere" && "opacity-55",
      )}
      data-testid={`board-test-${test.name}`}
      data-standing={standing}
    >
      <span onClick={(event) => event.stopPropagation()} className="flex items-center">
        <Checkbox
          checked={picked}
          onCheckedChange={(next) => onPick(next === true)}
          disabled={!test.runsHere}
          aria-label={`Pick ${test.name} to run`}
          className={cn(!picked && "opacity-40 group-hover:opacity-100")}
          data-testid={`board-test-${test.name}-pick`}
        />
      </span>
      <TestOutcomeIcon outcome={standing} />
      <span className="flex min-w-0 items-center gap-1.5">
        {test.description !== null
          ? <RichTooltip body={test.description}><span className="truncate font-mono text-[12.5px]">{test.name}</span></RichTooltip>
          : <span className="truncate font-mono text-[12.5px]">{test.name}</span>}
        {test.problems.length > 0 && (
          <Tooltip>
            <TooltipTrigger asChild><FileWarning className="size-4 shrink-0 text-destructive" data-testid="board-test-problems" /></TooltipTrigger>
            <TooltipContent className="max-w-md">It does not fit the schema of its type: {test.problems.join(" ")}</TooltipContent>
          </Tooltip>
        )}
        {test.changed && (
          <Tooltip>
            <TooltipTrigger asChild><PencilLine className="size-4 shrink-0 text-warning" data-testid="board-test-changed" /></TooltipTrigger>
            <TooltipContent className="max-w-sm">Changed since its latest result: run it again for a result of what it checks now.</TooltipContent>
          </Tooltip>
        )}
      </span>
      <span className={cn("hidden truncate text-[12px] md:block", STANDING_TEXT[verdict.standing] ?? "text-muted-foreground")} data-testid="board-test-verdict">
        {verdict.text}
      </span>
      <span className="hidden truncate text-right text-[11.5px] text-warning md:block" data-testid="board-test-stale">
        {stale !== null && (
          <Tooltip>
            <TooltipTrigger asChild>
              <span>run #{stale.assertionRunId}, <RelativeTime value={stale.completedUtc} absolute={false} /></span>
            </TooltipTrigger>
            <TooltipContent className="max-w-sm">The flow's last run left this test out, so this is its result from an earlier run. Run it for how the data stands now.</TooltipContent>
          </Tooltip>
        )}
      </span>
      <span onClick={(event) => event.stopPropagation()}>
        <Tooltip>
          <TooltipTrigger asChild>
            <Button variant="ghost" size="icon-xs" onClick={onRun} disabled={!test.runsHere} aria-label={`Run ${test.name}`} data-testid={`board-test-${test.name}-run`}>
              <Play />
            </Button>
          </TooltipTrigger>
          <TooltipContent>{test.runsHere ? "Run this test" : "It does not test this partition"}</TooltipContent>
        </Tooltip>
      </span>
    </div>
  );
}

/** The flow's own facts, read on hover of its name: what it is for and what fails a run of it. */
function flowAbout(flow: DeliveryAssertionFlow): string {
  const fails = flow.failRunOn === "never" ? "A run of it never fails on its tests; it only reports." : flow.failRunOn === "warning" ? "A run fails on a warning or worse." : "A run fails on a failed or errored test.";
  return `${flow.description ?? "Tests of what OSDU holds."}\n${fails}`;
}

/** One assertion flow on the board: its last run, the ways to run it, and its tests, one line each. */
function FlowSection({ flow, tests, scope, picked, onPick, onOpen, onLaunch }: {
  flow: DeliveryAssertionFlow;
  tests: readonly DeliveryAssertionTest[];
  scope: "all" | "flow";
  picked: ReadonlySet<string>;
  onPick: (name: string, next: boolean) => void;
  onOpen: (test: DeliveryAssertionTest) => void;
  onLaunch: (tests: readonly string[]) => void;
}) {
  const ordered = useMemo(() => byStanding(tests), [tests]);
  const kinds = new Set(flow.tests.map((t) => t.kind));
  const last = flow.lastRun;
  const lastVisual = last === null ? null : runStatusVisual(last.status);
  const runnable = flow.testsPartition && flow.problem === null;

  return (
    <Card className="gap-0 overflow-hidden rounded-lg p-0" data-testid={`board-flow-${flow.name}`}>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1.5 border-b bg-muted/30 px-3 py-2">
        {scope === "all" && (
          <RichTooltip body={flowAbout(flow)}>
            <Link to={`/pipelines/${flow.pipelineId}?tab=tests`} className="truncate font-mono text-[13px] font-medium hover:underline" data-testid="board-flow-link">
              {flow.name}
            </Link>
          </RichTooltip>
        )}
        {kinds.size === 1 && <span className="min-w-0 max-w-[26rem] text-muted-foreground"><KindText kind={[...kinds][0]!} /></span>}
        {last !== null && lastVisual !== null && (
          <span className="flex items-center gap-1.5 text-[12px] text-muted-foreground" data-testid="board-flow-last-run">
            <lastVisual.icon className={cn("size-3.5", STANDING_TEXT[last.status as TestStanding] ?? "")} aria-label={lastVisual.label} />
            <Link to={`/delivery/assertions/runs/${last.assertionRunId}`} className="font-mono hover:underline">#{last.assertionRunId}</Link>
            <RelativeTime value={last.completedUtc ?? last.startedUtc} absolute={false} />
          </span>
        )}
        <div className="ml-auto flex items-center gap-1.5">
          {last !== null && <ReportDownloads assertionRunId={last.assertionRunId} flowName={flow.name} partition={last.partition} testId="board-flow-report" />}
          <Button
            size="sm"
            onClick={() => onLaunch([...picked])}
            disabled={!runnable}
            data-testid={picked.size > 0 ? "board-flow-run-picked" : "board-flow-run-all"}
          >
            <Play />
            {picked.size > 0 ? `Run ${picked.size} picked` : "Run all"}
          </Button>
        </div>
      </div>
      {flow.problem !== null && (
        <Alert variant="destructive" className="rounded-none border-0 border-b" data-testid="board-flow-problem">
          <CircleAlert />
          <AlertDescription>{flow.problem}</AlertDescription>
        </Alert>
      )}
      {ordered.length === 0
        ? flow.problem === null && (
          <p className="px-3 py-3 text-[12.5px] text-muted-foreground" data-testid="board-flow-none">
            {flow.tests.length === 0 ? "The flow declares no test." : "No test of this flow matches the filters."}
          </p>
        )
        : (
          <div className="divide-y">
            {ordered.map((test, index) => (
              <div key={test.name}>
                {kinds.size > 1 && (index === 0 || ordered[index - 1]!.kind !== test.kind) && (
                  <div className="bg-muted/20 px-3 py-1 text-muted-foreground"><KindText kind={test.kind} /></div>
                )}
                <TestRow
                  test={test}
                  lastRunId={last?.assertionRunId ?? null}
                  picked={picked.has(test.name)}
                  onPick={(next) => onPick(test.name, next)}
                  onOpen={() => onOpen(test)}
                  onRun={() => onLaunch([test.name])}
                />
              </div>
            ))}
          </div>
        )}
    </Card>
  );
}

/** The test a link opens the sheet on (`?test=`, and on the board of every flow `?flow=`), when the board shows it. */
function openedTest(flows: readonly DeliveryAssertionFlow[], flowId: string | null, name: string | null) {
  if (name === null) {
    return null;
  }

  for (const flow of flows) {
    const test = flowId !== null && flow.pipelineId !== flowId ? undefined : flow.tests.find((t) => t.name === name);
    if (test !== undefined) {
      return { flow, test };
    }
  }

  return null;
}

/**
 * The board of assertion flows in the workbench's partition (docs/assertions-design.md section 8): how the data stands
 * now, one test a line, by flow. The status strip's counts are the filters to the tests they count; a test opens in a
 * sheet on what it found, and runs again from its line, with others picked, or with every test of its flow. How tests
 * came out over earlier runs is the flow's History and Reports tabs' to show.
 */
export function AssertionBoard({ board, scope }: { board: DeliveryAssertionBoard; scope: "all" | "flow" }) {
  const [params, setParams] = useSearchParams();
  const [term, setTerm] = useState("");
  const [tags, setTags] = useState<ReadonlySet<string>>(new Set());
  const [filter, setFilter] = useState<BoardFilter | null>(null);
  const [picked, setPicked] = useState<ReadonlyMap<string, ReadonlySet<string>>>(new Map());
  const [launch, setLaunch] = useState<AssertionLaunch | null>(null);

  const totals = board.totals;
  const allTags = useMemo(() => {
    const counts = new Map<string, number>();
    for (const test of board.flows.flatMap((flow) => flow.tests)) {
      for (const tag of test.tags) {
        counts.set(tag, (counts.get(tag) ?? 0) + 1);
      }
    }

    return [...counts.entries()].sort((a, b) => a[0].localeCompare(b[0]));
  }, [board.flows]);
  const lowered = term.trim().toLowerCase();
  const shown = useMemo(
    () => board.flows.map((flow) => ({ flow, tests: flow.tests.filter((test) => matches(test, lowered, tags, filter)) })),
    [board.flows, filter, lowered, tags],
  );
  const filtering = lowered !== "" || tags.size > 0 || filter !== null;
  const visible = filtering ? shown.filter((entry) => entry.tests.length > 0) : shown;
  const opened = openedTest(board.flows, scope === "all" ? params.get("flow") : null, params.get("test"));

  const open = (flow: DeliveryAssertionFlow, test: DeliveryAssertionTest | null) => {
    setParams((was) => {
      const next = new URLSearchParams(was);
      for (const key of SHEET_PARAMS) {
        next.delete(key);
      }

      if (test !== null) {
        next.set("test", test.name);
        if (scope === "all") {
          next.set("flow", flow.pipelineId);
        }
      }

      return next;
    }, { replace: true });
  };

  const pick = (flow: DeliveryAssertionFlow, name: string, on: boolean) => setPicked((was) => {
    const next = new Map(was);
    const set = new Set(next.get(flow.pipelineId) ?? []);
    if (on) {
      set.add(name);
    } else {
      set.delete(name);
    }

    next.set(flow.pipelineId, set);
    return next;
  });

  const cells: StripCell[] = [
    { key: "failed", label: "Failing", count: totals.failed, standing: "failed", hint: "A check of error severity failed." },
    { key: "errored", label: "Errored", count: totals.errored, standing: "errored", hint: "The test could not find out: a query OSDU refused, a service that did not answer." },
    { key: "warned", label: "Warned", count: totals.warned, standing: "warned", hint: "Only checks of warning severity failed." },
    { key: "notRun", label: "Not run", count: totals.notRun, standing: null, hint: "No result yet in this partition." },
    { key: "passed", label: "Passing", count: totals.passed, standing: "passed", hint: "Every check of error or warning severity passed in the latest run." },
    ...(totals.problems > 0 ? [{ key: "problems", label: "Unfit", count: totals.problems, standing: "errored" as const, hint: "Tests that do not fit the schema of their type, and so are not evaluated." }] : []),
    ...(totals.changed > 0 ? [{ key: "changed", label: "Changed", count: totals.changed, standing: "warned" as const, hint: "Tests changed since their latest result." }] : []),
  ];

  if (board.flows.length === 0) {
    return (
      <EmptyState
        icon={<ListChecks />}
        title="No assertion flows synced yet"
        description="Register a repository holding flowType: assertion documents; each flow's tests appear here with how they came out."
        data-testid="assertion-board-empty"
      />
    );
  }

  return (
    <div className="flex flex-col gap-3" data-testid="assertion-board">
      <StatusStrip
        passRate={totals.passRate}
        evaluated={totals.passed + totals.failed + totals.warned + totals.errored}
        bar={{ passed: totals.passed, warned: totals.warned, failed: totals.failed, errored: totals.errored, skipped: totals.notRun }}
        cells={cells}
        selected={filter}
        onSelect={(key) => setFilter(key as BoardFilter | null)}
        testId="assertion-scoreboard"
      />

      <FilterBar>
        <SearchInput
          value={term}
          onChange={setTerm}
          placeholder="Find a test by name, type, tag, query or check"
          label="Find a test"
          testId="assertion-board-search"
          className="sm:w-96"
        />
        <TagFilter tags={allTags} picked={tags} onChange={setTags} />
        {filtering && (
          <Button variant="ghost" size="sm" onClick={() => { setTerm(""); setTags(new Set()); setFilter(null); }} data-testid="assertion-board-clear">
            <X />
            Clear
          </Button>
        )}
      </FilterBar>

      {visible.length === 0
        ? <EmptyState title="No test matches" description="Clear the filters to see every test." data-testid="assertion-board-none" />
        : visible.map(({ flow, tests }) => (
          <FlowSection
            key={flow.pipelineId}
            flow={flow}
            tests={tests}
            scope={scope}
            picked={picked.get(flow.pipelineId) ?? new Set()}
            onPick={(name, on) => pick(flow, name, on)}
            onOpen={(test) => open(flow, test)}
            onLaunch={(names) => setLaunch({ pipelineId: flow.pipelineId, repoId: flow.repoId, flowName: flow.name, tests: names, tags: [] })}
          />
        ))}

      {opened !== null && (
        <AssertionTestSheet
          flow={opened.flow}
          test={opened.test}
          onClose={() => open(opened.flow, null)}
          onRun={(test) => setLaunch({ pipelineId: opened.flow.pipelineId, repoId: opened.flow.repoId, flowName: opened.flow.name, tests: [test.name], tags: [] })}
        />
      )}
      <AssertionRunDialog launch={launch} onClose={() => setLaunch(null)} />
    </div>
  );
}
