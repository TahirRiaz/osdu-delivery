import { useMemo, useState, type ReactNode } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { CircleAlert, FileWarning, ListChecks, PencilLine, Play, X } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { EmptyState } from "@/components/EmptyState";
import { FilterBar } from "@/components/FilterBar";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { cn } from "@/lib/utils";
import type { DeliveryAssertionBoard, DeliveryAssertionFlow, DeliveryAssertionTest } from "../../../api/delivery";
import { KindText } from "../KindText";
import { AssertionRunStatusBadge, HistoryStrip, PassRateRing, TagChip, TestOutcomeIcon } from "./AssertionBadges";
import { AssertionRunDialog, ReportDownloads, type AssertionLaunch } from "./AssertionRunDialog";
import { AssertionTestSheet } from "./AssertionTestSheet";
import { OUTCOME_ORDER, OUTCOME_VISUALS, STANDING_TEXT, counted, standingOf, type TestStanding } from "./assertionFormat";

/** What the scoreboard's tiles filter the tests by: where they stand, or what needs a look before they can be trusted. */
type BoardFilter = TestStanding | "problems" | "changed";

const TONE_TEXT: Record<string, string> = {
  success: "text-success",
  destructive: "text-destructive",
  warning: "text-warning",
  info: "text-info",
  muted: "text-muted-foreground",
};

interface Tile {
  filter: BoardFilter;
  label: string;
  count: number;
  hint: string;
}

/** One headline number that is also the filter to the tests it counts; pressed, it wears the accent ring. */
function ScoreTile({ tile, pressed, onToggle }: { tile: Tile; pressed: boolean; onToggle: () => void }) {
  const visual = tile.filter === "problems"
    ? { tone: "destructive", icon: FileWarning }
    : tile.filter === "changed"
      ? { tone: "warning", icon: PencilLine }
      : OUTCOME_VISUALS[tile.filter];
  const Icon = visual.icon;
  const quiet = tile.count === 0;
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <button
          type="button"
          aria-pressed={pressed}
          onClick={onToggle}
          className={cn(
            "flex min-w-[6.5rem] flex-1 flex-col items-start gap-0.5 rounded-lg border bg-card px-3 py-2 text-left transition-colors hover:bg-accent/50",
            pressed && "border-primary ring-1 ring-inset ring-primary",
          )}
          data-testid={`board-tile-${tile.filter}`}
        >
          <span className="flex items-center gap-1.5 text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
            <Icon className={cn("size-3.5", quiet ? "text-muted-foreground" : TONE_TEXT[visual.tone])} />
            {tile.label}
          </span>
          <span
            className={cn("font-mono text-xl font-semibold leading-7 tabular-nums", quiet ? "text-muted-foreground" : TONE_TEXT[visual.tone])}
            data-testid={`board-tile-${tile.filter}-value`}
          >
            {tile.count.toLocaleString("en-US")}
          </span>
        </button>
      </TooltipTrigger>
      <TooltipContent>{tile.hint}</TooltipContent>
    </Tooltip>
  );
}

function matches(test: DeliveryAssertionTest, term: string, tags: ReadonlySet<string>, filter: BoardFilter | null): boolean {
  if (tags.size > 0 && !test.tags.some((tag) => tags.has(tag))) {
    return false;
  }

  if (filter === "problems" ? test.problems.length === 0 : filter === "changed" ? !test.changed : filter !== null && standingOf(test) !== filter) {
    return false;
  }

  if (term === "") {
    return true;
  }

  return [test.name, test.description ?? "", test.kind, test.query ?? "", ...test.tags, ...test.assertions.map((a) => a.label)]
    .some((text) => text.toLowerCase().includes(term));
}

/** The order tests are listed in within a kind: what needs a look first, then as the document declares them. */
function byStanding(tests: readonly DeliveryAssertionTest[]): DeliveryAssertionTest[] {
  return tests
    .map((test, order) => ({ test, order }))
    .sort((a, b) => OUTCOME_ORDER.indexOf(standingOf(a.test)) - OUTCOME_ORDER.indexOf(standingOf(b.test)) || a.order - b.order)
    .map((entry) => entry.test);
}

