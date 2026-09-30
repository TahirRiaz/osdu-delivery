import { useNavigate } from "react-router-dom";
import { Shapes } from "lucide-react";
import { Button } from "@/components/ui/button";
import type { RunDetail } from "@/api/types";
import { DetailPair } from "@/components/DetailPair";
import { runResult } from "../runOutcome";

/** A count a dimension run reported, or a muted dash when it reported none (a plan, a run that stopped before its builds). */
function Count({ value, testId }: { value: number | null; testId: string }) {
  return value !== null
    ? <span className="font-mono tabular-nums" data-testid={testId}>{value.toLocaleString("en-US")}</span>
    : <span className="text-muted-foreground" data-testid={testId}>-</span>;
}

function numberOf(result: Record<string, unknown> | null, field: string): number | null {
  const value = result?.[field];
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

/** The members or originals a build run's dimensions hold together, summed from its result; null for a run that reported no build. */
function sumOf(result: Record<string, unknown> | null, field: "members" | "originals"): number | null {
  const dimensions = result?.dimensions;
  if (!Array.isArray(dimensions) || result?.operation !== "build") {
    return null;
  }

  return dimensions.reduce<number>((sum, entry) => {
    const value = entry !== null && typeof entry === "object" ? (entry as Record<string, unknown>)[field] : null;
    return sum + (typeof value === "number" && Number.isFinite(value) ? value : 0);
  }, 0);
}

/** A dimension run's way to the dimensions it built, on the page every partition's dimensions are read on. */
export function DimensionRunActions() {
  const navigate = useNavigate();
  return (
    <Button variant="outline" size="sm" onClick={() => navigate("/delivery/dimensions")} data-testid="run-open-dimensions">
      <Shapes />
      Open dimensions
    </Button>
  );
}

/** How the run's dimensions came out, in place of the row counts SQLFlow's own runs carry. */
export function DimensionRunCounts({ run }: { run: RunDetail }) {
  const result = runResult(run);
  return (
    <>
      <DetailPair label="Built"><Count value={numberOf(result, "built")} testId="run-dimensions-built" /></DetailPair>
      <DetailPair label="Failed"><Count value={numberOf(result, "failed")} testId="run-dimensions-failed" /></DetailPair>
      <DetailPair label="Skipped"><Count value={numberOf(result, "skipped")} testId="run-dimensions-skipped" /></DetailPair>
      <DetailPair label="Members"><Count value={sumOf(result, "members")} testId="run-dimensions-members" /></DetailPair>
      <DetailPair label="Originals"><Count value={sumOf(result, "originals")} testId="run-dimensions-originals" /></DetailPair>
    </>
  );
}
