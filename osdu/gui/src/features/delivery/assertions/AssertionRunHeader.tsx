import { useNavigate } from "react-router-dom";
import { FileText } from "lucide-react";
import { Button } from "@/components/ui/button";
import type { RunDetail } from "@/api/types";
import { DetailPair } from "@/components/DetailPair";
import { runResult } from "../runOutcome";

/** A count a test run reported, or a muted dash when it reported none (a plan, a run that stopped before its tests). */
function Count({ result, field, testId }: { result: Record<string, unknown> | null; field: string; testId: string }) {
  const value = result?.[field];
  return typeof value === "number" && Number.isFinite(value)
    ? <span className="font-mono tabular-nums" data-testid={testId}>{value.toLocaleString("en-US")}</span>
    : <span className="text-muted-foreground" data-testid={testId}>-</span>;
}

/** The report a test run kept, which its result names. */
function reportOf(run: RunDetail): number | null {
  const id = runResult(run)?.assertionRunId;
  return typeof id === "number" && Number.isSafeInteger(id) && id > 0 ? id : null;
}

/** An assertion run's way to the report it kept. */
export function AssertionRunActions({ run }: { run: RunDetail }) {
  const navigate = useNavigate();
  const report = reportOf(run);
  return report === null
    ? null
    : (
      <Button variant="outline" size="sm" onClick={() => navigate(`/delivery/assertions/runs/${report}`)} data-testid="run-assertion-report">
        <FileText />
        Open the report
      </Button>
    );
}

/** How the run's tests came out, in place of the row counts SQLFlow's own runs carry. */
export function AssertionRunCounts({ run }: { run: RunDetail }) {
  const result = runResult(run);
  return (
    <>
      <DetailPair label="Tests"><Count result={result} field="tests" testId="run-tests" /></DetailPair>
      <DetailPair label="Passed"><Count result={result} field="passed" testId="run-tests-passed" /></DetailPair>
      <DetailPair label="Failed"><Count result={result} field="failed" testId="run-tests-failed" /></DetailPair>
      <DetailPair label="Warned"><Count result={result} field="warned" testId="run-tests-warned" /></DetailPair>
      <DetailPair label="Errored"><Count result={result} field="errored" testId="run-tests-errored" /></DetailPair>
      <DetailPair label="Skipped"><Count result={result} field="skipped" testId="run-tests-skipped" /></DetailPair>
    </>
  );
}