/** One test on the board: where it stands, what it holds, how it has come out, and a way to run it on its own. */
function TestRow({ test, selected, onSelect, onOpen, onRun, onReport }: {
  test: DeliveryAssertionTest;
  selected: boolean;
  onSelect: (next: boolean) => void;
  onOpen: () => void;
  onRun: () => void;
  onReport: (assertionRunId: number) => void;
}) {
  const standing = standingOf(test);
  const latest = test.latest;
  const holding = latest === null ? null : latest.assertions - latest.failedAssertions;
  return (
    <div
      role="button"
      tabIndex={0}
      onClick={onOpen}
      onKeyDown={(event) => { if (event.key === "Enter") { onOpen(); } }}
      className={cn(
        "grid cursor-pointer grid-cols-[auto_auto_minmax(0,1fr)_auto] items-center gap-x-3 gap-y-1 px-3 py-2 outline-none transition-colors hover:bg-muted/40 focus-visible:bg-muted/40",
        "md:grid-cols-[auto_auto_minmax(0,1fr)_8rem_11.5rem_7rem_auto]",
        standing === "elsewhere" && "opacity-55",
      )}
      data-testid={`board-test-${test.name}`}
      data-standing={standing}
    >
      <span onClick={(event) => event.stopPropagation()} className="flex items-center">
        <Checkbox
          checked={selected}
          onCheckedChange={(next) => onSelect(next === true)}
          disabled={!test.runsHere}
          aria-label={`Pick ${test.name} to run`}
          data-testid={`board-test-${test.name}-pick`}
        />
      </span>
      <TestOutcomeIcon outcome={standing} />
      <div className="min-w-0">
        <div className="flex min-w-0 flex-wrap items-center gap-1.5">
          <span className="truncate font-mono text-[12.5px] font-medium">{test.name}</span>
          {test.tags.map((tag) => <span key={tag} className="font-mono text-[10.5px] text-muted-foreground">#{tag}</span>)}
          {test.problems.length > 0 && (
            <Tooltip>
              <TooltipTrigger asChild>
                <Badge variant="outline" className="border-destructive/50 text-[10.5px] text-destructive" data-testid="board-test-problems">
                  <FileWarning />
                  does not fit its template
                </Badge>
              </TooltipTrigger>
              <TooltipContent className="max-w-md">{test.problems.join(" ")}</TooltipContent>
            </Tooltip>
          )}
          {test.changed && (
            <Tooltip>
              <TooltipTrigger asChild>
                <Badge variant="outline" className="border-warning/55 text-[10.5px] text-warning" data-testid="board-test-changed">
                  <PencilLine />
                  changed
                </Badge>
              </TooltipTrigger>
              <TooltipContent className="max-w-sm">The test changed since its latest result: run it again for a result of what it asserts now.</TooltipContent>
            </Tooltip>
          )}
        </div>
        <div className="truncate text-[12px] text-muted-foreground">
          {test.description ?? test.assertions.map((a) => a.label).join(", ")}
        </div>
      </div>
      <div className="hidden text-right md:block">
        {holding === null || standing === "errored"
          ? (
            <span className={cn("text-[12px]", standing === "errored" ? "text-destructive" : "text-muted-foreground")}>
              {standing === "elsewhere" ? "other partition" : standing === "errored" ? "not evaluated" : "not run"}
            </span>
          )
          : (
            <span
              className={cn("font-mono text-[12px] tabular-nums", latest!.failedAssertions > 0 && STANDING_TEXT[standing === "passed" ? "noted" : standing])}
              title="assertions holding in the latest run"
            >
              {holding}/{latest!.assertions} hold
            </span>
          )}
        {latest !== null && latest.matched !== null && (
          <div className="font-mono text-[11px] tabular-nums text-muted-foreground" title="records the test matched">
            {latest.matched.toLocaleString("en-US")} rec{latest.sampled ? ", sample" : ""}
          </div>
        )}
      </div>
      <div className="hidden md:block" onClick={(event) => event.stopPropagation()}>
        <HistoryStrip points={test.history} slots={10} onPick={(point) => onReport(point.assertionRunId)} />
      </div>
      <div className="hidden truncate text-right text-[11.5px] text-muted-foreground md:block">
        {latest !== null ? <RelativeTime value={latest.completedUtc} absolute={false} /> : "-"}
      </div>
      <span onClick={(event) => event.stopPropagation()}>
        <Tooltip>
          <TooltipTrigger asChild>
            <Button
              variant="ghost"
              size="icon-sm"
              onClick={onRun}
              disabled={!test.runsHere}
              aria-label={`Run ${test.name}`}
              data-testid={`board-test-${test.name}-run`}
            >
              <Play />
            </Button>
          </TooltipTrigger>
          <TooltipContent>{test.runsHere ? "Run this test on its own" : "It does not test this partition"}</TooltipContent>
        </Tooltip>
      </span>
    </div>
  );
}

/** The tests of one kind within a flow, under the kind and how they stand. */
function KindGroup({ kind, tests, children }: { kind: string; tests: readonly DeliveryAssertionTest[]; children: ReactNode }) {
  const tally = OUTCOME_ORDER
    .map((standing) => ({ standing, count: tests.filter((t) => standingOf(t) === standing).length }))
    .filter((entry) => entry.count > 0);
  return (
    <div className="border-t first:border-t-0" data-testid="board-kind">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 bg-muted/30 px-3 py-1.5">
        <span className="min-w-0 max-w-[32rem] flex-1"><KindText kind={kind} /></span>
        <span className="flex flex-wrap items-center gap-2 text-[11px] text-muted-foreground">
          {tally.map((entry) => (
            <span key={entry.standing} className={cn("tabular-nums", TONE_TEXT[OUTCOME_VISUALS[entry.standing].tone])}>
              {entry.count} {OUTCOME_VISUALS[entry.standing].label}
            </span>
          ))}
        </span>
      </div>
      <div className="divide-y">{children}</div>
    </div>
  );
}

/** One assertion flow's part of the board: its last run, its tests by kind, and the ways to run them. */
function FlowSection({ flow, tests, scope, picked, onPick, onOpen, onLaunch, onReport }: {
  flow: DeliveryAssertionFlow;
  tests: readonly DeliveryAssertionTest[];
  scope: "all" | "flow";
  picked: ReadonlySet<string>;
  onPick: (name: string, next: boolean) => void;
  onOpen: (test: DeliveryAssertionTest) => void;
  onLaunch: (tests: readonly string[]) => void;
  onReport: (assertionRunId: number) => void;
}) {
  const byKind = useMemo(() => {
    const groups = new Map<string, DeliveryAssertionTest[]>();
    for (const test of byStanding(tests)) {
      groups.set(test.kind, [...(groups.get(test.kind) ?? []), test]);
    }

    return [...groups.entries()];
  }, [tests]);
  const last = flow.lastRun;
  const runnable = flow.testsPartition && flow.problem === null;

  return (
    <Card className="gap-0 overflow-hidden rounded-lg p-0" data-testid={`board-flow-${flow.name}`}>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2 border-b px-3 py-2.5">
        <div className="flex min-w-0 flex-1 flex-col">
          {scope === "all"
            ? (
              <Link to={`/pipelines/${flow.pipelineId}?tab=tests`} className="truncate font-mono text-[13px] font-medium hover:underline" data-testid="board-flow-link">
                {flow.name}
              </Link>
            )
            : <span className="text-[13px] font-medium">{counted(flow.tests.length, "test")}</span>}
          <span className="truncate text-[12px] text-muted-foreground">
            {flow.description ?? "Checks what OSDU holds once the data has landed."}
            {` A run fails on ${flow.failRunOn === "never" ? "nothing: it only reports" : flow.failRunOn === "warning" ? "a warning or worse" : "an error"}.`}
          </span>
        </div>
        {flow.partitions.length > 0 && (
          <span className="flex flex-wrap items-center gap-1" title="The partitions the flow tests">
            {flow.partitions.map((name) => (
              <Badge key={name} variant={name === flow.partition ? "default" : "secondary"} className="font-mono text-[11px]">{name}</Badge>
            ))}
          </span>
        )}
        {last !== null && (
          <span className="flex items-center gap-1.5 text-[12px] text-muted-foreground" data-testid="board-flow-last-run">
            <AssertionRunStatusBadge status={last.status} />
            <Link to={`/delivery/assertions/runs/${last.assertionRunId}`} className="font-mono hover:underline">#{last.assertionRunId}</Link>
            <RelativeTime value={last.completedUtc ?? last.startedUtc} absolute={false} />
          </span>
        )}
        <div className="flex items-center gap-1.5">
          {last !== null && <ReportDownloads assertionRunId={last.assertionRunId} flowName={flow.name} partition={last.partition} testId="board-flow-report" />}
          {picked.size > 0 && (
            <Button size="sm" variant="outline" onClick={() => onLaunch([...picked])} disabled={!runnable} data-testid="board-flow-run-picked">
              <Play />
              Run {picked.size} picked
            </Button>
          )}
          <Button size="sm" onClick={() => onLaunch([])} disabled={!runnable} data-testid="board-flow-run-all">
            <Play />
            Run all
          </Button>
        </div>
      </div>
      {flow.problem !== null && (
        <Alert variant="destructive" className="rounded-none border-0 border-b" data-testid="board-flow-problem">
          <CircleAlert />
          <AlertDescription>{flow.problem}</AlertDescription>
        </Alert>
      )}
      {byKind.length === 0
        ? (
          <p className="px-3 py-4 text-[12.5px] text-muted-foreground" data-testid="board-flow-none">
            {flow.tests.length === 0 ? "The flow declares no test the board can show here." : "No test of this flow matches the filters."}
          </p>
        )
        : byKind.map(([kind, group]) => (
          <KindGroup key={kind} kind={kind} tests={group}>
            {group.map((test) => (
              <TestRow
                key={test.name}
                test={test}
                selected={picked.has(test.name)}
                onSelect={(next) => onPick(test.name, next)}
                onOpen={() => onOpen(test)}
                onRun={() => onLaunch([test.name])}
                onReport={onReport}
              />
            ))}
          </KindGroup>
        ))}
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
 * The board of assertion flows in the workbench's partition (docs/assertions-design.md section 8): every test, where it
 * stands and how it has come out over the last runs, grouped by flow and by the kind it reads. The scoreboard's numbers
 * are the filters to the tests they count; a test opens in a sheet with what every assertion found, and runs from its row,
 * from the sheet, or with the others picked.
 */
export function AssertionBoard({ board, scope }: { board: DeliveryAssertionBoard; scope: "all" | "flow" }) {
  const navigate = useNavigate();
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

  const openFlow = params.get("flow");
  const openTest = params.get("test");
  const opened = openedTest(board.flows, scope === "all" ? openFlow : null, openTest);

  const open = (flow: DeliveryAssertionFlow, test: DeliveryAssertionTest | null) => {
    setParams((was) => {
      const next = new URLSearchParams(was);
      if (test === null) {
        next.delete("test");
        next.delete("flow");
      } else {
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

  const tiles: Tile[] = [
    { filter: "failed", label: "Failed", count: totals.failed, hint: "An error-severity assertion did not hold." },
    { filter: "errored", label: "Errored", count: totals.errored, hint: "The test could not find out: a query OSDU refused, a service that did not answer." },
    { filter: "warned", label: "Warned", count: totals.warned, hint: "Only a warning-severity assertion did not hold." },
    { filter: "notRun", label: "Not run", count: totals.notRun, hint: "No result yet in this partition." },
    { filter: "passed", label: "Passed", count: totals.passed, hint: "Every assertion held in the latest run." },
    { filter: "problems", label: "Unfit", count: totals.problems, hint: "Tests that do not fit the template of their kind, and so are not evaluated." },
    { filter: "changed", label: "Changed", count: totals.changed, hint: "Tests whose definition changed since their latest result." },
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
    <div className="flex flex-col gap-4" data-testid="assertion-board">
      <Card className="flex flex-col gap-3 rounded-lg p-3 lg:flex-row lg:items-center" data-testid="assertion-scoreboard">
        <div className="shrink-0 px-1">
          <PassRateRing rate={totals.passRate} evaluated={totals.passed + totals.failed + totals.warned + totals.errored} />
        </div>
        <div className="flex flex-1 flex-wrap gap-2">
          {tiles.map((tile) => (
            <ScoreTile key={tile.filter} tile={tile} pressed={filter === tile.filter} onToggle={() => setFilter((was) => (was === tile.filter ? null : tile.filter))} />
          ))}
        </div>
      </Card>

      <FilterBar>
        <SearchInput
          value={term}
          onChange={setTerm}
          placeholder="Find a test by name, kind, tag, query or assertion"
          label="Find a test"
          testId="assertion-board-search"
          className="sm:w-96"
        />
        {allTags.length > 0 && (
          <div className="flex flex-wrap items-center gap-1.5" role="group" aria-label="Show the tests carrying a tag">
            {allTags.map(([tag, count]) => (
              <TagChip
                key={tag}
                tag={tag}
                count={count}
                pressed={tags.has(tag)}
                onToggle={() => setTags((was) => {
                  const next = new Set(was);
                  if (!next.delete(tag)) {
                    next.add(tag);
                  }

                  return next;
                })}
              />
            ))}
          </div>
        )}
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
            onReport={(assertionRunId) => navigate(`/delivery/assertions/runs/${assertionRunId}`)}
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
